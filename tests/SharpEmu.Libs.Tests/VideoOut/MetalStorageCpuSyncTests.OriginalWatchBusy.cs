// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Real ledger admission/watch history over owned pinned CLR bytes. No native
    // selector, kernel mapping, GPU commit, replacement capture or byte retry.
    private sealed class OriginalWatchBusySource : ICpuMemory, ICpuMappedReadWatchSource, IDisposable
    {
        internal readonly DisplayLedgerMemory Memory = new();
        internal int Copies, BooleanQueries, StatusQueries, Releases;
        internal bool Allowed = true;
        internal Action<int>? BeforeStatus;
        public bool TryRead(ulong address, Span<byte> bytes) => throw new InvalidOperationException("No legacy byte read.");
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new InvalidOperationException("No legacy byte write.");
        public bool CanWaitForMappedReadWatchStatus => Allowed && Memory.Ledger.CanWaitForMappedReadWatchStatus;
        internal CpuMappedCopyObservation Capture()
        {
            Assert.Equal(CpuMappedCopyResult.Copied, TryReadMappedCopyWithWriteWatch(Address,
                DisplayLedgerMemory.ByteCount, Address + 32, new byte[16], out var original));
            return Assert.IsType<CpuMappedCopyObservation>(original);
        }
        internal object Begin(bool overlap = false) => typeof(HostBackingObject).GetMethod("BeginManagedWrite", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Memory.Backing, [overlap ? 32UL : 16384UL, 1UL])!;
        internal void End(object token) => typeof(HostBackingObject).GetMethod("EndManagedWrite", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [token, true]);
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong footprint, ulong source, Span<byte> destination,
            out CpuMappedCopyObservation? observation) => throw new InvalidOperationException("No fresh generic copy in original watch waiting.");
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong address, ulong footprint, ulong source, Span<byte> destination,
            out CpuMappedCopyObservation? observation)
        { Copies++; return Memory.Ledger.TryReadMappedCopyWithWriteWatch(Memory.Backing, address, footprint, source, destination, out observation); }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation original) => Memory.Ledger.IsCurrent(Memory.Backing, original);
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original)
        { BooleanQueries++; return Memory.Ledger.IsMappedCopyWriteWatchCurrent(Memory.Backing, original); }
        public CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(CpuMappedCopyObservation original)
        { StatusQueries++; BeforeStatus?.Invoke(StatusQueries); return Memory.Ledger.GetMappedCopyWriteWatchStatus(Memory.Backing, original); }
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation original)
        { Releases++; Memory.Ledger.ReleaseMappedCopyWriteWatch(original); }
        public void Dispose() => Memory.Dispose();
    }
    private static void WaitOriginalWatch(ICpuMappedReadWatchSource source, CpuMappedCopyObservation original,
        Action nonWatchCurrent, long? deadline = null, bool? canWait = null)
    {
        try
        {
            Presenter.GetMethod("WaitForOrdinaryStorageOriginalWatch", Private)!.Invoke(null,
                [source, original, canWait ?? source.CanWaitForMappedReadWatchStatus, deadline ?? Environment.TickCount64 + 500, nonWatchCurrent]);
        }
        catch (TargetInvocationException failure) when (failure.InnerException is not null)
        { ExceptionDispatchInfo.Capture(failure.InnerException).Throw(); }
    }

    // Exact-key metadata only. Armed scalar fields are controls, not native
    // protection authority; no Track/Untrack, permission call or byte access.
    private sealed class OriginalWatchBusyContentMetadata : IDisposable
    {
        private readonly IDictionary ranges = (IDictionary)typeof(GuestImageWriteTracker).GetField("_rangesByAddress", Private)!.GetValue(null)!;
        private readonly IDictionary watches = (IDictionary)typeof(GuestImageWriteTracker).GetField("_watchRangesByAddress", Private)!.GetValue(null)!;
        private readonly object gate = typeof(GuestImageWriteTracker).GetField("_gate", Private)!.GetValue(null)!;
        private readonly FieldInfo snapshot = typeof(GuestImageWriteTracker).GetField("_watchRangeSnapshot", Private)!;
        private readonly MethodInfo rebuild = typeof(GuestImageWriteTracker).GetMethod("RebuildWatchSnapshotLocked", Private)!;
        private readonly ulong address;
        private readonly object owner, watch;
        private object? previousOwner, previousWatch, previousSnapshot, installedSnapshot;
        private bool installed, disposed;
        internal OriginalWatchBusyContentMetadata(ulong address, ulong byteCount)
        {
            this.address = address; owner = Range(true, false); watch = Range(false, true);
            try
            {
                lock (gate)
                {
                    previousOwner = ranges[address]; previousWatch = watches[address]; previousSnapshot = snapshot.GetValue(null);
                    installed = true; // own possible partial insertion before either mutation
                    try { ranges[address] = owner; watches[address] = watch; rebuild.Invoke(null, null); }
                    finally { installedSnapshot = snapshot.GetValue(null); }
                }
            }
            catch (Exception primary)
            {
                try { Dispose(); } catch (Exception cleanup) { primary.Data["OriginalWatchBusyMetadataCleanup"] = cleanup; }
                ExceptionDispatchInfo.Capture(primary).Throw(); throw;
            }
            object Range(bool protect, bool managed)
            {
                var value = Activator.CreateInstance(typeof(GuestImageWriteTracker).GetNestedType("TrackedRange", BindingFlags.NonPublic)!, true)!;
                foreach (var (name, scalar) in new (string, object)[] { ("Address", address), ("ByteCount", byteCount),
                    ("Start", address), ("End", checked(address + byteCount)), ("Protect", protect), ("Armed", protect ? 1 : 0),
                    ("ManagedWriter", managed), ("WriteGeneration", 0L) }) value.GetType().GetField(name)!.SetValue(value, scalar);
                return value;
            }
        }
        internal void Generation(long value) => owner.GetType().GetField("WriteGeneration")!.SetValue(owner, value);
        public void Dispose()
        {
            if (disposed) return; disposed = true; if (!installed) return;
            Exception? first = null;
            void Attempt(Action action) { try { action(); } catch (Exception failure) { first ??= failure; } }
            lock (gate)
            {
                Attempt(() => Restore(ranges, owner, previousOwner));
                Attempt(() => Restore(watches, watch, previousWatch));
                Attempt(() => { Assert.Same(installedSnapshot, snapshot.GetValue(null)); snapshot.SetValue(null, previousSnapshot); });
            }
            if (first is not null) ExceptionDispatchInfo.Capture(first).Throw();
            void Restore(IDictionary table, object owned, object? previous)
            {
                // A setup failure before insertion leaves the exact prior row.
                if (ReferenceEquals(table[address], previous)) return;
                Assert.Same(owned, table[address]);
                if (previous is null) table.Remove(address); else table[address] = previous;
            }
        }
    }

    [Theory]
    [InlineData("busy-release")] [InlineData("current-then-busy")]
    public void SameActualOriginalWatchOrdinaryBusyClearsWithoutBooleanResampleOrRecapture(string variant)
    {
        ActualArchitecture(); using var state = new State(); using var source = new OriginalWatchBusySource();
        var original = source.Capture(); var before = source.Memory.Expected; object? token = null;
        try
        {
            if (variant == "busy-release") token = source.Begin();
            else
            {
                WaitOriginalWatch(source, original, () => throw new InvalidOperationException("Current requires no deferred callback."));
                Assert.Equal(1, source.StatusQueries);
                source.BeforeStatus = count => { if (count == 2) token = source.Begin(); };
            }
            var checks = 0;
            WaitOriginalWatch(source, original, () =>
            {
                checks++; Assert.True(source.IsMappedCopyObservationCurrent(original));
                if (token is not null) { var exact = token; token = null; source.End(exact); }
            });
            Assert.True(checks >= 2); Assert.True(source.StatusQueries >= 2);
            Assert.Equal(0, source.BooleanQueries); Assert.Equal(1, source.Copies);
            Assert.Equal(before, source.Memory.Expected);
            Assert.Equal(CpuMappedReadWatchStatus.Current, source.Memory.Ledger.GetMappedCopyWriteWatchStatus(source.Memory.Backing, original));
        }
        finally { if (token is not null) source.End(token); source.ReleaseMappedCopyWriteWatch(original); }
        Assert.Equal(1, source.Releases); Assert.Equal(0, source.Memory.Backing.ActiveAccesses);
    }

    [Theory]
    [InlineData("overlap")] [InlineData("publication")] [InlineData("released")]
    public void SameOriginalWatchStaleNeverWaitsOrResamplesAsOrdinaryBusy(string variant)
    {
        ActualArchitecture(); using var state = new State(); using var source = new OriginalWatchBusySource();
        var original = source.Capture(); object? token = null; CpuMappedReadValidation? validation = null;
        var released = false;
        try
        {
            if (variant == "overlap") token = source.Begin(overlap: true);
            if (variant == "publication")
            {
                Assert.Equal(CpuMappedPublicationPreparationResult.Prepared, source.Memory.Ledger.TryPrepareMappedReadValidation(source.Memory.Backing, original, out validation));
                Assert.NotNull(validation); Assert.True(validation!.IsOriginalCurrent(original));
            }
            if (variant == "released") { released = true; source.ReleaseMappedCopyWriteWatch(original); }
            Assert.Throws<NotSupportedException>(() => WaitOriginalWatch(source, original,
                () => throw new InvalidOperationException("Stale must not use deferred callback.")));
            Assert.Equal(1, source.StatusQueries); Assert.Equal(0, source.BooleanQueries); Assert.Equal(1, source.Copies);
        }
        finally
        {
            var exact = validation; validation = null; exact?.Dispose();
            if (token is not null) source.End(token);
            if (!released) source.ReleaseMappedCopyWriteWatch(original);
        }
        Assert.Equal(1, source.Releases); Assert.Equal(0, source.Memory.Backing.ActiveAccesses);
    }

    [Theory]
    [InlineData("expired")] [InlineData("owner-change")] [InlineData("session-change")]
    [InlineData("lost-capability")] [InlineData("provider-gate")]
    public void OriginalBusyWaitPreservesOwnerSessionDeadlineAndNoGateRefusals(string variant)
    {
        ActualArchitecture(); using var state = new State(); using var source = new OriginalWatchBusySource();
        var original = source.Capture(); var token = source.Begin(); var changed = new InvalidOperationException(variant);
        var checks = 0;
        try
        {
            if (variant == "lost-capability") source.BeforeStatus = _ => source.Allowed = false;
            Action current = () => { checks++; if (variant is "owner-change" or "session-change") throw changed; };
            if (variant == "provider-gate")
            {
                lock (source.Memory.Gate)
                    Assert.Throws<NotSupportedException>(() => WaitOriginalWatch(source, original, current, canWait: true));
                Assert.Equal(0, source.StatusQueries);
            }
            else if (variant is "owner-change" or "session-change")
                Assert.Same(changed, Assert.Throws<InvalidOperationException>(() => WaitOriginalWatch(source, original, current)));
            else if (variant == "lost-capability")
            {
                var failure = Assert.Throws<NotSupportedException>(() => WaitOriginalWatch(source, original, current, Environment.TickCount64 + 500));
                Assert.Contains("lost provider closure", failure.Message);
                Assert.Equal(1, source.StatusQueries); Assert.Equal(1, checks);
            }
            else Assert.Throws<NotSupportedException>(() => WaitOriginalWatch(source, original, current, Environment.TickCount64));
            Assert.Equal(0, source.BooleanQueries); Assert.Equal(1, source.Copies);
            if (variant is "owner-change" or "session-change") Assert.Equal(1, checks);
        }
        finally { source.End(token); source.ReleaseMappedCopyWriteWatch(original); }
        Assert.Equal(1, source.Releases); Assert.Equal(0, source.Memory.Backing.ActiveAccesses);
    }

    [Theory]
    [InlineData("busy-release")] [InlineData("current-then-busy")]
    [InlineData("owner-change")] [InlineData("session-change")] [InlineData("token-change")]
    public void CompleteOriginalContentGuardUsesActualWatchAndPreservesOriginalMetadataDuringBusy(string variant)
    {
        ActualArchitecture(); var address = Address + 32;
        using var state = new State(); using var protection = new OriginalWatchBusyContentMetadata(address, 16);
        using var source = new OriginalWatchBusySource(); var mapping = source.Capture();
        MetalVideoPresenter.AttachGuestMemory(source);
        var read = GuestImageWriteTracker.BeginReadSnapshot(address, 16, source: "original-watch-busy-control");
        Assert.True(read.Active); Assert.True(GuestImageWriteTracker.IsReadSnapshotStable(read));
        var content = MetalVideoPresenter.ObserveCpuTextureSnapshot(address, 16, HostMemory.MappingGeneration);
        Assert.True(content.Observed);
        // Metadata-only original DTO: does not register a GPU producer, native
        // texture, permission, capture authority or qualified descriptor layout.
        var descriptor = new GuestDrawTexture(address, 2, 2, 4, 7, [], false, true, Descriptor: new uint[8]);
        var physical = new MetalStoragePhysicalPlacement.Observation(source, source, mapping, descriptor,
            address, 16, MetalVideoPresenter.CurrentStorageCpuSeedSession);
        var type = Presenter.GetNestedType("OrdinaryStorageOriginal", BindingFlags.NonPublic)!;
        var original = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [physical, source, read, content], culture: null)!;
        object? token = variant == "current-then-busy" ? null : source.Begin();
        source.BeforeStatus = query =>
        {
            if (variant == "current-then-busy" && query == 2) token = source.Begin();
            if (query == 1 && variant == "owner-change") MetalVideoPresenter.AttachGuestMemory(new Memory([1]));
            if (query == 1 && variant == "session-change") Field("_selectedStorageMipSession").SetValue(null, physical.Session + 1);
            if (query == 1 && variant == "token-change") protection.Generation(1);
            if ((variant == "busy-release" && query == 2 || variant == "current-then-busy" && query == 3) && token is not null)
            { var exact = token; token = null; source.End(exact); }
        };
        try
        {
            void Check()
            {
                try { Presenter.GetMethod("RequireOrdinaryStorageOriginalContent", Private)!.Invoke(null, [original, null]); }
                catch (TargetInvocationException failure) when (failure.InnerException is not null)
                { ExceptionDispatchInfo.Capture(failure.InnerException).Throw(); }
            }
            if (variant is "busy-release" or "current-then-busy")
            {
                Check(); Assert.True(source.StatusQueries >= 3);
                Assert.True(source.IsMappedCopyObservationCurrent(mapping));
                Assert.True(GuestImageWriteTracker.IsReadSnapshotStable(read));
            }
            else
            { Assert.Throws<NotSupportedException>(Check); Assert.Equal(1, source.StatusQueries); }
            Assert.Equal(0, source.BooleanQueries); Assert.Equal(1, source.Copies);
            Assert.Same(mapping, physical.Mapping); Assert.Same(source, physical.Memory); Assert.Same(source, physical.Source);
        }
        finally { if (token is not null) source.End(token); source.ReleaseMappedCopyWriteWatch(mapping); }
        Assert.Equal(1, source.Releases); Assert.Equal(0, source.Memory.Backing.ActiveAccesses);
    }

    [Theory]
    [InlineData("busy-contention-clears")] [InlineData("busy-contention-expired")] [InlineData("current-lock")]
    public void PresenterMetadataContentionIsBoundedWithoutChangedOwnerInference(string variant)
    {
        ActualArchitecture(); using var state = new State();
        var gate = Field("_gate").GetValue(null)!;
        var entered = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        Exception? actorFailure = null; var started = false; var startAttempted = false;
        var actor = new Thread(() =>
        {
            try
            {
                lock (gate)
                {
                    entered.Set();
                    if (!release.Wait(10000)) throw new TimeoutException("Owned metadata actor release bound expired.");
                    if (variant != "busy-contention-expired") Thread.Sleep(20);
                }
            }
            catch (Exception failure) { actorFailure = failure; }
        });
        try
        {
            startAttempted = true; actor.Start(); started = true; Assert.True(entered.Wait(10000));
            var unexpectedEntered = Monitor.TryEnter(gate);
            if (unexpectedEntered) Monitor.Exit(gate);
            Assert.False(unexpectedEntered); // Actual foreign-thread gate contention, no native owner.
            if (variant != "busy-contention-expired") release.Set();
            void Enter()
            {
                try
                {
                    Presenter.GetMethod("EnterOriginalWatchMetadataGate", Private)!.Invoke(null,
                        [variant == "current-lock" ? null : (object)(Environment.TickCount64 + (variant == "busy-contention-expired" ? 25 : 500))]);
                }
                catch (TargetInvocationException failure) when (failure.InnerException is not null)
                { ExceptionDispatchInfo.Capture(failure.InnerException).Throw(); }
                Monitor.Exit(gate); // Release only a known successful acquisition.
            }
            if (variant == "busy-contention-expired")
            {
                var failure = Assert.Throws<NotSupportedException>(Enter);
                Assert.Contains("metadata wait expired", failure.Message);
                Assert.Contains("no changed-owner inference", failure.Message);
            }
            else Enter();
        }
        finally
        {
            release.Set();
            // Exact actor has one bounded join. Unknown join retains its events
            // through the actor closure instead of disposing objects it may use.
            if (startAttempted && !started) throw new InvalidOperationException("Owned metadata actor start outcome unknown; events retained.");
            if (started && !actor.Join(10000)) throw new TimeoutException("Owned metadata actor termination unknown; events retained.");
            entered.Dispose(); release.Dispose();
        }
        Assert.Null(actorFailure);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void LegacyBooleanOnlyOriginalCurrentPathIsPreservedWithoutStatusAuthority(bool current)
    {
        ActualArchitecture(); using var state = new State(); using var source = new OriginalWatchBusySource();
        var original = source.Capture(); var legacy = new OriginalWatchBooleanOnly(current);
        try
        {
            if (current) WaitOriginalWatch(legacy, original, () => throw new InvalidOperationException("No legacy wait."));
            else Assert.Throws<NotSupportedException>(() => WaitOriginalWatch(legacy, original, () => throw new InvalidOperationException("No legacy wait.")));
            Assert.Equal(1, legacy.BooleanQueries); Assert.Equal(0, legacy.StatusQueries); Assert.Equal(1, source.Copies);
        }
        finally { source.ReleaseMappedCopyWriteWatch(original); }
    }
    private sealed class OriginalWatchBooleanOnly(bool current) : ICpuMappedReadWatchSource
    {
        internal int BooleanQueries, StatusQueries;
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation original) => true;
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original) { BooleanQueries++; return current; }
        public CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(CpuMappedCopyObservation original)
        { StatusQueries++; throw new InvalidOperationException("Legacy provider has no status authority."); }
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation original) => throw new InvalidOperationException("No new watch owner.");
        public CpuMappedCopyResult TryReadMappedCopy(ulong a, ulong b, ulong c, Span<byte> bytes, out CpuMappedCopyObservation? original)
            => throw new InvalidOperationException("No recapture.");
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong a, ulong b, ulong c, Span<byte> bytes, out CpuMappedCopyObservation? original)
            => throw new InvalidOperationException("No recapture.");
    }
}
