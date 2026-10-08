// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

public sealed unsafe partial class MappedCopyReadWatchTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExactOriginalReadReservationHasNoWriteEntitlementOrOriginalRefresh(bool readOnly)
    {
        using var state = new State();
        if (readOnly)
            lock (state.Gate)
            {
                var view = state.Ledger.PrepareView(state.Backing, State.Address, State.Page, 0, HostPageProtection.ReadOnly);
                Assert.NotNull(view); Assert.True(state.Ledger.BeforeNativeEffect(State.Address, State.Page)); state.Ledger.PublishView(view);
            }
        var original = state.Capture();
        var bytes = new ReadOnlySpan<byte>((void*)(state.Backing.AliasBase + 32), 16).ToArray();
        var segments = original.Segments.ToArray();
        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
            state.Ledger.TryPrepareMappedReadValidation(state.Backing, original, out var read));
        Assert.NotNull(read);
        try
        {
            Assert.True(read.IsOriginalCurrent(original));
            Assert.False(state.Current(original)); // ordinary guard still refuses PublicationBit
            lock (state.Gate)
            {
                Assert.True(state.Ledger.HasOverlappingPublicationLocked(State.Address, State.Page));
                Assert.False(state.Ledger.BeforeNativeEffect(State.Address, State.Page));
                Assert.False(state.Ledger.HasOverlappingPublicationLocked(State.Address + State.Page, State.Page));
            }
            var rejected = state.Begin(State.Page, 1); // disjoint write STILL cannot pass whole-backing exclusion
            var writerType = typeof(HostBackingObject).Assembly.GetType("SharpEmu.HLE.Host.HostManagedWriteIntervals")!;
            Assert.False((bool)writerType.GetMethod("AllowsBytes", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [rejected])!);
            Assert.Equal(segments, original.Segments);
            Assert.Equal(bytes, new ReadOnlySpan<byte>((void*)(state.Backing.AliasBase + 32), 16).ToArray());
        }
        finally { read.Dispose(); }
        Assert.False(read.IsOriginalCurrent(original));
        read.Dispose(); // exact consumed receipt, no second release
        Assert.True(state.Current(original));
        Assert.Equal(segments, original.Segments);
        Assert.Equal(bytes, new ReadOnlySpan<byte>((void*)(state.Backing.AliasBase + 32), 16).ToArray());
    }

    [Fact]
    public void OriginalReadReservationNeverBorrowsAnotherWatchOnTheSamePhysicalBytes()
    {
        using var state = new State(); var first = state.Capture(); var other = state.Capture();
        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
            state.Ledger.TryPrepareMappedReadValidation(state.Backing, first, out var read));
        Assert.NotNull(read);
        try
        {
            Assert.True(read.IsOriginalCurrent(first));
            Assert.False(read.IsOriginalCurrent(other));
            state.Ledger.ReleaseMappedCopyWriteWatch(first);
            Assert.False(read.IsOriginalCurrent(first));
        }
        finally { read.Dispose(); }
        Assert.False(state.Current(first)); Assert.True(state.Current(other));
    }

    [Theory]
    [InlineData("busy")] [InlineData("overlap")] [InlineData("released")]
    [InlineData("ABA")] [InlineData("failed-envelope")] [InlineData("foreign")]
    public void OriginalReadReservationDeclinesBusyOrStaleWithoutCopyOrFreshObservation(string variant)
    {
        using var state = new State(); using var foreign = new State(); var original = state.Capture();
        object? interval = null;
        try
        {
            if (variant == "busy") interval = state.Begin(State.Page, 1);
            if (variant == "overlap") state.End(state.Begin(32, 1));
            if (variant == "released") state.Ledger.ReleaseMappedCopyWriteWatch(original);
            if (variant == "ABA") state.Map(State.Address, 0);
            if (variant == "failed-envelope") state.End(state.Envelope(), false);
            var before = new ReadOnlySpan<byte>((void*)(state.Backing.AliasBase + 32), 16).ToArray();
            Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
                (variant == "foreign" ? foreign.Ledger : state.Ledger).TryPrepareMappedReadValidation(state.Backing, original, out var read));
            Assert.Null(read);
            Assert.Equal(before, new ReadOnlySpan<byte>((void*)(state.Backing.AliasBase + 32), 16).ToArray());
        }
        finally { if (interval is not null) state.End(interval); }
        if (variant == "busy")
        {
            Assert.True(state.Current(original));
            Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                state.Ledger.TryPrepareMappedReadValidation(state.Backing, original, out var retry));
            Assert.NotNull(retry); Assert.True(retry.IsOriginalCurrent(original)); retry.Dispose();
        }
    }
}
