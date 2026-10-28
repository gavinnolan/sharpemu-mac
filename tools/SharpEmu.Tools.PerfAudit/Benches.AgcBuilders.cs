// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.GpuRemote;
using SharpEmu.GpuSplit;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Host.MacOS;
using SharpEmu.Libs.Agc;

namespace SharpEmu.PerfAudit;

/// <summary>
/// The guest-side cost of an sceAgc command builder in the split's guest process: the builder body over
/// a real PhysicalVirtualMemory with a GuestGpuMemory whose image store is the split's page filter
/// (a real SplitSharedState), and each per-write hook on its own. Select with <c>agc</c>.
/// </summary>
internal static unsafe class AgcBuilderBenches
{
    private const ulong GuestBase = 0x2_0000_0000;
    private const ulong Dwords = 0x8000;

    // The guest process's image store minus the transport: the interest filter of RemoteImageStore.
    private sealed class FilterStore(SplitSharedState state) : IGuestImageStore
    {
        public long Landed;
        public bool MarkCpuWrite(ulong address, ulong size) => false;
        public bool MarkPreciseCpuWrite(ulong address, ulong size) => state.MayInterest(address, size) && false;
        public bool MarkCpuWriteLanded(ulong address, ulong size)
        {
            if (!state.MayInterest(address, size)) return false;
            Landed++;
            return true;
        }

        public void Unregister(ulong address, ulong size) { }
    }

    public static void Run()
    {
        Console.WriteLine("--- AGC builder cost in the split guest process ---");
        HostMemoryAuthority.Initialize(MacOSHost.CreateMemory);
        var views = MacOSHost.CreateViewMemory();
        using var pvm = new PhysicalVirtualMemory(viewHost: views, backingBytes: 32UL * 1024 * 1024);
        const ulong Region = 8UL << 20;
        var hole = (Region + views.Granularity - 1) / views.Granularity * views.Granularity;
        if (!pvm.TryHoldRangeAtOrAbove(GuestBase, hole, views.Granularity, out var address) ||
            !pvm.TryMapBacked(address, Region, 0, GuestPageProtection.Read | GuestPageProtection.Write, out _))
        {
            throw new InvalidOperationException("PVM map failed");
        }

        using var channel = GpuRemoteChannel.Create(SharedMemoryRegion.CreateUniqueName("agcbench"), SplitSharedState.Layout);
        var state = new SplitSharedState(channel);
        // A watched page far from the command buffer, as the images a title draws to are.
        state.SetPage(address + 0x40_0000);
        using var gpuMemory = new GuestGpuMemory(pvm);
        gpuMemory.Register(address, Region, GuestPageProtection.Read | GuestPageProtection.Write);
        var store = new FilterStore(state);
        gpuMemory.AttachStores(null, store);
        GuestGpuMemoryHook.Attach(gpuMemory);
        var memory = new TrackedCpuMemory(pvm);
        var ctx = new CpuContext(memory, Generation.Gen5);

        var cb = address + 0x1000;
        var packets = address + 0x10_0000;
        var cursorSlot = cb + 0x10;
        var resetState = new byte[0x38];
        void ResetCb()
        {
            BitConverter.TryWriteBytes(resetState.AsSpan(0x10), packets);
            BitConverter.TryWriteBytes(resetState.AsSpan(0x18), packets + Dwords * 4);
            memory.TryWrite(cb, resetState);
        }

        ResetCb();
        var eight = new byte[8];
        var forty = new byte[40];
        var statebuf = new byte[0x24];
        long sink = 0;

        // ---- the hooks on their own
        Harness.Measure("a1", "GuestImageWriteTracker.NotifyManagedWrite (no tracked images)", 1, 1, i =>
            GuestImageWriteTracker.NotifyManagedWrite(packets + (ulong)((i & 255) << 3), 8), unit: "call");
        Harness.Measure("a2", "GuestGpuMemoryHook.MarkCpuWrite (covered, no interest)", 1, 1, i =>
            GuestGpuMemoryHook.MarkCpuWrite(packets + (ulong)((i & 255) << 3), 8), unit: "call");
        Harness.Measure("a3", "GuestGpuMemoryHook.MarkCpuWriteLanded (covered, no interest)", 1, 1, i =>
            GuestGpuMemoryHook.MarkCpuWriteLanded(packets + (ulong)((i & 255) << 3), 8), unit: "call");
        Harness.Measure("a4", "GuestGpuMemory.Covers", 1, 1, i =>
            sink += gpuMemory.Covers(packets + (ulong)((i & 255) << 3), 8) ? 1 : 0, unit: "call");
        Harness.Measure("a5", "Interlocked.MemoryBarrier", 1, 1, i => Interlocked.MemoryBarrier(), unit: "call");
        Harness.Measure("a6", "SplitSharedState.MayInterest (1 page, clear)", 1, 1, i =>
            sink += state.MayInterest(packets + (ulong)((i & 255) << 3), 8) ? 1 : 0, unit: "call");
        Harness.Measure("a7", "PVM.TryWriteBacking 8 B (no hooks)", 1, 1, i =>
            pvm.TryWriteBacking(packets + (ulong)((i & 255) << 3), eight), unit: "call");

        // ---- the guest accesses a builder makes
        Harness.Measure("b1", "TrackedCpuMemory.TryRead 36 B (command-buffer state)", 1, 1, i =>
            memory.TryRead(cursorSlot, statebuf), unit: "call");
        Harness.Measure("b2", "ctx.TryWriteUInt64 (cursor store, with hooks)", 1, 1, i =>
            ctx.TryWriteUInt64(packets + (ulong)((i & 255) << 3), 1), unit: "call");
        Harness.Measure("b3", "TrackedCpuMemory.TryWrite 40 B (packet, with hooks)", 1, 1, i =>
            memory.TryWrite(packets + (ulong)((i & 255) << 3), forty), unit: "call");
        Harness.Measure("b4", "TrackedCpuMemory.TryWrite 8 B at a page with interest (hooks fire, filter hit)", 1, 1, i =>
            memory.TryWrite(address + 0x40_0000 + (ulong)((i & 255) << 3), eight), unit: "call");

        // ---- whole builders, leaf-style (callbacks declined) as the import dispatcher runs them
        ctx.DeclineGuestCallbacks = true;
        var calls = 0;
        void Bench(string id, string name, Action<CpuContext> body, int perReset)
        {
            Harness.Measure(id, name, 1, 1, i =>
            {
                if ((calls++ % perReset) == 0) ResetCb();
                body(ctx);
            }, unit: "call");
        }

        ulong Reg(uint offset, uint value) => offset | ((ulong)value << 32);
        Bench("c0", "CbNop(2)", c => { c[CpuRegister.Rdi] = cb; c[CpuRegister.Rsi] = 2; AgcExports.CbNop(c); }, 4000);
        Bench("c1", "DcbSetShRegisterDirect", c => { c[CpuRegister.Rdi] = cb; c[CpuRegister.Rsi] = Reg(0x8C, 0xDEADBEEF); AgcExports.SetShaderRegisterDirect(c); }, 4000);
        Bench("c2", "DcbSetCxRegisterDirect", c => { c[CpuRegister.Rdi] = cb; c[CpuRegister.Rsi] = Reg(0x10F, 0x3F800000); AgcExports.DcbSetCxRegisterDirect(c); }, 4000);
        Bench("c3", "DcbSetNumInstances", c => { c[CpuRegister.Rdi] = cb; c[CpuRegister.Rsi] = 1; AgcExports.DcbSetNumInstances(c); }, 4000);
        Bench("c4", "DcbDrawIndexAuto", c => { c[CpuRegister.Rdi] = cb; c[CpuRegister.Rsi] = 3; c[CpuRegister.Rdx] = 0; AgcExports.DcbDrawIndexAuto(c); }, 4000);
        Bench("c5", "DcbDrawIndex", c => { c[CpuRegister.Rdi] = cb; c[CpuRegister.Rsi] = 3; c[CpuRegister.Rdx] = 0x9_0000_0000; c[CpuRegister.Rcx] = 0; AgcExports.DcbDrawIndex(c); }, 2500);
        Bench("c6", "indexed draw sequence (10 builders)", c =>
        {
            c[CpuRegister.Rdi] = cb;
            c[CpuRegister.Rsi] = Reg(0x8C, 1); AgcExports.SetShaderRegisterDirect(c);
            c[CpuRegister.Rdi] = cb; c[CpuRegister.Rsi] = Reg(0x8D, 2); AgcExports.SetShaderRegisterDirect(c);
            c[CpuRegister.Rdi] = cb; c[CpuRegister.Rsi] = Reg(0x0C, 3); AgcExports.SetShaderRegisterDirect(c);
            c[CpuRegister.Rdi] = cb; c[CpuRegister.Rsi] = Reg(0x10F, 0x3F800000); AgcExports.DcbSetCxRegisterDirect(c);
            c[CpuRegister.Rdi] = cb; c[CpuRegister.Rsi] = Reg(0x08E, 0xF); AgcExports.DcbSetCxRegisterDirect(c);
            c[CpuRegister.Rdi] = cb; c[CpuRegister.Rsi] = Reg(0x205, 0); AgcExports.DcbSetCxRegisterDirect(c);
            c[CpuRegister.Rdi] = cb; c[CpuRegister.Rsi] = Reg(0x242, 4); AgcExports.DcbSetUcRegisterDirect(c);
            c[CpuRegister.Rdi] = cb; c[CpuRegister.Rsi] = 1; c[CpuRegister.Rdx] = 0; AgcExports.DcbSetIndexSize(c);
            c[CpuRegister.Rdi] = cb; c[CpuRegister.Rsi] = 1; AgcExports.DcbSetNumInstances(c);
            c[CpuRegister.Rdi] = cb; c[CpuRegister.Rsi] = 3; c[CpuRegister.Rdx] = 0x9_0000_0000; c[CpuRegister.Rcx] = 0; AgcExports.DcbDrawIndex(c);
        }, 400);
        if (sink == long.MinValue) Console.WriteLine(sink);
        GuestGpuMemoryHook.Attach(null);
    }
}
