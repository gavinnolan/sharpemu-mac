// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.HLE;

/// <summary>
/// The decisions the slot trap makes before it touches a page: which mappings carry the
/// structure being watched, which page of one holds the slot, and whether an access that
/// faulted is the slot's own rather than its page-mate's. A trap that answers the last one
/// loosely reports every allocator write; one that answers it too tightly reports nothing.
/// </summary>
public sealed class GuestSlotTrapTests
{
    private const ulong Block = 0x10000;
    private const ulong Slot = 0x720;
    private const ulong Mask = 0xFFFF;

    [Theory]
    [InlineData(0x10000, 0xF2, true)]
    [InlineData(0x10000, 0xF1, false)]
    [InlineData(0x20000, 0xF2, false)]
    [InlineData(0x1000, 0xF2, false)]
    public void OnlyMappingsOfTheWatchedShapeAreAdopted(ulong length, ulong protection, bool expected) =>
        Assert.Equal(expected, GuestSlotTrap.MatchesShape(length, protection, Block, 0xF2));

    [Theory]
    // The slot itself, and every byte of it.
    [InlineData(0x9003260720UL, true)]
    [InlineData(0x9003260727UL, true)]
    // Its page-mates, including the count beside it.
    [InlineData(0x9003260728UL, false)]
    [InlineData(0x900326071FUL, false)]
    [InlineData(0x9003260000UL, false)]
    // The same offset in another block is the same slot: the base moves between runs.
    [InlineData(0x9003330720UL, true)]
    [InlineData(0x7003260720UL, true)]
    public void OnlyTheSlotItselfCountsAsAHit(ulong address, bool expected) =>
        Assert.Equal(expected, GuestSlotTrap.HitsSlot(address, Slot, Mask, slotBytes: 8));

    // A title maps hundreds of blocks of the right shape before the one that matters, so
    // without a band the trap spends every slot it has on the wrong heap.
    [Theory]
    [InlineData(0x9003260000UL, 0x90UL, true)]
    [InlineData(0x7000010000UL, 0x90UL, false)]
    [InlineData(0x7000010000UL, 0x70UL, true)]
    [InlineData(0x7000010000UL, 0UL, true)]
    public void OnlyMappingsInTheWatchedBandAreAdopted(ulong address, ulong band, bool expected) =>
        Assert.Equal(expected, GuestSlotTrap.MatchesBand(address, band));

    [Fact]
    public void TheWatchedPageIsTheOneTheSlotLivesOn()
    {
        Assert.Equal(0x9003260000UL, GuestSlotTrap.PageOfSlot(0x9003260000UL, Slot));
        // A slot past the first page moves the watch with it.
        Assert.Equal(0x9003263000UL, GuestSlotTrap.PageOfSlot(0x9003260000UL, 0x3720));
    }

    [Fact]
    public void AMappingOfTheWrongShapeIsIgnoredEvenWhenTheTrapIsOff()
    {
        // Off by default, so this must be a no-op rather than an exception: the hook runs
        // on every direct mapping of every run.
        GuestSlotTrap.OnDirectMapping(0x9003260000, 0x10000, 0xF2);
        Assert.Equal(0, GuestSlotTrap.CopyBlocks(stackalloc ulong[4]));
    }
}

/// <summary>
/// The machine state a fault publishes for the recovery chain to read back.
/// </summary>
public sealed class GuestFaultContextTests
{
    [Fact]
    public void ThePublishedRegistersComeBackInWin64Order()
    {
        Span<ulong> registers = stackalloc ulong[16];
        for (var index = 0; index < registers.Length; index++)
        {
            registers[index] = 0x1000UL + (ulong)index;
        }

        GuestFaultContext.Publish(0xDEADBEEF, registers);
        var snapshot = GuestFaultContext.Capture();
        Assert.True(snapshot.IsValid);
        Assert.Equal(0xDEADBEEFUL, snapshot.Rip);
        var text = snapshot.DescribeRegisters();
        Assert.StartsWith("rax=0x0000000000001000", text, StringComparison.Ordinal);
        Assert.Contains("rdx=0x0000000000001002", text, StringComparison.Ordinal);
        Assert.Contains("r15=0x000000000000100F", text, StringComparison.Ordinal);

        GuestFaultContext.Clear();
        Assert.False(GuestFaultContext.Capture().IsValid);
        Assert.Equal("<unavailable>", GuestFaultContext.Capture().DescribeRegisters());
    }

    // A store's operand is in a register when it faults, which is how a trap tells the
    // write it is hunting from every other write to the same address.
    [Fact]
    public void AStoresValueIsFoundInTheFaultingRegisters()
    {
        Span<ulong> registers = stackalloc ulong[16];
        registers[15] = 0x4000000040000000;
        GuestFaultContext.Publish(0x8013B626B, registers);
        var snapshot = GuestFaultContext.Capture();
        Assert.True(snapshot.CarriesValue(0x4000000040000000));
        Assert.False(snapshot.CarriesValue(0x3F8000003F800000));

        GuestFaultContext.Clear();
        Assert.False(GuestFaultContext.Capture().CarriesValue(0x4000000040000000));
    }

    [Fact]
    public void CodeIsDescribedThroughTheInstalledReader()
    {
        var previousReader = GuestCodeReader.Reader;
        var previousDisassembler = GuestCodeReader.Disassembler;
        try
        {
            GuestCodeReader.Reader = (_, count) => new byte[count];
            GuestCodeReader.Disassembler = (_, _) => "nop";
            GuestFaultContext.Publish(0x8013B5F4D, stackalloc ulong[16]);
            var described = GuestFaultContext.Capture().DescribeCode();
            Assert.Contains("00", described, StringComparison.Ordinal);
            Assert.Contains("(nop)", described, StringComparison.Ordinal);

            // A reader that cannot read says so rather than inventing an instruction.
            GuestCodeReader.Reader = (_, _) => null;
            Assert.Equal("<unreadable>", GuestFaultContext.Capture().DescribeCode());
        }
        finally
        {
            GuestCodeReader.Reader = previousReader;
            GuestCodeReader.Disassembler = previousDisassembler;
            GuestFaultContext.Clear();
        }
    }
}
