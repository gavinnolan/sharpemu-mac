// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.Memory;

[Collection("GuestImageWriteTracker")]
public sealed class GuestImageManagedWatchGenerationTests
{
    private const ulong Address = 0x5FEE00000020;
    private const ulong Bytes = 16;
    public GuestImageManagedWatchGenerationTests(ITestOutputHelper output)
    {
        var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
        if (!string.IsNullOrEmpty(expected)) Assert.Equal(expected, RuntimeInformation.ProcessArchitecture.ToString());
        output.WriteLine($"Managed watch actual CLR={RuntimeInformation.ProcessArchitecture} framework={RuntimeInformation.FrameworkDescription} expected={expected ?? "unset"}.");
    }

    private sealed class WatchScope : IDisposable
    {
        private readonly FieldInfo enabled = typeof(GuestImageWriteTracker).GetField("_enabled", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly bool previous;
        private readonly ulong[] addresses;
        internal WatchScope(params ulong[] owners)
        {
            // Pure scalar managed-watch controls: no real mapping/pointer,
            // Configure warm-up, native permission syscall or fault callback.
            previous = (bool)enabled.GetValue(null)!; enabled.SetValue(null, true);
            addresses = owners;
            foreach (var address in addresses) GuestImageWriteTracker.TrackManagedWriter(address, Bytes);
        }
        public void Dispose()
        {
            foreach (var address in addresses) GuestImageWriteTracker.Untrack(address);
            enabled.SetValue(null, previous);
        }
    }

    private static long Generation(ulong address)
    {
        Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(address, out var generation));
        return generation;
    }

    [Fact]
    public void EverySerialManagedWriteRevokesNewSnapshotEvenWhileDirtyRemainsSet()
    {
        using var scope = new WatchScope(Address);
        for (var write = 1; write <= 3; write++)
        {
            var snapshot = GuestImageWriteTracker.BeginReadSnapshot(Address, Bytes);
            Assert.True(snapshot.Active); Assert.True(GuestImageWriteTracker.IsReadSnapshotStable(snapshot));
            GuestImageWriteTracker.NotifyManagedWrite(Address + 1, 1);
            Assert.False(GuestImageWriteTracker.IsReadSnapshotStable(snapshot));
            Assert.Equal(write, Generation(Address)); Assert.True(GuestImageWriteTracker.PeekDirty(Address));
        }
        Assert.True(GuestImageWriteTracker.ConsumeDirty(Address));
        Assert.False(GuestImageWriteTracker.ConsumeDirty(Address)); Assert.Equal(3, Generation(Address));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ConsumeAndManagedRetrackDoNotResetGenerationOrHideNextWrite(bool retrack)
    {
        using var scope = new WatchScope(Address);
        GuestImageWriteTracker.NotifyManagedWrite(Address, 1);
        Assert.True(GuestImageWriteTracker.ConsumeDirty(Address)); Assert.Equal(1, Generation(Address));
        if (retrack) GuestImageWriteTracker.TrackManagedWriter(Address, Bytes);
        var snapshot = GuestImageWriteTracker.BeginReadSnapshot(Address, Bytes);
        Assert.Equal(1, snapshot.Generation); Assert.False(GuestImageWriteTracker.PeekDirty(Address));
        GuestImageWriteTracker.NotifyManagedWrite(Address, 1);
        Assert.Equal(2, Generation(Address)); Assert.False(GuestImageWriteTracker.IsReadSnapshotStable(snapshot));
        Assert.True(GuestImageWriteTracker.TryGetProtectionState(Address, out var protect, out var armed));
        Assert.False(protect); Assert.False(armed);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ExactEndBoundaryUnrelatedAndEmptyNotificationsPreserveSnapshot(int kind)
    {
        using var scope = new WatchScope(Address);
        var snapshot = GuestImageWriteTracker.BeginReadSnapshot(Address, Bytes);
        var token = GuestImageWriteTracker.TryGetContentToken(Address, out var before); Assert.True(token);
        if (kind == 0) GuestImageWriteTracker.NotifyManagedWrite(Address - 1, 1); // Ends exactly at owner start.
        if (kind == 1) GuestImageWriteTracker.NotifyManagedWrite(Address + Bytes, 1); // Begins at owner end.
        if (kind == 2) GuestImageWriteTracker.NotifyManagedWrite(Address + 1, 0);
        if (kind == 3) GuestImageWriteTracker.NotifyManagedWrite(0, ulong.MaxValue);
        Assert.Equal(0, Generation(Address)); Assert.False(GuestImageWriteTracker.PeekDirty(Address));
        Assert.True(GuestImageWriteTracker.IsReadSnapshotStable(snapshot));
        Assert.True(GuestImageWriteTracker.TryGetContentToken(Address, out var after)); Assert.Equal(before, after);
    }

    [Fact]
    public void DistinctExactWatchSpansOnSameGuestPageRevokeOnlyIntersectingOwnersOnce()
    {
        const ulong second = Address + 32;
        using var scope = new WatchScope(Address, second);
        var untouched = GuestImageWriteTracker.BeginReadSnapshot(second, Bytes);
        GuestImageWriteTracker.NotifyManagedWrite(Address + 4, 1);
        Assert.Equal(1, Generation(Address)); Assert.Equal(0, Generation(second));
        Assert.True(GuestImageWriteTracker.IsReadSnapshotStable(untouched));
        // Gap ends exactly at second span. Whole-page rounding must not revoke it.
        GuestImageWriteTracker.NotifyManagedWrite(Address + Bytes, 16);
        Assert.Equal(1, Generation(Address)); Assert.Equal(0, Generation(second));
        GuestImageWriteTracker.NotifyManagedWrite(Address + Bytes - 1, 34);
        Assert.Equal(2, Generation(Address)); Assert.Equal(1, Generation(second));
        Assert.False(GuestImageWriteTracker.IsReadSnapshotStable(untouched));
        Assert.True(GuestImageWriteTracker.ConsumeDirty(Address)); Assert.False(GuestImageWriteTracker.ConsumeDirty(Address));
        Assert.True(GuestImageWriteTracker.ConsumeDirty(second)); Assert.False(GuestImageWriteTracker.ConsumeDirty(second));
    }

    [Fact]
    public async Task ConcurrentNotificationsCountEveryCallWithoutMultiplyingDirtyConsumption()
    {
        using var scope = new WatchScope(Address);
        GuestImageWriteTracker.NotifyManagedWrite(Address, 1); // Keep Dirty set for every concurrent call.
        var snapshot = GuestImageWriteTracker.BeginReadSnapshot(Address, Bytes);
        var writes = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        { for (var count = 0; count < 250; count++) GuestImageWriteTracker.NotifyManagedWrite(Address + 1, 1); })).ToArray();
        await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1001, Generation(Address)); Assert.False(GuestImageWriteTracker.IsReadSnapshotStable(snapshot));
        Assert.True(GuestImageWriteTracker.ConsumeDirty(Address)); Assert.False(GuestImageWriteTracker.ConsumeDirty(Address));
    }
}
