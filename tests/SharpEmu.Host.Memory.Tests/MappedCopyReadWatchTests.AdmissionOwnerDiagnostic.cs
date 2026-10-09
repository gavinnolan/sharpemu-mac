// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

public sealed unsafe partial class MappedCopyReadWatchTests
{
    private const BindingFlags OwnerFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object DiagnosticField(object value, string name) => value.GetType().GetField(name, OwnerFields)!.GetValue(value)!;
    private static object ReceiptOwner(object receipt) => DiagnosticField(DiagnosticField(receipt, "transaction"), "Admission");
    private static void EnableOwnerDiagnostics(State state, bool enabled = true)
    {
        typeof(HostBackingObject).GetMethod("EnrollManagedWrites", OwnerFields)!.Invoke(state.Backing, null);
        var writers = typeof(HostBackingObject).GetProperty("ManagedWrites", OwnerFields)!.GetValue(state.Backing)!;
        writers.GetType().GetField("admissionDiagnosticsForControl", OwnerFields)!.SetValue(writers, enabled ? 1 : 0);
    }
    private static int OwnerScalar(object owner, string name) => (int)DiagnosticField(owner, name);
    private static long OwnerIdentity(object owner) => (long)DiagnosticField(owner, "Identity");

    [Theory]
    [InlineData("publication")] [InlineData("validation")] [InlineData("original-read")]
    public void DisabledDiagnosticsNeverBackfillAnExistingExclusiveOwner(string kind)
    {
        using var state = new State(); EnableOwnerDiagnostics(state, false); var watch = state.Capture();
        var source = kind == "original-read" ? watch : PublicationOriginal(state, State.Address + State.Page, 32);
        IDisposable receipt;
        if (kind == "publication") receipt = Prepare(state, source, source.SourceAddress);
        else if (kind == "validation")
        {
            Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, source, out var exact));
            receipt = Assert.IsType<CpuMappedPublicationValidation>(exact);
        }
        else
        {
            Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                state.Ledger.TryPrepareMappedReadValidation(state.Backing, source, out var exact));
            receipt = Assert.IsType<CpuMappedReadValidation>(exact);
        }
        try
        {
            var transaction = DiagnosticField(receipt, "transaction");
            Assert.Null(transaction.GetType().GetField("Admission", OwnerFields)!.GetValue(transaction));
            Assert.Equal(CpuMappedReadWatchStatus.Stale, state.Ledger.GetMappedCopyWriteWatchStatus(state.Backing, watch, out var first));
            Assert.Equal("owner-gap", first.AdmissionOwner!.Value.Refusal); Assert.Null(first.AdmissionOwner.Value.Identity);
            EnableOwnerDiagnostics(state); // metadata enablement cannot recreate this owner's record
            Assert.Equal(CpuMappedReadWatchStatus.Stale, state.Ledger.GetMappedCopyWriteWatchStatus(state.Backing, watch, out var later));
            Assert.Equal("owner-gap", later.AdmissionOwner!.Value.Refusal); Assert.Null(later.AdmissionOwner.Value.Identity);
            Assert.Equal(1L << 32, first.PrimaryAdmission); Assert.False(state.Current(watch));
        }
        finally { receipt.Dispose(); }
        Assert.True(state.Current(watch)); Assert.Equal(0, state.Backing.ActiveAccesses);
        state.Ledger.ReleaseMappedCopyWriteWatch(watch);
    }

    [Theory]
    [InlineData("publication")] [InlineData("validation")] [InlineData("original-read")]
    public void ActualPreparedOwnerMetadataNeverChangesFirstStaleOrBytes(string kind)
    {
        using var state = new State(); EnableOwnerDiagnostics(state); var watch = state.Capture();
        var source = kind == "original-read" ? watch : PublicationOriginal(state, State.Address + State.Page, 32);
        var bytes = new ReadOnlySpan<byte>((void*)state.Backing.AliasBase, checked((int)state.Backing.Size)).ToArray();
        IDisposable receipt;
        var expected = kind switch
        {
            "publication" => CpuMappedAdmissionOwnerKind.Publication,
            "validation" => CpuMappedAdmissionOwnerKind.PublicationValidation,
            _ => CpuMappedAdmissionOwnerKind.OriginalReadValidation,
        };
        if (kind == "publication") receipt = Prepare(state, source, source.SourceAddress);
        else if (kind == "validation")
        {
            Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, source, out var exact));
            receipt = Assert.IsType<CpuMappedPublicationValidation>(exact);
        }
        else
        {
            Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                state.Ledger.TryPrepareMappedReadValidation(state.Backing, source, out var exact));
            receipt = Assert.IsType<CpuMappedReadValidation>(exact);
        }
        var owner = ReceiptOwner(receipt);
        CpuMappedReadWatchDiagnostic first;
        try
        {
            Assert.Equal(CpuMappedReadWatchStatus.Stale,
                state.Ledger.GetMappedCopyWriteWatchStatus(state.Backing, watch, out first));
            Assert.Equal(1L << 32, first.PrimaryAdmission); Assert.Equal("admission-nonzero", first.WatchGuard);
            Assert.Equal("publication-admission", first.ClassificationGuard);
            var metadata = Assert.IsType<CpuMappedAdmissionOwnerDiagnostic>(first.AdmissionOwner);
            Assert.Equal(OwnerIdentity(owner), metadata.Identity); Assert.True(metadata.Identity > 0);
            Assert.Equal(expected, metadata.Kind); Assert.Equal(Environment.CurrentManagedThreadId, metadata.AcquiringThread);
            Assert.Equal(CpuMappedAdmissionOwnerPhase.Prepared, metadata.Phase);
            Assert.Equal(CpuMappedAdmissionOwnerOutcome.None, metadata.Outcome);
            Assert.Equal("diagnostic-only-no-wait-authority", metadata.Refusal);
            EnableOwnerDiagnostics(state, false); // existing exact record survives a diagnostic toggle
            Assert.False(state.Ledger.IsMappedCopyWriteWatchCurrent(state.Backing, watch, out var boolean));
            Assert.Equal(metadata, boolean.AdmissionOwner); Assert.False(boolean.BooleanCurrent);
            Assert.Null(boolean.WatchStatus); Assert.Null(boolean.ProviderStatus);
            Assert.Equal(bytes, new ReadOnlySpan<byte>((void*)state.Backing.AliasBase, checked((int)state.Backing.Size)).ToArray());
        }
        finally { receipt.Dispose(); }
        Assert.Equal((int)CpuMappedAdmissionOwnerOutcome.AdmissionReleased, OwnerScalar(owner, "Outcome"));
        Assert.Equal((int)CpuMappedAdmissionOwnerPhase.AdmissionReleased, OwnerScalar(owner, "Phase"));
        Assert.True(state.Current(watch)); Assert.Equal(CpuMappedReadWatchStatus.Stale, first.ProviderStatus);
        Assert.Equal(CpuMappedAdmissionOwnerOutcome.None, first.AdmissionOwner!.Value.Outcome); // detached, never refreshed
        receipt.Dispose(); Assert.True(state.Current(watch)); Assert.Equal(0, state.Backing.ActiveAccesses);
        Assert.Equal(bytes, new ReadOnlySpan<byte>((void*)state.Backing.AliasBase, checked((int)state.Backing.Size)).ToArray());
        state.Ledger.ReleaseMappedCopyWriteWatch(watch);
    }

    [Fact]
    public void ActualReadWatchEnrollmentRetainsOnlyItsReleasedDiagnosticRecord()
    {
        using var state = new State(); EnableOwnerDiagnostics(state); var watch = state.Capture();
        var capture = typeof(CpuMappedCopyObservation).GetProperty("OwnerIdentity", OwnerFields)!.GetValue(watch)!;
        var ownership = DiagnosticField(capture, "WatchOwnership");
        var enrolled = DiagnosticField(ownership, "Watch");
        var owner = DiagnosticField(enrolled, "EnrollmentOwner");
        Assert.Equal(CpuMappedAdmissionOwnerKind.ReadWatchEnrollment, DiagnosticField(owner, "Kind"));
        Assert.Equal(Environment.CurrentManagedThreadId, OwnerScalar(owner, "Thread"));
        Assert.True(OwnerIdentity(owner) > 0);
        Assert.Equal((int)CpuMappedAdmissionOwnerPhase.AdmissionReleased, OwnerScalar(owner, "Phase"));
        Assert.Equal((int)CpuMappedAdmissionOwnerOutcome.AdmissionReleased, OwnerScalar(owner, "Outcome"));
        Assert.Equal(CpuMappedReadWatchStatus.Current, state.Ledger.GetMappedCopyWriteWatchStatus(state.Backing, watch, out var first));
        Assert.Null(first.AdmissionOwner); Assert.Equal(0L, first.PrimaryAdmission);
        state.Ledger.ReleaseMappedCopyWriteWatch(watch);
    }

    [Theory]
    [InlineData("gap")] [InlineData("changed")] [InlineData("phase-changed")]
    [InlineData("missing-after")] [InlineData("ordinary")]
    public void DetachedBracketControlsReportUnknownAndNeverClaimWaitAuthority(string kind)
    {
        using var state = new State(); using var other = new State();
        EnableOwnerDiagnostics(state); EnableOwnerDiagnostics(other);
        var source = PublicationOriginal(state, State.Address + State.Page, 32);
        var otherSource = PublicationOriginal(other, State.Address + State.Page, 32);
        using var receipt = Prepare(state, source, source.SourceAddress);
        using var otherReceipt = Prepare(other, otherSource, otherSource.SourceAddress);
        var owner = ReceiptOwner(receipt); var otherOwner = ReceiptOwner(otherReceipt);
        Assert.NotEqual(OwnerIdentity(owner), OwnerIdentity(otherOwner));
        var method = owner.GetType().DeclaringType!.GetMethod("FinishAdmissionOwnerSample", BindingFlags.Static | BindingFlags.NonPublic)!;
        // Negative bracket inputs only. Real installed records and admission
        // are untouched; this pure reporting helper has no authority consumer.
        var first = (CpuMappedAdmissionOwnerDiagnostic)method.Invoke(null,
            [kind == "ordinary" ? 1L : 1L << 32, kind == "gap" ? null : owner,
             kind == "phase-changed" ? -1 : OwnerScalar(owner, "Phase"), OwnerScalar(owner, "Outcome"),
             kind == "changed" ? otherOwner : kind == "missing-after" ? null : owner])!;
        Assert.Equal(kind switch
        {
            "gap" or "missing-after" => "owner-gap", "changed" => "owner-changed",
            "phase-changed" => "owner-phase-changed", _ => "not-exclusive-admission",
        }, first.Refusal);
        Assert.NotEqual("diagnostic-only-no-wait-authority", first.Refusal);
        Assert.Equal((int)CpuMappedAdmissionOwnerPhase.Prepared, OwnerScalar(owner, "Phase"));
        Assert.Equal((int)CpuMappedAdmissionOwnerPhase.Prepared, OwnerScalar(otherOwner, "Phase"));
    }

    [Fact]
    public void AdmissionABAUsesDistinctOwnerAndCannotRewriteDetachedFirstSample()
    {
        using var state = new State(); EnableOwnerDiagnostics(state); var watch = state.Capture();
        var source = PublicationOriginal(state, State.Address + State.Page, 32);
        using var firstReceipt = Prepare(state, source, source.SourceAddress);
        Assert.Equal(CpuMappedReadWatchStatus.Stale, state.Ledger.GetMappedCopyWriteWatchStatus(state.Backing, watch, out var first));
        firstReceipt.Dispose();
        using var secondReceipt = Prepare(state, source, source.SourceAddress);
        Assert.Equal(CpuMappedReadWatchStatus.Stale, state.Ledger.GetMappedCopyWriteWatchStatus(state.Backing, watch, out var second));
        Assert.NotEqual(first.AdmissionOwner!.Value.Identity, second.AdmissionOwner!.Value.Identity);
        Assert.Equal(CpuMappedAdmissionOwnerOutcome.None, first.AdmissionOwner.Value.Outcome);
        Assert.False(firstReceipt.IsOriginalDisjointReadWatchCurrent(watch));
        Assert.True(secondReceipt.IsOriginalDisjointReadWatchCurrent(watch));
        secondReceipt.Dispose(); state.Ledger.ReleaseMappedCopyWriteWatch(watch);
    }

    [Theory]
    [InlineData("released")] [InlineData("revoked")] [InlineData("mapping-aba")]
    public void EarlierOriginalVetoNeverReportsLaterAdmissionOwner(string kind)
    {
        using var state = new State(); EnableOwnerDiagnostics(state); var watch = state.Capture();
        if (kind == "released") state.Ledger.ReleaseMappedCopyWriteWatch(watch);
        if (kind == "revoked") state.End(state.Begin(32, 1));
        if (kind == "mapping-aba") state.Map(State.Address, 0);
        var source = PublicationOriginal(state, State.Address + State.Page, 32);
        using var receipt = Prepare(state, source, source.SourceAddress);
        Assert.Equal(CpuMappedReadWatchStatus.Stale, state.Ledger.GetMappedCopyWriteWatchStatus(state.Backing, watch, out var first));
        Assert.Null(first.PrimaryAdmission); Assert.Null(first.AdmissionOwner);
        Assert.Equal(kind == "mapping-aba" ? "view-revoked" : null, first.MappingGuard);
        Assert.Equal(kind switch { "released" => "released", "revoked" => "revoked", _ => null }, first.WatchGuard);
        receipt.Dispose(); state.Ledger.ReleaseMappedCopyWriteWatch(watch);
    }

    // Fault injection removes only provider linkage. Once exact cancellation
    // becomes unknown, no forced release, restoration, retry or teardown occurs.
    // Strong roots deliberately retain the real backing, pin and consumed ticket.
    private static readonly List<(State State, IDisposable Receipt, CpuMappedCopyObservation Watch)> unknownDiagnosticOwners = new();
    [Theory]
    [InlineData("publication")] [InlineData("validation")] [InlineData("original-read")]
    public void ActualLostReservationLinkReportsUnknownAndKeepsExactExclusion(string kind)
    {
        var state = new State(); EnableOwnerDiagnostics(state); var watch = state.Capture(); IDisposable? receipt = null;
        var retained = false;
        try
        {
            var source = kind == "original-read" ? watch : PublicationOriginal(state, State.Address + State.Page, 32);
            if (kind == "publication") receipt = Prepare(state, source, source.SourceAddress);
            else if (kind == "validation")
            {
                Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                    state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, source, out var exact));
                receipt = Assert.IsType<CpuMappedPublicationValidation>(exact);
            }
            else
            {
                Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                    state.Ledger.TryPrepareMappedReadValidation(state.Backing, source, out var exact));
                receipt = Assert.IsType<CpuMappedReadValidation>(exact);
            }
            var beforeBytes = new ReadOnlySpan<byte>((void*)state.Backing.AliasBase, checked((int)state.Backing.Size)).ToArray();
            var beforePins = state.Backing.ActiveAccesses;
            var owner = ReceiptOwner(receipt);
            lock (unknownDiagnosticOwners) unknownDiagnosticOwners.Add((state, receipt, watch));
            retained = true; // BEFORE removing the real link; failure also stays rooted
            lock (state.Gate) typeof(HostMappedCopyLedger).GetField(kind switch
            { "publication" => "publications", "validation" => "validations", _ => "readValidations" }, OwnerFields)!.SetValue(state.Ledger, null);
            Assert.Throws<InvalidOperationException>(() => receipt.Dispose());
            Assert.Equal(beforePins, state.Backing.ActiveAccesses);
            Assert.Equal(CpuMappedReadWatchStatus.Stale, state.Ledger.GetMappedCopyWriteWatchStatus(state.Backing, watch, out var first));
            var metadata = Assert.IsType<CpuMappedAdmissionOwnerDiagnostic>(first.AdmissionOwner);
            Assert.Equal(OwnerIdentity(owner), metadata.Identity); Assert.Equal("owner-unknown", metadata.Refusal);
            Assert.Equal(CpuMappedAdmissionOwnerPhase.Unknown, metadata.Phase);
            Assert.Equal(CpuMappedAdmissionOwnerOutcome.Unknown, metadata.Outcome);
            Assert.Equal(1L << 32, first.PrimaryAdmission); Assert.False(state.Current(watch));
            Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
                State.Address, State.Page, State.Address + 32, new byte[16], out var missing));
            Assert.Null(missing); Assert.Equal(beforePins, state.Backing.ActiveAccesses);
            Assert.Equal(beforeBytes, new ReadOnlySpan<byte>((void*)state.Backing.AliasBase, checked((int)state.Backing.Size)).ToArray());
        }
        finally { if (!retained) { receipt?.Dispose(); state.Dispose(); } }
    }
}
