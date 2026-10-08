// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

public sealed unsafe partial class MappedCopyReadWatchTests
{
    private static void EvictHistory(State state)
    {
        for (var index = 0; index < 1600; index++) state.End(state.Begin(State.Page, 4));
    }

    [Fact]
    public void OriginalWatchedValidationSurvivesDisjointHistoryEvictionWithoutRecapture()
    {
        using var state = new State(); var original = state.Capture();
        EvictHistory(state);
        Assert.True(state.Current(original));
        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, original, out var validation));
        using var exact = Assert.IsType<CpuMappedPublicationValidation>(validation);
        Assert.True(exact.IsCurrent); Assert.False(state.Current(original));
        exact.Dispose(); Assert.True(state.Current(original));
        state.Ledger.ReleaseMappedCopyWriteWatch(original);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void EvictedHistoryNeverRestoresOverlappingReleasedOrReplacedOriginal(int mutation)
    {
        using var state = new State(); var original = state.Capture();
        if (mutation == 0) state.End(state.Begin(32, 1));
        else if (mutation == 1) state.Ledger.ReleaseMappedCopyWriteWatch(original);
        else state.Map(State.Address, 0);
        EvictHistory(state);
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, original, out var validation));
        Assert.Null(validation);
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Ledger.TryPrepareMappedPublication(state.Backing, original, [new(original.SourceAddress, new byte[4])], out var publication));
        Assert.Null(publication); state.Ledger.ReleaseMappedCopyWriteWatch(original);
    }

    [Fact]
    public void UnwatchedOriginalRetainsHistoryWindowLimit()
    {
        using var state = new State(); var original = PublicationOriginal(state, State.Address + 32, 16);
        EvictHistory(state);
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, original, out var validation));
        Assert.Null(validation);
    }

    [Fact]
    public void OwnAppliedBytesProduceOneExactWatchedSuccessorAndInvalidateOldReceipt()
    {
        using var state = new State(); var original = state.Capture(); EvictHistory(state);
        using var publication = Prepare(state, original, original.SourceAddress);
        Assert.Null(publication.SuccessfulObservation);
        Assert.Equal(CpuMappedPublicationResult.Applied, publication.TryCommit());
        var successor = Assert.IsType<CpuMappedCopyObservation>(publication.SuccessfulObservation);
        Assert.False(state.Current(original)); Assert.True(state.Current(successor));
        Assert.Equal(original.Segments.ToArray(), successor.Segments.ToArray());
        Assert.Equal(new byte[] { 0x71, 0x71, 0x71, 0x71 }, new ReadOnlySpan<byte>((void*)(state.Backing.AliasBase + 32), 4).ToArray());
        EvictHistory(state);
        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, successor, out var validation));
        Assert.IsType<CpuMappedPublicationValidation>(validation).Dispose();
        state.End(state.Begin(33, 1)); Assert.False(state.Current(successor));
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, successor, out var rejected));
        Assert.Null(rejected); state.Ledger.ReleaseMappedCopyWriteWatch(successor);
    }

    [Fact]
    public void CancelledWatchedPublicationReleasesPrivateSuccessorWithoutConsumingOriginal()
    {
        using var state = new State(); var original = state.Capture();
        for (var index = 0; index < 160; index++)
        {
            using var publication = Prepare(state, original, original.SourceAddress);
            publication.Dispose(); Assert.Null(publication.SuccessfulObservation);
            Assert.True(state.Current(original));
        }
        state.Ledger.ReleaseMappedCopyWriteWatch(original);
        var all = Enumerable.Range(0, 128).Select(_ => state.Capture()).ToArray();
        foreach (var item in all) state.Ledger.ReleaseMappedCopyWriteWatch(item);
    }
}
