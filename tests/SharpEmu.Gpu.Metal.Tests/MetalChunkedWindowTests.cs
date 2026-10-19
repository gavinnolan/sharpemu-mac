// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// Windows over the chunked guest backing, where every 2 MiB purgeable object (32 chunks of
/// 64 KiB) is its own VM map entry. Under Rosetta a no-copy buffer costs about the square of
/// the objects it spans, so windows there stop at
/// <see cref="MetalGuestMemory.MaxChunkedWindowEntries"/> objects (1 GiB); a window wider
/// than the 32 MiB that 64 KiB objects allowed must still be quick to create and read the
/// chunks' bytes, and a chunk emptied out of its object reads on the GPU what it reads on the
/// CPU. The backing here is built the way MacHostViews builds it.
/// </summary>
public sealed unsafe class MetalChunkedWindowTests(ITestOutputHelper output)
{
    private const ulong Chunk = 64 * 1024;
    private const ulong Mib = 1024 * 1024;
    private const ulong ObjectBytes = 2 * Mib;
    private const ulong GuestBase = 0x2_0000_0000;
    private const ulong BackingSize = 96 * Mib;
    private const ulong WindowBytes = 64 * Mib;
    private const ulong GranuleBytes = Mib;
    private const ulong RequestedMaxWindowBytes = 1024 * Mib;

    private static readonly nint SelBlit = MetalNative.Selector("blitCommandEncoder");
    private static readonly nint SelEnd = MetalNative.Selector("endEncoding");
    private static readonly nint SelCopy =
        MetalNative.Selector("copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:");

    private sealed class ChunkedBacking : IGuestBackedSpace, IDisposable
    {
        private readonly ulong _alias;

        public ChunkedBacking(bool chunked)
        {
            var task = mach_task_self();
            ulong alias = 0;
            Assert.Equal(0, mach_vm_map(task, ref alias, BackingSize, Chunk - 1, VmFlagsAnywhere, 0, 0, 0, 0, 3, 0));
            for (ulong offset = 0; chunked && offset < BackingSize; offset += ObjectBytes)
            {
                var entrySize = ObjectBytes;
                Assert.Equal(0, mach_make_memory_entry_64(task, ref entrySize, 0, MapMemNamedCreate | MapMemPurgable | 3, out var entry, 0));
                var target = alias + offset;
                Assert.Equal(0, mach_vm_map(task, ref target, ObjectBytes, 0, VmFlagsFixed | VmFlagsOverwrite, entry, 0, 0, 3, 3, 0));
                Assert.Equal(0, mach_port_deallocate(task, entry));
            }

            if (!chunked)
            {
                // One anonymous entry over the whole range.
                var target = alias;
                Assert.Equal(0, mach_vm_map(task, ref target, BackingSize, 0, VmFlagsFixed | VmFlagsOverwrite, 0, 0, 0, 3, 3, 0));
            }

            _alias = alias;
            BackingChunkBytes = chunked ? Chunk : 0;
            BackingObjectBytes = chunked ? ObjectBytes : 0;
        }

        public ulong BackingChunkBytes { get; }

        public ulong BackingObjectBytes { get; }

        public void Dispose() => mach_vm_deallocate(mach_task_self(), _alias, BackingSize);

        // One chunk out of its object, as MacHostViews.DiscardChunks does it: the rest of the
        // object wired, EMPTY then NONVOLATILE, unwired.
        public void Empty(ulong offset)
        {
            var objectStart = _alias + offset / ObjectBytes * ObjectBytes;
            var chunk = _alias + offset;
            var below = (nuint)(chunk - objectStart);
            var above = (nuint)(objectStart + ObjectBytes - chunk - Chunk);
            Assert.Equal(0, below == 0 ? 0 : mlock((nint)objectStart, below));
            Assert.Equal(0, above == 0 ? 0 : mlock((nint)(chunk + Chunk), above));
            var state = 2;
            Assert.Equal(0, mach_vm_purgable_control(mach_task_self(), objectStart, 0, ref state));
            state = 0;
            Assert.Equal(0, mach_vm_purgable_control(mach_task_self(), objectStart, 0, ref state));
            Assert.Equal(0, below == 0 ? 0 : munlock((nint)objectStart, below));
            Assert.Equal(0, above == 0 ? 0 : munlock((nint)(chunk + Chunk), above));
        }

        private static bool Contains(ulong address, ulong length) =>
            address >= GuestBase && address - GuestBase + length <= BackingSize;

        public byte* Alias(ulong address) => (byte*)(_alias + (address - GuestBase));

        public bool TryHoldRange(ulong address, ulong size) => false;
        public bool TryHoldRangeAtOrAbove(ulong searchStart, ulong size, ulong alignment, out ulong address) { address = 0; return false; }
        public bool TryMapBacked(ulong address, ulong size, ulong backingOffset, GuestPageProtection protection, out HostViewFailure failure) { failure = HostViewFailure.None; return false; }
        public bool TryUnmapBacked(ulong address, ulong size) => false;
        public bool TryClearBacking(ulong offset, ulong size) => false;
        public bool IsBackedView(ulong address) => Contains(address, 1);
        public bool IsBackedRange(ulong address, ulong size) => Contains(address, size);
        public (ulong Base, ulong Size) BackingAliasRange => (_alias, BackingSize);

        public bool TryResolveBackingAlias(ulong address, ulong size, out ulong alias)
        {
            alias = Contains(address, size) ? (ulong)Alias(address) : 0;
            return alias != 0;
        }

        public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data)
        {
            if (!Contains(address, (ulong)data.Length)) return false;
            data.CopyTo(new Span<byte>(Alias(address), data.Length));
            return true;
        }

        public bool TryReadBacking(ulong address, Span<byte> data)
        {
            if (!Contains(address, (ulong)data.Length)) return false;
            new ReadOnlySpan<byte>(Alias(address), data.Length).CopyTo(data);
            return true;
        }
    }

    private sealed class BackingMemory(ChunkedBacking backing) : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => backing.TryReadBacking(virtualAddress, destination);
        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => backing.TryWriteBacking(virtualAddress, source);
        public bool CanRead(ulong address, ulong size) => backing.IsBackedRange(address, size);
    }

    private static MetalGuestMemory Memory(ChunkedBacking backing) =>
        new(Device.Value, backing, new BackingMemory(backing), GranuleBytes, RequestedMaxWindowBytes);

    [Fact]
    public void WindowsOverChunksStopAtTheEntryCap()
    {
        if (!RequireDevice())
        {
            return;
        }

        using (var chunked = new ChunkedBacking(chunked: true))
        using (var memory = Memory(chunked))
        {
            Assert.Equal(Math.Min(RequestedMaxWindowBytes, MetalGuestMemory.MaxChunkedWindowEntries * ObjectBytes), memory.MaxWindowBytes);
            Assert.True(memory.MaxWindowBytes > MetalGuestMemory.MaxChunkedWindowEntries * Chunk);
        }

        // A single-object backing keeps the window it asked for (within the device's limit).
        using var plain = new ChunkedBacking(chunked: false);
        using var whole = Memory(plain);
        Assert.True(whole.MaxWindowBytes > MetalGuestMemory.MaxChunkedWindowEntries * Chunk);
    }

    // A window twice the 32 MiB that 64 KiB objects allowed (32 objects, 1024 chunks): it is
    // created quickly, the GPU reads every chunk's bytes through it, and after a chunk is
    // emptied out of its object the GPU reads what the CPU reads there.
    [Fact]
    public void AWindowAcrossManyChunksIsQuickAndReadsEveryChunk()
    {
        if (!RequireDevice())
        {
            return;
        }

        using var backing = new ChunkedBacking(chunked: true);
        using var harness = new MetalTransferHarness(Device.Value);
        using var memory = Memory(backing);
        Assert.True(memory.MaxWindowBytes >= WindowBytes);
        var size = WindowBytes;
        var address = GuestBase + 16 * Mib;
        for (ulong offset = 0; offset < size; offset += Chunk)
        {
            new Span<byte>(backing.Alias(address + offset), (int)Chunk).Fill((byte)(offset / Chunk * 7 + 1));
        }

        harness.Begin();
        memory.BeginSubmission(1, 0);
        var timer = Stopwatch.StartNew();
        var range = memory.Resolve(address, size);
        timer.Stop();
        output.WriteLine($"window of {size / ObjectBytes} objects ({size / Chunk} chunks) created in {timer.Elapsed.TotalMilliseconds:F2} ms");
        Assert.True(memory.IsInPlace(range));
        Assert.True(timer.ElapsedMilliseconds < 250, $"window creation took {timer.Elapsed.TotalMilliseconds:F1} ms");

        var readback = harness.Buffer(size);
        Copy(harness, range, readback, size);
        harness.Submit();
        Assert.True(harness.Read(readback, 0, size).AsSpan().SequenceEqual(new ReadOnlySpan<byte>(backing.Alias(address), (int)size)));

        // Chunk 5 of the window emptied: the CPU and the GPU agree on what it holds now.
        var emptied = address + 5 * Chunk;
        backing.Empty(emptied - GuestBase);
        harness.Begin();
        memory.BeginSubmission(2, 1);
        var again = memory.Resolve(emptied, Chunk);
        Assert.True(memory.IsInPlace(again));
        var chunkReadback = harness.Buffer(Chunk);
        Copy(harness, again, chunkReadback, Chunk);
        harness.Submit();
        Assert.True(harness.Read(chunkReadback, 0, Chunk).AsSpan().SequenceEqual(new ReadOnlySpan<byte>(backing.Alias(emptied), (int)Chunk)));
        Assert.Equal(0UL, memory.RingFallbacks);
    }

    private static void Copy(MetalTransferHarness harness, in MetalGuestRange range, nint destination, ulong size)
    {
        var blit = MetalNative.Send(harness.CommandBuffer, SelBlit);
        MetalNative.SendCopyBuffer(blit, SelCopy, range.Buffer, (nuint)range.Offset, destination, 0, (nuint)size);
        MetalNative.SendVoid(blit, SelEnd);
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Chunked-backing windows run on the device only behind the exclusive device gate.");
            return false;
        }

        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        return true;
    }

    private static readonly Lazy<nint> Device = new(MetalNative.MTLCreateSystemDefaultDevice);

    private const int VmFlagsFixed = 0x0000;
    private const int VmFlagsAnywhere = 0x0001;
    private const int VmFlagsOverwrite = 0x4000;
    private const int MapMemNamedCreate = 0x020000;
    private const int MapMemPurgable = 0x040000;

    [DllImport("libSystem.B.dylib")]
    private static extern uint mach_task_self();

    [DllImport("libSystem.B.dylib")]
    private static extern int mach_vm_map(uint task, ref ulong address, ulong size, ulong mask, int flags,
        uint memoryObject, ulong offset, int copy, int currentProtection, int maximumProtection, uint inheritance);

    [DllImport("libSystem.B.dylib")]
    private static extern int mach_make_memory_entry_64(uint task, ref ulong size, ulong offset, int permission,
        out uint objectHandle, uint parentEntry);

    [DllImport("libSystem.B.dylib")]
    private static extern int mach_vm_purgable_control(uint task, ulong address, int control, ref int state);

    [DllImport("libSystem.B.dylib")]
    private static extern int mach_port_deallocate(uint task, uint name);

    [DllImport("libSystem.B.dylib")]
    private static extern int mach_vm_deallocate(uint task, ulong address, ulong size);

    [DllImport("libc")]
    private static extern int mlock(nint address, nuint length);

    [DllImport("libc")]
    private static extern int munlock(nint address, nuint length);
}
