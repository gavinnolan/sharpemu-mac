// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalImmutableGpuVersionTests
{
    [FiniteStoragePublicationNativeFact]
    public void ActualCanonicalDirtyPendingAfterKnownReleaseDefersSameOwnerBeforeFutureEop() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, pendingCanonicalWindow: "retry");
    [FiniteStoragePublicationNativeFact]
    public void ActualPendingCanonicalDirtyCannotBorrowChangedNativeVersion() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, pendingCanonicalWindow: "version");
    [FiniteStoragePublicationNativeFact]
    public void ActualPendingCanonicalDirtyCannotBorrowChangedOriginalProtectedToken() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, pendingCanonicalWindow: "token");
    [FiniteStoragePublicationNativeFact]
    public void ActualPendingCanonicalDirtyCannotBorrowStaleOriginalVolumeWatch() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, pendingCanonicalWindow: "volume-stale");
    [FiniteStoragePublicationNativeFact]
    public void ActualPendingCanonicalDirtyCannotBorrowBusyOriginalVolumeWatch() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, pendingCanonicalWindow: "volume-busy");
    [FiniteStoragePublicationNativeFact]
    public void ActualPendingCanonicalDirtyWithTakenWorkRemainsStrict() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, pendingCanonicalWindow: "taken");
    [FiniteStoragePublicationNativeFact]
    public void ActualUnrelatedPostReleaseFailureCannotBorrowPendingCanonicalWork() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, pendingCanonicalWindow: "release-error");

    private void RunCanonicalPendingWork(NativeState state, PhysicalVirtualMemory physical, TrackedCpuMemory memory,
        MetalCommandStreamHost host, FiniteStorageFixtureOwners owners, ulong address, int imageBytes,
        byte[] expected, object image, object origin, Func<byte[]> readCanonical, string scenario)
    {
        var gate = Field("_gate").GetValue(null)!;
        var pvmGate = Assert.IsType<ReaderWriterLockSlim>(StorageMember(physical, "_gate"));
        var releaseHook = Field("_canonicalDirtyAfterValidationReleasedForControl");
        var oldHook = releaseHook.GetValue(null); var oldRetry = Field("_deferredCanonicalDirty").GetValue(null);
        var pendingHook = Field("_canonicalDirtyAfterPendingWorkRefusalForControl"); var oldPendingHook = pendingHook.GetValue(null);
        Assert.Null(oldRetry); Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null));
        var token = (GuestImageWriteTracker.ContentToken)StorageMember(StorageMember(origin, "Observation")!, "Token")!;
        var version = StorageMember(image, "ContentVersion")!; var texture = StorageMember(image, "Texture")!;
        var debt = FiniteDebt(image); var completed = Field("_completedGuestWorkSequence").GetValue(null);
        var batch = Field("_lastFlushedCommandBuffer").GetValue(null);
        var counterNames = new[] { "_finiteStorageRuntimeProducers", "_finiteStorageRuntimeApplied", "_finiteStorageRuntimeCanonical" };
        var counters = counterNames.Select(name => Field(name).GetValue(null)).ToArray();
        var nativeVolumes = owners.VolumeImages.Select(ReadVolume).ToArray();
        var volumeOrigins = owners.VolumeImages.Select(volume => StorageMember(volume, "StorageCpuOrigin")).ToArray();
        var firstVolumeMapping = (CpuMappedCopyObservation)StorageMember(volumeOrigins[0]!, "Mapping")!;
        var views = Assert.IsType<SharedBackingViews>(StorageMember(StorageMember(physical, "_backedSpace")!, "_views"));
        var viewGate = StorageMember(views, "_lock")!;
        var ledger = StorageMember(StorageMember(views, "_backing")!, "_managedWrites")!;
        var writeHook = views.GetType().GetField("_beforeCanonicalAliasCopyForControl", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var oldWriteHook = writeHook.GetValue(views);
        var pause = new BusyOrdinaryWriter(); owners.OrdinaryWriterOwner = pause;
        var writeAddress = checked(address + (ulong)imageBytes + 32768);
        Assert.True(views.TryResolveAlias(writeAddress, 1, out var writerAlias));
        var futureAddress = checked(address + (ulong)imageBytes + 8);
        var releaseCalls = 0; var pendingCalls = 0; var actionCalls = 0; long ordinary = 0, eop = 0;
        var injected = new IOException("exact unrelated post-release cleanup refusal");
        Exception? primary = null;
        owners.AfterOwnedRetirement = () => Field("_deferredCanonicalDirty").SetValue(null, oldRetry);
        try
        {
            pendingHook.SetValue(null, (Action)(() =>
            {
                Assert.False(Monitor.IsEntered(gate)); Assert.Equal(1, releaseCalls); pendingCalls++;
                Assert.Equal(0, actionCalls); Assert.False(MetalVideoPresenter.WaitForGuestWork(eop, 0));
                if (scenario == "token")
                {
                    Assert.True(memory.TryWrite(address, new byte[] { 0x5B })); expected[0] = 0x5B;
                    Assert.False(GuestImageWriteTracker.TryGetProtectedObservedContentToken(address, (ulong)imageBytes, out var current) && current == token);
                }
                else if (scenario == "volume-stale")
                {
                    Assert.True(physical.TryWriteBacking(firstVolumeMapping.SourceAddress, new byte[] { 0xA9 }));
                    expected[checked((int)(firstVolumeMapping.SourceAddress - address))] = 0xA9;
                    Assert.Equal(CpuMappedReadWatchStatus.Stale, memory.GetMappedCopyWriteWatchStatus(firstVolumeMapping));
                }
                else if (scenario == "volume-busy")
                {
                    writeHook.SetValue(views, (Action<ulong, int>)((destination, count) =>
                    {
                        Assert.Equal(writerAlias, destination); Assert.Equal(1, count);
                        Assert.False(Monitor.IsEntered(gate)); Assert.False(Monitor.IsEntered(viewGate));
                        Assert.False(pvmGate.IsReadLockHeld || pvmGate.IsWriteLockHeld || pvmGate.IsUpgradeableReadLockHeld);
                        Assert.Equal(2L, StorageMember(ledger, "admission"));
                        writeHook.SetValue(views, oldWriteHook); pause.Admitted.Set();
                        if (!pause.Resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("pending original writer did not resume");
                    }));
                    pause.Thread = new Thread(() =>
                    {
                        try { pause.Written = views.TryWriteBacking(writeAddress, new byte[] { 0xA9 }); }
                        catch (Exception failure) { pause.Failure = failure; }
                    }) { IsBackground = true, Name = "pending canonical lower watch Busy" };
                    pause.Thread.Start(); Assert.True(pause.Admitted.Wait(TimeSpan.FromSeconds(5)));
                    Assert.Equal(CpuMappedReadWatchStatus.Busy, memory.GetMappedCopyWriteWatchStatus(firstVolumeMapping));
                }
            }));
            releaseHook.SetValue(null, (Action)(() =>
            {
                Assert.False(Monitor.IsEntered(gate));
                Assert.False(pvmGate.IsReadLockHeld || pvmGate.IsWriteLockHeld || pvmGate.IsUpgradeableReadLockHeld);
                Assert.False(GuestImageWriteTracker.PeekDirty(address));
                Assert.Equal(1, ++releaseCalls); releaseHook.SetValue(null, oldHook);
                // A fresh real cancel-only ticket must be available now: the
                // original acknowledgment ticket was actually disposed.
                Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                    physical.TryPrepareMappedPublicationValidation((CpuMappedCopyObservation)StorageMember(origin, "Mapping")!, out var probe));
                Assert.NotNull(probe); probe!.Dispose();
                using (MetalVideoPresenter.EnterGuestQueue("canonical.pending.ordinary", 91))
                    ordinary = MetalVideoPresenter.SubmitOrderedGuestAction(() =>
                    {
                        Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
                        Assert.False(GuestImageWriteTracker.PeekDirty(address)); actionCalls++;
                    }, "original retry must precede this action");
                using (MetalVideoPresenter.EnterGuestQueue("canonical.pending.eop", 92))
                {
                    host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write32, 3, futureAddress, 0x2468ACE0));
                    eop = (long)Field("_enqueuedGuestWorkSequence").GetValue(null)!;
                }
                Assert.Equal(2, Field("_pendingGuestWorkCount").GetValue(null));
                if (scenario == "version")
                    image.GetType().GetField("ContentVersion")!.SetValue(image, (int)version + 1);
                else if (scenario == "taken")
                {
                    object?[] take = [null]; Assert.True((bool)Call("TryTakeGuestWork", take)!);
                    Assert.Equal(ordinary, Field("_takenGuestWorkSequence").GetValue(null));
                    Assert.Equal(1, Field("_pendingGuestWorkCount").GetValue(null));
                }
                else if (scenario == "release-error") throw injected;
            }));
            GuestImageWriteTracker.RestoreDirtyAtProtectedOwner(address, token);
            if (scenario == "retry")
            {
                Assert.Equal("Deferred", Call("TryAcknowledgeFiniteCanonicalDirty", address, null)!.ToString());
                var retry = Field("_deferredCanonicalDirty").GetValue(null)!; Assert.NotNull(retry);
                Assert.Same(image, StorageMember(retry, "Image")); Assert.Same(debt, StorageMember(retry, "Debt"));
                Assert.Same(origin, StorageMember(retry, "Origin")); Assert.Equal(version, StorageMember(retry, "Version"));
                Assert.Equal(texture, StorageMember(retry, "Texture")); Assert.True(GuestImageWriteTracker.PeekDirty(address));
                Assert.Equal(0, actionCalls); Assert.False(MetalVideoPresenter.WaitForGuestWork(eop, 0));
                Assert.Equal(completed, Field("_completedGuestWorkSequence").GetValue(null));
                Assert.Equal(batch, Field("_lastFlushedCommandBuffer").GetValue(null));
                Assert.Equal(expected, readCanonical()); AssertFiniteDebtSettled(image);
                // Real public drain retries the retained receipt BEFORE taking
                // either future action, then executes the independent EOP once.
                var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + 5 * System.Diagnostics.Stopwatch.Frequency;
                while (!MetalVideoPresenter.WaitForGuestWork(eop, 0))
                {
                    Assert.True(state.TryDrainBusyNative());
                    Assert.True(System.Diagnostics.Stopwatch.GetTimestamp() < deadline,
                        "bounded actual render wrapper must finish original retry and future EOP");
                }
                Assert.Null(Field("_deferredCanonicalDirty").GetValue(null)); Assert.False(GuestImageWriteTracker.PeekDirty(address));
                Assert.Equal(1, actionCalls); Assert.True(MetalVideoPresenter.WaitForGuestWork(ordinary, 0));
                Assert.True(MetalVideoPresenter.WaitForGuestWork(eop, 0));
                BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(imageBytes + 8), 0x2468ACE0);
                Assert.Equal(expected, readCanonical()); AssertFiniteDebtSettled(image);
            }
            else
            {
                var refusal = Assert.Throws<TargetInvocationException>(() => Call("TryAcknowledgeFiniteCanonicalDirty", address, null));
                if (scenario == "release-error") Assert.Same(injected, refusal.InnerException);
                else if (scenario == "version") Assert.Contains("presenter-owner-session-or-version", refusal.InnerException!.Message);
                else
                {
                    Assert.Equal("CanonicalDirtyPendingWork", refusal.InnerException!.GetType().Name);
                    Assert.Equal("Pending", refusal.InnerException.Data["CanonicalDirtyFirstWorkGuard"]);
                }
                Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
                Assert.True(GuestImageWriteTracker.PeekDirty(address));
                Assert.True((bool)StorageMember(debt, "Failed")!); Assert.True((bool)StorageMember(debt, "Pending")!);
                Assert.NotNull(Field("_textureSourceCleanupFailure").GetValue(null));
                Assert.Equal(0, actionCalls); Assert.False(MetalVideoPresenter.WaitForGuestWork(eop, 0));
                Assert.Equal(completed, Field("_completedGuestWorkSequence").GetValue(null));
                Assert.Equal(batch, Field("_lastFlushedCommandBuffer").GetValue(null));
                Assert.Equal(expected, readCanonical());
                Call("CompletePresenterShutdown"); // no retry or future acknowledgment
            }
            Assert.Equal(1, releaseCalls); Assert.Same(origin, StorageMember(image, "StorageCpuOrigin"));
            Assert.Equal(scenario is "version" or "release-error" ? 0 : 1, pendingCalls);
            Assert.Equal(texture, StorageMember(image, "Texture"));
            for (var i = 0; i < counterNames.Length; i++) Assert.Equal(counters[i], Field(counterNames[i]).GetValue(null));
            for (var i = 0; i < owners.VolumeImages.Count; i++)
            {
                Assert.Same(volumeOrigins[i], StorageMember(owners.VolumeImages[i], "StorageCpuOrigin"));
                Assert.Equal(nativeVolumes[i], ReadVolume(owners.VolumeImages[i]));
            }
            if (scenario == "volume-busy")
            {
                pause.Resume.Set();
                if (!pause.Thread!.Join(TimeSpan.FromSeconds(5)))
                {
                    owners.OrdinaryWriterUnconfirmed = true;
                    throw new TimeoutException("actual lower watch Busy writer could not be joined");
                }
                if (pause.Failure is { } writerFailure) ExceptionDispatchInfo.Capture(writerFailure).Throw();
                Assert.True(pause.Written); expected[imageBytes + 32768] = 0xA9;
                Assert.Equal(expected, readCanonical()); Assert.False(MetalVideoPresenter.WaitForGuestWork(eop, 0));
            }
            output.WriteLine($"canonical-pending-native scenario={scenario}; actual-initial-status4; after-known-cancel; original-owner-and-full-volume-bytes; no-encoder-or-Applied-repeat; future-EOP={(scenario == "retry" ? "once-after-same-retry" : "not-acknowledged")}; all-canonical-bytes={expected.Length}");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            Exception? cleanup = null;
            pause.Resume.Set();
            try
            {
                if (pause.Thread is { } writer && !writer.Join(TimeSpan.FromSeconds(5)))
                {
                    owners.OrdinaryWriterUnconfirmed = true;
                    cleanup = new TimeoutException("pending original writer remains live; exact native/backing owners quarantined");
                }
                else if (pause.Failure is { } writerFailure) cleanup = writerFailure;
            }
            catch (Exception failure) { owners.OrdinaryWriterUnconfirmed = true; cleanup = failure; }
            try { releaseHook.SetValue(null, oldHook); } catch (Exception failure) { cleanup ??= failure; }
            try { pendingHook.SetValue(null, oldPendingHook); } catch (Exception failure) { cleanup ??= failure; }
            try { writeHook.SetValue(views, oldWriteHook); } catch (Exception failure) { cleanup ??= failure; }
            if (!owners.OrdinaryWriterUnconfirmed)
                try { pause.Dispose(); } catch (Exception failure) { cleanup ??= failure; }
            // Restoration is teardown only; negative cases never revalidate or
            // retry this owner. Existing owner disposal retains unknown debt.
            try { image.GetType().GetField("ContentVersion")!.SetValue(image, version); } catch (Exception failure) { cleanup ??= failure; }
            if (cleanup is not null)
            {
                if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                try { primary.Data["CanonicalPendingFixtureCleanup"] = cleanup; } catch { }
            }
        }
        byte[] ReadVolume(object volume) => ReadOriginalVolume((nint)StorageMember(volume, "Texture")!,
            (uint)StorageMember(volume, "Width")!, (uint)StorageMember(volume, "Height")!, (uint)StorageMember(volume, "Depth")!);
    }
}
