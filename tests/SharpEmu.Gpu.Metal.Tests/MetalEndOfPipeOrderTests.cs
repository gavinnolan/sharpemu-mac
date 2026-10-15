// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// End-of-pipe writes of a stream land in the order they were recorded. A 32-bit label no
/// window covers is published by its submission's completion; a 64-bit label recorded
/// after it that the GPU could write itself used to land as soon as the GPU reached it,
/// before that completion ran, and a guest reusing memory on the newer signal then had
/// the older label land in what it had reused. The completion thread is held here so the
/// gap between the GPU finishing and the host publishing is as wide as it is in a slow
/// frame. Device work is behind the exclusive device gate.
/// </summary>
public sealed unsafe class MetalEndOfPipeOrderTests(ITestOutputHelper output)
{
    private const ulong GuestBase = 0x2_0000_0000;
    private const ulong WindowedLabel = GuestBase + 0x1000;
    private const ulong LaterLabel = GuestBase + 0x2000;

    // No window covers this page, so a label here can only be published by the host.
    private const ulong HoleOffset = 0x8000;
    private const ulong HoleSize = 0x1000;
    private const ulong UnwindowedLabel = GuestBase + HoleOffset + 0x40;

    [Theory]
    [InlineData(false, 0L, true)]
    [InlineData(false, 3L, true)]
    [InlineData(true, 0L, true)]
    [InlineData(true, 1L, false)]
    public void ALabelIsWrittenByTheGpuOnlyWhileNoOlderLabelIsOwed(bool ordered, long owed, bool expected) =>
        Assert.Equal(expected, MetalRenderHost.MayEncodeLabelOnGpu(ordered, owed));

    [Fact]
    public void ALaterGpuWrittenLabelWaitsBehindAnOwed32BitLabel()
    {
        if (!RequireDevice()) return;
        var (owedLanded, laterLanded, ordered) = RecordOwedThenLater(orderedEndOfPipe: true);

        // While the GPU has finished both and the completion is held, neither is visible:
        // the later label is not allowed to say more than the owed one.
        Assert.Equal(0UL, owedLanded.WhileHeld);
        Assert.Equal(0UL, laterLanded.WhileHeld);
        Assert.Equal(1L, ordered);

        Assert.Equal(1UL, owedLanded.AfterDrain);
        Assert.Equal(2UL, laterLanded.AfterDrain);
    }

    [Fact]
    public void TheOptOutLetsTheLaterLabelOvertakeTheOwedOne()
    {
        if (!RequireDevice()) return;
        var (owedLanded, laterLanded, ordered) = RecordOwedThenLater(orderedEndOfPipe: false);

        // SHARPEMU_METAL_V2_EOP_ORDERED=0: the GPU writes the 64-bit label at once, while
        // the 32-bit one recorded before it still waits for the held completion.
        Assert.Equal(0UL, owedLanded.WhileHeld);
        Assert.Equal(2UL, laterLanded.WhileHeld);
        Assert.Equal(0L, ordered);

        Assert.Equal(1UL, owedLanded.AfterDrain);
        Assert.Equal(2UL, laterLanded.AfterDrain);
    }

    [Fact]
    public void ACommandProcessorWriteWaitsBehindAnOwed32BitLabel()
    {
        if (!RequireDevice()) return;
        var (owedLanded, laterLanded, _) = RecordOwedThenLater(orderedEndOfPipe: true,
            later: host => host.RecordCommandProcessorWrite(LaterLabel, BitConverter.GetBytes(2UL), "WRITE_DATA"));

        Assert.Equal(0UL, owedLanded.WhileHeld);
        Assert.Equal(0UL, laterLanded.WhileHeld);
        Assert.Equal(1UL, owedLanded.AfterDrain);
        Assert.Equal(2UL, laterLanded.AfterDrain);
    }

    /// <summary>
    /// A WRITE_DATA or DMA fill recorded after a label is copied by the GPU behind it, not
    /// written as parsed: until the GPU reaches it the guest sees neither, so it can never
    /// see the store ahead of the label.
    /// </summary>
    [Fact]
    public void CommandProcessorWritesLandWithTheLabelBeforeThemAndNotAtParse()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var host = rig.Host;
        Assert.True(host.DefersCommandProcessorWrites);
        const ulong filled = GuestBase + 0x3000;

        host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write32, 1, WindowedLabel, 1));
        host.RecordCommandProcessorWrite(LaterLabel, BitConverter.GetBytes(0x55UL), "WRITE_DATA");
        host.FillBufferInOrder(filled, sizeof(uint), 0x77);

        Assert.Equal(0u, rig.Backing.Read32(WindowedLabel));
        Assert.Equal(0UL, rig.Backing.Read64(LaterLabel));
        Assert.Equal(0u, rig.Backing.Read32(filled));
        Assert.Equal((2L, 0L), host.TakeCommandProcessorWriteCounters());

        host.FlushCommandBuffer();
        WaitForGpu(host, host.LastWorkSerial);
        WaitForCompletions(host);
        Assert.Equal(1u, rig.Backing.Read32(WindowedLabel));
        Assert.Equal(0x55UL, rig.Backing.Read64(LaterLabel));
        Assert.Equal(0x77u, rig.Backing.Read32(filled));
    }

    [Fact]
    public void TheCommandProcessorWriteOptOutLeavesThemToTheParser()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        Assert.True(rig.Host.DefersCommandProcessorWrites);
        rig.Host.CommandProcessorWritesAtParse = true;
        Assert.False(rig.Host.DefersCommandProcessorWrites);
    }

    private ((ulong WhileHeld, ulong AfterDrain) Owed, (ulong WhileHeld, ulong AfterDrain) Later, long Ordered)
        RecordOwedThenLater(bool orderedEndOfPipe, Action<MetalRenderHost>? later = null)
    {
        using var hold = new ManualResetEventSlim(false);
        using var rig = new Rig(Device.Value);
        var host = rig.Host;
        host.OrderedEndOfPipe = orderedEndOfPipe;
        try
        {
            // GPU work in the submission, so the owed label's completion has to wait for it.
            host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write32, 1, WindowedLabel, 0xA0));
            var held = host.SubmissionSerial;
            host.Completions.Enqueue(held, () => hold.Wait(TimeSpan.FromSeconds(10)));

            host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write32, 1, UnwindowedLabel, 1));
            if (later is null)
            {
                host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write64, 1, LaterLabel, 2));
            }
            else
            {
                later(host);
            }

            host.FlushCommandBuffer();
            WaitForGpu(host, host.LastWorkSerial);

            var owedWhileHeld = rig.Backing.Read32(UnwindowedLabel);
            var laterWhileHeld = rig.Backing.Read64(LaterLabel);
            var ordered = host.TakeOrderedHostLabels();

            hold.Set();
            WaitForCompletions(host);
            return ((owedWhileHeld, rig.Backing.Read32(UnwindowedLabel)),
                (laterWhileHeld, rig.Backing.Read64(LaterLabel)), ordered);
        }
        finally
        {
            hold.Set();
        }
    }

    private static void WaitForGpu(MetalRenderHost host, ulong serial)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (host.CompletedSerial < serial)
        {
            Assert.True(Environment.TickCount64 < deadline, $"The GPU did not reach serial {serial}.");
            Thread.Sleep(1);
        }
    }

    private static void WaitForCompletions(MetalRenderHost host)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (host.Completions.PendingCount != 0)
        {
            Assert.True(Environment.TickCount64 < deadline, "The held completions did not drain.");
            Thread.Sleep(1);
        }
    }

    private sealed class AddressSpace : IGuestAddressSpace
    {
        public ulong ProtectionPageSize => 4096;
        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) => true;
        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true) => throw new NotSupportedException();
        public bool TryBackFixedRange(ulong address, ulong size, bool executable) => throw new NotSupportedException();
        public bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress) => throw new NotSupportedException();
        public bool TryEnsureRangeCommitted(ulong address, ulong size) => throw new NotSupportedException();
        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address) => throw new NotSupportedException();
        public bool TryFreeGuestMemory(ulong address) => throw new NotSupportedException();
    }

    // One page-aligned native block seen as guest memory at GuestBase, with one page the
    // GPU windows do not reach: the CPU can write it, the GPU has no address for it.
    private sealed class Backing : IGuestBackedSpace, ICpuMemory, IDisposable
    {
        public const ulong Size = 1024 * 1024;
        private readonly void* _block = NativeMemory.AlignedAlloc((nuint)Size, 16384);

        public Backing() => NativeMemory.Clear(_block, (nuint)Size);

        public void Dispose() => NativeMemory.AlignedFree(_block);

        private static bool Contains(ulong address, ulong length) =>
            address >= GuestBase && address - GuestBase + length <= Size;

        private static bool TouchesHole(ulong address, ulong length) =>
            address - GuestBase < HoleOffset + HoleSize && address - GuestBase + length > HoleOffset;

        public uint Read32(ulong address)
        {
            Span<byte> bytes = stackalloc byte[4];
            Assert.True(TryRead(address, bytes));
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        public ulong Read64(ulong address)
        {
            Span<byte> bytes = stackalloc byte[8];
            Assert.True(TryRead(address, bytes));
            return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }

        public bool TryHoldRange(ulong address, ulong size) => false;
        public bool TryHoldRangeAtOrAbove(ulong searchStart, ulong size, ulong alignment, out ulong address) { address = 0; return false; }
        public bool TryMapBacked(ulong address, ulong size, ulong backingOffset, GuestPageProtection protection, out HostViewFailure failure) { failure = HostViewFailure.None; return false; }
        public bool TryUnmapBacked(ulong address, ulong size) => false;
        public bool TryClearBacking(ulong offset, ulong size) => false;
        public bool IsBackedView(ulong address) => Contains(address, 1);
        public bool IsBackedRange(ulong address, ulong size) => Contains(address, size);
        public (ulong Base, ulong Size) BackingAliasRange => ((ulong)_block, Size);

        public bool TryResolveBackingAlias(ulong address, ulong size, out ulong alias)
        {
            alias = Contains(address, size) && !TouchesHole(address, size) ? (ulong)_block + (address - GuestBase) : 0;
            return alias != 0;
        }

        public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data) => TryWrite(address, data);
        public bool TryReadBacking(ulong address, Span<byte> data) => TryRead(address, data);

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!Contains(virtualAddress, (ulong)destination.Length)) return false;
            new ReadOnlySpan<byte>((byte*)_block + (virtualAddress - GuestBase), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!Contains(virtualAddress, (ulong)source.Length)) return false;
            source.CopyTo(new Span<byte>((byte*)_block + (virtualAddress - GuestBase), source.Length));
            return true;
        }

        public bool CanRead(ulong address, ulong size) => Contains(address, size);
    }

    private sealed class Rig : IDisposable
    {
        private readonly nint _queue;
        private readonly AddressSpace _space = new();
        private readonly PageGuard _pages;

        public Rig(nint device)
        {
            _queue = MetalNative.Send(device, MetalNative.Selector("newCommandQueue"));
            _pages = new PageGuard(_space);
            Host = new MetalRenderHost(device, _queue, Backing, Backing, _pages, new MetalGpuWorkerRelay(static () => { }));
        }

        public Backing Backing { get; } = new();

        public MetalRenderHost Host { get; }

        public void Dispose()
        {
            Host.ImageCache.StopWatching();
            Host.Dispose();
            _pages.Dispose();
            Backing.Dispose();
            MetalNative.SendVoid(_queue, MetalNative.Selector("release"));
        }
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("End-of-pipe ordering runs on the device only behind the exclusive device gate.");
            return false;
        }

        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        return true;
    }

    private static readonly Lazy<nint> Device = new(MetalNative.MTLCreateSystemDefaultDevice);
}
