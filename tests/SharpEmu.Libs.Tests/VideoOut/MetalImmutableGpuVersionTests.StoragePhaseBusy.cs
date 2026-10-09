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
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.Tests.Fixtures;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalImmutableGpuVersionTests
{
    [FiniteStoragePublicationNativeFact]
    public void ActualPreencodeWaitsForHealthyDifferentBackingWriterBeforeAnyDispatch() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, phaseBusyWindow: "preencode:disjoint");
    [FiniteStoragePublicationNativeFact]
    public void ActualPreencodeOverlappingOriginalWriterNeverEncodesOrAcknowledges() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, phaseBusyWindow: "preencode:overlap");
    [FiniteStoragePublicationNativeFact]
    public void ActualPreencodeChangedProtectedTokenCannotBorrowConcurrentBusy() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, phaseBusyWindow: "preencode:token");
    [FiniteStoragePublicationNativeFact]
    public void ActualStatus4PrebufferWaitCommitsMixedBufferAndImageOnceAfterOriginalWriter() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, mixedBuffer: true, originalVolumes: true, phaseBusyWindow: "prebuffer:disjoint");
    [FiniteStoragePublicationNativeFact]
    public void ActualStatus4PrebufferOverlappingOriginalWriterNeverPublishesBufferOrImage() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, mixedBuffer: true, originalVolumes: true, phaseBusyWindow: "prebuffer:overlap");
    [FiniteStoragePublicationNativeFact]
    public void ActualStatus4PrebufferChangedProtectedTokenNeverPublishesBufferOrImage() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, mixedBuffer: true, originalVolumes: true, phaseBusyWindow: "prebuffer:token");
    [FiniteStoragePublicationNativeFact]
    public void ActualEmptyBufferPhaseNewBusyReachesCompletedImagePrecommitWithoutStrictDuplicate() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, phaseBusyWindow: "post-empty-buffer:disjoint");

    [FiniteStoragePublicationNativeFact]
    public void ActualComputeEndedReadbackWaitKeepsOneDispatchAndOneFullPublication() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, phaseBusyWindow: "readback:disjoint");
    [FiniteStoragePublicationNativeFact]
    public void ActualComputeEndedReadbackOverlappingWriterNeverEncodesBlitOrAcknowledges() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, phaseBusyWindow: "readback:overlap");
    [FiniteStoragePublicationNativeFact]
    public void ActualComputeEndedReadbackChangedTokenCannotBorrowBusy() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, phaseBusyWindow: "readback:token");
    [FiniteStoragePublicationNativeFact]
    public void ActualAllocatedReadbackHealthyBusyReachesStatus4PrebufferWithoutReencoding() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, phaseBusyWindow: "readback-allocated:disjoint");
    [FiniteStoragePublicationNativeFact]
    public void ActualAllocatedReadbackOverlappingWriterStillVetoesBeforeBlit() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, phaseBusyWindow: "readback-allocated:overlap");
    [FiniteStoragePublicationNativeFact]
    public void ActualAllocatedReadbackChangedOriginalTokenStillVetoesBeforeBlit() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, phaseBusyWindow: "readback-allocated:token");

    private void RunActualStoragePhaseOriginalBusy(NativeState state, PhysicalVirtualMemory physical, TrackedCpuMemory memory,
        FiniteStorageFixtureOwners owners, ulong address, int imageBytes, int storeOffset, byte[] expected,
        long sequence, long eop, MetalCompiledGuestShader shader, Func<byte[]> canonical, string scenario, bool mixedBuffer)
    {
        var parts = scenario.Split(':'); var phase = parts[0]; scenario = parts[1];
        Assert.True(phase is "preencode" or "prebuffer" or "post-empty-buffer" or "readback" or "readback-allocated");
        var readback = phase is "readback" or "readback-allocated";
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var volumePhysical = Assert.IsType<PhysicalVirtualMemory>(owners.BodyVolumePhysical);
        SharedBackingViews Views(PhysicalVirtualMemory pvm) => Assert.IsType<SharedBackingViews>(StorageMember(StorageMember(pvm, "_backedSpace")!, "_views"));
        var ownViews = Views(physical); var volumeViews = Views(volumePhysical);
        var ownLedger = StorageMember(StorageMember(ownViews, "_backing")!, "_managedWrites")!;
        var volumeLedger = StorageMember(StorageMember(volumeViews, "_backing")!, "_managedWrites")!;
        Assert.NotSame(ownLedger, volumeLedger);
        Assert.Equal(2, owners.VolumeImages.Count);
        var volumeOriginals = owners.VolumeImages.Select(image => StorageMember(image, "StorageCpuOrigin")!).ToArray();
        var volumeTextures = owners.VolumeImages.Select(image => StorageMember(image, "Texture")).ToArray();
        var volumeVersions = owners.VolumeImages.Select(image => StorageMember(image, "ContentVersion")).ToArray();
        var volumeNative = owners.VolumeImages.Select(ReadNativeVolume).ToArray();
        var watched = (CpuMappedCopyObservation)StorageMember(volumeOriginals[0], "Mapping")!;
        var destination = scenario == "overlap" ? watched.SourceAddress : owners.BodyVolumeAddress + 32768;
        Assert.True(volumeViews.TryResolveAlias(destination, 1, out var alias));
        var extraBefore = ReadExtra(); var extraExpected = extraBefore.ToArray();
        var fields = phase == "post-empty-buffer"
            ? new[] { "_finiteStorageAfterEmptyBufferValidationForControl", "_finiteStoragePrecommitBusyForControl" }
            : phase == "readback"
            ? new[] { "_finiteStorageBeforeReadbackValidationForControl", "_finiteStorageReadbackBusyForControl" }
            : phase == "readback-allocated"
            ? new[] { "_finiteStorageAfterReadbackAllocationForControl", "_finiteStoragePrebufferBusyForControl" }
            : phase == "preencode"
            ? new[] { "_finiteStorageBeforePreencodeValidationForControl", "_finiteStoragePreencodeBusyForControl" }
            : new[] { "_finiteStorageBeforePrebufferValidationForControl", "_finiteStoragePrebufferBusyForControl" };
        var saved = fields.Select(name => Field(name).GetValue(null)).ToArray();
        var writeHook = typeof(SharedBackingViews).GetField("_beforeCanonicalAliasCopyForControl", instance)!;
        var oldWrite = writeHook.GetValue(volumeViews);
        var encoderHook = Field("_computeEncoderCreationOverride"); var oldEncoder = encoderHook.GetValue(null);
        var encoders = 0;
        var pause = new BusyOrdinaryWriter(); owners.OrdinaryWriterOwner = pause;
        object? completedReceipt = null; var completions = 0; var started = false; var waited = 0;
        var completedBefore = Field("_completedGuestWorkSequence").GetValue(null);
        Exception? primary = null;
        try
        {
            if (readback) encoderHook.SetValue(null, (Func<nint, nint>)(command =>
            {
                Interlocked.Increment(ref encoders);
                return oldEncoder is Func<nint, nint> prior ? prior(command) :
                    MetalNative.Send(command, MetalNative.Selector("computeCommandEncoder"));
            }));
            writeHook.SetValue(volumeViews, (Action<ulong, int>)((target, count) =>
            {
                Assert.Equal(alias, target); Assert.Equal(1, count);
                Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                Assert.False(Monitor.IsEntered(StorageMember(volumeViews, "_lock")!));
                var pvmGate = Assert.IsType<ReaderWriterLockSlim>(StorageMember(volumePhysical, "_gate"));
                Assert.False(pvmGate.IsReadLockHeld || pvmGate.IsWriteLockHeld || pvmGate.IsUpgradeableReadLockHeld);
                Assert.Equal(2L, StorageMember(volumeLedger, "admission"));
                writeHook.SetValue(volumeViews, oldWrite); // one-shot, future EOP retains ordinary behavior
                pause.Admitted.Set();
                if (!pause.Resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("phase writer pause bound");
            }));
            Field(fields[0]).SetValue(null, (Action<object>)(receipt =>
            {
                if (started) { Assert.Same(completedReceipt, receipt); return; }
                completedReceipt = receipt; completions++; started = true;
                Assert.False((bool)StorageMember(receipt, "SuccessfullyCompleted")!);
                Assert.Equal(phase is "prebuffer" or "post-empty-buffer", (bool)StorageMember(receipt, "Encoded")!);
                if (readback) Assert.Equal(1, Volatile.Read(ref encoders)); // actual compute selector called ONCE before this phase
                var commandBuffer = (nint)StorageMember(receipt, "CommandBuffer")!;
                if (phase is "prebuffer" or "post-empty-buffer" or "readback-allocated")
                {
                    Assert.NotEqual((nint)0, commandBuffer);
                    if (phase != "readback-allocated")
                        Assert.Equal((nint)4, MetalNative.Send(commandBuffer, MetalNative.Selector("status")));
                    else Assert.Equal(Field("_batchCommandBuffer").GetValue(null), commandBuffer); // same still-unsubmitted batch
                    Assert.NotEqual((nint)0, (nint)StorageMember(receipt, "Staging")!);
                }
                else { Assert.Equal((nint)0, commandBuffer); Assert.Equal((nint)0, StorageMember(receipt, "Staging")); }
                Assert.Same(owners.OriginalOrigin, StorageMember(receipt, "Origin"));
                Assert.Equal(expected, canonical()); // before ANY provider image/buffer commit or EOP
                Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                Assert.Equal(0L, StorageMember(ownLedger, "admission"));
                pause.Thread = new Thread(() =>
                {
                    try { pause.Written = volumeViews.TryWriteBacking(destination, new byte[] { 0xA9 }); }
                    catch (Exception failure) { pause.Failure = failure; }
                }) { IsBackground = true, Name = "original volume phase writer" };
                pause.Thread.Start(); Assert.True(pause.Admitted.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(scenario == "overlap" ? CpuMappedReadWatchStatus.Stale : CpuMappedReadWatchStatus.Busy,
                    ((ICpuMappedReadWatchSource)memory).GetMappedCopyWriteWatchStatus(watched));
                if (scenario == "token") GuestImageWriteTracker.NotifyManagedWrite(watched.SourceAddress, 1);
            }));
            Field(fields[1]).SetValue(null, (Action)(() =>
            {
                waited++; Assert.Equal(1, waited); Assert.True(started);
                Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                Assert.Equal(0L, StorageMember(ownLedger, "admission")); // no provider ticket exists BEFORE phase wait
                Assert.Equal(expected, canonical()); Assert.Equal(extraBefore, ReadExtra());
                Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0)); Assert.False(MetalVideoPresenter.WaitForGuestWork(eop, 0));
                Assert.Equal(completedBefore, Field("_completedGuestWorkSequence").GetValue(null));
                Assert.False((bool)StorageMember(completedReceipt!, "Published")!);
                if (phase == "readback-allocated")
                {
                    Assert.True((bool)StorageMember(completedReceipt!, "Encoded")!);
                    Assert.Equal((nint)4, MetalNative.Send((nint)StorageMember(completedReceipt!, "CommandBuffer")!, MetalNative.Selector("status")));
                }
                var receiptImage = StorageMember(completedReceipt!, "Image")!;
                Assert.Same(owners.OriginalOrigin, StorageMember(receiptImage, "StorageCpuOrigin"));
                Assert.Equal(StorageMember(completedReceipt!, "Version"), StorageMember(receiptImage, "ContentVersion"));
                FinishWriter();
                Assert.Equal(CpuMappedReadWatchStatus.Current, ((ICpuMappedReadWatchSource)memory).GetMappedCopyWriteWatchStatus(watched));
                foreach (var origin in volumeOriginals) Call("ValidateSingletonVolumeOriginalContent", origin);
            }));
            if (scenario is "overlap" or "token")
            {
                var failed = Assert.Throws<TargetInvocationException>(() => state.DrainWithControlledBudget());
                var fatal = Assert.IsType<GpuFatalException>(failed.InnerException);
                Assert.Contains("ORIGINAL", fatal.InnerException!.Message);
                Assert.Equal(0, waited); Assert.Equal(expected, canonical());
                if (readback) Assert.False((bool)StorageMember(completedReceipt!, "Encoded")!);
                Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0)); Assert.False(MetalVideoPresenter.WaitForGuestWork(eop, 0));
                Assert.Equal(completedBefore, Field("_completedGuestWorkSequence").GetValue(null));
                Assert.False((bool)StorageMember(completedReceipt!, "Published")!);
                var debt = FiniteDebt(StorageMember(completedReceipt!, "Image")!);
                Assert.True((bool)StorageMember(debt, "Failed")!); Assert.True((bool)StorageMember(debt, "Pending")!);
                FinishWriter();
            }
            else
            {
                state.DrainWithControlledBudget(); Assert.Equal(1, waited);
                Assert.True(MetalVideoPresenter.WaitForGuestWork(eop, 0));
                BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(storeOffset), 0x11223344);
                if (mixedBuffer) BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(imageBytes + 16384), 0xCAFEBABE);
                BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(imageBytes), 0x13579BDF);
                Assert.Equal(expected, canonical());
                if (mixedBuffer)
                {
                    var originalPhase = StorageMember(completedReceipt!, "OriginalPhase")!;
                    var publications = Assert.IsAssignableFrom<Array>(StorageMember(originalPhase, "BufferPublications"));
                    var publication = Assert.Single(publications.Cast<object>());
                    Assert.True((bool)StorageMember(publication, "Started")!);
                    Assert.True((bool)StorageMember(publication, "Complete")!);
                    Assert.True((bool)StorageMember(publication, "DisposalAttempted")!);
                    Assert.False((bool)StorageMember(publication, "Unknown")!);
                    Assert.Null(StorageMember(publication, "Transaction"));
                    var original = StorageMember(publication, "Origin")!;
                    var continuation = Assert.IsType<CpuMappedCopyObservation>(StorageMember(publication, "Applied"));
                    var input = Assert.IsType<CpuMappedCopyObservation>(StorageMember(original, "Mapping"));
                    Assert.NotSame(input, continuation); Assert.Equal(input.Segments, continuation.Segments);
                    Call("ValidateFiniteStorageCompletedBuffer", publication);
                    Assert.Throws<TargetInvocationException>(() => Call("RequireFiniteStorageBufferOrigin", StorageMember(publication, "Buffer")));
                }
                var image = ((IDictionary)Field("_guestImages").GetValue(null)!)[address]!;
                AssertFiniteDebtSettled(image); state.AssertFinitePipelineCompleted(shader);
                Assert.Equal(2, StorageMember(image, "ContentVersion"));
            }
            Assert.True(started); Assert.Equal(1, completions); Assert.Equal(extraExpected, ReadExtra());
            if (readback) Assert.Equal(1, Volatile.Read(ref encoders));
            for (var i = 0; i < owners.VolumeImages.Count; i++)
            {
                Assert.Same(volumeOriginals[i], StorageMember(owners.VolumeImages[i], "StorageCpuOrigin"));
                Assert.Equal(volumeTextures[i], StorageMember(owners.VolumeImages[i], "Texture"));
                Assert.Equal(volumeVersions[i], StorageMember(owners.VolumeImages[i], "ContentVersion"));
                Assert.Equal(volumeNative[i], ReadNativeVolume(owners.VolumeImages[i]));
            }
            output.WriteLine($"finite-storage-phase-native phase={phase}; scenario={scenario}; actual_compute_encoders={(readback ? Volatile.Read(ref encoders) : -1)}; actual_status4_positive_only=true; distinct_backings=true; real_admission=2; encoded_readback={(bool)StorageMember(completedReceipt!, "Encoded")!}; full_guest_and_native_bytes_positive_only=true; original_native_owners_unchanged=true; publication={(scenario != "disjoint" ? "none" : "one-canonical-version")}; EOP={(scenario != "disjoint" ? "unacknowledged" : "after-publication")}");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            Exception? cleanup = null; pause.Resume.Set();
            try
            {
                if (pause.Thread is { } writer && !writer.Join(TimeSpan.FromSeconds(5)))
                { owners.OrdinaryWriterUnconfirmed = true; cleanup = new TimeoutException("phase writer live; exact owners quarantined"); }
            }
            catch (Exception failure) { owners.OrdinaryWriterUnconfirmed = true; cleanup = failure; }
            for (var i = 0; i < fields.Length; i++) { var index = i; Attempt(() => Field(fields[index]).SetValue(null, saved[index])); }
            Attempt(() => writeHook.SetValue(volumeViews, oldWrite));
            if (readback) Attempt(() => encoderHook.SetValue(null, oldEncoder));
            if (!owners.OrdinaryWriterUnconfirmed) Attempt(pause.Dispose);
            if (cleanup is not null)
            {
                if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                try { primary.Data["FiniteStoragePhaseFixtureCleanup"] = cleanup; } catch { }
            }
            void Attempt(Action action) { try { action(); } catch (Exception failure) { cleanup ??= failure; } }
        }
        void FinishWriter()
        {
            pause.Resume.Set(); Assert.True(pause.Thread!.Join(TimeSpan.FromSeconds(5)));
            if (pause.Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.True(pause.Written); extraExpected[checked((int)(destination - owners.BodyVolumeAddress))] = 0xA9;
        }
        byte[] ReadExtra() { var bytes = new byte[3 * 65536]; Assert.True(volumePhysical.TryRead(owners.BodyVolumeAddress, bytes)); return bytes; }
        byte[] ReadNativeVolume(object volume) => ReadOriginalVolume((nint)StorageMember(volume, "Texture")!,
            (uint)StorageMember(volume, "Width")!, (uint)StorageMember(volume, "Height")!, (uint)StorageMember(volume, "Depth")!);
    }
}
