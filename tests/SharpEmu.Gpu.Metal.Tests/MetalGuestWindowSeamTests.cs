// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// A binding that crosses the edge of a tiled window used to be staged into the ring on
/// the CPU at record time: it missed GPU writes still in flight to those bytes, and a
/// shader's stores into it never reached guest memory. With the windows overlapping by
/// the seam the same binding resolves to one window and sees, and writes, the guest bytes
/// in stream order. A small backing with 1 MiB windows and a 64 KiB seam stands in for
/// the 4 GiB windows; device work is behind the exclusive device gate.
/// </summary>
public sealed unsafe class MetalGuestWindowSeamTests(ITestOutputHelper output)
{
    internal const ulong GuestBase = 0x2_0000_0000;
    internal const ulong BackingSize = 4 * 1024 * 1024;
    internal const ulong WindowBytes = 1024 * 1024;
    internal const ulong SeamBytes = 64 * 1024;

    // Half overlap would ask for 7 MiB; the seam layout asks for 4.25 MiB.
    internal const ulong Budget = BackingSize + (BackingSize / 8);

    // 32 KiB on either side of the first tiled window edge.
    private const ulong StraddleAddress = GuestBase + WindowBytes - (32 * 1024);
    private const ulong StraddleSize = 64 * 1024;

    private static readonly nint SelBlit = MetalNative.Selector("blitCommandEncoder");
    private static readonly nint SelEnd = MetalNative.Selector("endEncoding");
    private static readonly nint SelNewFence = MetalNative.Selector("newFence");
    private static readonly nint SelUpdateFence = MetalNative.Selector("updateFence:");
    private static readonly nint SelWaitForFence = MetalNative.Selector("waitForFence:");
    private static readonly nint SelRelease = MetalNative.Selector("release");
    private static readonly nint SelFill = MetalNative.Selector("fillBuffer:range:value:");
    private static readonly nint SelCopy =
        MetalNative.Selector("copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:");

    internal sealed class Backing : IGuestBackedSpace, IDisposable
    {
        private readonly void* _block = NativeMemory.AlignedAlloc((nuint)BackingSize, 16384);

        public Backing() => NativeMemory.Clear(_block, (nuint)BackingSize);

        public void Dispose() => NativeMemory.AlignedFree(_block);

        private static bool Contains(ulong address, ulong length) =>
            address >= GuestBase && address - GuestBase + length <= BackingSize;

        public bool TryHoldRange(ulong address, ulong size) => false;
        public bool TryHoldRangeAtOrAbove(ulong searchStart, ulong size, ulong alignment, out ulong address) { address = 0; return false; }
        public bool TryMapBacked(ulong address, ulong size, ulong backingOffset, GuestPageProtection protection, out HostViewFailure failure) { failure = HostViewFailure.None; return false; }
        public bool TryUnmapBacked(ulong address, ulong size) => false;
        public bool TryClearBacking(ulong offset, ulong size) => false;
        public bool IsBackedView(ulong address) => Contains(address, 1);
        public bool IsBackedRange(ulong address, ulong size) => Contains(address, size);
        public (ulong Base, ulong Size) BackingAliasRange => ((ulong)_block, BackingSize);

        public bool TryResolveBackingAlias(ulong address, ulong size, out ulong alias)
        {
            alias = Contains(address, size) ? (ulong)_block + (address - GuestBase) : 0;
            return alias != 0;
        }

        public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data)
        {
            if (!Contains(address, (ulong)data.Length)) return false;
            data.CopyTo(new Span<byte>((byte*)_block + (address - GuestBase), data.Length));
            return true;
        }

        public bool TryReadBacking(ulong address, Span<byte> data)
        {
            if (!Contains(address, (ulong)data.Length)) return false;
            new ReadOnlySpan<byte>((byte*)_block + (address - GuestBase), data.Length).CopyTo(data);
            return true;
        }
    }

    // The CPU view of the same bytes, which is what the ring staged from.
    internal sealed class BackingMemory(Backing backing) : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => backing.TryReadBacking(virtualAddress, destination);
        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => backing.TryWriteBacking(virtualAddress, source);
        public bool CanRead(ulong address, ulong size) => backing.IsBackedRange(address, size);
    }

    // A read binding across the edge, recorded after a GPU write to those bytes that has
    // not run yet, reads what the GPU wrote rather than what the CPU saw at record time.
    [Fact]
    public void AStraddlingReadBindingSeesTheGpuWriteBeforeIt()
    {
        if (!RequireDevice())
        {
            return;
        }

        using var backing = new Backing();
        using var harness = new MetalTransferHarness(Device.Value);
        using var memory = new MetalGuestMemory(
            Device.Value, backing, new BackingMemory(backing), WindowBytes, SeamBytes, Budget);
        backing.TryWriteBacking(StraddleAddress, Enumerable.Repeat((byte)0x11, (int)StraddleSize).ToArray());

        harness.Begin();
        // The GPU write reaches the bytes through the two halves, each its own binding.
        var half = StraddleSize / 2;
        var low = memory.Resolve(StraddleAddress, half);
        var high = memory.Resolve(StraddleAddress + half, half);
        var write = MetalNative.Send(harness.CommandBuffer, SelBlit);
        Fill(write, low, 0xAB);
        Fill(write, high, 0xAB);
        // The windows are untracked, so the read is ordered after the write the way the
        // backend orders guest work: a fence between the two encoders.
        var fence = MetalNative.Send(Device.Value, SelNewFence);
        MetalNative.SendVoid(write, SelUpdateFence, fence);
        MetalNative.SendVoid(write, SelEnd);

        // Recorded before that write has run: a CPU staging copy would capture 0x11.
        var straddle = memory.Resolve(StraddleAddress, StraddleSize);
        var readback = harness.Buffer(StraddleSize);
        var read = MetalNative.Send(harness.CommandBuffer, SelBlit);
        MetalNative.SendVoid(read, SelWaitForFence, fence);
        MetalNative.SendCopyBuffer(read, SelCopy, straddle.Buffer, (nuint)straddle.Offset, readback, 0, (nuint)StraddleSize);
        MetalNative.SendVoid(read, SelEnd);
        harness.Submit();
        MetalNative.SendVoid(fence, SelRelease);

        Assert.All(harness.Read(readback, 0, StraddleSize), value => Assert.Equal((byte)0xAB, value));
        Assert.Equal(0UL, memory.RingFallbacks);
    }

    // A written binding across the edge stores into guest memory on both sides of it.
    [Fact]
    public void AStraddlingWrittenBindingStoresIntoGuestMemory()
    {
        if (!RequireDevice())
        {
            return;
        }

        using var backing = new Backing();
        using var harness = new MetalTransferHarness(Device.Value);
        using var memory = new MetalGuestMemory(
            Device.Value, backing, new BackingMemory(backing), WindowBytes, SeamBytes, Budget);

        harness.Begin();
        var straddle = memory.Resolve(StraddleAddress, StraddleSize);
        var write = MetalNative.Send(harness.CommandBuffer, SelBlit);
        Fill(write, straddle, 0xCD);
        MetalNative.SendVoid(write, SelEnd);
        harness.Submit();

        var bytes = new byte[StraddleSize];
        Assert.True(backing.TryReadBacking(StraddleAddress, bytes));
        Assert.All(bytes, value => Assert.Equal((byte)0xCD, value));
        Assert.Equal(0UL, memory.RingFallbacks);
    }

    // A range too large to stage whose start one window covers binds that prefix in
    // place: the window's bytes are what the GPU writes and reads, and nothing goes
    // through the ring. The upload cap is the seam here so a range inside the backing
    // can exceed it.
    [Fact]
    public void AnOversizedRangeBindsTheWindowPrefixInPlace()
    {
        if (!RequireDevice())
        {
            return;
        }

        using var backing = new Backing();
        using var harness = new MetalTransferHarness(Device.Value);
        using var memory = new MetalGuestMemory(
            Device.Value, backing, new BackingMemory(backing), WindowBytes, SeamBytes, Budget, maxUploadBytes: SeamBytes);

        // Starts half-way into the first window and runs past its end into bytes only
        // the second window holds.
        var address = GuestBase + (WindowBytes / 2);
        var size = WindowBytes - (WindowBytes / 4);
        var covered = WindowBytes / 2;

        harness.Begin();
        var prefix = memory.Resolve(address, size);
        Assert.True(prefix.IsValid);
        Assert.Equal(covered, prefix.Size);
        Assert.Equal(1UL, memory.OversizedInPlace);
        Assert.Equal(0UL, memory.RingFallbacks);
        var write = MetalNative.Send(harness.CommandBuffer, SelBlit);
        Fill(write, prefix, 0xCD);
        MetalNative.SendVoid(write, SelEnd);
        harness.Submit();

        var bytes = new byte[covered];
        Assert.True(backing.TryReadBacking(address, bytes));
        Assert.All(bytes, value => Assert.Equal((byte)0xCD, value));
        var beyond = new byte[16];
        Assert.True(backing.TryReadBacking(address + covered, beyond));
        Assert.All(beyond, value => Assert.Equal((byte)0, value));
    }

    // A storage binding that straddles a window edge and is too large to stage binds the
    // prefix in place: the host treats it as guest memory, not as a ring copy to gather
    // into and write back, and the shader's bound is the prefix, so no access reaches
    // past the window into memory the binding does not mean.
    [Fact]
    public void AnOversizedStorageBindingIsGuestMemoryBoundedByItsPrefix()
    {
        if (!RequireDevice())
        {
            return;
        }

        using var backing = new Backing();
        using var memory = new MetalGuestMemory(
            Device.Value, backing, new BackingMemory(backing), WindowBytes, SeamBytes, Budget, maxUploadBytes: SeamBytes);

        // Straddles the edge between the first two windows by a quarter window each way.
        var address = GuestBase + WindowBytes - (WindowBytes / 4);
        var size = WindowBytes / 2;
        memory.BeginSubmission(1, 0);
        var staged = memory.Stage(address, size);

        Assert.True(staged.IsValid);
        Assert.True(memory.IsInPlace(staged));
        Assert.True(staged.Size < size);
        Assert.Equal((uint)staged.Size, MetalRenderHost.BoundBufferBytes(size, staged));
        Assert.Equal(0UL, memory.RingFallbacks);

        var ring = memory.Allocate(256, 256, out _);
        Assert.False(memory.IsInPlace(ring));
        Assert.Equal(256u, MetalRenderHost.BoundBufferBytes(size, ring));
        Assert.False(memory.IsInPlace(default));
    }

    private static void Fill(nint encoder, in MetalGuestRange range, byte value)
    {
        Assert.True(range.IsValid);
        MetalNative.SendFillBuffer(encoder, SelFill, range.Buffer,
            new NsRange { Location = (nuint)range.Offset, Length = (nuint)range.Size }, value);
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Guest window seams run on the device only behind the exclusive device gate.");
            return false;
        }

        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        return true;
    }

    private static readonly Lazy<nint> Device = new(MetalNative.MTLCreateSystemDefaultDevice);
}
