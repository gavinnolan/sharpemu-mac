// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

public sealed partial class MappedPublicationTests
{
    [Fact]
    public void OwnOrdinaryWriterAdmissionDoesNotChangeOriginalIdentity()
    {
        using var state = new State(); var original = state.Capture();
        using var publication = state.Prepare(original, State.Address, [7]);
        using var reservation = Busy(state); publication.Dispose();
        Assert.Equal(OrdinaryBackingWriteAdmission.Admitted, reservation.TryAcquire(TimeSpan.Zero));
        Assert.True(reservation.IsOriginalCurrent);
        Assert.True(reservation.TryIsOriginalCurrent(out var reason));
        Assert.Null(reason.Reason); Assert.Null(reason.Provider.Reason);
        Assert.All(state.Bytes(), value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void DisjointSharedViewChangeIsLabeledSnapshotReplacementWithoutProviderResample()
    {
        using var state = new State(); var original = state.Capture();
        using var publication = state.Prepare(original, State.Address, [7]);
        using var reservation = Busy(state); publication.Dispose();
        Assert.Equal(OrdinaryBackingWriteAdmission.Admitted, reservation.TryAcquire(TimeSpan.Zero));
        Assert.True(state.Views.Unmap(State.Address + State.Page, State.Page, out _));
        Assert.False(reservation.TryIsOriginalCurrent(out var reason));
        Assert.Equal("original-shared-snapshot-replaced", reason.Reason);
        Assert.Null(reason.Provider.Reason); Assert.Null(reason.Provider.ComparedIncarnation);
        Assert.True(state.Host.Ledger.IsCurrent(state.Backing, original));
        Assert.False(reservation.IsOriginalCurrent); // diagnosis never grants disjointness authority
        Assert.Throws<InvalidOperationException>(() => reservation.BeginWriteAttempt());
    }

    [Fact]
    public void RawProviderEffectLabelsOriginalRevocationWithUnchangedSharedSnapshot()
    {
        using var state = new State(); var original = state.Capture();
        using var publication = state.Prepare(original, State.Address, [7]);
        using var reservation = Busy(state); publication.Dispose();
        Assert.Equal(OrdinaryBackingWriteAdmission.Admitted, reservation.TryAcquire(TimeSpan.Zero));
        var snapshot = state.Views.AliasSnapshot;
        Assert.True(state.Host.Effect(State.Address, State.Page));
        Assert.Same(snapshot, state.Views.AliasSnapshot);
        Assert.False(reservation.TryIsOriginalCurrent(out var reason));
        Assert.Equal("original-provider-identity-changed", reason.Reason);
        Assert.Equal("original-view-revoked", reason.Provider.Reason);
        Assert.Equal(State.Address, reason.Provider.ViewAddress);
        Assert.Null(reason.Provider.ComparedIncarnation); // incarnation predicate was not reached
        Assert.Throws<InvalidOperationException>(() => reservation.BeginWriteAttempt());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualDerivedProtectionDistinguishesUnavailableFromChangedIncarnation(bool complete)
    {
        using var state = new State(); var original = state.Capture();
        using var publication = state.Prepare(original, State.Address, [7]);
        using var reservation = Busy(state); publication.Dispose();
        Assert.Equal(OrdinaryBackingWriteAdmission.Admitted, reservation.TryAcquire(TimeSpan.Zero));
        var gate = typeof(FakeProvider).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state.Host)!;
        HostMappedCopyLedger.DerivedProtectionReceipt receipt;
        lock (gate)
        {
            Assert.True(state.Host.Ledger.BeginDerivedProtection(State.Address, State.Page,
                GuestPageProtection.Read | GuestPageProtection.Write, HostPageProtection.ReadOnly, out receipt));
            if (complete) state.Host.Ledger.CompleteDerivedProtection(receipt, true);
        }
        try
        {
            Assert.False(reservation.TryIsOriginalCurrent(out var reason));
            Assert.Equal(complete ? "original-view-incarnation-changed" : "original-view-copy-unavailable", reason.Provider.Reason);
            if (complete)
            {
                Assert.True(reason.Provider.ComparedIncarnation.HasValue);
                Assert.NotEqual(reason.Provider.OriginalIncarnation, reason.Provider.ComparedIncarnation.Value);
            }
            else Assert.Null(reason.Provider.ComparedIncarnation);
            Assert.False(reservation.IsOriginalCurrent);
            Assert.Throws<InvalidOperationException>(() => reservation.BeginWriteAttempt());
            Assert.All(state.Bytes(), value => Assert.Equal((byte)0, value));
        }
        finally { if (!complete) lock (gate) state.Host.Ledger.CompleteDerivedProtection(receipt, true); }
        // Real ledger transitions; native syscall outcome explicitly synthetic.
    }

    [Fact]
    public void UnknownProviderWinsBeforeRevokedViewWithoutBorrowingLaterState()
    {
        using var state = new State(); var original = state.Capture();
        using var publication = state.Prepare(original, State.Address, [7]);
        using var reservation = Busy(state); publication.Dispose();
        Assert.Equal(OrdinaryBackingWriteAdmission.Admitted, reservation.TryAcquire(TimeSpan.Zero));
        Assert.True(state.Host.Effect(State.Address, State.Page)); state.Host.Unknown();
        Assert.False(reservation.TryIsOriginalCurrent(out var reason));
        Assert.Equal("provider-unknown-alias", reason.Provider.Reason);
        Assert.Equal(-1, reason.Provider.ViewIndex); Assert.Null(reason.Provider.ComparedIncarnation);
        Assert.False(reservation.IsOriginalCurrent);
        Assert.Throws<InvalidOperationException>(() => reservation.BeginWriteAttempt());
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("revoked")]
    [InlineData("unknown")]
    public void AcquisitionSavesFirstOriginalChangedPredicateBeforeWriterAdmission(string change)
    {
        using var state = new State(); var original = state.Capture();
        using var publication = state.Prepare(original, State.Address, [7]);
        using var reservation = Busy(state); publication.Dispose();
        if (change == "snapshot") Assert.True(state.Views.Unmap(State.Address + State.Page, State.Page, out _));
        else if (change == "revoked") Assert.True(state.Host.Effect(State.Address, State.Page));
        else state.Host.Unknown();
        Assert.Equal(OrdinaryBackingWriteAdmission.OriginalChanged,
            reservation.TryAcquire(TimeSpan.Zero, out var first));
        Assert.Equal(change == "snapshot" ? "original-shared-snapshot-replaced" :
            "original-provider-identity-changed", first.Reason);
        Assert.Equal(change switch { "snapshot" => null, "revoked" => "original-view-revoked",
            _ => "provider-unknown-alias" }, first.Provider.Reason);
        Assert.Null(first.Provider.ComparedIncarnation);
        // A younger provider effect cannot replace this first detached reason.
        state.Host.Unknown();
        Assert.Equal(change switch { "snapshot" => null, "revoked" => "original-view-revoked",
            _ => "provider-unknown-alias" }, first.Provider.Reason);
        Assert.Throws<InvalidOperationException>(() => reservation.BeginWriteAttempt());
        Assert.Throws<InvalidOperationException>(() => reservation.TryAcquire(TimeSpan.Zero));
        Assert.All(state.Bytes(), value => Assert.Equal((byte)0, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcquisitionAttributionPreservesAdmittedAndTimedOutDecisions(bool release)
    {
        using var state = new State(); var original = state.Capture();
        using var publication = state.Prepare(original, State.Address, [7]);
        using var reservation = Busy(state);
        if (release) publication.Dispose();
        Assert.Equal(release ? OrdinaryBackingWriteAdmission.Admitted : OrdinaryBackingWriteAdmission.TimedOut,
            reservation.TryAcquire(TimeSpan.Zero, out var first));
        Assert.Null(first.Reason); Assert.Null(first.Provider.Reason);
        Assert.Throws<InvalidOperationException>(() => reservation.TryAcquire(TimeSpan.Zero));
        Assert.All(state.Bytes(), value => Assert.Equal((byte)0, value));
    }

}
