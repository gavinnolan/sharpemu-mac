// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

// Genuine participating ledger writes over pinned bytes. No native syscall,
// render-queue ownership or guest command execution is manufactured here.
public sealed unsafe partial class MappedCopyReadWatchTests
{
    private const ulong CommandWriteOffset = 64;
    private static readonly byte[] commandWritePayload = [4, 0, 0, 0, 0, 0, 0, 0];
    private static int CommandWriteWatchCharges(State state) => (int)typeof(HostMappedCopyLedger)
        .GetField("readWatchCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state.Ledger)!;
    private static CpuMappedCopyObservation CommandWriteOriginal(State state, bool watched = true)
    {
        var address = State.Address + CommandWriteOffset;
        CpuMappedCopyObservation? original;
        var result = watched
            ? state.Ledger.TryReadMappedCopyWithWriteWatch(state.Backing, address, 8, address, new byte[8], out original)
            : state.Ledger.TryReadMappedCopy(state.Backing, address, 8, address, new byte[8], out original);
        Assert.Equal(CpuMappedCopyResult.Copied, result);
        Assert.Equal(8ul, original!.FootprintByteCount); Assert.Equal(8ul, original.SourceByteCount);
        return original;
    }
    private static void CompleteDisjointCommandHistory(State state, byte[] expected, int count = 2022)
    {
        for (var index = 0; index < count; index++)
        {
            var offset = State.Page + (ulong)(index % (int)State.Page);
            var token = state.Begin(offset, 1);
            try
            {
                var value = (byte)(index * 19 + 7);
                *(byte*)(state.Backing.AliasBase + offset) = value;
                expected[checked((int)offset)] = value;
            }
            finally { state.End(token); }
        }
    }
    private static byte[] CommandBackingBytes(State state) =>
        new ReadOnlySpan<byte>((void*)state.Backing.AliasBase, checked((int)(State.Page * 2))).ToArray();

    [Fact]
    public void CompleteOriginalEightByteWatchAppliesAfter2022DisjointWritesWithoutHistoryRefresh()
    {
        using var state = new State(); var original = CommandWriteOriginal(state);
        var unwatched = CommandWriteOriginal(state, watched: false);
        var expected = CommandBackingBytes(state); CpuMappedCopyObservation? successor = null;
        try
        {
            CompleteDisjointCommandHistory(state, expected);
            Assert.True(state.Current(original)); Assert.Equal(1, CommandWriteWatchCharges(state));
            Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
                state.Ledger.TryPrepareMappedPublication(state.Backing, unwatched,
                    [new(unwatched.SourceAddress, commandWritePayload)], out var expired));
            Assert.Null(expired); Assert.Equal(expected, CommandBackingBytes(state));
            Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                state.Ledger.TryPrepareMappedPublication(state.Backing, original,
                    [new(original.SourceAddress, commandWritePayload)], out var prepared));
            using var exact = Assert.IsType<CpuMappedPublication>(prepared);
            Assert.Equal(2, CommandWriteWatchCharges(state)); Assert.Null(exact.SuccessfulObservation);
            Assert.Equal(CpuMappedPublicationResult.Applied, exact.TryCommit());
            successor = Assert.IsType<CpuMappedCopyObservation>(exact.SuccessfulObservation);
            commandWritePayload.CopyTo(expected, (int)CommandWriteOffset);
            Assert.Equal(expected, CommandBackingBytes(state)); Assert.False(state.Current(original)); Assert.True(state.Current(successor));
            Assert.Equal(original.Segments.ToArray(), successor.Segments.ToArray());
            Assert.Equal(1, CommandWriteWatchCharges(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
            Assert.Equal(CpuMappedPublicationResult.Rejected, exact.TryCommit());
            Assert.Equal(expected, CommandBackingBytes(state));
        }
        finally
        {
            state.Ledger.ReleaseMappedCopyWriteWatch(original);
            if (successor is not null) state.Ledger.ReleaseMappedCopyWriteWatch(successor);
        }
        Assert.Equal(0, CommandWriteWatchCharges(state));
    }

    [Theory]
    [InlineData("overlap")] [InlineData("unknown")] [InlineData("released")]
    [InlineData("replacement")] [InlineData("rights")] [InlineData("physical-alias")]
    public void CompleteOriginalWriteNeverBorrowsDisjointHistoryAfterItsExactProofIsLost(string mutation)
    {
        using var state = new State(); var original = CommandWriteOriginal(state);
        var expected = CommandBackingBytes(state);
        try
        {
            switch (mutation)
            {
                case "overlap":
                    state.End(state.Begin(CommandWriteOffset + 7, 1)); break;
                case "unknown":
                    state.End(state.Begin(State.Page, 1), succeeded: false); break;
                case "released":
                    state.Ledger.ReleaseMappedCopyWriteWatch(original);
                    state.Ledger.ReleaseMappedCopyWriteWatch(original); break;
                case "replacement":
                    state.Map(State.Address, 0); break;
                case "rights":
                    lock (state.Gate)
                    {
                        var rights = state.Ledger.PrepareProtectionChange(State.Address, State.Page, HostPageProtection.ReadOnly);
                        Assert.NotNull(rights); Assert.True(state.Ledger.BeginProtectionChange(rights, State.Address, State.Page));
                        state.Ledger.CompleteProtectionChange(rights!, nativeSuccess: true);
                    }
                    break;
                case "physical-alias":
                    state.Map(State.Address + State.Page, 0);
                    state.End(state.Begin(CommandWriteOffset, 1)); break;
                default: throw new InvalidOperationException(mutation);
            }
            CompleteDisjointCommandHistory(state, expected);
            Assert.False(state.Current(original));
            Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
                state.Ledger.TryPrepareMappedPublication(state.Backing, original,
                    [new(original.SourceAddress, commandWritePayload)], out var rejected));
            Assert.Null(rejected); Assert.Equal(expected, CommandBackingBytes(state));
            Assert.Equal(0, state.Backing.ActiveAccesses);
        }
        finally { state.Ledger.ReleaseMappedCopyWriteWatch(original); }
        Assert.Equal(0, CommandWriteWatchCharges(state));
    }

    [Fact]
    public void CancelledCompleteCommandWriteReleasesOnlyPrivateSuccessorAndLeavesOriginalUnconsumed()
    {
        using var state = new State(); var original = CommandWriteOriginal(state);
        var expected = CommandBackingBytes(state);
        try
        {
            CompleteDisjointCommandHistory(state, expected);
            for (var index = 0; index < 160; index++)
            {
                Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                    state.Ledger.TryPrepareMappedPublication(state.Backing, original,
                        [new(original.SourceAddress, commandWritePayload)], out var prepared));
                using var exact = Assert.IsType<CpuMappedPublication>(prepared);
                Assert.Equal(2, CommandWriteWatchCharges(state)); exact.Dispose(); exact.Dispose();
                Assert.Null(exact.SuccessfulObservation); Assert.True(state.Current(original));
                Assert.Equal(1, CommandWriteWatchCharges(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
            }
            Assert.Equal(expected, CommandBackingBytes(state));
        }
        finally { state.Ledger.ReleaseMappedCopyWriteWatch(original); }
        Assert.Equal(0, CommandWriteWatchCharges(state));
    }

    [Fact]
    public void KnownCompleteCommandWritesReleaseBothExactWatchOwnersWithoutExhausting128Slots()
    {
        using var state = new State();
        for (var index = 0; index < 160; index++)
        {
            var original = CommandWriteOriginal(state); CpuMappedCopyObservation? successor = null;
            try
            {
                using var exact = Prepare(state, original, original.SourceAddress, length: 8);
                Assert.Equal(CpuMappedPublicationResult.Applied, exact.TryCommit());
                successor = Assert.IsType<CpuMappedCopyObservation>(exact.SuccessfulObservation);
                Assert.Equal(1, CommandWriteWatchCharges(state)); Assert.True(state.Current(successor));
            }
            finally
            {
                state.Ledger.ReleaseMappedCopyWriteWatch(original);
                if (successor is not null) { state.Ledger.ReleaseMappedCopyWriteWatch(successor); state.Ledger.ReleaseMappedCopyWriteWatch(successor); }
            }
            Assert.Equal(0, CommandWriteWatchCharges(state)); Assert.Equal(0, state.Backing.ActiveAccesses);
        }
    }
}
