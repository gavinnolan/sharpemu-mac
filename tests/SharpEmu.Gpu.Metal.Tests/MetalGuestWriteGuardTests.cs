// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// The rule the write guard enforces: a GPU-side write to guest memory may change the
/// bytes it declared and no others. Writing past the end of a declared range is how a
/// retile with the wrong footprint or a fill sized from padding corrupts the guest's own
/// allocator, which does not fault until the title follows the clobbered pointer.
/// </summary>
public sealed class MetalGuestWriteGuardTests
{
    // Flat guest memory, addressed from zero, so a declared range has readable margins.
    private sealed class FlatMemory : ICpuMemory
    {
        public FlatMemory(int size) => Bytes = new byte[size];

        public byte[] Bytes { get; }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (virtualAddress + (ulong)destination.Length > (ulong)Bytes.Length)
            {
                return false;
            }

            Bytes.AsSpan((int)virtualAddress, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (virtualAddress + (ulong)source.Length > (ulong)Bytes.Length)
            {
                return false;
            }

            source.CopyTo(Bytes.AsSpan((int)virtualAddress, source.Length));
            return true;
        }

        public bool CanRead(ulong address, ulong size) => address + size <= (ulong)Bytes.Length;
    }

    private const ulong Range = 0x10000;
    private const ulong Size = 0x1000;

    private static (MetalGuestWriteGuard Guard, FlatMemory Memory) Armed()
    {
        GuestWriteJournal.Enable();
        var memory = new FlatMemory(0x40000);
        return (new MetalGuestWriteGuard(memory), memory);
    }

    [Fact]
    public void AWriteThatStaysInsideItsDeclaredRangeIsNotAViolation()
    {
        var (guard, memory) = Armed();
        guard.Capture(Range, Size, serial: 7, "test");
        memory.Bytes.AsSpan((int)Range, (int)Size).Fill(0xAB);
        guard.Verify(completedSerial: 7);
        Assert.Equal(1, guard.Captures);
        Assert.Equal(0, guard.Violations);
        Assert.Equal(0, guard.Live);
    }

    [Fact]
    public void AWriteRunningOffTheEndOfItsRangeIsAViolation()
    {
        var (guard, memory) = Armed();
        guard.Capture(Range, Size, serial: 7, "retile");
        // One byte past the declared end: the padded-footprint bug in miniature.
        memory.Bytes[(int)(Range + Size)] = 0x5A;
        guard.Verify(completedSerial: 7);
        Assert.Equal(1, guard.Violations);
    }

    [Fact]
    public void AWriteBeforeTheStartOfItsRangeIsAViolation()
    {
        var (guard, memory) = Armed();
        guard.Capture(Range, Size, serial: 7, "fill");
        memory.Bytes[(int)Range - 1] = 0x5A;
        guard.Verify(completedSerial: 7);
        Assert.Equal(1, guard.Violations);
    }

    [Fact]
    public void AWatchIsNotCheckedUntilItsSubmissionCompletes()
    {
        var (guard, memory) = Armed();
        guard.Capture(Range, Size, serial: 7, "retile");
        memory.Bytes[(int)(Range + Size)] = 0x5A;
        guard.Verify(completedSerial: 6);
        Assert.Equal(0, guard.Violations);
        Assert.Equal(1, guard.Live);
        guard.Verify(completedSerial: 7);
        Assert.Equal(1, guard.Violations);
    }

    [Fact]
    public void BytesAnotherProducerDeclaredAreNotChargedToThisOne()
    {
        var (guard, memory) = Armed();
        guard.Capture(Range, Size, serial: 7, "retile");
        // The neighbour owns the bytes that follow, so changing them is its business.
        guard.Capture(Range + Size, Size, serial: 7, "neighbour");
        memory.Bytes[(int)(Range + Size)] = 0x5A;
        guard.Verify(completedSerial: 7);
        Assert.Equal(0, guard.Violations);
        Assert.True(guard.Overlaps > 0);
    }

    [Fact]
    public void TheJournalNamesTheProducerThatDeclaredAnAddress()
    {
        GuestWriteJournal.Enable();
        GuestWriteJournal.Record(0x9003270000, 0x10000, "writeback extent=1x1");
        Assert.True(GuestWriteJournal.TryFind(0x9003270720, out var start, out var size, out var producer));
        Assert.Equal(0x9003270000UL, start);
        Assert.Equal(0x10000UL, size);
        Assert.Equal("writeback extent=1x1", producer);
        Assert.False(GuestWriteJournal.TryFind(0x9003260000, out _, out _, out _));
    }
}
