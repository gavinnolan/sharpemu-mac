// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

// Real ledger reservations over pinned managed backing; no native syscall claim.
public sealed unsafe partial class MappedCopyReadWatchTests
{
    private static CpuMappedCopyObservation PublicationOriginal(State state, ulong source, int length)
    {
        var page = source & ~(State.Page - 1);
        Assert.Equal(CpuMappedCopyResult.Copied, state.Ledger.TryReadMappedCopy(state.Backing,
            page, State.Page, source, new byte[length], out var original));
        return Assert.IsType<CpuMappedCopyObservation>(original);
    }
    private static CpuMappedPublication Prepare(State state, CpuMappedCopyObservation original, ulong address, int length = 4)
    {
        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
            state.Ledger.TryPrepareMappedPublication(state.Backing, original,
                [new(address, Enumerable.Repeat((byte)0x71, length).ToArray())], out var publication));
        return Assert.IsType<CpuMappedPublication>(publication);
    }

    [Fact]
    public void PreparedDisjointTicketKeepsOnlyOriginalWatchWhileOrdinaryReadsStayStrict()
    {
        using var state = new State(); var watch = state.Capture();
        var original = PublicationOriginal(state, State.Address + State.Page, 32);
        using var publication = Prepare(state, original, original.SourceAddress);
        Assert.False(state.Current(watch));
        lock (state.Gate) Assert.True(publication.IsOriginalDisjointReadWatchCurrent(watch)); // scalar, no nested gate
        Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
            State.Address, State.Page, State.Address + 32, new byte[16], out var newWatch));
        Assert.Null(newWatch);
        Assert.Equal(CpuMappedPublicationResult.Applied, publication.TryCommit());
        Assert.False(publication.IsOriginalDisjointReadWatchCurrent(watch));
        Assert.True(state.Current(watch));
        Assert.Equal(new byte[] { 0x71, 0x71, 0x71, 0x71 }, new ReadOnlySpan<byte>((void*)(state.Backing.AliasBase + State.Page), 4).ToArray());
        state.Ledger.ReleaseMappedCopyWriteWatch(watch);
    }

    [Theory]
    [InlineData(31ul, true)] [InlineData(32ul, false)] [InlineData(47ul, false)] [InlineData(48ul, true)]
    public void ScopedCheckUsesExactPhysicalHalfOpenRuns(ulong offset, bool current)
    {
        using var state = new State(); var watch = state.Capture();
        var original = PublicationOriginal(state, State.Address, 128);
        using var publication = Prepare(state, original, State.Address + offset, 1);
        Assert.Equal(current, publication.IsOriginalDisjointReadWatchCurrent(watch));
        publication.Dispose(); Assert.True(state.Current(watch));
        state.Ledger.ReleaseMappedCopyWriteWatch(watch);
    }

    [Fact]
    public void DifferentVirtualAliasCannotHidePhysicalWatchOverlap()
    {
        using var state = new State(); state.Map(State.Address + State.Page, 0);
        var watch = state.Capture(); var original = PublicationOriginal(state, State.Address + State.Page, 128);
        using var publication = Prepare(state, original, State.Address + State.Page + 32);
        Assert.False(publication.IsOriginalDisjointReadWatchCurrent(watch));
        publication.Dispose(); state.Ledger.ReleaseMappedCopyWriteWatch(watch);
    }

    [Fact]
    public void CancelledOldTicketCannotBorrowDifferentLiveReservation()
    {
        using var state = new State(); var watch = state.Capture();
        var original = PublicationOriginal(state, State.Address + State.Page, 32);
        using var old = Prepare(state, original, original.SourceAddress);
        Assert.True(old.IsOriginalDisjointReadWatchCurrent(watch)); old.Dispose();
        using var next = Prepare(state, original, original.SourceAddress + 4);
        Assert.False(old.IsOriginalDisjointReadWatchCurrent(watch));
        Assert.True(next.IsOriginalDisjointReadWatchCurrent(watch));
        next.Dispose(); state.Ledger.ReleaseMappedCopyWriteWatch(watch);
    }

    [Fact]
    public void CancelOnlyTicketUsesOriginalResolvedSourcePrefixAndConsumesExactlyOnce()
    {
        using var state = new State(); var watch = state.Capture();
        var original = PublicationOriginal(state, State.Address + State.Page, 32);
        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, original, out var validation));
        using var exact = Assert.IsType<CpuMappedPublicationValidation>(validation);
        lock (state.Gate) Assert.True(exact.IsOriginalDisjointReadWatchCurrent(watch));
        Assert.False(state.Current(watch)); exact.Dispose();
        Assert.False(exact.IsOriginalDisjointReadWatchCurrent(watch)); Assert.True(state.Current(watch));
        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, original, out var replacement));
        using var next = Assert.IsType<CpuMappedPublicationValidation>(replacement);
        Assert.False(exact.IsOriginalDisjointReadWatchCurrent(watch)); Assert.True(next.IsOriginalDisjointReadWatchCurrent(watch));
        next.Dispose(); state.Ledger.ReleaseMappedCopyWriteWatch(watch);
    }

    [Fact]
    public void CancelOnlyFullSourceOverlapDeclinesEvenWhenNoPayloadExists()
    {
        using var state = new State(); var watch = state.Capture();
        var original = PublicationOriginal(state, State.Address, 128);
        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, original, out var validation));
        using var exact = Assert.IsType<CpuMappedPublicationValidation>(validation);
        Assert.False(exact.IsOriginalDisjointReadWatchCurrent(watch));
        exact.Dispose(); state.Ledger.ReleaseMappedCopyWriteWatch(watch);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void SyntheticNonpreparedStateCannotAuthorizeEvenWithOriginalExclusionHeld(int stateValue)
    {
        using var state = new State(); var watch = state.Capture();
        var original = PublicationOriginal(state, State.Address + State.Page, 32);
        using var publication = Prepare(state, original, original.SourceAddress);
        var transaction = typeof(CpuMappedPublication).GetField("transaction", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(publication)!;
        var phase = transaction.GetType().GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try { phase.SetValue(transaction, stateValue); Assert.False(publication.IsOriginalDisjointReadWatchCurrent(watch)); }
        finally { phase.SetValue(transaction, 0); } // synthetic phase only; exact ordinary cancel balances real pin
        publication.Dispose(); state.Ledger.ReleaseMappedCopyWriteWatch(watch);
    }

    [Fact]
    public void ForeignReleasedAndRevokedOriginalsNeverAcquireScopedAuthority()
    {
        using var state = new State(); using var foreign = new State();
        var watch = state.Capture(); var foreignWatch = foreign.Capture();
        var original = PublicationOriginal(state, State.Address + State.Page, 32);
        using var publication = Prepare(state, original, original.SourceAddress);
        Assert.False(publication.IsOriginalDisjointReadWatchCurrent(foreignWatch));
        state.Ledger.ReleaseMappedCopyWriteWatch(watch);
        Assert.False(publication.IsOriginalDisjointReadWatchCurrent(watch));
        publication.Dispose(); foreign.Ledger.ReleaseMappedCopyWriteWatch(foreignWatch);
        var revoked = state.Capture(); state.End(state.Begin(32, 1));
        original = PublicationOriginal(state, State.Address + State.Page, 32);
        using var second = Prepare(state, original, original.SourceAddress);
        Assert.False(second.IsOriginalDisjointReadWatchCurrent(revoked));
        second.Dispose(); state.Ledger.ReleaseMappedCopyWriteWatch(revoked);
    }

    [Fact]
    public void InterposedDisjointViewReplacementCannotReopenOldWatchDuringOwnReservation()
    {
        using var state = new State(); var watch = state.Capture();
        var original = PublicationOriginal(state, State.Address + State.Page, 32);
        using var publication = Prepare(state, original, original.SourceAddress);
        Assert.True(publication.IsOriginalDisjointReadWatchCurrent(watch));
        state.Map(State.Address, 0); // target reservation remains valid; watched ORIGINAL incarnation does not
        Assert.False(publication.IsOriginalDisjointReadWatchCurrent(watch));
        publication.Dispose(); Assert.False(state.Current(watch));
        state.Ledger.ReleaseMappedCopyWriteWatch(watch);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ClosingOrUnknownMappingCannotUsePreparedDisjointAuthority(bool unknown)
    {
        using var state = new State(); var watch = state.Capture();
        var original = PublicationOriginal(state, State.Address + State.Page, 32);
        using var publication = Prepare(state, original, original.SourceAddress);
        if (unknown) { lock (state.Gate) state.Ledger.QuarantineUnknownAlias(); }
        else state.Backing.CloseAccess();
        Assert.False(publication.IsOriginalDisjointReadWatchCurrent(watch));
        publication.Dispose(); state.Ledger.ReleaseMappedCopyWriteWatch(watch);
    }
}
