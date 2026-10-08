// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

// Actual bounded ledger/admission over pinned bytes, no native syscall claim.
public sealed unsafe partial class MappedCopyReadWatchTests
{
    private static object HistoryWriters(State state) => typeof(HostBackingObject)
        .GetProperty("ManagedWrites", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state.Backing)!;
    private static int Incomplete(State state) => (int)HistoryWriters(state).GetType()
        .GetField("coverageIncomplete", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(HistoryWriters(state))!;
    private static long HistoryLosses(State state) => (long)HistoryWriters(state).GetType()
        .GetProperty("HistoryRecordLosses", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(HistoryWriters(state))!;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DelayedDisjointCompletionLosesHistoryWithoutPoisoningOriginalPhysicalWatch(bool envelope)
    {
        using var state = new State(); var original = state.Capture();
        var unwatched = PublicationOriginal(state, original.SourceAddress, 16);
        var delayed = envelope ? state.Envelope() : state.Begin(State.Page, 4);
        try
        {
            EvictHistory(state);
            Assert.False(state.Current(original));
            Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
                State.Address, State.Page, original.SourceAddress, new byte[16], out var during));
            Assert.Null(during);
        }
        finally { state.End(delayed); }
        Assert.Equal(0, Incomplete(state)); Assert.True(HistoryLosses(state) > 0);
        Assert.True(state.Current(original));
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, unwatched, out var expired));
        Assert.Null(expired);
        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, original, out var validation));
        Assert.IsType<CpuMappedPublicationValidation>(validation).Dispose(); Assert.True(state.Current(original));
        var fresh = state.Capture(); Assert.True(state.Current(fresh));
        state.Ledger.ReleaseMappedCopyWriteWatch(fresh); state.Ledger.ReleaseMappedCopyWriteWatch(original);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DelayedUnknownCompletionRevokesAllOriginalWatchesDespiteLostHistory(bool envelope)
    {
        using var state = new State(); var original = state.Capture();
        var delayed = envelope ? state.Envelope() : state.Begin(State.Page, 4);
        try { EvictHistory(state); }
        finally { state.End(delayed, false); }
        Assert.Equal(0, Incomplete(state)); Assert.True(HistoryLosses(state) > 0);
        Assert.False(state.Current(original));
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, original, out var validation));
        Assert.Null(validation);
        // New bytes copied after the unknown writer finishes are a separate receipt.
        var fresh = state.Capture(); Assert.True(state.Current(fresh));
        state.Ledger.ReleaseMappedCopyWriteWatch(fresh); state.Ledger.ReleaseMappedCopyWriteWatch(original);
    }

    [Fact]
    public void OverlapInsideLongSuccessfulEnvelopeNeverRestoresOriginalWatch()
    {
        using var state = new State(); var original = state.Capture(); var envelope = state.Envelope();
        try { state.End(state.Begin(32, 1)); EvictHistory(state); }
        finally { state.End(envelope); }
        Assert.Equal(0, Incomplete(state)); Assert.True(HistoryLosses(state) > 0);
        Assert.False(state.Current(original));
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Ledger.TryPrepareMappedPublication(state.Backing, original,
                [new(original.SourceAddress, new byte[] { 71 })], out var publication));
        Assert.Null(publication); state.Ledger.ReleaseMappedCopyWriteWatch(original);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RealActiveCoverageLossRemainsStickyAfterAllWritersFinish(bool invalidBounds)
    {
        using var state = new State(); var original = state.Capture();
        if (invalidBounds) state.End(state.Begin(State.Page * 2, 1));
        else
        {
            var held = Enumerable.Range(0, 128).Select(_ => state.Begin(State.Page, 1)).ToArray();
            var overflow = state.Begin(State.Page, 1);
            foreach (var token in held) state.End(token);
            state.End(overflow);
        }
        Assert.Equal(1, Incomplete(state)); Assert.False(state.Current(original));
        Assert.Equal(invalidBounds ? 1 : 4, (int)HistoryWriters(state).GetType()
            .GetProperty("FirstCoverageLoss", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(HistoryWriters(state))!);
        Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
            State.Address, State.Page, original.SourceAddress, new byte[16], out var fresh));
        Assert.Null(fresh); state.Ledger.ReleaseMappedCopyWriteWatch(original);
    }

    [Fact]
    public void MappingOnlyCopyDuringDelayedWriteCannotBorrowHistoryAfterQuiescence()
    {
        using var state = new State(); var enrolled = state.Capture(); var delayed = state.Begin(State.Page, 4);
        CpuMappedCopyObservation during;
        try { during = PublicationOriginal(state, State.Address + 32, 16); EvictHistory(state); }
        finally { state.End(delayed); }
        Assert.Equal(0, Incomplete(state)); Assert.True(HistoryLosses(state) > 0);
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, during, out var validation));
        Assert.Null(validation); Assert.True(state.Current(enrolled));
        state.Ledger.ReleaseMappedCopyWriteWatch(enrolled);
    }
}
