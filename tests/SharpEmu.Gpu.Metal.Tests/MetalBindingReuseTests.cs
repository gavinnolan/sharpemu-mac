// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// The per-draw binding memos: argument-buffer ranges found again by content, only within the
/// submission that wrote them, and the guest-window resolve memo, which has to follow the
/// submission and the window layout.
/// </summary>
public sealed unsafe class MetalBindingReuseTests(ITestOutputHelper output)
{
    private const ulong Mib = 1024 * 1024;
    private const ulong GuestBase = 0x3_0000_0000;
    private const ulong BackingSize = 64 * Mib;
    private const ulong Granule = Mib;

    private static readonly Lazy<nint> Device = new(MetalNative.MTLCreateSystemDefaultDevice);

    private static byte[] Bytes(byte fill, int length = 64) => Enumerable.Repeat(fill, length).ToArray();

    private static MetalGuestRange Range(nint buffer, ulong offset) => new(buffer, offset, 64, 0x1000 + offset);

    [Fact]
    public void ARangeIsFoundOnlyBySameBytesInTheSameSubmission()
    {
        var memo = new MetalRegionMemo(4);
        var range = Range(1, 0);
        memo.Add(Bytes(1), serial: 7, in range);

        Assert.True(memo.TryFind(Bytes(1), 7, out var found));
        Assert.Equal(range, found);
        // A changed binding is a different range, and a shorter or longer one is too.
        Assert.False(memo.TryFind(Bytes(2), 7, out _));
        Assert.False(memo.TryFind(Bytes(1, 32), 7, out _));
        Assert.False(memo.TryFind(Bytes(1, 128), 7, out _));
        // The next submission never gets the range back: the ring may be recycled under it.
        Assert.False(memo.TryFind(Bytes(1), 8, out _));
        Assert.False(memo.TryFind(Bytes(1), 6, out _));
        Assert.Equal(1, memo.Hits);
    }

    [Fact]
    public void TheOldestRangeIsReplacedWhenTheSlotsAreFull()
    {
        var memo = new MetalRegionMemo(2);
        memo.Add(Bytes(1), 1, Range(1, 0));
        memo.Add(Bytes(2), 1, Range(1, 256));
        memo.Add(Bytes(3), 1, Range(1, 512));
        Assert.False(memo.TryFind(Bytes(1), 1, out _));
        Assert.True(memo.TryFind(Bytes(2), 1, out var second));
        Assert.Equal(256UL, second.Offset);
        Assert.True(memo.TryFind(Bytes(3), 1, out var third));
        Assert.Equal(512UL, third.Offset);
        memo.Clear();
        Assert.False(memo.TryFind(Bytes(3), 1, out _));
    }

    // A range written for a command buffer that has not completed is never overwritten by the next
    // one, and the memo does not hand it to the next one either: the new bytes get a new range,
    // the old range keeps the old bytes, and a recycled ring is never mistaken for the old range.
    [Fact]
    public void ARangeStillInFlightIsNeitherReusedNorOverwritten()
    {
        if (!RequireDevice())
        {
            return;
        }

        using var backing = new MappedBacking();
        using var memory = new MetalGuestMemory(Device.Value, backing, new NoCpu(), Granule, 64 * Mib);
        var memo = new MetalRegionMemo(4);
        var first = Bytes(0xA1);
        var second = Bytes(0xB2);

        memory.BeginSubmission(1, 0);
        var range = memory.Allocate(64, 256, out var span);
        first.CopyTo(span);
        memo.Add(first, 1, in range);
        Assert.True(memo.TryFind(first, 1, out var again));
        Assert.Equal(range, again);

        // Submission 1 has not completed (completed = 0): a lookup in submission 2 finds nothing,
        // and what it writes lands elsewhere in the ring.
        memory.BeginSubmission(2, 0);
        Assert.False(memo.TryFind(first, 2, out _));
        var next = memory.Allocate(64, 256, out var nextSpan);
        second.CopyTo(nextSpan);
        Assert.False(next.Buffer == range.Buffer && next.Offset == range.Offset);
        Assert.True(memory.RingSpan(in range).SequenceEqual(first), "the in-flight range was overwritten");
        memo.Add(second, 2, in next);

        // Submissions 1 and 2 complete: the ring may be recycled and rewritten, and no old range
        // is offered for the same bytes in submission 3.
        memory.BeginSubmission(3, 2);
        Assert.False(memo.TryFind(first, 3, out _));
        Assert.False(memo.TryFind(second, 3, out _));
        output.WriteLine($"in-flight range 0x{range.Offset:X}, next 0x{next.Offset:X}");
    }

    // Two resolves of ranges in one window take the memo; a new submission, a moved cut and a
    // range in another window each take the full path, and every answer is the window's address
    // plus the offset in it.
    [Fact]
    public void TheWindowMemoFollowsTheSubmissionAndTheLayout()
    {
        if (!RequireDevice())
        {
            return;
        }

        using var backing = new MappedBacking();
        using var memory = new MetalGuestMemory(Device.Value, backing, new NoCpu(), Granule, 64 * Mib);

        MetalGuestRange Resolve(ulong offset, ulong size)
        {
            Assert.True(memory.TryResolveInPlace(GuestBase + offset, size, out var range));
            Assert.Equal(size, range.Size);
            Assert.NotEqual(0, range.Buffer);
            return range;
        }

        memory.BeginSubmission(1, 0);
        var (hits0, misses0) = memory.WindowMemoCounts;
        var a = Resolve(0x100, 0x40);
        Assert.Equal((hits0, misses0 + 1), memory.WindowMemoCounts);

        // The same range, and another inside the window: no lock, no placement.
        Assert.Equal(a, Resolve(0x100, 0x40));
        var b = Resolve(0x2000, 0x80);
        Assert.Equal(a.Buffer, b.Buffer);
        Assert.Equal(0x2000UL - 0x100, b.GpuAddress - a.GpuAddress);
        var (hits1, misses1) = memory.WindowMemoCounts;
        Assert.Equal((hits0 + 2, misses0 + 1), (hits1, misses1));

        // A range across an edge no resolve used moves the cut: the layout version changed, so
        // the next resolve of the first window's range is placed again (and still right).
        var versionBefore = memory.PartitionVersion;
        var across = Resolve(5 * Granule - 0x100, 0x200);
        Assert.NotEqual(versionBefore, memory.PartitionVersion);
        var (hits2, misses2) = memory.WindowMemoCounts;
        Assert.Equal((hits1, misses1 + 1), (hits2, misses2));
        Assert.Equal(a, Resolve(0x100, 0x40));
        Assert.Equal((hits2, misses2 + 1), memory.WindowMemoCounts);
        Assert.Equal(a, Resolve(0x100, 0x40));
        Assert.Equal((hits2 + 1, misses2 + 1), memory.WindowMemoCounts);
        Assert.True(across.IsValid);

        // The next submission starts over, and a range in another window is not the first's.
        memory.BeginSubmission(2, 1);
        var (hits3, misses3) = memory.WindowMemoCounts;
        Assert.Equal(a, Resolve(0x100, 0x40));
        Assert.Equal((hits3, misses3 + 1), memory.WindowMemoCounts);
        var other = Resolve(9 * Granule + 0x10, 0x20);
        Assert.NotEqual(a.Buffer, other.Buffer);
        Assert.Equal((hits3, misses3 + 2), memory.WindowMemoCounts);
        Assert.Equal(other, Resolve(9 * Granule + 0x10, 0x20));
        Assert.Equal((hits3 + 1, misses3 + 2), memory.WindowMemoCounts);
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Runs on the device only behind the exclusive device gate.");
            return false;
        }

        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        return true;
    }

    private sealed class NoCpu : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;
        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
        public bool CanRead(ulong address, ulong size) => false;
    }

    // An anonymous shared mapping standing in for the guest backing; GuestBase is its first byte.
    private sealed class MappedBacking : IGuestBackedSpace, IDisposable
    {
        public MappedBacking()
        {
            Base = Mmap(0, (nuint)BackingSize, 3, 0x0001 | 0x1000, -1, 0);
            Assert.NotEqual(-1, Base);
        }

        public nint Base { get; }

        public void Dispose() => Munmap(Base, (nuint)BackingSize);

        private static bool Contains(ulong address, ulong length) =>
            address >= GuestBase && address - GuestBase + length <= BackingSize;

        public bool TryHoldRange(ulong address, ulong size) => false;
        public bool TryHoldRangeAtOrAbove(ulong searchStart, ulong size, ulong alignment, out ulong address) { address = 0; return false; }
        public bool TryMapBacked(ulong address, ulong size, ulong backingOffset, GuestPageProtection protection, out HostViewFailure failure) { failure = HostViewFailure.None; return false; }
        public bool TryUnmapBacked(ulong address, ulong size) => false;
        public bool TryClearBacking(ulong offset, ulong size) => false;
        public bool IsBackedView(ulong address) => Contains(address, 1);
        public bool IsBackedRange(ulong address, ulong size) => Contains(address, size);
        public (ulong Base, ulong Size) BackingAliasRange => ((ulong)Base, BackingSize);

        public bool TryResolveBackingAlias(ulong address, ulong size, out ulong alias)
        {
            alias = Contains(address, size) ? (ulong)Base + (address - GuestBase) : 0;
            return alias != 0;
        }

        public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data) => false;

        public bool TryReadBacking(ulong address, Span<byte> data)
        {
            if (!Contains(address, (ulong)data.Length)) return false;
            new ReadOnlySpan<byte>((byte*)Base + (address - GuestBase), data.Length).CopyTo(data);
            return true;
        }

        [DllImport("libc", EntryPoint = "mmap")]
        private static extern nint Mmap(nint address, nuint length, int protection, int flags, int fd, long offset);

        [DllImport("libc", EntryPoint = "munmap")]
        private static extern int Munmap(nint address, nuint length);
    }
}
