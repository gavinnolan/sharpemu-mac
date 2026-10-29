// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.Gpu.Metal;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Gpu.Metal.Tests.Scenes;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Host.MacOS;

namespace SharpEmu.PerfAudit;

/// <summary>
/// 6. Guest-memory read layers (raw pointer -> ICpuMemory -> real PhysicalVirtualMemory -> TrackedCpuMemory ->
/// MetalRenderHost readers) and write-tracking bookkeeping (PageGuard watch add/remove, real mprotect).
/// </summary>
internal static unsafe class MemoryBenches
{
    private const ulong GuestBase = 0x2_0000_0000;
    private const double WordReadsPerFrame = 13825 * 40.0; // ~40 guest words per materialization (PERF-REPORT: "a few dozen")
    private const double RangeReadsPerFrame = 13825 * 6.0;

    [DllImport("libc", EntryPoint = "mmap")] private static extern nint Mmap(nint addr, nuint len, int prot, int flags, int fd, nint offset);
    [DllImport("libc", EntryPoint = "mprotect")] private static extern int Mprotect(nint addr, nuint len, int prot);

    public static void Run()
    {
        Console.WriteLine("--- 6. Guest memory read layers + write tracking ---");
        long sink = 0;

        // 6a: raw native pointer, 32 bytes.
        using var native = new NativeGuestMemory(8 << 20);
        native.WriteWords(NativeGuestMemory.Base + 0x1000, Enumerable.Range(0, 1024).Select(v => (uint)v).ToArray());
        var raw = native.Raw(NativeGuestMemory.Base + 0x1000);
        Harness.Measure("6a", "raw pointer: Unsafe read 4 x uint (16 B), no checks", 1, WordReadsPerFrame / 4, i =>
        {
            var p = (uint*)(raw + ((i & 63) << 4));
            sink += p[0] + p[1] + p[2] + p[3];
        }, unit: "16B read", note: "floor: one cache line, no abstraction");
        var buffer = new byte[32];
        Harness.Measure("6b", "NativeGuestMemory.TryRead 32 B (ICpuMemory interface call, bounds + memcpy)", 1, RangeReadsPerFrame, i =>
        {
            native.TryRead(NativeGuestMemory.Base + 0x1000 + (ulong)((i & 63) << 5), buffer);
        }, unit: "32B read");

        // Real PhysicalVirtualMemory on the macOS view host.
        HostMemoryAuthority.Initialize(MacOSHost.CreateMemory);
        var views = MacOSHost.CreateViewMemory();
        using var pvm = new PhysicalVirtualMemory(viewHost: views, backingBytes: 32UL * 1024 * 1024);
        const ulong Region = 8UL << 20;
        var hole = (Region + views.Granularity - 1) / views.Granularity * views.Granularity;
        if (!pvm.TryHoldRangeAtOrAbove(GuestBase, hole, views.Granularity, out var address) ||
            !pvm.TryMapBacked(address, Region, 0, GuestPageProtection.Read | GuestPageProtection.Write, out var failure))
        {
            throw new InvalidOperationException("PVM map failed");
        }

        using var gpuMemory = new GuestGpuMemory(pvm);
        gpuMemory.Register(address, Region, GuestPageProtection.Read | GuestPageProtection.Write);
        var seed = new byte[4096];
        for (var i = 0; i < seed.Length; i++) seed[i] = (byte)i;
        pvm.TryWriteBacking(address, seed);
        var tracked = new TrackedCpuMemory(pvm);
        var word = new byte[4];
        Harness.Measure("6c", "PhysicalVirtualMemory.TryRead 32 B (backed, single-view lock-free path)", 1, RangeReadsPerFrame, i =>
            pvm.TryRead(address + (ulong)((i & 63) << 5), buffer), unit: "32B read", note: "Core/Memory/PhysicalVirtualMemory.cs:2330");
        Harness.Measure("6d", "PhysicalVirtualMemory.TryRead 4 B (a descriptor word)", 1, WordReadsPerFrame, i =>
            pvm.TryRead(address + (ulong)((i & 255) << 2), word), unit: "word");
        Harness.Measure("6e", "PhysicalVirtualMemory.TryReadBacking 32 B", 1, RangeReadsPerFrame, i =>
            pvm.TryReadBacking(address + (ulong)((i & 63) << 5), buffer), unit: "32B read", note: "Core/Memory/PhysicalVirtualMemory.cs:1483");
        Harness.Measure("6f", "TrackedCpuMemory.TryRead 32 B (the CPU-side wrapper)", 1, RangeReadsPerFrame, i =>
            tracked.TryRead(address + (ulong)((i & 63) << 5), buffer), unit: "32B read", note: "Core/Cpu/TrackedCpuMemory.cs:128 (guest CPU path; GPU uses the inner view)");

        // MetalRenderHost reader layers over the real PVM.
        var device = MetalNative.MTLCreateSystemDefaultDevice();
        var queue = MetalNative.Send(device, MetalNative.Selector("newCommandQueue"));
        var relay = new MetalGpuWorkerRelay(static () => { });
        var host = new MetalRenderHost(device, queue, pvm, pvm, gpuMemory.Pages, relay);
        relay.BindCurrentThread();
        Harness.Measure("6g", "MetalRenderHost.TryReadGuest 32 B (no overlay/GPU-write cover)", 1, RangeReadsPerFrame, i =>
            host.TryReadGuest(address + (ulong)((i & 63) << 5), buffer), unit: "32B read", note: "V2/MetalRenderHost.cs:1642 -> :1695 MayCover/MayOwn tests -> PVM.TryRead");
        Harness.Measure("6h", "MetalRenderHost.TryReadGuestWord, no word-line scope (falls to TryReadGuest)", 1, WordReadsPerFrame, i =>
        {
            host.TryReadGuestWord(address + (ulong)((i & 255) << 2), out var w);
            sink += w;
        }, unit: "word", note: "V2/MetalRenderHost.Pipelines.cs:1713");
        host.BeginGuestWordReads();
        Harness.Measure("6i", "MetalRenderHost.TryReadGuestWord inside Begin/EndGuestWordReads (line-cache hit)", 1, WordReadsPerFrame, i =>
        {
            host.TryReadGuestWord(address + (ulong)((i & 15) << 2), out var w);
            sink += w;
        }, unit: "word", note: "V2/MetalRenderHost.WordLines.cs:63 TryReadWordFromLine; hit path");
        host.EndGuestWordReads();
        Harness.Measure("6j", "Begin/EndGuestWordReads + 40 word reads over 4 lines (per scope, fills included)", 1, 13825, i =>
        {
            host.BeginGuestWordReads();
            for (var w = 0; w < 40; w++)
            {
                host.TryReadGuestWord(address + (ulong)(((i & 63) * 256) + w * 4 + (w >> 3) * 64), out var v);
                sink += v;
            }

            host.EndGuestWordReads();
        }, unit: "materialization", note: "per materialization call with ~40 reads over 5 lines");
        Harness.Measure("6k", "MetalRenderHost.TryReadCleanGuestWord", 1, WordReadsPerFrame, i =>
        {
            host.TryReadCleanGuestWord(address + (ulong)((i & 255) << 2), out var w);
            sink += w;
        }, unit: "word", note: "V2/MetalRenderHost.Pipelines.cs:1738 image-cache test + TryReadBacking + 2 overlay applies");
        Harness.Measure("6l", "MetalRenderHost.TryReadResidentGuestBytes 32 B", 1, RangeReadsPerFrame, i =>
            host.TryReadResidentGuestBytes(address + (ulong)((i & 63) << 5), buffer, true), unit: "32B read", note: "V2/MetalRenderHost.Pipelines.cs:1758");
        {
            var addrs = new ulong[6];
            var lens = new int[6];
            var offs = new int[6];
            var recorded = new byte[6 * 32];
            for (var r = 0; r < 6; r++) { addrs[r] = address + (ulong)(r * 512); lens[r] = 32; offs[r] = r * 32; }
            for (var r = 0; r < 6; r++) pvm.TryRead(addrs[r], recorded.AsSpan(r * 32, 32));
            Harness.Measure("6m", "MetalRenderHost.TryCompareResidentGuestBytes, 6 clustered 32 B ranges (all equal)", 1, 13825, i =>
                sink += host.TryCompareResidentGuestBytes(addrs, lens, offs, recorded) ? 1 : 0, unit: "validation",
                note: "V2/MetalRenderHost.Pipelines.cs:1790; the warm-hit validation");
        }

        // Write tracking: PageGuard bookkeeping alone (no protection syscall), then with the real PVM address space.
        var space = new SceneAddressSpace();
        using var guard = new PageGuard(space);
        var watchBase = GuestBase + 0x100_0000;
        Harness.Measure("6n", "PageGuard.AddWatch + RemoveWatch, 1 page, no-op address space (bookkeeping only)", 1, 1500, i =>
        {
            var a = watchBase + (ulong)((i & 1023) << 12);
            guard.AddWatch(a, 4096, false);
            guard.RemoveWatch(a, 4096, false);
        }, unit: "texture", note: "HLE/GpuMemory/PageGuard.cs:176/178");
        Harness.Measure("6o", "PageGuard.AddWatch + RemoveWatch, 64 pages (256 KiB), no-op address space", 1, 100, i =>
        {
            var a = watchBase + (ulong)((i & 15) << 18);
            guard.AddWatch(a, 64 * 4096, false);
            guard.RemoveWatch(a, 64 * 4096, false);
        }, unit: "texture", note: "bookkeeping only");
        guard.AddWatch(watchBase, 4096, false);
        Harness.Measure("6p", "PageGuard.Allows (read fault check on a watched page)", 1, 1500, i =>
            sink += guard.Allows(watchBase + (ulong)((i & 7) << 2), FaultKind.Read) ? 1 : 0, unit: "check", note: "PageGuard.cs:185");
        guard.RemoveWatch(watchBase, 4096, false);

        // Real protection: GuestGpuMemory's page guard over the PVM, so Add/RemoveWatch really change page protection.
        var realBase = address + 0x20_0000;
        Harness.Measure("6q", "GuestGpuMemory.Pages AddWatch + RemoveWatch, 1 page, REAL protection change (PVM)", 1, 1500, i =>
        {
            var a = realBase + (ulong)((i & 63) << 12);
            gpuMemory.Pages.AddWatch(a, 4096, false);
            gpuMemory.Pages.RemoveWatch(a, 4096, false);
        }, unit: "texture", note: "write watch -> PVM.TryProtect -> host vm_protect, restored on remove");
        Harness.Measure("6r", "GuestGpuMemory.Pages AddWatch + RemoveWatch, 64 pages, REAL protection change (PVM)", 1, 100, i =>
        {
            var a = realBase + (ulong)((i & 3) << 18);
            gpuMemory.Pages.AddWatch(a, 64 * 4096, false);
            gpuMemory.Pages.RemoveWatch(a, 64 * 4096, false);
        }, unit: "texture", note: "one protect call per contiguous run");

        // Syscall bound: raw mprotect round trip on an anonymous page. The SIGSEGV fault path itself (a guest write to a
        // watched page -> signal handler -> PageGuard -> unprotect -> resume) cannot be driven from managed code here.
        var map = Mmap(0, 1 << 20, 3, 0x1002, -1, 0); // PROT_READ|WRITE, MAP_PRIVATE|MAP_ANON
        Harness.Measure("6s", "raw mprotect(RO) + mprotect(RW), 1 page (syscall bound for a watch round trip)", 1, 1500, i =>
        {
            var a = map + ((i & 63) << 12);
            Mprotect(a, 4096, 1);
            Mprotect(a, 4096, 3);
        }, unit: "texture", note: "lower bound for 1 watch add+remove; SIGSEGV fault delivery NOT measured");
        GC.KeepAlive(sink);
    }

    private sealed class MemoryFlipTarget : IMetalFlipTarget
    {
        public bool HasFlipSlot() => true;

        public void PublishGuestFrame(nint texture, uint width, uint height, ulong requestId, ulong serial, bool isHdr)
        {
        }
    }
}
