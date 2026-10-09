// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

public sealed unsafe partial class MappedCopyReadWatchTests
{
    private static CpuMappedPublicationValidation PrepareLocalDisjointValidation(State state, CpuMappedCopyObservation original)
    {
        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, original, out var receipt));
        return Assert.IsType<CpuMappedPublicationValidation>(receipt);
    }
    private static object LocalDiagnosticWatch(CpuMappedCopyObservation original)
    {
        var capture = typeof(CpuMappedCopyObservation).GetProperty("OwnerIdentity", OwnerFields)!.GetValue(original)!;
        return DiagnosticField(DiagnosticField(capture, "WatchOwnership"), "Watch");
    }

    // Actual managed provider reservations. Negative field-count injection is
    // metadata-only; it supplies no receipt, bytes or currentness authority.
    [Theory]
    [InlineData("disjoint")] [InlineData("physical-overlap")] [InlineData("watch-released")]
    [InlineData("watch-revoked")] [InlineData("watched-aba")] [InlineData("watch-missing")]
    [InlineData("cancelled")] [InlineData("physical-count")] [InlineData("unknown-alias-and-released")]
    [InlineData("closing-and-released")] [InlineData("coverage-incomplete")]
    public void ActualLocalValidationDiagnosticPreservesFirstLowerPredicateAndFullBytes(string kind)
    {
        using var state = new State(); EnableOwnerDiagnostics(state);
        var watch = state.Capture();
        if (kind == "watch-revoked") state.End(state.Begin(32, 1));
        if (kind == "watched-aba") state.Map(State.Address, 0);
        var original = PublicationOriginal(state, kind == "physical-overlap" ? State.Address + 32 : State.Address + State.Page, 32);
        if (kind == "watch-missing")
        {
            state.Ledger.ReleaseMappedCopyWriteWatch(watch); // retire the actual enrolled watch before substituting plain metadata
            watch = PublicationOriginal(state, State.Address + 32, 16);
        }
        var before = new ReadOnlySpan<byte>((void*)state.Backing.AliasBase, checked((int)state.Backing.Size)).ToArray();
        using var receipt = PrepareLocalDisjointValidation(state, original);
        var transaction = DiagnosticField(receipt, "transaction");
        var countField = transaction.GetType().GetField("PhysicalCount", OwnerFields)!;
        var savedCount = (int)countField.GetValue(transaction)!;
        CpuMappedDisjointReadWatchDiagnostic first;
        try
        {
            if (kind is "watch-released" or "unknown-alias-and-released" or "closing-and-released")
                state.Ledger.ReleaseMappedCopyWriteWatch(watch);
            if (kind == "cancelled") receipt.Dispose();
            if (kind == "physical-count") countField.SetValue(transaction, 0);
            if (kind == "unknown-alias-and-released") lock (state.Gate) state.Ledger.QuarantineUnknownAlias();
            if (kind == "closing-and-released") state.Backing.CloseAccess();
            if (kind == "coverage-incomplete")
            {
                var writers = typeof(HostBackingObject).GetProperty("ManagedWrites", OwnerFields)!.GetValue(state.Backing)!;
                writers.GetType().GetMethod("QuarantinePublication", OwnerFields)!.Invoke(writers, null);
            }
            var actual = receipt.IsOriginalDisjointReadWatchCurrent(watch, out first);
            Assert.Equal(receipt.IsOriginalDisjointReadWatchCurrent(watch), actual); // stable control, never changes first sample
            Assert.Equal(kind == "disjoint", actual); Assert.Equal(actual, first.BooleanCurrent);
            Assert.Equal(OwnerIdentity(ReceiptOwner(receipt)), first.LocalAdmissionOwnerIdentity);
            Assert.Equal(state.Backing.Identity, first.ReservedBackingIdentity);
            Assert.Equal(kind switch
            {
                "disjoint" => "current", "physical-overlap" => "physical-overlap",
                "cancelled" => "validation-consumed", "physical-count" => "validation-physical-count",
                "unknown-alias-and-released" or "closing-and-released" => "reservation-mapping-initial",
                "watched-aba" => "watched-mapping-initial", "watch-missing" => "watched-watch-missing",
                _ => "watched-watch-initial",
            }, first.Guard);
            Assert.Equal(kind switch
            {
                "unknown-alias-and-released" => "ledger-unknown-alias", "closing-and-released" => "backing-closing",
                "watched-aba" => "view-revoked", _ => null,
            }, first.MappingGuard);
            Assert.Equal(kind switch
            {
                "watch-released" => "released", "watch-revoked" => "revoked",
                "coverage-incomplete" => "coverage-incomplete", _ => null,
            }, first.WatchGuard);
            Assert.Null(first.PrimaryAdmission); // no failing admission predicate was reached
            if (kind == "physical-overlap")
            {
                Assert.Equal(32UL, first.WatchedOffset); Assert.Equal(16UL, first.WatchedCount);
                Assert.Equal(32UL, first.ReservedOffset); Assert.Equal(32UL, first.ReservedCount);
                Assert.Equal(state.Backing.Identity, first.WatchedBackingIdentity);
            }
            else
            { Assert.Null(first.WatchedOffset); Assert.Null(first.WatchedCount); Assert.Null(first.ReservedOffset); Assert.Null(first.ReservedCount); }
            Assert.Equal(before, new ReadOnlySpan<byte>((void*)state.Backing.AliasBase, checked((int)state.Backing.Size)).ToArray());
        }
        finally
        {
            countField.SetValue(transaction, savedCount); // restore only synthetic count; exact receipt cancellation unchanged
            receipt.Dispose();
            state.Ledger.ReleaseMappedCopyWriteWatch(watch);
        }
        Assert.Equal(0, state.Backing.ActiveAccesses);
        if (kind == "watch-released")
        {
            Assert.Equal("released", first.WatchGuard); Assert.False(first.BooleanCurrent);
            Assert.Equal(CpuMappedReadWatchStatus.Stale, state.Ledger.GetMappedCopyWriteWatchStatus(state.Backing, watch));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LocalValidationFirstDiagnosticDistinguishesForeignLedgerAndSameLedgerOtherBacking(bool sameLedger)
    {
        using var state = new State(); using var other = new State(); EnableOwnerDiagnostics(state);
        CpuMappedCopyObservation watch;
        if (sameLedger)
        {
            var address = State.Address + State.Page * 2;
            lock (state.Gate)
            {
                state.Ledger.RegisterBacking(other.Backing);
                var view = state.Ledger.PrepareView(other.Backing, address, State.Page, 0, HostPageProtection.ReadWrite);
                Assert.NotNull(view); Assert.True(state.Ledger.BeforeNativeEffect(address, State.Page)); state.Ledger.PublishView(view);
            }
            Assert.Equal(CpuMappedCopyResult.Copied, state.Ledger.TryReadMappedCopyWithWriteWatch(other.Backing,
                address, State.Page, address + 32, new byte[16], out var captured));
            watch = Assert.IsType<CpuMappedCopyObservation>(captured);
        }
        else watch = other.Capture();
        var original = PublicationOriginal(state, State.Address + State.Page, 32);
        using var receipt = PrepareLocalDisjointValidation(state, original);
        try
        {
            Assert.False(receipt.IsOriginalDisjointReadWatchCurrent(watch, out var first));
            Assert.Equal("watched-mapping-initial", first.Guard);
            Assert.Equal(sameLedger ? "view-backing" : "capture-ledger", first.MappingGuard);
            Assert.Equal(state.Backing.Identity, first.ReservedBackingIdentity);
            Assert.Equal(other.Backing.Identity, first.WatchedBackingIdentity);
            Assert.Null(first.WatchGuard); Assert.Null(first.PrimaryAdmission);
            Assert.Null(first.WatchedOffset); Assert.Null(first.ReservedOffset);
            Assert.False(receipt.IsOriginalDisjointReadWatchCurrent(watch));
        }
        finally
        {
            receipt.Dispose();
            (sameLedger ? state.Ledger : other.Ledger).ReleaseMappedCopyWriteWatch(watch);
        }
        Assert.Equal(0, state.Backing.ActiveAccesses); Assert.Equal(0, other.Backing.ActiveAccesses);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DisabledLocalValidationDiagnosticPerformsTheSameOriginalBooleanCheck(bool overlap)
    {
        using var state = new State(); EnableOwnerDiagnostics(state, false); var watch = state.Capture();
        var original = PublicationOriginal(state, overlap ? State.Address + 32 : State.Address + State.Page, 32);
        using var receipt = PrepareLocalDisjointValidation(state, original);
        try
        {
            Assert.Equal(!overlap, receipt.IsOriginalDisjointReadWatchCurrent(watch, out var first));
            Assert.Equal(!overlap, first.BooleanCurrent); Assert.Equal("diagnostics-disabled", first.Guard);
            Assert.Null(first.LocalAdmissionOwnerIdentity); Assert.Null(first.ReservedBackingIdentity);
            Assert.Null(first.WatchedBackingIdentity); Assert.Null(first.MappingGuard); Assert.Null(first.WatchGuard);
            Assert.Null(first.WatchedOffset); Assert.Null(first.ReservedOffset);
        }
        finally { receipt.Dispose(); state.Ledger.ReleaseMappedCopyWriteWatch(watch); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LowerHeldWatchDiagnosticReportsTheActualZeroAdmissionAfterKnownCancellation(bool foreignWriter)
    {
        using var state = new State(); using var other = new State(); EnableOwnerDiagnostics(state);
        var watch = state.Capture(); var original = PublicationOriginal(state, State.Address + State.Page, 32);
        using var receipt = PrepareLocalDisjointValidation(state, original); receipt.Dispose();
        var otherWatch = other.Capture(); // enroll the actual other writer ledger, no forged token
        var watched = LocalDiagnosticWatch(watch);
        var writers = typeof(HostBackingObject).GetProperty("ManagedWrites", OwnerFields)!.GetValue((foreignWriter ? other : state).Backing)!;
        object?[] args = [writers, null, -1L];
        Assert.False((bool)watched.GetType().GetMethod("TryIsUnrevokedDuringPublication", OwnerFields)!.Invoke(watched, args)!);
        Assert.Equal(foreignWriter ? "writer-ledger" : "admission-not-exclusive", args[1]);
        Assert.Equal(foreignWriter ? -1L : 0L, args[2]);
        Assert.True(state.Current(watch)); state.Ledger.ReleaseMappedCopyWriteWatch(watch);
        other.Ledger.ReleaseMappedCopyWriteWatch(otherWatch);
    }
}
