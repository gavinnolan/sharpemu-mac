// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Collections;
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
    public void ActualNativeBusyPostReleaseRestoresDirtyAndBlocksFrameAndFutureEopUntilOriginalRetry() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, busyOriginalRetry: "retry");

    [FiniteStoragePublicationNativeFact]
    public void ActualNativeBusyOriginalRetryCannotAdoptReplacementOwnerWithNoPendingWork() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, busyOriginalRetry: "replacement");

    [FiniteStoragePublicationNativeFact]
    public void ActualNativeBusyOriginalRetryCannotAdoptNewSessionWithNoPendingWork() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, busyOriginalRetry: "session");

    private void RunBusyOriginalRetry(NativeState state, PhysicalVirtualMemory physical, TrackedCpuMemory memory,
        MetalCommandStreamHost host, FiniteStorageFixtureOwners owners, ulong address, int imageBytes,
        byte[] expected, object image, object origin, Func<byte[]> readCanonical, string scenario)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        // Freeze the selected reader and ORIGINAL identities before any scenario
        // effect. Later checks must not resolve a new view or repair a receipt.
        var selectedReader = host.Memory;
        Assert.Same(physical, selectedReader);
        Assert.Same(selectedReader, Field("_guestMemory").GetValue(null));
        Assert.Equal(2, owners.VolumeImages.Count);
        var originalVolumes = owners.VolumeImages.Select(volume =>
        {
            var original = StorageMember(volume, "StorageCpuOrigin")!;
            var originalMemory = StorageMember(original, "Memory")!;
            var originalSource = StorageMember(original, "Source")!;
            Assert.Same(selectedReader, originalMemory);
            Assert.Same(selectedReader, originalSource);
            return (Image: volume, Origin: original, Memory: originalMemory, Source: originalSource,
                Mapping: (CpuMappedCopyObservation)StorageMember(original, "Mapping")!,
                CopySource: Assert.IsAssignableFrom<ICpuMappedCopySource>(originalSource),
                WatchSource: Assert.IsAssignableFrom<ICpuMappedReadWatchSource>(originalSource));
        }).ToArray();
        // This is the SAME real canonical backing used by the PVM. Invoke its
        // ordinary single-view write route directly: PVM.TryWriteBacking holds
        // a read gate across the copy, which must not be held while paused.
        var space = StorageMember(physical, "_backedSpace")!;
        var views = Assert.IsType<SharedBackingViews>(StorageMember(space, "_views"));
        var writerLedger = StorageMember(StorageMember(views, "_backing")!, "_managedWrites")!;
        Assert.NotNull(writerLedger);
        var viewGate = StorageMember(views, "_lock")!;
        var pvmGate = Assert.IsType<ReaderWriterLockSlim>(StorageMember(physical, "_gate"));
        var presenterGate = Field("_gate").GetValue(null)!;
        var writeAddress = checked(address + (ulong)imageBytes + 32768);
        Assert.True(views.TryResolveAlias(writeAddress, 1, out var alias));
        Assert.True(alias >= views.AliasBase && alias - views.AliasBase < views.Size);
        var writeHook = typeof(SharedBackingViews).GetField("_beforeCanonicalAliasCopyForControl", instance)!;
        var oldWriteHook = writeHook.GetValue(views);
        var releaseHook = Field("_canonicalDirtyAfterValidationReleasedForControl");
        var oldReleaseHook = releaseHook.GetValue(null);
        var oldReceipt = Field("_deferredCanonicalDirty").GetValue(null);
        Assert.Null(oldReceipt);
        var oldSession = Field("_selectedStorageMipSession").GetValue(null);
        var debt = FiniteDebt(image);
        var version = StorageMember(image, "ContentVersion")!;
        var texture = StorageMember(image, "Texture")!;
        var observation = StorageMember(origin, "Observation")!;
        var token = (GuestImageWriteTracker.ContentToken)StorageMember(observation, "Token")!;
        var nativeBuffer = Field("_lastFlushedCommandBuffer").GetValue(null);
        var completed = Field("_completedGuestWorkSequence").GetValue(null);
        var presented = Field("_presentedSequence").GetValue(null);
        var protectedDirty = GuestImageWriteTracker.PeekDirty(address);
        Assert.False(protectedDirty);
        var pause = new BusyOrdinaryWriter();
        owners.OrdinaryWriterOwner = pause;
        Exception? primary = null;
        Exception? releaseControlFailure = null;
        var registry = (IDictionary)Field("_guestImages").GetValue(null)!;
        var replaced = false;
        owners.AfterOwnedRetirement = () =>
        {
            Field("_deferredCanonicalDirty").SetValue(null, oldReceipt);
            Field("_selectedStorageMipSession").SetValue(null, oldSession);
        };
        try
        {
            Assert.Equal(2, owners.VolumeImages.Count);
            CheckOriginalVolumes(CpuMappedReadWatchStatus.Current);
            writeHook.SetValue(views, (Action<ulong, int>)((destination, count) =>
            {
                Assert.Equal(alias, destination); Assert.Equal(1, count);
                Assert.False(Monitor.IsEntered(viewGate));
                Assert.False(Monitor.IsEntered(presenterGate));
                Assert.False(pvmGate.IsReadLockHeld || pvmGate.IsWriteLockHeld || pvmGate.IsUpgradeableReadLockHeld);
                Assert.Equal(2L, StorageMember(writerLedger, "admission")); // real envelope + physical chunk
                Assert.Equal(0, StorageMember(writerLedger, "coverageIncomplete"));
                // Only this exact real admitted chunk is paused. Later EOP
                // canonical writes must execute with their ordinary behavior.
                writeHook.SetValue(views, oldWriteHook);
                // The real ordinary envelope and exact physical chunk, plus
                // the canonical alias lifetime pin, are held BEFORE any byte.
                pause.Admitted.Set();
                if (!pause.Resume.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Busy ordinary writer was not released within the controlled bound.");
            }));
            releaseHook.SetValue(null, (Action)(() =>
            {
                try
                {
                    Assert.False(Monitor.IsEntered(presenterGate));
                    Assert.False(Monitor.IsEntered(viewGate));
                    Assert.False(GuestImageWriteTracker.PeekDirty(address)); // actual consumption precedes release boundary
                    Assert.Equal(1, Interlocked.Increment(ref pause.ReleaseCallbacks));
                    releaseHook.SetValue(null, oldReleaseHook); // one actual post-disposal race
                    pause.Thread = new Thread(() =>
                    {
                        try { pause.Written = views.TryWriteBacking(writeAddress, new byte[] { 0xA9 }); }
                        catch (Exception failure) { pause.Failure = failure; }
                    }) { IsBackground = true, Name = "controlled canonical Busy writer" };
                    pause.Thread.Start();
                    Assert.True(pause.Admitted.Wait(TimeSpan.FromSeconds(5)), "real ordinary chunk admission must be reached");
                    Assert.True(pause.Thread.IsAlive);
                    CheckOriginalVolumes(CpuMappedReadWatchStatus.Busy);
                }
                catch (Exception failure) { releaseControlFailure = failure; throw; }
            }));
            // Restore only the existing ORIGINAL protected owner's coalesced
            // dirty bit. No new token, arm, generation or watch is supplied.
            GuestImageWriteTracker.RestoreDirtyAtProtectedOwner(address, token);
            Assert.True(GuestImageWriteTracker.PeekDirty(address));
            Assert.Equal("Deferred", Call("TryAcknowledgeFiniteCanonicalDirty", address, null)!.ToString());
            if (releaseControlFailure is { } hookFailure) ExceptionDispatchInfo.Capture(hookFailure).Throw();
            Assert.Equal(1, pause.ReleaseCallbacks);
            Assert.True(GuestImageWriteTracker.PeekDirty(address)); // consumed bit was actually restored
            Assert.Null(Field("_guestWorkFailure").GetValue(null));
            Assert.Null(Field("_textureSourceCleanupFailure").GetValue(null));
            AssertFiniteDebtSettled(image);
            var receipt = Field("_deferredCanonicalDirty").GetValue(null)!;
            Assert.NotNull(receipt);
            Assert.Same(image, StorageMember(receipt, "Image"));
            Assert.Same(debt, StorageMember(receipt, "Debt"));
            Assert.Same(origin, StorageMember(receipt, "Origin"));
            Assert.Equal(version, StorageMember(receipt, "Version"));
            Assert.Equal(texture, StorageMember(receipt, "Texture"));
            Assert.Equal(address, StorageMember(receipt, "Address"));
            Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null));
            Assert.Equal(expected, readCanonical()); // paused writer has not applied a byte
            CheckOriginalVolumes(CpuMappedReadWatchStatus.Busy);

            if (scenario != "retry")
            {
                // Both negatives occur BEFORE any future work is admitted.
                // The exact old receipt must reject at pendingCount=0 too.
                if (scenario == "replacement")
                {
                    lock (presenterGate) registry[address] = owners.VolumeImages[0];
                    replaced = true; // actual distinct native owner, never a fake handle
                }
                else
                {
                    Assert.Equal("session", scenario);
                    Assert.Empty((IEnumerable)Field("_selectedStorageMipImages").GetValue(null)!);
                    Call("ReleaseSelectedStorageMipImages"); // actual lifetime/session transition
                    Assert.NotEqual(oldSession, Field("_selectedStorageMipSession").GetValue(null));
                }
                Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null));
                var failure = Assert.Throws<TargetInvocationException>(() => Call("RetryDeferredCanonicalDirty"));
                Assert.Contains("ORIGINAL receipt owner or version changed", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
                Assert.Same(receipt, Field("_deferredCanonicalDirty").GetValue(null));
                Assert.True(GuestImageWriteTracker.PeekDirty(address));
            }

            var futureAddress = checked(address + (ulong)imageBytes + 8);
            var futureOrdinary = 0;
            long ordinary, eop;
            using (MetalVideoPresenter.EnterGuestQueue("busy.future.ordinary", 2))
                ordinary = MetalVideoPresenter.SubmitOrderedGuestAction(() => futureOrdinary++, "Busy future ordinary");
            using (MetalVideoPresenter.EnterGuestQueue("busy.future.eop", 3))
            {
                host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write32, 3, futureAddress, 0x2468ACE0));
                eop = (long)Field("_enqueuedGuestWorkSequence").GetValue(null)!;
            }
            Assert.True(ordinary > (long)completed! && eop > ordinary);
            if (scenario == "retry")
            {
                for (var tick = 0; tick < 2; tick++)
                {
                    Assert.False(state.TryDrainBusyNative());
                    state.RenderBusyNativeFrame(); // actual native pool/frame entry, no later frame work
                    Assert.Same(receipt, Field("_deferredCanonicalDirty").GetValue(null));
                    Assert.True(GuestImageWriteTracker.PeekDirty(address));
                    CheckOriginalVolumes(CpuMappedReadWatchStatus.Busy);
                }
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => state.TryDrainBusyNative());
            }
            object?[] take = [null];
            Assert.False((bool)Call("TryTakeGuestWork", take)!);
            Assert.Equal(0, futureOrdinary);
            Assert.Equal(2, Field("_pendingGuestWorkCount").GetValue(null));
            Assert.False(MetalVideoPresenter.WaitForGuestWork(ordinary, 0));
            Assert.False(MetalVideoPresenter.WaitForGuestWork(eop, 0));
            Assert.Equal(completed, Field("_completedGuestWorkSequence").GetValue(null));
            Assert.Equal(presented, Field("_presentedSequence").GetValue(null));
            Assert.Equal(nativeBuffer, Field("_lastFlushedCommandBuffer").GetValue(null));
            Assert.False((bool)Field("_batchOpen").GetValue(null)!);
            Assert.Equal(expected, readCanonical());

            pause.Resume.Set();
            Assert.True(pause.Thread!.Join(TimeSpan.FromSeconds(5)), "ordinary writer must finish before any owner cleanup");
            if (pause.Failure is { } writerFailure) ExceptionDispatchInfo.Capture(writerFailure).Throw();
            Assert.True(pause.Written);
            expected[checked(imageBytes + 32768)] = 0xA9;
            Assert.Equal(expected, readCanonical());
            CheckOriginalVolumes(CpuMappedReadWatchStatus.Current, checkNativeOwner: scenario != "session");
            Assert.Same(origin, StorageMember(image, "StorageCpuOrigin"));
            Assert.Equal(version, StorageMember(image, "ContentVersion"));
            Assert.Equal(texture, StorageMember(image, "Texture"));
            AssertFiniteDebtSettled(image);
            if (scenario == "retry")
            {
                Assert.True(state.TryDrainBusyNative());
                Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
                Assert.False(GuestImageWriteTracker.PeekDirty(address));
                Assert.Equal(1, futureOrdinary);
                Assert.True(MetalVideoPresenter.WaitForGuestWork(ordinary, 0));
                Assert.True(MetalVideoPresenter.WaitForGuestWork(eop, 0));
                BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(imageBytes + 8), 0x2468ACE0);
                Assert.Equal(expected, readCanonical());
                AssertFiniteDebtSettled(image);
                CheckOriginalVolumes(CpuMappedReadWatchStatus.Current);
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => state.TryDrainBusyNative());
                Assert.Same(receipt, Field("_deferredCanonicalDirty").GetValue(null));
                Assert.True(GuestImageWriteTracker.PeekDirty(address));
                Assert.Equal(0, futureOrdinary);
                Assert.False(MetalVideoPresenter.WaitForGuestWork(eop, 0));
                Assert.Equal(completed, Field("_completedGuestWorkSequence").GetValue(null));
                Assert.Equal(expected, readCanonical());
                Call("CompletePresenterShutdown"); // close execution before exact owned resources retire; no queued ack
                Assert.Equal(completed, Field("_completedGuestWorkSequence").GetValue(null));
                Assert.Same(receipt, Field("_deferredCanonicalDirty").GetValue(null));
            }
            output.WriteLine($"busy-original-native scenario={scenario}; initial-CBstatus=4; real-envelope-and-chunk; post-release-dirty-restored; frame-and-future-work-blocked; original-protected-and-physical-watch-retained; all-owned-bytes={expected.Length}; result={(scenario == "retry" ? "exact-retry-then-EOP" : "exact-receipt-veto-no-EOP")}");
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
                    cleanup = new TimeoutException("Busy ordinary writer remains live; backing, native owners and events are quarantined.");
                }
            }
            catch (Exception failure) { owners.OrdinaryWriterUnconfirmed = true; cleanup = failure; }
            Attempt(() => releaseHook.SetValue(null, oldReleaseHook));
            Attempt(() => writeHook.SetValue(views, oldWriteHook));
            if (replaced) Attempt(() => { lock (presenterGate) registry[address] = image; }); // cleanup identity only, never another retry
            if (!owners.OrdinaryWriterUnconfirmed) Attempt(pause.Dispose);
            if (cleanup is not null)
            {
                if (primary is not null) { try { primary.Data["BusyNativeWriterCleanup"] = cleanup; } catch { } }
                else ExceptionDispatchInfo.Capture(cleanup).Throw();
            }
            void Attempt(Action action) { try { action(); } catch (Exception failure) { cleanup ??= failure; } }
        }

        void CheckOriginalVolumes(CpuMappedReadWatchStatus expectedStatus, bool checkNativeOwner = true)
        {
            Assert.Equal(originalVolumes.Length, owners.VolumeImages.Count);
            for (var index = 0; index < originalVolumes.Length; index++)
            {
                var saved = originalVolumes[index];
                var volume = saved.Image;
                Assert.Same(volume, owners.VolumeImages[index]);
                var volumeOrigin = StorageMember(volume, "StorageCpuOrigin")!;
                Assert.Same(saved.Origin, volumeOrigin);
                Assert.Same(saved.Memory, StorageMember(volumeOrigin, "Memory"));
                Assert.Same(saved.Source, StorageMember(volumeOrigin, "Source"));
                var volumeObservation = StorageMember(volumeOrigin, "Observation")!;
                var volumeToken = (GuestImageWriteTracker.ContentToken)StorageMember(volumeObservation, "Token")!;
                var layout = StorageMember(volumeOrigin, "Layout")!;
                var volumeAddress = (ulong)StorageMember(layout, "Address")!;
                var physicalBytes = (ulong)StorageMember(layout, "PhysicalBytes")!;
                var watch = StorageMember(volumeOrigin, "VolumeWatch")!;
                var mapping = (CpuMappedCopyObservation)StorageMember(volumeOrigin, "Mapping")!;
                Assert.Same(mapping, StorageMember(watch, "Mapping"));
                Assert.Same(saved.Mapping, mapping);
                Assert.Equal(1, StorageMember(watch, "State"));
                Assert.Equal(false, StorageMember(watch, "UnknownProducer"));
                Assert.Same(volume, StorageMember(watch, "Image"));
                Assert.Equal(StorageMember(volume, "ContentVersion"), StorageMember(watch, "Version"));
                Assert.Equal(StorageMember(volume, "Texture"), StorageMember(watch, "Texture"));
                Assert.True(saved.CopySource.IsMappedCopyObservationCurrent(saved.Mapping));
                Assert.Equal(expectedStatus, saved.WatchSource.GetMappedCopyWriteWatchStatus(saved.Mapping));
                Assert.True(GuestImageWriteTracker.TryGetProtectedObservedContentToken(volumeAddress, physicalBytes, out var currentToken));
                Assert.Equal(volumeToken, currentToken);
                Assert.True(GuestImageWriteTracker.IsReadSnapshotStable((GuestImageWriteTracker.ReadSnapshot)StorageMember(watch, "Read")!));
                if (checkNativeOwner && expectedStatus == CpuMappedReadWatchStatus.Current)
                    lock (presenterGate) Assert.True((bool)Call("IsSingletonVolumeNativeOwnerCurrentLocked", volume, volumeOrigin)!);
            }
            Assert.True(GuestImageWriteTracker.TryGetProtectedObservedContentToken(address, (ulong)imageBytes, out var imageToken));
            Assert.Equal(token, imageToken);
        }
    }

    private sealed class BusyOrdinaryWriter : IDisposable
    {
        internal readonly ManualResetEventSlim Admitted = new();
        internal readonly ManualResetEventSlim Resume = new();
        internal Thread? Thread;
        internal Exception? Failure;
        internal bool Written;
        internal int ReleaseCallbacks;
        public void Dispose() { Admitted.Dispose(); Resume.Dispose(); }
    }

    private sealed partial class NativeState
    {
        public bool TryDrainBusyNative() => MetalVideoPresenter.TryDrainGuestWork(device, queue);
        public void RenderBusyNativeFrame()
        {
            var oldDevice = Field("_device").GetValue(null);
            var oldQueue = Field("_commandQueue").GetValue(null);
            var oldClose = Field("_closeRequested").GetValue(null);
            try
            {
                Field("_device").SetValue(null, device);
                Field("_commandQueue").SetValue(null, queue);
                Field("_closeRequested").SetValue(null, false);
                Call("RenderFrame");
            }
            finally
            {
                Field("_device").SetValue(null, oldDevice);
                Field("_commandQueue").SetValue(null, oldQueue);
                Field("_closeRequested").SetValue(null, oldClose);
            }
        }
    }
}
