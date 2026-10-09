// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using System.Reflection;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

public sealed partial class MappedPublicationTests
{
    private sealed class UnknownValidationProvider : ICpuMappedPublicationSource
    {
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(CpuMappedCopyObservation original,
            IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication)
        { publication = null; return CpuMappedPublicationPreparationResult.Rejected; }
    }

    [Fact]
    public void UnknownValidationParticipantCannotClassifyOrWaitForBusy()
    {
        using var state = new State(); ICpuMappedPublicationSource legacy = new UnknownValidationProvider();
        Assert.Equal(CpuMappedPublicationPreparationResult.Unavailable,
            legacy.TryPrepareMappedPublicationValidation(state.Capture(), out var ticket, out var refusal));
        Assert.Null(ticket); Assert.Equal(CpuMappedPublicationValidationRefusalKind.UnknownRejected, refusal.Kind);
        Assert.False(legacy.CanWaitForMappedPublicationValidation);
    }

    [Fact]
    public void ValidationWaitCapabilityDeclinesInheritedActualProviderGate()
    {
        using var state = new State(); Assert.True(state.Views.CanWaitForMappedPublicationValidation);
        var gate = typeof(FakeProvider).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state.Host)!;
        lock (gate) Assert.False(state.Views.CanWaitForMappedPublicationValidation);
        Assert.True(state.Views.CanWaitForMappedPublicationValidation);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExactFailedValidationCasAfterAppliedNeverCertifiesLaterWriterHistory(bool overlap)
    {
        using var state = new State(); var original = state.Capture();
        using var publication = state.Prepare(original, State.Address, [13]);
        Assert.Equal(CpuMappedPublicationResult.Applied, publication.TryCommit());
        var successor = Assert.IsType<CpuMappedCopyObservation>(publication.SuccessfulObservation);
        var operation = state.Backing.BeginManagedWriteOperation();
        Assert.True(operation.Identity > 0);
        try
        {
            Assert.True(state.Views.TryWriteBacking(State.Address + (overlap ? 0ul : 32ul), new byte[] { 91 }));
            Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
                state.Views.TryPrepareMappedPublicationValidation(successor, out var validation, out var refusal));
            Assert.Null(validation);
            Assert.Equal(CpuMappedPublicationValidationRefusalKind.AdmissionBusy, refusal.Kind);
            Assert.Equal(1, refusal.RejectedAdmission); // exact outer operation still admitted
            Assert.True(state.Views.IsMappedCopyObservationCurrent(successor));
        }
        finally { HostBackingObject.EndManagedWrite(operation, true); }
        Assert.Equal(overlap ? CpuMappedPublicationPreparationResult.Rejected : CpuMappedPublicationPreparationResult.Prepared,
            state.Views.TryPrepareMappedPublicationValidation(successor, out var next, out var finalRefusal));
        using (next) { if (!overlap) Assert.True(next!.IsCurrent); }
        Assert.Equal(CpuMappedPublicationValidationRefusalKind.UnknownRejected, finalRefusal.Kind);
        Assert.Same(successor, publication.SuccessfulObservation); // no younger observation
        Assert.Equal(overlap ? 91 : 13, state.Bytes()[0]); Assert.Equal(overlap ? 0 : 91, state.Bytes()[32]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BusyValidationDoesNotCertifyExpiredOrUnknownUnwatchedHistory(bool unknown)
    {
        using var state = new State(); var original = state.Capture();
        if (unknown)
        {
            var failed = state.Backing.BeginManagedWrite(State.Page, 1);
            HostBackingObject.EndManagedWrite(failed, false); // real participating unknown completion
        }
        else for (var i = 0; i < 520; i++) Assert.True(state.Views.TryWriteBacking(State.Address + State.Page, new byte[] { 5 }));
        var operation = state.Backing.BeginManagedWriteOperation(); Assert.True(operation.Identity > 0);
        try
        {
            Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
                state.Views.TryPrepareMappedPublicationValidation(original, out var validation, out var refusal));
            Assert.Null(validation); Assert.Equal(CpuMappedPublicationValidationRefusalKind.AdmissionBusy, refusal.Kind);
        }
        finally { HostBackingObject.EndManagedWrite(operation, true); }
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Views.TryPrepareMappedPublicationValidation(original, out var rejected, out var finalRefusal));
        Assert.Null(rejected); Assert.Equal(CpuMappedPublicationValidationRefusalKind.UnknownRejected, finalRefusal.Kind);
    }

    [Theory]
    [InlineData("revoked")] [InlineData("unknown")] [InlineData("readonly")] [InlineData("foreign")]
    public void OriginalMetadataRefusalCannotBorrowAnAdmittedWriterBusy(string failure)
    {
        using var state = new State(failure == "readonly" ? HostPageProtection.ReadOnly : HostPageProtection.ReadWrite);
        using var other = new State(); var original = failure == "foreign" ? other.Capture() : state.Capture();
        if (failure == "revoked") Assert.True(state.Host.Effect(State.Address, State.Page));
        if (failure == "unknown") state.Host.Unknown();
        var operation = state.Backing.BeginManagedWriteOperation(); Assert.True(operation.Identity > 0);
        try
        {
            Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
                state.Views.TryPrepareMappedPublicationValidation(original, out var ticket, out var refusal));
            Assert.Null(ticket); Assert.Equal(CpuMappedPublicationValidationRefusalKind.UnknownRejected, refusal.Kind);
            Assert.Equal(0, refusal.RejectedAdmission);
        }
        finally { HostBackingObject.EndManagedWrite(operation, true); }
    }
}
