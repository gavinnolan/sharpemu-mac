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
