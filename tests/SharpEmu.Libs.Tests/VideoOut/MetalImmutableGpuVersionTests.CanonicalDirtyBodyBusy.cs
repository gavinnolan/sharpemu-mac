// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Memory;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalImmutableGpuVersionTests
{
    [FiniteStoragePublicationNativeFact]
    public void ActualDifferentBackingInitialOwnerBusyClearsBeforeDeferralAndRetriesSameOriginal() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, bodyBusyWindow: "precheck-clears");

    [FiniteStoragePublicationNativeFact]
    public void ActualDifferentBackingBodyBusyBeforeDirtyConsumptionDefersUntilOriginalRetry() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, bodyBusyWindow: "before");
    [FiniteStoragePublicationNativeFact]
    public void ActualDifferentBackingBodyBusyAfterDirtyConsumptionRestoresOriginalAndRetries() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, bodyBusyWindow: "after");
    [FiniteStoragePublicationNativeFact]
    public void ActualDifferentBackingOverlappingWriterDuringDirtyBodyRemainsFatal() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, bodyBusyWindow: "overlap");
    [FiniteStoragePublicationNativeFact]
    public void ActualUnrelatedDirtyBodyExceptionCannotBorrowConcurrentOrdinaryBusy() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, bodyBusyWindow: "unrelated-error");
    [FiniteStoragePublicationNativeFact]
    public void ActualConsumedDirtyBodyBusyWithUncertainCancellationPreservesDirtyAndQuarantinesOwners()
    {
        var failed = Assert.Throws<TargetInvocationException>(() =>
            RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, bodyBusyWindow: "cancel-unknown"));
        Assert.Equal("SingletonVolumeOriginalBusy", failed.InnerException!.GetType().Name);
        Assert.Contains("simulated uncertain validation cancellation", Assert.IsType<IOException>(
            failed.InnerException.Data["CanonicalDirtyValidationCleanup"]).Message);
        Assert.NotNull(finiteStorageFixtureQuarantine); // fresh worker exit owns all exact backing/native owners
    }

    // Dispatches only into two actual, separately owned PVM providers. No
    // opaque observation, watch, status, writer history or token is fabricated.
    private sealed class BodyBusyRoutedMemory(PhysicalVirtualMemory image, PhysicalVirtualMemory volumes,
        ulong volumeAddress, ulong volumeBytes, bool failValidationCancellation) : ICpuMemory, ICpuMappedReadWatchSource, ICpuMappedPublicationSource
    {
        private CpuMappedPublicationValidation? retainedUnknownValidation;
        private bool validationCancellationArmed;
        internal void ArmValidationCancellation()
        {
            Assert.True(failValidationCancellation); Assert.False(validationCancellationArmed);
            Assert.Null(retainedUnknownValidation); validationCancellationArmed = true;
        }
        private PhysicalVirtualMemory Route(ulong address, ulong count) =>
            address >= volumeAddress && address - volumeAddress <= volumeBytes && count <= volumeBytes - (address - volumeAddress)
                ? volumes : image;
        private PhysicalVirtualMemory Route(CpuMappedCopyObservation original) => Route(original.FootprintAddress, original.FootprintByteCount);
        public bool TryRead(ulong address, Span<byte> destination) => Route(address, (ulong)destination.Length).TryRead(address, destination);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => Route(address, (ulong)source.Length).TryWrite(address, source);
        public bool CanRead(ulong address, ulong count) => Route(address, count).CanRead(address, count);
        public string DescribeReadRange(ulong address, ulong count) => Route(address, count).DescribeReadRange(address, count);
        public CpuMappedCopyResult TryReadMappedCopy(ulong address, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? original) =>
            Route(address, count).TryReadMappedCopy(address, count, source, bytes, out original);
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong address, ulong count, ulong source, Span<byte> bytes, out CpuMappedCopyObservation? original) =>
            Route(address, count).TryReadMappedCopyWithWriteWatch(address, count, source, bytes, out original);
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation original) => Route(original).IsMappedCopyObservationCurrent(original);
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation original) => Route(original).IsMappedCopyWriteWatchCurrent(original);
        public CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(CpuMappedCopyObservation original) => Route(original).GetMappedCopyWriteWatchStatus(original);
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation original) => Route(original).ReleaseMappedCopyWriteWatch(original);
        public CpuMappedPublicationPreparationResult TryPrepareMappedReadValidation(CpuMappedCopyObservation original, out CpuMappedReadValidation? ticket) =>
            Route(original).TryPrepareMappedReadValidation(original, out ticket);
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(CpuMappedCopyObservation original, out CpuMappedPublicationValidation? ticket)
        {
            var result = Route(original).TryPrepareMappedPublicationValidation(original, out ticket);
            if (validationCancellationArmed && ticket is not null)
            {
                // Failure injection AFTER a real provider cancellation. The
                // consumer cannot certify its return; no fake positive receipt.
                validationCancellationArmed = false;
                retainedUnknownValidation = ticket;
                ticket = new CpuMappedPublicationValidation(new UncertainBodyCancellation(ticket));
            }
            return result;
        }
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(CpuMappedCopyObservation original, IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? ticket) =>
            Route(original).TryPrepareMappedPublication(original, runs, out ticket);
    }

    private sealed class UncertainBodyCancellation(CpuMappedPublicationValidation original) : CpuMappedPublicationValidationTransaction
    {
        private bool consumed;
        internal override bool IsCurrent => !consumed && original.IsCurrent;
        internal override bool IsOriginalDisjointReadWatchCurrent(CpuMappedCopyObservation watch) =>
            !consumed && original.IsOriginalDisjointReadWatchCurrent(watch);
        internal override void Cancel()
        {
            Assert.False(consumed); consumed = true;
            original.Dispose();
            throw new IOException("simulated uncertain validation cancellation");
        }
    }

    private void RunBodyBusyOriginalRetry(NativeState state, PhysicalVirtualMemory physical, TrackedCpuMemory memory,
        FiniteStorageFixtureOwners owners, ulong address, int imageBytes, byte[] expected, object image,
        object origin, Func<byte[]> readCanonical, string scenario)
    {
        if (scenario == "precheck-clears")
        {
            RunInitialOwnerBusyClears(state, physical, memory, owners, address, imageBytes, expected, image, origin, readCanonical);
            return;
        }
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var volumePhysical = Assert.IsType<PhysicalVirtualMemory>(owners.BodyVolumePhysical);
        SharedBackingViews Views(PhysicalVirtualMemory pvm) => Assert.IsType<SharedBackingViews>(StorageMember(StorageMember(pvm, "_backedSpace")!, "_views"));
        var ownViews = Views(physical); var volumeViews = Views(volumePhysical);
        var ownLedger = StorageMember(StorageMember(ownViews, "_backing")!, "_managedWrites")!;
        var volumeLedger = StorageMember(StorageMember(volumeViews, "_backing")!, "_managedWrites")!;
        Assert.NotSame(ownLedger, volumeLedger);
        var ownMapping = (CpuMappedCopyObservation)StorageMember(origin, "Mapping")!;
        Assert.Equal(2, owners.VolumeImages.Count);
        var firstVolume = owners.VolumeImages[0];
        var firstOrigin = StorageMember(firstVolume, "StorageCpuOrigin")!;
        var volumeMapping = (CpuMappedCopyObservation)StorageMember(firstOrigin, "Mapping")!;
        Assert.NotEqual(ownMapping.Segments[0].BackingIdentity, volumeMapping.Segments[0].BackingIdentity);
        var volumeOriginals = owners.VolumeImages.Select(owner => StorageMember(owner, "StorageCpuOrigin")!).ToArray();
        var nativeOriginals = owners.VolumeImages.Select(owner => ReadNativeVolume(owner)).ToArray();
        // The independent volume backing retained the INITIAL seed; the image-only EOP never writes it.
        var extraExpected = Enumerable.Range(imageBytes, 3 * 65536).Select(i => (byte)(i * 19 + 7)).ToArray();
        var extraBefore = ReadExtra(); Assert.Equal(extraExpected, extraBefore);
        var writeAddress = scenario == "overlap" ? volumeMapping.SourceAddress : owners.BodyVolumeAddress + 32768;
        Assert.True(volumeViews.TryResolveAlias(writeAddress, 1, out var alias));
        var writeHook = typeof(SharedBackingViews).GetField("_beforeCanonicalAliasCopyForControl", instance)!;
        var oldWrite = writeHook.GetValue(volumeViews);
        var bodyHook = Field("_canonicalDirtyBeforeBodyValidationForControl"); var oldBody = bodyHook.GetValue(null);
        var oldRetry = Field("_deferredCanonicalDirty").GetValue(null); Assert.Null(oldRetry);
        var token = (GuestImageWriteTracker.ContentToken)StorageMember(StorageMember(origin, "Observation")!, "Token")!;
        var version = StorageMember(image, "ContentVersion"); var texture = StorageMember(image, "Texture");
        var completed = Field("_completedGuestWorkSequence").GetValue(null);
        var nativeBuffer = Field("_lastFlushedCommandBuffer").GetValue(null);
        var pause = new BusyOrdinaryWriter(); owners.OrdinaryWriterOwner = pause;
        var cause = new IOException("unrelated ORIGINAL body validation failure");
        Exception? primary = null; var hookReached = false;
        owners.AfterOwnedRetirement = () => Field("_deferredCanonicalDirty").SetValue(null, oldRetry);
        try
        {
            writeHook.SetValue(volumeViews, (Action<ulong, int>)((destination, count) =>
            {
                Assert.Equal(alias, destination); Assert.Equal(1, count);
                Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                Assert.False(Monitor.IsEntered(StorageMember(volumeViews, "_lock")!));
                var gate = Assert.IsType<ReaderWriterLockSlim>(StorageMember(volumePhysical, "_gate"));
                Assert.False(gate.IsReadLockHeld || gate.IsWriteLockHeld || gate.IsUpgradeableReadLockHeld);
                Assert.Equal(2L, StorageMember(volumeLedger, "admission"));
                writeHook.SetValue(volumeViews, oldWrite);
                pause.Admitted.Set();
                if (!pause.Resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("different-backing writer bound");
            }));
            bodyHook.SetValue(null, (Action<bool>)(consumed =>
            {
                if (consumed != (scenario is "after" or "cancel-unknown")) return;
                Assert.False(hookReached); hookReached = true;
                bodyHook.SetValue(null, oldBody);
                Assert.Equal(4294967296L, StorageMember(ownLedger, "admission")); // actual own validation is held
                Assert.Equal(!consumed, GuestImageWriteTracker.PeekDirty(address));
                pause.Thread = new Thread(() =>
                {
                    try { pause.Written = volumeViews.TryWriteBacking(writeAddress, new byte[] { 0xA9 }); }
                    catch (Exception failure) { pause.Failure = failure; }
                }) { IsBackground = true, Name = "different-backing dirty-body writer" };
                pause.Thread.Start(); Assert.True(pause.Admitted.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(scenario == "overlap" ? CpuMappedReadWatchStatus.Stale : CpuMappedReadWatchStatus.Busy,
                    ((ICpuMappedReadWatchSource)memory).GetMappedCopyWriteWatchStatus(volumeMapping));
                if (scenario == "unrelated-error") throw cause;
            }));
            GuestImageWriteTracker.RestoreDirtyAtProtectedOwner(address, token);
            if (scenario == "cancel-unknown")
            {
                owners.ConstructorReservationUnconfirmed = true; // consumer has no known cancellation outcome
                var failed = Assert.Throws<TargetInvocationException>(() => Call("TryAcknowledgeFiniteCanonicalDirty", address, null));
                Assert.Equal("SingletonVolumeOriginalBusy", failed.InnerException!.GetType().Name);
                Assert.IsType<IOException>(failed.InnerException.Data["CanonicalDirtyValidationCleanup"]);
                Assert.True(GuestImageWriteTracker.PeekDirty(address));
                Assert.True((bool)StorageMember(FiniteDebt(image), "Failed")!);
                Assert.True((bool)StorageMember(FiniteDebt(image), "Pending")!);
                Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
                Assert.Equal(completed, Field("_completedGuestWorkSequence").GetValue(null));
                Assert.Equal(texture, StorageMember(image, "Texture")); Assert.Equal(version, StorageMember(image, "ContentVersion"));
                output.WriteLine("canonical-dirty-body-native scenario=cancel-unknown; distinct_backings=true; initial_status=4; consumed_original_dirty_restored=true; debt_sticky=true; no_ack=true; owners_quarantined=true; simulated_cancellation_error_after_real_provider_release=true");
                throw failed;
            }
            if (scenario is "before" or "after")
            {
                Assert.Equal("Deferred", Call("TryAcknowledgeFiniteCanonicalDirty", address, null)!.ToString());
                Assert.NotNull(Field("_deferredCanonicalDirty").GetValue(null));
                AssertFiniteDebtSettled(image);
                Assert.Null(Field("_textureSourceCleanupFailure").GetValue(null));
            }
            else
            {
                var failure = Assert.Throws<TargetInvocationException>(() => Call("TryAcknowledgeFiniteCanonicalDirty", address, null));
                if (scenario == "unrelated-error") Assert.Same(cause, failure.InnerException);
                else
                {
                    Assert.IsType<InvalidOperationException>(failure.InnerException);
                    Assert.Contains("lost its ORIGINAL", failure.InnerException!.Message);
                }
                Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
            }
            Assert.True(hookReached); Assert.True(GuestImageWriteTracker.PeekDirty(address));
            Assert.Equal(0L, StorageMember(ownLedger, "admission")); // canceled before retry metadata
            Assert.Equal(version, StorageMember(image, "ContentVersion")); Assert.Equal(texture, StorageMember(image, "Texture"));
            Assert.Same(origin, StorageMember(image, "StorageCpuOrigin"));
            Assert.Equal(completed, Field("_completedGuestWorkSequence").GetValue(null));
            Assert.Equal(nativeBuffer, Field("_lastFlushedCommandBuffer").GetValue(null));
            Assert.False((bool)Field("_batchOpen").GetValue(null)!);
            Assert.Equal(expected, readCanonical()); Assert.Equal(extraBefore, ReadExtra()); // writer is still BEFORE bytes
            var label = 0; long sequence = 0;
            if (scenario is "before" or "after")
            {
                sequence = MetalVideoPresenter.SubmitOrderedGuestAction(() => label++, "body Busy future action");
                Assert.False(state.TryDrainBusyNative()); Assert.Equal(0, label);
                Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
            }
            pause.Resume.Set(); Assert.True(pause.Thread!.Join(TimeSpan.FromSeconds(5)));
            if (pause.Failure is { } writerFailure) ExceptionDispatchInfo.Capture(writerFailure).Throw();
            Assert.True(pause.Written); extraExpected[checked((int)(writeAddress - owners.BodyVolumeAddress))] = 0xA9;
            Assert.Equal(extraExpected, ReadExtra()); Assert.Equal(expected, readCanonical());
            if (scenario is "before" or "after")
            {
                Assert.True(state.TryDrainBusyNative()); Assert.Equal(1, label);
                Assert.True(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
                Assert.False(GuestImageWriteTracker.PeekDirty(address)); Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
                AssertFiniteDebtSettled(image);
            }
            for (var index = 0; index < owners.VolumeImages.Count; index++)
            {
                Assert.Same(volumeOriginals[index], StorageMember(owners.VolumeImages[index], "StorageCpuOrigin"));
                Assert.Equal(nativeOriginals[index], ReadNativeVolume(owners.VolumeImages[index]));
            }
            Assert.Equal(version, StorageMember(image, "ContentVersion")); Assert.Equal(texture, StorageMember(image, "Texture"));
            output.WriteLine($"canonical-dirty-body-native scenario={scenario}; distinct_backings=true; own_validation_held=true; initial_status=4; all_native_volume_bytes=true; all_canonical_neighbors=true; original_token=true; result={(scenario is "before" or "after" ? "deferred-then-exact-retry" : "strict-original-failure-no-ack")}");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            Exception? cleanup = null; pause.Resume.Set();
            try
            {
                if (pause.Thread is { } writer && !writer.Join(TimeSpan.FromSeconds(5)))
                { owners.OrdinaryWriterUnconfirmed = true; cleanup = new TimeoutException("dirty-body writer still live; all owners quarantined"); }
            }
            catch (Exception failure) { owners.OrdinaryWriterUnconfirmed = true; cleanup = failure; }
            Attempt(() => bodyHook.SetValue(null, oldBody)); Attempt(() => writeHook.SetValue(volumeViews, oldWrite));
            if (!owners.OrdinaryWriterUnconfirmed) Attempt(pause.Dispose);
            if (cleanup is not null)
            {
                if (primary is not null) { try { primary.Data["DirtyBodyWriterCleanup"] = cleanup; } catch { } }
                else ExceptionDispatchInfo.Capture(cleanup).Throw();
            }
            void Attempt(Action action) { try { action(); } catch (Exception failure) { cleanup ??= failure; } }
        }
        byte[] ReadExtra() { var bytes = new byte[3 * 65536]; Assert.True(volumePhysical.TryRead(owners.BodyVolumeAddress, bytes)); return bytes; }
        byte[] ReadNativeVolume(object volume) => ReadOriginalVolume((nint)StorageMember(volume, "Texture")!,
            (uint)StorageMember(volume, "Width")!, (uint)StorageMember(volume, "Height")!, (uint)StorageMember(volume, "Depth")!);
    }

    private void RunInitialOwnerBusyClears(NativeState state, PhysicalVirtualMemory physical, TrackedCpuMemory memory,
        FiniteStorageFixtureOwners owners, ulong address, int imageBytes, byte[] expected, object image,
        object origin, Func<byte[]> readCanonical)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var volumePhysical = Assert.IsType<PhysicalVirtualMemory>(owners.BodyVolumePhysical);
        SharedBackingViews Views(PhysicalVirtualMemory pvm) => Assert.IsType<SharedBackingViews>(StorageMember(StorageMember(pvm, "_backedSpace")!, "_views"));
        var ownViews = Views(physical); var volumeViews = Views(volumePhysical);
        var ownBacking = StorageMember(ownViews, "_backing")!; var volumeBacking = StorageMember(volumeViews, "_backing")!;
        var ownLedger = StorageMember(ownBacking, "_managedWrites")!; var volumeLedger = StorageMember(volumeBacking, "_managedWrites")!;
        Assert.NotSame(ownLedger, volumeLedger); Assert.Equal(2, owners.VolumeImages.Count);
        var ownMapping = (CpuMappedCopyObservation)StorageMember(origin, "Mapping")!;
        var originals = owners.VolumeImages.Select(owner => StorageMember(owner, "StorageCpuOrigin")!).ToArray();
        var mappings = originals.Select(original => (CpuMappedCopyObservation)StorageMember(original, "Mapping")!).ToArray();
        Assert.NotEqual(ownMapping.Segments[0].BackingIdentity, mappings[0].Segments[0].BackingIdentity);
        var nativeBytes = owners.VolumeImages.Select(ReadNative).ToArray();
        var extraExpected = Enumerable.Range(imageBytes, 3 * 65536).Select(i => (byte)(i * 19 + 7)).ToArray();
        Assert.Equal(extraExpected, ReadExtra()); Assert.Equal(expected, readCanonical());
        var ownPins = StorageMember(ownBacking, "ActiveAccesses"); var volumePins = StorageMember(volumeBacking, "ActiveAccesses");
        var token = (GuestImageWriteTracker.ContentToken)StorageMember(StorageMember(origin, "Observation")!, "Token")!;
        var version = StorageMember(image, "ContentVersion"); var texture = StorageMember(image, "Texture");
        var completed = Field("_completedGuestWorkSequence").GetValue(null);
        var nativeBuffer = Field("_lastFlushedCommandBuffer").GetValue(null);
        var writeAddress = owners.BodyVolumeAddress + 32768;
        Assert.True(volumeViews.TryResolveAlias(writeAddress, 1, out var alias));
        var writeHook = typeof(SharedBackingViews).GetField("_beforeCanonicalAliasCopyForControl", instance)!;
        var oldWrite = writeHook.GetValue(volumeViews);
        var firstHook = Field("_canonicalDirtyAfterInitialOwnerCheckForControl"); var oldFirst = firstHook.GetValue(null);
        var bodyHook = Field("_canonicalDirtyBeforeBodyValidationForControl"); var oldBody = bodyHook.GetValue(null);
        var oldRetry = Field("_deferredCanonicalDirty").GetValue(null); Assert.Null(oldRetry);
        var pause = new BusyOrdinaryWriter(); owners.OrdinaryWriterOwner = pause;
        owners.AfterOwnedRetirement = () => Field("_deferredCanonicalDirty").SetValue(null, oldRetry);
        Exception? primary = null; var hookReached = false;
        try
        {
            writeHook.SetValue(volumeViews, (Action<ulong, int>)((destination, count) =>
            {
                Assert.Equal(alias, destination); Assert.Equal(1, count);
                Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                Assert.False(Monitor.IsEntered(StorageMember(volumeViews, "_lock")!));
                var gate = Assert.IsType<ReaderWriterLockSlim>(StorageMember(volumePhysical, "_gate"));
                Assert.False(gate.IsReadLockHeld || gate.IsWriteLockHeld || gate.IsUpgradeableReadLockHeld);
                Assert.Equal(2L, StorageMember(volumeLedger, "admission"));
                writeHook.SetValue(volumeViews, oldWrite); pause.Admitted.Set();
                if (!pause.Resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("initial-owner writer bound");
            }));
            bodyHook.SetValue(null, (Action<bool>)(_ => throw new InvalidOperationException("first healthy Busy must defer before dirty-body validation")));
            firstHook.SetValue(null, (Action)(() =>
            {
                Assert.False(hookReached); hookReached = true; firstHook.SetValue(null, oldFirst);
                Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                Assert.True(GuestImageWriteTracker.PeekDirty(address));
                Assert.Equal(0L, StorageMember(ownLedger, "admission"));
                Assert.Equal(2L, StorageMember(volumeLedger, "admission"));
                pause.Resume.Set(); Assert.True(pause.Thread!.Join(TimeSpan.FromSeconds(5)));
                if (pause.Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
                Assert.True(pause.Written);
                foreach (var mapping in mappings)
                    Assert.Equal(CpuMappedReadWatchStatus.Current, ((ICpuMappedReadWatchSource)memory).GetMappedCopyWriteWatchStatus(mapping));
                Assert.Equal(ownPins, StorageMember(ownBacking, "ActiveAccesses"));
                Assert.Equal(volumePins, StorageMember(volumeBacking, "ActiveAccesses"));
            }));
            GuestImageWriteTracker.RestoreDirtyAtProtectedOwner(address, token);
            pause.Thread = new Thread(() =>
            {
                try { pause.Written = volumeViews.TryWriteBacking(writeAddress, new byte[] { 0xA9 }); }
                catch (Exception failure) { pause.Failure = failure; }
            }) { IsBackground = true, Name = "initial-owner different-backing writer" };
            pause.Thread.Start(); Assert.True(pause.Admitted.Wait(TimeSpan.FromSeconds(5)));
            foreach (var mapping in mappings)
                Assert.Equal(CpuMappedReadWatchStatus.Busy, ((ICpuMappedReadWatchSource)memory).GetMappedCopyWriteWatchStatus(mapping));
            Assert.Equal("Deferred", Call("TryAcknowledgeFiniteCanonicalDirty", address, null)!.ToString());
            Assert.True(hookReached); Assert.NotNull(Field("_deferredCanonicalDirty").GetValue(null));
            Assert.True(GuestImageWriteTracker.PeekDirty(address)); AssertFiniteDebtSettled(image);
            Assert.Equal(completed, Field("_completedGuestWorkSequence").GetValue(null));
            Assert.Equal(nativeBuffer, Field("_lastFlushedCommandBuffer").GetValue(null));
            Assert.Equal(version, StorageMember(image, "ContentVersion")); Assert.Equal(texture, StorageMember(image, "Texture"));
            Assert.Same(origin, StorageMember(image, "StorageCpuOrigin")); Assert.False((bool)Field("_batchOpen").GetValue(null)!);
            extraExpected[checked((int)(writeAddress - owners.BodyVolumeAddress))] = 0xA9;
            Assert.Equal(extraExpected, ReadExtra()); Assert.Equal(expected, readCanonical());
            for (var i = 0; i < owners.VolumeImages.Count; i++)
            { Assert.Same(originals[i], StorageMember(owners.VolumeImages[i], "StorageCpuOrigin")); Assert.Equal(nativeBytes[i], ReadNative(owners.VolumeImages[i])); }
            bodyHook.SetValue(null, oldBody); // next ordinary drain may perform the original exact-current body
            var label = 0; var sequence = MetalVideoPresenter.SubmitOrderedGuestAction(() => label++, "initial-owner Busy future action");
            Assert.Equal(0, label); Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
            Assert.True(state.TryDrainBusyNative()); Assert.Equal(1, label); Assert.True(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
            Assert.False(GuestImageWriteTracker.PeekDirty(address)); Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
            AssertFiniteDebtSettled(image); Assert.Equal(expected, readCanonical()); Assert.Equal(extraExpected, ReadExtra());
            Assert.Equal(version, StorageMember(image, "ContentVersion")); Assert.Equal(texture, StorageMember(image, "Texture"));
            Assert.Equal(ownPins, StorageMember(ownBacking, "ActiveAccesses")); Assert.Equal(volumePins, StorageMember(volumeBacking, "ActiveAccesses"));
            for (var i = 0; i < owners.VolumeImages.Count; i++) Assert.Equal(nativeBytes[i], ReadNative(owners.VolumeImages[i]));
            output.WriteLine("canonical-dirty-body-native scenario=precheck-clears; distinct_backings=true; initial_status=4; first_actual_Busy=true; writer_joined_before_deferral=true; same_original_Current=true; first_ack_deferred_no_dirty_consume=true; next_drain_exact_retry=true; all_native_volume_bytes=true; all_canonical_neighbors=true; original_pins_retained=true; result=deferred-then-exact-retry");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            Exception? cleanup = null; pause.Resume.Set();
            try
            {
                if (pause.Thread is { } writer && !writer.Join(TimeSpan.FromSeconds(5)))
                { owners.OrdinaryWriterUnconfirmed = true; cleanup = new TimeoutException("initial-owner writer still live; all owners quarantined"); }
            }
            catch (Exception failure) { owners.OrdinaryWriterUnconfirmed = true; cleanup = failure; }
            Attempt(() => firstHook.SetValue(null, oldFirst)); Attempt(() => bodyHook.SetValue(null, oldBody)); Attempt(() => writeHook.SetValue(volumeViews, oldWrite));
            if (!owners.OrdinaryWriterUnconfirmed) Attempt(pause.Dispose);
            if (cleanup is not null)
            {
                if (primary is not null) { try { primary.Data["InitialOwnerWriterCleanup"] = cleanup; } catch { } }
                else ExceptionDispatchInfo.Capture(cleanup).Throw();
            }
            void Attempt(Action action) { try { action(); } catch (Exception failure) { cleanup ??= failure; } }
        }
        byte[] ReadExtra() { var bytes = new byte[3 * 65536]; Assert.True(volumePhysical.TryRead(owners.BodyVolumeAddress, bytes)); return bytes; }
        byte[] ReadNative(object volume) => ReadOriginalVolume((nint)StorageMember(volume, "Texture")!,
            (uint)StorageMember(volume, "Width")!, (uint)StorageMember(volume, "Height")!, (uint)StorageMember(volume, "Depth")!);
    }
}
