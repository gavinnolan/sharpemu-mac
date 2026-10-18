// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

[Collection("GuestImageWriteTracker")]
public sealed class GuestImageObservedContentTokenTests
{
    private const ulong Address = 0x71F5_1000_00;
    private static readonly GuestImageWriteTracker.ContentToken Absent = new(0, -1, 0, -1, 0, 0);

    [Fact]
    public void MissingAndDisabledObservationFailClosed()
    {
        using var state = new State();
        Assert.False(GuestImageWriteTracker.TryGetObservedContentToken(Address, 16, out var missing));
        Assert.Equal(Absent, missing);
        GuestImageWriteTracker.TrackManagedWriter(Address, 4096);
        Assert.True(GuestImageWriteTracker.TryGetObservedContentToken(Address, 16, out _));
        state.Enabled = false;
        Assert.False(GuestImageWriteTracker.TryGetObservedContentToken(Address, 16, out var disabled));
        Assert.Equal(Absent, disabled);
        Assert.True(GuestImageWriteTracker.TryGetContentToken(Address, out _)); // legacy observation unchanged
    }

    [Fact]
    public void OrdinaryWatchOnlyCannotQualifyButExplicitManagedCoverageCan()
    {
        using var state = new State();
        GuestImageWriteTracker.Track(Address, 4096, protect: false);
        Assert.True(GuestImageWriteTracker.TryGetContentToken(Address, out var ordinary));
        Assert.False(GuestImageWriteTracker.TryGetObservedContentToken(Address, 4096, out var rejected));
        Assert.Equal(Absent, rejected);
        GuestImageWriteTracker.TrackManagedWriter(Address, 4096);
        Assert.True(GuestImageWriteTracker.TryGetObservedContentToken(Address, 4096, out var managed));
        Assert.Equal(ordinary, managed); // identities/counters alone do not describe writer coverage
        Assert.False(GuestImageWriteTracker.TryGetObservedContentToken(Address, 4097, out _));
    }

    [Theory]
    [InlineData(0)] // failed protection and successfully disarmed share this metadata state
    [InlineData(2)] // invalidating
    [InlineData(3)] // arming
    public void RegisteredNonArmedProtectedOwnerCannotQualify(int armed)
    {
        using var state = new State();
        state.InstallProtected(4096, armed);
        Assert.True(GuestImageWriteTracker.TryGetContentToken(Address, out var registered));
        Assert.True(registered.ProtectedIdentity > 0);
        Assert.False(GuestImageWriteTracker.TryGetObservedContentToken(Address, 4096, out var rejected));
        Assert.Equal(Absent, rejected);
    }

    [Fact]
    public void ArmedMetadataRequiresProtectionAndCompleteFootprint()
    {
        using var state = new State();
        var owner = state.InstallProtected(4096, 1, protect: false);
        Assert.False(GuestImageWriteTracker.TryGetObservedContentToken(Address, 4096, out _));
        owner.GetType().GetField("Protect")!.SetValue(owner, true);
        Assert.True(GuestImageWriteTracker.TryGetObservedContentToken(Address, 4096, out var armed));
        Assert.True(GuestImageWriteTracker.TryGetContentToken(Address, out var legacy));
        Assert.Equal(legacy, armed);
        Assert.False(GuestImageWriteTracker.TryGetObservedContentToken(Address, 4097, out _));
        Assert.False(GuestImageWriteTracker.TryGetObservedContentToken(Address, 0, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void ExplicitManagedFootprintCanQualifyAlongsideTransitionalProtectedOwner(int armed)
    {
        using var state = new State();
        state.InstallProtected(16, armed);
        GuestImageWriteTracker.TrackManagedWriter(Address, 4096);
        Assert.True(GuestImageWriteTracker.TryGetObservedContentToken(Address, 4096, out var token));
        Assert.Equal((16UL, 4096UL), (token.ProtectedByteCount, token.WatchByteCount));
        Assert.True(token.ProtectedIdentity > 0 && token.WatchIdentity > 0);
        Assert.False(GuestImageWriteTracker.TryGetObservedContentToken(Address, 4097, out _));
    }

    [Fact]
    public void WrappedAndZeroFootprintsCannotQualify()
    {
        using var state = new State();
        GuestImageWriteTracker.TrackManagedWriter(Address, ulong.MaxValue);
        Assert.False(GuestImageWriteTracker.TryGetObservedContentToken(Address, ulong.MaxValue, out _));
        Assert.False(GuestImageWriteTracker.TryGetObservedContentToken(Address, 0, out _));
        Assert.False(GuestImageWriteTracker.TryGetObservedContentToken(0, 16, out _));
    }

    [Fact]
    public void ManagedNotificationAdvancesTokenAndRetirementRevokesIdentity()
    {
        using var state = new State();
        GuestImageWriteTracker.TrackManagedWriter(Address, 4096);
        Assert.True(GuestImageWriteTracker.TryGetObservedContentToken(Address, 16, out var before));
        GuestImageWriteTracker.NotifyManagedWrite(Address, 4);
        Assert.True(GuestImageWriteTracker.TryGetObservedContentToken(Address, 16, out var notified));
        Assert.NotEqual(before, notified);
        GuestImageWriteTracker.Untrack(Address);
        Assert.False(GuestImageWriteTracker.TryGetObservedContentToken(Address, 16, out _));
        GuestImageWriteTracker.TrackManagedWriter(Address, 4096);
        Assert.True(GuestImageWriteTracker.TryGetObservedContentToken(Address, 16, out var recreated));
        Assert.Equal(before.WatchGeneration, recreated.WatchGeneration);
        Assert.NotEqual(before.WatchIdentity, recreated.WatchIdentity);
    }

    private static FieldInfo Field(string name) => typeof(GuestImageWriteTracker)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private sealed class State : IDisposable
    {
        private readonly object gate = Field("_gate").GetValue(null)!;
        private readonly IDictionary ranges = (IDictionary)Field("_rangesByAddress").GetValue(null)!;
        private readonly bool oldEnabled = (bool)Field("_enabled").GetValue(null)!;
        private object? protectedOwner;
        public State()
        {
            GuestImageWriteTracker.Untrack(Address);
            Enabled = true; // no Configure/WarmUp/native memory activity
        }
        public bool Enabled { set { lock (gate) Field("_enabled").SetValue(null, value); } }
        public object InstallProtected(ulong bytes, int armed, bool protect = true)
        {
            // Metadata-only fixture; does not arm pages or publish a signal
            // snapshot and cannot prove an actual kernel protection receipt.
            var type = typeof(GuestImageWriteTracker).GetNestedType("TrackedRange", BindingFlags.NonPublic)!;
            protectedOwner = Activator.CreateInstance(type, nonPublic: true)!;
            type.GetField("Address")!.SetValue(protectedOwner, Address);
            type.GetField("ByteCount")!.SetValue(protectedOwner, bytes);
            type.GetField("Protect")!.SetValue(protectedOwner, protect);
            type.GetField("Armed")!.SetValue(protectedOwner, armed);
            lock (gate) ranges.Add(Address, protectedOwner);
            return protectedOwner;
        }
        public void Dispose()
        {
            // Remove fabricated armed metadata before the production Untrack
            // path could interpret it as native ownership.
            lock (gate)
            {
                if (protectedOwner is not null) ranges.Remove(Address);
                Field("_enabled").SetValue(null, oldEnabled);
            }
            GuestImageWriteTracker.Untrack(Address);
        }
    }
}
