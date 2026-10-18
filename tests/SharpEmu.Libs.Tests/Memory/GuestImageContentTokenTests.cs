// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

[Collection("GuestImageWriteTracker")]
public sealed class GuestImageContentTokenTests
{
    private const ulong Address = 0x71F5_0000_00;

    [Fact]
    public void AbsentOwnersCannotAuthorizeContent()
    {
        using var state = new State();
        Assert.False(GuestImageWriteTracker.TryGetContentToken(Address, out var token));
        Assert.Equal(new GuestImageWriteTracker.ContentToken(0, -1, 0, -1, 0, 0), token);
    }

    [Fact]
    public void WriteThenUntrackAndRetrackCannotReviveCopiedGenerationZero()
    {
        using var state = new State();
        GuestImageWriteTracker.TrackManagedWriter(Address, 4096);
        var copied = Token();
        Assert.Equal(0, copied.WatchGeneration);
        Assert.Equal(0, copied.ProtectedIdentity);
        Assert.Equal(-1, copied.ProtectedGeneration);
        Assert.Equal(0UL, copied.ProtectedByteCount);
        Assert.Equal(4096UL, copied.WatchByteCount);

        GuestImageWriteTracker.NotifyManagedWrite(Address, 16);
        var written = Token();
        Assert.Equal(copied.WatchIdentity, written.WatchIdentity);
        Assert.Equal(1, written.WatchGeneration);
        GuestImageWriteTracker.Untrack(Address);
        Assert.False(GuestImageWriteTracker.TryGetContentToken(Address, out _));
        GuestImageWriteTracker.TrackManagedWriter(Address, 4096);
        var recreated = Token();

        // The old scalar generation really compares equal again. The actual
        // production token must reject those old copied bytes despite that.
        Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(Address, out var generation));
        Assert.Equal(copied.WatchGeneration, generation);
        Assert.Equal(0, recreated.WatchGeneration);
        Assert.True(recreated.WatchIdentity > copied.WatchIdentity);
        Assert.NotEqual(copied, recreated);
    }

    [Fact]
    public void SelectiveWatchRetirementAlsoRevokesItsOldIdentity()
    {
        using var state = new State();
        GuestImageWriteTracker.TrackManagedWriter(Address, 4096);
        var before = Token();
        GuestImageWriteTracker.UntrackWatchOnly(Address);
        GuestImageWriteTracker.TrackManagedWriter(Address, 4096);
        var after = Token();
        Assert.Equal(before.WatchGeneration, after.WatchGeneration);
        Assert.NotEqual(before.WatchIdentity, after.WatchIdentity);
    }

    [Fact]
    public void DirtyConsumeKeepsIdentityAndGeneration()
    {
        using var state = new State();
        GuestImageWriteTracker.TrackManagedWriter(Address, 4096);
        GuestImageWriteTracker.NotifyManagedWrite(Address, 16);
        var written = Token();
        Assert.True(GuestImageWriteTracker.ConsumeDirty(Address));
        Assert.Equal(written, Token());
    }

    [Fact]
    public void SameExtentRegistrationKeepsIdentityButWideningCannotSilentlyRefreshOldToken()
    {
        using var state = new State();
        GuestImageWriteTracker.TrackManagedWriter(Address, 4096);
        GuestImageWriteTracker.NotifyManagedWrite(Address, 16);
        var before = Token();
        GuestImageWriteTracker.TrackManagedWriter(Address, 2048);
        Assert.Equal(before, Token());
        GuestImageWriteTracker.TrackManagedWriter(Address, 8192);
        var widened = Token();
        Assert.Equal(before.WatchGeneration, widened.WatchGeneration);
        Assert.NotEqual(before.WatchIdentity, widened.WatchIdentity);
        Assert.Equal(4096UL, before.WatchByteCount);
        Assert.Equal(8192UL, widened.WatchByteCount);
        Assert.NotEqual(before, widened);
    }

    [Fact]
    public void SameGenerationAtDifferentAddressHasDifferentOwnerIdentity()
    {
        using var state = new State();
        GuestImageWriteTracker.TrackManagedWriter(Address, 4096);
        GuestImageWriteTracker.TrackManagedWriter(Address + 8192, 4096);
        var first = Token();
        Assert.True(GuestImageWriteTracker.TryGetContentToken(Address + 8192, out var second));
        Assert.Equal(first.WatchGeneration, second.WatchGeneration);
        Assert.NotEqual(first.WatchIdentity, second.WatchIdentity);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void IndependentOwnerCountersCannotCollapseToOneAggregatedGeneration()
    {
        using var state = new State();
        GuestImageWriteTracker.TrackManagedWriter(Address, 4096);
        // Safe metadata fixture: install an ordinary tracked-owner object into
        // the protected registry without arming pages or publishing a native
        // signal snapshot. This qualifies token field independence only.
        var rangeType = typeof(GuestImageWriteTracker).GetNestedType("TrackedRange", BindingFlags.NonPublic)!;
        var protectedOwner = Activator.CreateInstance(rangeType, nonPublic: true)!;
        rangeType.GetField("ByteCount")!.SetValue(protectedOwner, 2048UL);
        var generationField = rangeType.GetField("WriteGeneration")!;
        generationField.SetValue(protectedOwner, 7L);
        var gate = Field("_gate").GetValue(null)!;
        var ranges = (IDictionary)Field("_rangesByAddress").GetValue(null)!;
        lock (gate) ranges.Add(Address, protectedOwner);
        try
        {
            var first = Token();
            Assert.True(first.ProtectedIdentity > 0);
            Assert.True(first.WatchIdentity > 0);
            Assert.NotEqual(first.ProtectedIdentity, first.WatchIdentity);
            Assert.Equal((7L, 0L), (first.ProtectedGeneration, first.WatchGeneration));
            Assert.Equal((2048UL, 4096UL), (first.ProtectedByteCount, first.WatchByteCount));
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(Address, out var sumBefore));
            generationField.SetValue(protectedOwner, 6L);
            GuestImageWriteTracker.NotifyManagedWrite(Address, 16);
            var second = Token();
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(Address, out var sumAfter));
            Assert.Equal(sumBefore, sumAfter);
            Assert.Equal((6L, 1L), (second.ProtectedGeneration, second.WatchGeneration));
            Assert.NotEqual(first, second);
            GuestImageWriteTracker.UntrackWatchOnly(Address);
            var protectedOnly = Token();
            Assert.Equal(first.ProtectedIdentity, protectedOnly.ProtectedIdentity);
            Assert.Equal(0, protectedOnly.WatchIdentity);
            Assert.Equal(-1, protectedOnly.WatchGeneration);
            Assert.Equal(0UL, protectedOnly.WatchByteCount);
        }
        finally { lock (gate) ranges.Remove(Address); }
    }

    private static GuestImageWriteTracker.ContentToken Token()
    {
        Assert.True(GuestImageWriteTracker.TryGetContentToken(Address, out var token));
        return token;
    }
    private static FieldInfo Field(string name) => typeof(GuestImageWriteTracker)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private sealed class State : IDisposable
    {
        public State()
        {
            GuestImageWriteTracker.Untrack(Address);
            GuestImageWriteTracker.Untrack(Address + 8192);
        }
        public void Dispose()
        {
            GuestImageWriteTracker.Untrack(Address);
            GuestImageWriteTracker.Untrack(Address + 8192);
        }
    }
}
