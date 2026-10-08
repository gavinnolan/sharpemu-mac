// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

public sealed unsafe partial class MappedCopyReadWatchTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BusyOriginalWatchIsOnlyARetryRefusalUntilOrdinaryWriterEnds(bool envelope)
    {
        using var state = new State(); var original = state.Capture();
        var token = envelope ? state.Envelope() : state.Begin(State.Page, 1);
        Assert.Equal(CpuMappedReadWatchStatus.Busy, state.Ledger.GetMappedCopyWriteWatchStatus(state.Backing, original));
        Assert.False(state.Current(original));
        state.End(token);
        Assert.Equal(CpuMappedReadWatchStatus.Current, state.Ledger.GetMappedCopyWriteWatchStatus(state.Backing, original));
        state.Ledger.ReleaseMappedCopyWriteWatch(original);
    }

    [Theory]
    [InlineData("overlap")] [InlineData("failed-envelope")] [InlineData("released")]
    [InlineData("aba")] [InlineData("publication")]
    public void UnknownRevokedReleasedReplacedOrPublicationWatchNeverBecomesBusy(string kind)
    {
        using var state = new State(); var original = state.Capture();
        object? token = null; CpuMappedPublication? publication = null;
        try
        {
            if (kind == "overlap") token = state.Begin(32, 1);
            if (kind == "failed-envelope") state.End(state.Envelope(), false);
            if (kind == "released") state.Ledger.ReleaseMappedCopyWriteWatch(original);
            if (kind == "aba") state.Map(State.Address, 0);
            if (kind == "publication")
            {
                var source = PublicationOriginal(state, State.Address + State.Page, 32);
                publication = Prepare(state, source, source.SourceAddress);
            }
            Assert.Equal(CpuMappedReadWatchStatus.Stale, state.Ledger.GetMappedCopyWriteWatchStatus(state.Backing, original));
            Assert.False(state.Current(original));
        }
        finally
        {
            publication?.Dispose(); if (token is not null) state.End(token);
            state.Ledger.ReleaseMappedCopyWriteWatch(original);
        }
    }

    [Fact]
    public void UnsupportedStatusCannotBorrowLegacyBooleanCurrentness()
    {
        using var state = new State(); var original = state.Capture();
        ICpuMappedReadWatchSource source = new LegacyBooleanWatch();
        Assert.True(source.IsMappedCopyWriteWatchCurrent(original));
        Assert.Equal(CpuMappedReadWatchStatus.Stale, source.GetMappedCopyWriteWatchStatus(original));
        state.Ledger.ReleaseMappedCopyWriteWatch(original);
    }

    private sealed class LegacyBooleanWatch : ICpuMappedReadWatchSource
    {
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation observation) => true;
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation) => true;
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation observation) { }
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprint, ulong count, ulong source, Span<byte> bytes,
            out CpuMappedCopyObservation? observation) => throw new InvalidOperationException("No new copy in status control.");
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong footprint, ulong count, ulong source, Span<byte> bytes,
            out CpuMappedCopyObservation? observation) => throw new InvalidOperationException("No new watched copy in status control.");
    }
}
