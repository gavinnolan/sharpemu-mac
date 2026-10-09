// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

public sealed unsafe partial class MappedCopyReadWatchTests
{
    // Real original ledger and physical writer admissions. Boolean attribution
    // must not run Status's second admission/classification or mapping query.
    [Theory]
    [InlineData("current")] [InlineData("physical-busy")] [InlineData("envelope-busy")]
    [InlineData("overlap")] [InlineData("failed-envelope")] [InlineData("released")]
    [InlineData("aba")] [InlineData("publication")] [InlineData("unknown-alias")]
    public void FirstBooleanDiagnosticKeepsStrictOriginalGuardWithoutStatusReclassification(string kind)
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
            var current = state.Ledger.IsMappedCopyWriteWatchCurrent(state.Backing, original, out var first);
            Assert.Equal(kind == "current", current); Assert.Equal(state.Current(original), current);
            Assert.Equal(current, first.BooleanCurrent);
            Assert.Null(first.WatchStatus); Assert.Null(first.ProviderStatus); Assert.Null(first.ClassificationGuard);
            Assert.Null(first.SharedClosure); Assert.Null(first.OwnerClosure); Assert.Null(first.VirtualClosure);
            if (kind is "aba" or "unknown-alias")
            {
                Assert.Equal(kind == "aba" ? "view-revoked" : "ledger-unknown-alias", first.MappingGuard);
                Assert.Equal("before-watch", first.MappingCheck); Assert.Null(first.WatchGuard); Assert.Null(first.PrimaryAdmission);
            }
            else
            {
                Assert.Null(first.MappingGuard); Assert.Null(first.MappingCheck);
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
                if (kind == "publication") Assert.True(publication!.IsOriginalDisjointReadWatchCurrent(original));
            }
        }
        finally
        {
            publication?.Dispose(); if (token is not null) state.End(token);
            state.Ledger.ReleaseMappedCopyWriteWatch(original);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DefaultBooleanDiagnosticUsesOneBooleanAndNeverQueriesStatusOrMapping(bool first)
    {
        using var state = new State(); var original = state.Capture();
        var implementation = new FirstBooleanOnlySource(first); ICpuMappedReadWatchSource source = implementation;
        try
        {
            Assert.Equal(first, source.IsMappedCopyWriteWatchCurrent(original, out var diagnostic));
            Assert.Equal(1, implementation.Queries); Assert.Equal(first, diagnostic.BooleanCurrent);
            Assert.Equal("source-boolean-unattributed", diagnostic.WatchGuard);
            Assert.Null(diagnostic.ProviderStatus); Assert.Null(diagnostic.PrimaryAdmission); Assert.Null(diagnostic.MappingGuard);
            Assert.Equal(!first, source.IsMappedCopyWriteWatchCurrent(original)); Assert.Equal(2, implementation.Queries);
        }
        finally { state.Ledger.ReleaseMappedCopyWriteWatch(original); }
    }

    private sealed class FirstBooleanOnlySource(bool first) : ICpuMappedReadWatchSource
    {
        internal int Queries;
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original) => ++Queries == 1 ? first : !first;
        public CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(CpuMappedCopyObservation original) => throw new InvalidOperationException("No Status query in Boolean attribution.");
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation original) => throw new InvalidOperationException("No additional mapping query in default attribution.");
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation original) => throw new InvalidOperationException("No release in attribution.");
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprint, ulong count, ulong address, Span<byte> bytes, out CpuMappedCopyObservation? original) => throw new InvalidOperationException("No copy in attribution.");
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong footprint, ulong count, ulong address, Span<byte> bytes, out CpuMappedCopyObservation? original) => throw new InvalidOperationException("No watched copy in attribution.");
    }
}
