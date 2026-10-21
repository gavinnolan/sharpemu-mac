// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

public sealed partial class MappedPublicationTests
{
    private static long PreparationAdmission(State state)
    {
        var writers = typeof(HostBackingObject).GetField("_managedWrites", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state.Backing)!;
        return (long)writers.GetType().GetField("admission", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(writers)!;
    }
    private static CpuMappedCopyObservation PreparationCapture(State state, bool watched, ulong address = State.Address)
    {
        CpuMappedCopyObservation? original;
        var result = watched
            ? state.Host.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing, address, State.Page, address, new byte[8], out original)
            : state.Host.Ledger.TryReadMappedCopy(state.Backing, address, State.Page, address, new byte[8], out original);
        Assert.Equal(CpuMappedCopyResult.Copied, result);
        return Assert.IsType<CpuMappedCopyObservation>(original);
    }

    [Theory]
    [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(true, 1)] [InlineData(true, 2)]
    public void PublicationPreparationReportsExactOrdinaryAdmissionWithoutTicketOrEffects(bool watched, int writers)
    {
        using var state = new State(); var original = PreparationCapture(state, watched); var bytes = state.Bytes();
        var tokens = new object[writers]; var consumed = 0;
        try
        {
            for (; consumed < tokens.Length; consumed++) tokens[consumed] = BeginValidationWriterOperation(state.Backing);
            Assert.Equal(writers, PreparationAdmission(state));
            Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
                state.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[] { 7 })], out var ticket, out var refusal));
            Assert.Null(ticket); Assert.Equal(CpuMappedPublicationPreparationRefusalKind.AdmissionNonzero, refusal.Kind);
            Assert.Equal(writers, refusal.RejectedAdmission); Assert.Equal(0, refusal.OriginalRevision);
            Assert.Equal(writers, PreparationAdmission(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
            Assert.Equal(bytes, state.Bytes()); Assert.True(state.Views.IsMappedCopyObservationCurrent(original));
        }
        finally
        {
            for (var index = 0; index < consumed; index++) EndValidationWriter(tokens[index], true);
            if (watched) state.Host.Ledger.ReleaseMappedCopyWriteWatch(original);
        }
        Assert.Equal(0, PreparationAdmission(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PublicationPreparationReportsPublicationBitWithoutOrdinaryWaitAuthority(bool watched)
    {
        using var state = new State(); var owner = state.Capture();
        var original = PreparationCapture(state, watched, State.Address + State.Page);
        var bytes = state.Bytes();
        try
        {
            using (var held = state.Prepare(owner, State.Address, [9]))
            {
                Assert.Equal(1L << 32, PreparationAdmission(state)); Assert.Equal(1, state.Backing.ActiveAccesses);
                Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
                    state.Views.TryPrepareMappedPublication(original, [new(State.Address + State.Page, new byte[] { 7 })], out var ticket, out var refusal));
                Assert.Null(ticket); Assert.Equal(CpuMappedPublicationPreparationRefusalKind.AdmissionNonzero, refusal.Kind);
                Assert.Equal(1L << 32, refusal.RejectedAdmission); Assert.Equal(1L << 32, PreparationAdmission(state));
                Assert.Equal(1, state.Backing.ActiveAccesses); Assert.Equal(bytes, state.Bytes());
            }
            Assert.Equal(0, PreparationAdmission(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
        }
        finally { if (watched) state.Host.Ledger.ReleaseMappedCopyWriteWatch(original); }
    }

    [Theory]
    [InlineData("mapping")] [InlineData("readonly")] [InlineData("foreign")]
    [InlineData("empty-runs")] [InlineData("history-overlap")] [InlineData("coverage-unknown")]
    public void PublicationPreparationDoesNotAttributeOtherFirstGuardsToAdmission(string reason)
    {
        using var state = new State(reason == "readonly" ? HostPageProtection.ReadOnly : HostPageProtection.ReadWrite);
        using var other = new State(); var local = state.Capture();
        var original = reason == "foreign" ? other.Capture() : local;
        if (reason == "mapping") Assert.True(state.Host.Effect(State.Address, State.Page));
        if (reason == "history-overlap") Assert.True(state.Views.TryWriteBacking(State.Address, new byte[] { 5 }));
        if (reason == "coverage-unknown") { var failed = BeginValidationWriter(state.Backing, State.Page, 1); EndValidationWriter(failed, false); }
        var bytes = state.Bytes(); var admission = PreparationAdmission(state);
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Views.TryPrepareMappedPublication(original, reason == "empty-runs" ? [] : [new(State.Address, new byte[] { 7 })],
                out var ticket, out var refusal));
        Assert.Null(ticket); Assert.Equal(default, refusal); Assert.Equal(bytes, state.Bytes());
        Assert.Equal(admission, PreparationAdmission(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PublicationPreparedAndCancelPreserveOriginalNoBytesAndKnownCleanup(bool watched)
    {
        using var state = new State(); var original = PreparationCapture(state, watched); var bytes = state.Bytes();
        try
        {
            Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                state.Views.TryPrepareMappedPublication(original, [new(State.Address, new byte[] { 7 })], out var ticket, out var refusal));
            using (ticket)
            {
                Assert.NotNull(ticket); Assert.Equal(default, refusal); Assert.Equal(1L << 32, PreparationAdmission(state));
                Assert.Equal(1, state.Backing.ActiveAccesses); Assert.Equal(bytes, state.Bytes());
            }
            Assert.Equal(0, PreparationAdmission(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
            Assert.Equal(bytes, state.Bytes()); Assert.True(state.Views.IsMappedCopyObservationCurrent(original));
        }
        finally { if (watched) state.Host.Ledger.ReleaseMappedCopyWriteWatch(original); }
    }

    private sealed class LegacyPreparationProvider : ICpuMappedPublicationSource, IHostMappedPublicationMemory
    {
        internal int Calls;
        internal CpuMappedCopyObservation? Original;
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(CpuMappedCopyObservation original,
            IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication)
        { Calls++; Original = original; publication = null; return CpuMappedPublicationPreparationResult.Rejected; }
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(HostBackingObject backing,
            CpuMappedCopyObservation original, IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication)
            => TryPrepareMappedPublication(original, runs, out publication);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LegacyPublicationPreparationCallsOriginalOnceAndRemainsUnknown(bool host)
    {
        using var state = new State(); var original = state.Capture(); var provider = new LegacyPreparationProvider();
        CpuMappedPublicationPreparationRefusal refusal; CpuMappedPublication? ticket;
        var result = host
            ? ((IHostMappedPublicationMemory)provider).TryPrepareMappedPublication(state.Backing, original,
                [new(State.Address, new byte[] { 7 })], out ticket, out refusal)
            : ((ICpuMappedPublicationSource)provider).TryPrepareMappedPublication(original,
                [new(State.Address, new byte[] { 7 })], out ticket, out refusal);
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected, result); Assert.Null(ticket);
        Assert.Equal(default, refusal); Assert.Equal(1, provider.Calls); Assert.Same(original, provider.Original);
        Assert.Equal(0, state.Backing.ActiveAccesses);
    }
}
