// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

// Actual ledger/watch enrollment and pinned managed bytes, no kernel receipts.
public sealed unsafe partial class MappedCopyReadWatchTests
{
    private static object CaptureOwner(CpuMappedCopyObservation original) => typeof(CpuMappedCopyObservation)
        .GetProperty("OwnerIdentity", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original)!;
    private static long OriginalRevision(CpuMappedCopyObservation original) => (long)CaptureOwner(original)
        .GetType().GetField("WriterRevision", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(CaptureOwner(original))!;
    private static long EnrollmentRevision(CpuMappedCopyObservation original)
    {
        var owner = CaptureOwner(original);
        var watch = owner.GetType().GetProperty("ReadWatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        return (long)watch.GetType().GetField("EnrollmentRevision", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watch)!;
    }
    private static void RevisionHook(State state, Action<bool, long>? hook) => typeof(HostMappedCopyLedger)
        .GetField("afterWriterRevisionForControl", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state.Ledger, hook);
    private static void Enroll(State state)
    {
        var initial = state.Capture(); state.Ledger.ReleaseMappedCopyWriteWatch(initial);
    }
    private static void ValidateOriginal(State state, CpuMappedCopyObservation original)
    {
        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, original, out var ticket));
        using var exact = Assert.IsType<CpuMappedPublicationValidation>(ticket);
        Assert.True(exact.IsCurrent);
    }

    [Fact]
    public void BusyFirstSampleUsesExactWatchEnrollmentBeforeCopiedBytes()
    {
        using var state = new State(); Enroll(state);
        var held = state.Begin(32, 16); var calls = 0;
        RevisionHook(state, (captured, revision) =>
        {
            Assert.False(captured); Assert.Equal(-1L, revision); calls++;
            new Span<byte>((void*)(state.Backing.AliasBase + 32), 16).Fill(0x4b);
            state.End(held); // Actual admitted writer completes BEFORE watch enrollment.
        });
        var bytes = new byte[16];
        Assert.Equal(CpuMappedCopyResult.Copied, state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
            State.Address, State.Page, State.Address + 32, bytes, out var result));
        var original = Assert.IsType<CpuMappedCopyObservation>(result);
        Assert.Equal(1, calls); Assert.All(bytes, value => Assert.Equal((byte)0x4b, value));
        Assert.Equal(1L, EnrollmentRevision(original)); Assert.Equal(1L, OriginalRevision(original));
        Assert.True(state.Current(original)); ValidateOriginal(state, original);
        state.Ledger.ReleaseMappedCopyWriteWatch(original);
    }

    [Fact]
    public void DisjointWriteAfterEnrollmentDoesNotAdoptYoungerPostCopyRevision()
    {
        using var state = new State(); Enroll(state);
        var held = state.Begin(32, 16);
        RevisionHook(state, (captured, revision) => { Assert.False(captured); Assert.Equal(-1L, revision); state.End(held); });
        state.Hook(() => state.End(state.Begin(State.Page, 1)));
        var original = state.Capture();
        Assert.Equal(1L, EnrollmentRevision(original)); Assert.Equal(1L, OriginalRevision(original));
        Assert.True(state.Current(original)); ValidateOriginal(state, original);
        state.Ledger.ReleaseMappedCopyWriteWatch(original);
    }

    [Fact]
    public void UnwatchedBusyFirstSampleStillHasNoPublicationRevision()
    {
        using var state = new State(); Enroll(state); var held = state.Begin(32, 16);
        RevisionHook(state, (captured, revision) => { Assert.False(captured); Assert.Equal(-1L, revision); state.End(held); });
        var original = PublicationOriginal(state, State.Address + 32, 16);
        Assert.Equal(-1L, OriginalRevision(original));
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, original, out var rejected));
        Assert.Null(rejected);
    }

    [Theory]
    [InlineData(32ul, true)] [InlineData(32ul, false)] [InlineData(16384ul, false)]
    public void OverlappingOrUnknownWriteAfterEnrollmentCannotMintOriginal(ulong offset, bool succeeded)
    {
        using var state = new State(); Enroll(state); var held = state.Begin(32, 16);
        RevisionHook(state, (_, _) => state.End(held));
        state.Hook(() => state.End(state.Begin(offset, 1), succeeded));
        Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
            State.Address, State.Page, State.Address + 32, new byte[16], out var rejected));
        Assert.Null(rejected); Assert.Equal(0, state.Backing.ActiveAccesses);
    }

    [Fact]
    public void StillAdmittedWriterRejectsEnrollmentBeforeAnyCopyBytes()
    {
        using var state = new State(); Enroll(state); var held = state.Begin(32, 16);
        var destination = Enumerable.Repeat((byte)0xa7, 16).ToArray();
        try
        {
            RevisionHook(state, (captured, revision) => { Assert.False(captured); Assert.Equal(-1L, revision); });
            Assert.Equal(CpuMappedCopyResult.Rejected, state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
                State.Address, State.Page, State.Address + 32, destination, out var rejected));
            Assert.Null(rejected); Assert.All(destination, value => Assert.Equal((byte)0xa7, value));
            Assert.Equal(0, state.Backing.ActiveAccesses);
        }
        finally { state.End(held); }
    }

    [Fact]
    public void YoungerWatchCannotRepairRevokedOriginalEvenAfterHistoryEviction()
    {
        using var state = new State(); var old = state.Capture();
        state.End(state.Begin(32, 1)); var younger = state.Capture(); EvictHistory(state);
        Assert.Equal(0L, OriginalRevision(old)); Assert.Equal(1L, OriginalRevision(younger));
        Assert.False(state.Current(old)); Assert.True(state.Current(younger));
        Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
            state.Ledger.TryPrepareMappedPublicationValidation(state.Backing, old, out var rejected));
        Assert.Null(rejected); ValidateOriginal(state, younger);
        state.Ledger.ReleaseMappedCopyWriteWatch(old); state.Ledger.ReleaseMappedCopyWriteWatch(younger);
    }

    [Fact]
    public void AppliedSuccessorKeepsOwnCommitRevisionSeparateFromEnrollment()
    {
        using var state = new State(); var original = state.Capture();
        state.End(state.Begin(State.Page, 1));
        using var publication = Prepare(state, original, original.SourceAddress);
        Assert.Equal(CpuMappedPublicationResult.Applied, publication.TryCommit());
        var successor = Assert.IsType<CpuMappedCopyObservation>(publication.SuccessfulObservation);
        Assert.Equal(0L, OriginalRevision(original)); Assert.Equal(1L, EnrollmentRevision(successor));
        Assert.Equal(2L, OriginalRevision(successor)); Assert.True(state.Current(successor));
        ValidateOriginal(state, successor); state.Ledger.ReleaseMappedCopyWriteWatch(successor);
    }

    [Fact]
    public void ThrowingInitialSampleControlBalancesBackingPinWithoutMintingWatch()
    {
        using var state = new State(); var cause = new IOException("before enrollment");
        RevisionHook(state, (_, _) => throw cause);
        Assert.Same(cause, Assert.Throws<IOException>(() => state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing,
            State.Address, State.Page, State.Address + 32, new byte[16], out _)));
        Assert.Equal(0, state.Backing.ActiveAccesses); RevisionHook(state, null);
        var all = Enumerable.Range(0, 128).Select(_ => state.Capture()).ToArray();
        foreach (var original in all) state.Ledger.ReleaseMappedCopyWriteWatch(original);
    }
}
