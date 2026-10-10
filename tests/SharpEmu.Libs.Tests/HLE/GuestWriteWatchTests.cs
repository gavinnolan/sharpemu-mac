// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.HLE;

public sealed class GuestWriteWatchTests
{
    [Theory]
    [InlineData(0x0000001000000001, "torn")]
    [InlineData(0x0000FFFF00000001, "torn")]
    [InlineData(0x0000000008000000, "shift")]
    [InlineData(0x0000000080015F00, "shift")]
    [InlineData(0x0000000007FFFFFF, null)]
    [InlineData(0x0000000009000000, null)]
    [InlineData(0x000000003F800000, null)]
    [InlineData(0x0000000080015F01, null)]
    [InlineData(0x0001000000000001, null)]
    public void ClassifyBulkValue_RecognizesCorruptionSignatures(ulong value, string? expected)
    {
        Assert.Equal(expected, GuestWriteWatch.ClassifyBulkValue(value));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 7)]
    [InlineData(3, 5)]
    [InlineData(7, 1)]
    [InlineData(8, 0)]
    public void FirstAlignedOffset_ReturnsTheNextEightByteBoundary(ulong address, int expected)
    {
        Assert.Equal(expected, GuestWriteWatch.FirstAlignedOffset(address));
    }

    // The address a corrupted structure lives at moves with the title's allocation order,
    // so the watch that catches it is keyed on the value instead.
    [Fact]
    public void TryFindValue_FindsTheValueInAnAlignedSlotOfTheWrite()
    {
        var data = new byte[32];
        BitConverter.TryWriteBytes(data.AsSpan(16), 0x4000000040000000UL);

        Assert.True(GuestWriteWatch.TryFindValue(0x9003260710, data, 0x4000000040000000UL, 0, out var hit));
        Assert.Equal(0x9003260720UL, hit);
        Assert.False(GuestWriteWatch.TryFindValue(0x9003260710, data, 0x3F8000003F800000UL, 0, out _));
    }

    [Fact]
    public void TryFindValue_SkipsSlotsTheWriteOnlyStraddles()
    {
        var data = new byte[16];
        // Written four bytes in, so the value spans two aligned slots and matches neither.
        BitConverter.TryWriteBytes(data.AsSpan(4), 0x4000000040000000UL);
        Assert.False(GuestWriteWatch.TryFindValue(0x9003260000, data, 0x4000000040000000UL, 0, out _));
    }

    // The corrupted slot sits at a fixed offset inside a 64 KiB block whose base the run
    // chooses, so the filter names the offset and lets the base be anything.
    [Fact]
    public void TryFindValue_HonoursTheSlotFilterInsideTheScan()
    {
        var data = new byte[0x1000];
        // The value appears twice: once in an uninteresting slot, once in the watched one.
        BitConverter.TryWriteBytes(data.AsSpan(0x100), 0x4000000040000000UL);
        BitConverter.TryWriteBytes(data.AsSpan(0x720), 0x4000000040000000UL);

        Assert.True(GuestWriteWatch.TryFindValue(
            0x9003260000, data, 0x4000000040000000UL, 0, 0x720, 0xFFFF, out var hit));
        Assert.Equal(0x9003260720UL, hit);

        // A different 64 KiB block, same offset: still the slot that was asked for.
        Assert.True(GuestWriteWatch.TryFindValue(
            0x9003330000, data, 0x4000000040000000UL, 0, 0x720, 0xFFFF, out hit));
        Assert.Equal(0x9003330720UL, hit);
    }

    [Fact]
    public void TryFindValue_WithASlotFilterIgnoresEveryOtherSlot()
    {
        var data = new byte[0x1000];
        BitConverter.TryWriteBytes(data.AsSpan(0x100), 0x4000000040000000UL);
        Assert.False(GuestWriteWatch.TryFindValue(
            0x9003260000, data, 0x4000000040000000UL, 0, 0x720, 0xFFFF, out _));
        // Without the filter the same write is a hit.
        Assert.True(GuestWriteWatch.TryFindValue(0x9003260000, data, 0x4000000040000000UL, 0, out _));
    }

    [Fact]
    public void TryFindValue_HonoursTheAddressBand()
    {
        var data = new byte[8];
        BitConverter.TryWriteBytes(data.AsSpan(), 0x4000000040000000UL);
        Assert.True(GuestWriteWatch.TryFindValue(0x9003260720, data, 0x4000000040000000UL, 0x90, out _));
        Assert.False(GuestWriteWatch.TryFindValue(0x7003260720, data, 0x4000000040000000UL, 0x90, out _));
    }

    [Theory]
    [InlineData(0x1000, 8, 0x1000, true)]
    [InlineData(0x0FFF, 1, 0x1000, false)]
    [InlineData(0x0FFF, 2, 0x1000, true)]
    [InlineData(0x1008, 1, 0x1000, false)]
    [InlineData(ulong.MaxValue - 3, 4, ulong.MaxValue - 1, true)]
    [InlineData(ulong.MaxValue, 1, ulong.MaxValue, true)]
    [InlineData(0x1000, 0, 0x1000, false)]
    public void Overlaps_HandlesBoundariesWithoutOverflow(
        ulong address,
        int length,
        ulong slot,
        bool expected)
    {
        Assert.Equal(expected, GuestWriteWatch.Overlaps(address, length, slot));
    }

    [Theory]
    [InlineData(0x10000, 0xF2, true)]
    [InlineData(0x10001, 0xF2, false)]
    [InlineData(0x10000, 0xF1, false)]
    public void IsPoolMapping_RequiresTheExpectedSizeAndProtection(
        ulong length,
        int protection,
        bool expected)
    {
        Assert.Equal(expected, GuestWriteWatch.IsPoolMapping(length, protection));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("not-hex", 0)]
    [InlineData("80", 0x80)]
    [InlineData("0x80", 0x80)]
    [InlineData(" 0X801DB3BBB ", 0x801DB3BBB)]
    public void Parse_HandlesHexadecimalWatchValues(string? text, ulong expected)
    {
        Assert.Equal(expected, GuestWriteWatch.Parse(text));
    }
}
