// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

public sealed unsafe partial class MappedCopyReadWatchTests
{
    // Real original mappings/writer enrollment; labels confer no authority.
    [Theory]
    [InlineData("current")] [InlineData("physical-busy")] [InlineData("envelope-busy")]
    [InlineData("overlap")] [InlineData("failed-envelope")] [InlineData("released")]
    [InlineData("aba")] [InlineData("publication")] [InlineData("unknown-alias")]
    public void FirstStatusDiagnosticReportsReachedGuardWithoutChangingOriginalStatus(string kind)
    {
        using var state = new State(); var original = state.Capture();
        object? token = null; CpuMappedPublication? publication = null;
        try
        {
            if (kind == "physical-busy") token = state.Begin(State.Page, 1);
            if (kind == "envelope-busy") token = state.Envelope();
            if (kind == "overlap") token = state.Begin(32, 1);
            if (kind == "failed-envelope") state.End(state.Envelope(), false);
            if (kind == "released") state.Ledger.ReleaseMappedCopyWriteWatch(original);
            if (kind == "aba") state.Map(State.Address, 0);
            if (kind == "unknown-alias") { lock (state.Gate) state.Ledger.QuarantineUnknownAlias(); }
            if (kind == "publication")
            {
                var source = PublicationOriginal(state, State.Address + State.Page, 32);
                publication = Prepare(state, source, source.SourceAddress);
            }
            var status = state.Ledger.GetMappedCopyWriteWatchStatus(state.Backing, original, out var first);
            Assert.Equal(state.Ledger.GetMappedCopyWriteWatchStatus(state.Backing, original), status);
            Assert.Equal(status, first.ProviderStatus);
            Assert.Null(first.SharedClosure); Assert.Null(first.OwnerClosure); Assert.Null(first.VirtualClosure);
            if (kind is "aba" or "unknown-alias")
            {
                Assert.Equal(kind == "aba" ? "view-revoked" : "ledger-unknown-alias", first.MappingGuard);
                Assert.Equal("before-watch", first.MappingCheck);
                Assert.Null(first.WatchStatus); Assert.Null(first.PrimaryAdmission);
                Assert.Equal(CpuMappedReadWatchStatus.Stale, status);
            }
            else
            {
                Assert.Null(first.MappingGuard); Assert.Null(first.MappingCheck);
                Assert.Equal(status, first.WatchStatus);
                Assert.Equal(kind switch
                {
                    "current" => "current", "physical-busy" or "envelope-busy" or "publication" => "admission-nonzero",
                    "released" => "released", _ => "revoked",
                }, first.WatchGuard);
                Assert.Equal(kind switch
                {
                    "current" => 0L, "physical-busy" or "envelope-busy" => 1L,
                    "publication" => 1L << 32, _ => (long?)null,
                }, first.PrimaryAdmission);
                Assert.Equal(kind switch
                {
                    "publication" => "publication-admission", "overlap" or "failed-envelope" or "released" => "initial-watch-refusal",
                    _ => null,
                }, first.ClassificationGuard);
                Assert.Equal(kind switch
                {
                    "current" => CpuMappedReadWatchStatus.Current,
                    "physical-busy" or "envelope-busy" => CpuMappedReadWatchStatus.Busy,
                    _ => CpuMappedReadWatchStatus.Stale,
                }, status);
                if (kind == "publication") Assert.True(publication!.IsOriginalDisjointReadWatchCurrent(original));
                // That separate local proof does not rewrite ordinary status.
            }
        }
        finally
        {
            publication?.Dispose(); if (token is not null) state.End(token);
            state.Ledger.ReleaseMappedCopyWriteWatch(original);
        }
    }

    [Fact]
    public void FirstStatusDiagnosticRejectsForeignCaptureBeforeInspectingItsWatch()
    {
        using var own = new State(); using var other = new State(); var original = other.Capture();
        try
        {
            Assert.Equal(CpuMappedReadWatchStatus.Stale, own.Ledger.GetMappedCopyWriteWatchStatus(own.Backing, original, out var first));
            Assert.Equal("capture-ledger", first.MappingGuard); Assert.Equal("before-watch", first.MappingCheck);
            Assert.Null(first.WatchGuard); Assert.Null(first.PrimaryAdmission); Assert.Null(first.WatchStatus);
        }
        finally { other.Ledger.ReleaseMappedCopyWriteWatch(original); }
    }

    [Theory]
    [InlineData(CpuMappedReadWatchStatus.Stale, CpuMappedReadWatchStatus.Busy)]
    [InlineData(CpuMappedReadWatchStatus.Busy, CpuMappedReadWatchStatus.Current)]
    public void DefaultFirstStatusDiagnosticUsesOneOriginalQueryAndNeverBorrowsLaterStatus(
        CpuMappedReadWatchStatus firstStatus, CpuMappedReadWatchStatus laterStatus)
    {
        using var state = new State(); var original = state.Capture();
        var implementation = new FirstStatusOnlySource(firstStatus, laterStatus);
        ICpuMappedReadWatchSource source = implementation;
        try
        {
            Assert.Equal(firstStatus, source.GetMappedCopyWriteWatchStatus(original, out var first));
            Assert.Equal(1, implementation.Queries); Assert.Equal(firstStatus, first.ProviderStatus);
            Assert.Equal("source-status-unattributed", first.WatchGuard); Assert.Null(first.PrimaryAdmission);
            Assert.Null(first.MappingGuard); Assert.Null(first.WatchStatus);
            Assert.Equal(laterStatus, source.GetMappedCopyWriteWatchStatus(original)); Assert.Equal(2, implementation.Queries);
        }
        finally { state.Ledger.ReleaseMappedCopyWriteWatch(original); }
    }

    private sealed class FirstStatusOnlySource(CpuMappedReadWatchStatus first, CpuMappedReadWatchStatus later) : ICpuMappedReadWatchSource
    {
        internal int Queries;
        public CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(CpuMappedCopyObservation original) => ++Queries == 1 ? first : later;
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original) => throw new InvalidOperationException("No Boolean query in status attribution.");
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation original) => throw new InvalidOperationException("No additional mapping query in default attribution.");
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation original) => throw new InvalidOperationException("No release in status attribution.");
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprint, ulong count, ulong address, Span<byte> bytes, out CpuMappedCopyObservation? original) => throw new InvalidOperationException("No copy in status attribution.");
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong footprint, ulong count, ulong address, Span<byte> bytes, out CpuMappedCopyObservation? original) => throw new InvalidOperationException("No watched copy in status attribution.");
    }
}
