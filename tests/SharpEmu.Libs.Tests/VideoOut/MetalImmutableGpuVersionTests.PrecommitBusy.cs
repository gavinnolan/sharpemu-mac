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
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalImmutableGpuVersionTests
{
    [FiniteStoragePublicationNativeFact]
    public void ActualCompletedGpuStagingWaitsForDisjointOriginalVolumeWriterBeforePublication() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, precommitBusyWindow: "before-prepare");
    [FiniteStoragePublicationNativeFact]
    public void ActualUnstartedPublicationCancelsBeforeOriginalVolumeBusyWaitAndCommitsOnce() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, precommitBusyWindow: "held-ticket");
    [FiniteStoragePublicationNativeFact]
    public void ActualCompletedGpuStagingCannotOverwriteStaleOriginalVolumeOrSignalEop() =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, precommitBusyWindow: "overlap");

    private void RunActualPrecommitOriginalBusy(NativeState state, PhysicalVirtualMemory physical, TrackedCpuMemory memory,
        FiniteStorageFixtureOwners owners, ulong address, int imageBytes, int storeOffset, byte[] expected,
        long sequence, long eop, MetalCompiledGuestShader shader, Func<byte[]> canonical, string scenario)
    {
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
        var fields = new[] { "_finiteStorageCompletedPrecommitForControl", "_finiteStorageBeforePrecommitValidationForControl", "_finiteStoragePrecommitBusyForControl" };
        var saved = fields.Select(name => Field(name).GetValue(null)).ToArray();
        var writeHook = typeof(SharedBackingViews).GetField("_beforeCanonicalAliasCopyForControl", instance)!;
        var oldWrite = writeHook.GetValue(volumeViews);
        var pause = new BusyOrdinaryWriter(); owners.OrdinaryWriterOwner = pause;
        object? completedReceipt = null; var completions = 0; var started = false; var waited = 0;
        var completedBefore = Field("_completedGuestWorkSequence").GetValue(null);
        Exception? primary = null;
        try
        {
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
                if (!pause.Resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("precommit writer pause bound");
            }));
            Field(fields[0]).SetValue(null, (Action<object>)(receipt =>
            {
                Assert.Null(completedReceipt); completedReceipt = receipt; completions++;
                Assert.True((bool)StorageMember(receipt, "SuccessfullyCompleted")!);
                var commandBuffer = (nint)StorageMember(receipt, "CommandBuffer")!;
                Assert.NotEqual((nint)0, commandBuffer); Assert.Equal((nint)4, MetalNative.Send(commandBuffer, MetalNative.Selector("status")));
                Assert.Same(owners.OriginalOrigin, StorageMember(receipt, "Origin"));
                var staging = (nint)StorageMember(receipt, "Staging")!;
                var pointer = MetalNative.Send(staging, MetalNative.Selector("contents")); Assert.NotEqual((nint)0, pointer);
                var linear = Bgra1024Oracle.LinearSeed();
                BinaryPrimitives.WriteUInt32LittleEndian(linear.AsSpan((511 * 1024 + 257) * 4), 0x11223344);
                Assert.Equal(linear, Assert.IsType<byte[]>(Call("ReadFiniteStorageLinearBytes", receipt, pointer)));
                Assert.Equal(expected, canonical()); // whole completed native staging, still NO guest write
            }));
            Field(fields[1]).SetValue(null, (Action<bool>)(held =>
            {
                if (started || held != (scenario == "held-ticket")) return;
                Assert.NotNull(completedReceipt); started = true;
                Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                Assert.Equal(held ? 4294967296L : 0L, StorageMember(ownLedger, "admission"));
                pause.Thread = new Thread(() =>
                {
                    try { pause.Written = volumeViews.TryWriteBacking(destination, new byte[] { 0xA9 }); }
                    catch (Exception failure) { pause.Failure = failure; }
                }) { IsBackground = true, Name = "original volume precommit writer" };
                pause.Thread.Start(); Assert.True(pause.Admitted.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(scenario == "overlap" ? CpuMappedReadWatchStatus.Stale : CpuMappedReadWatchStatus.Busy,
                    ((ICpuMappedReadWatchSource)memory).GetMappedCopyWriteWatchStatus(watched));
            }));
            Field(fields[2]).SetValue(null, (Action)(() =>
            {
                waited++; Assert.Equal(1, waited); Assert.True(started);
                Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                Assert.Equal(0L, StorageMember(ownLedger, "admission")); // exact unstarted ticket cancelled BEFORE wait
                Assert.Equal(expected, canonical()); Assert.Equal(extraBefore, ReadExtra());
                Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0)); Assert.False(MetalVideoPresenter.WaitForGuestWork(eop, 0));
                Assert.Equal(completedBefore, Field("_completedGuestWorkSequence").GetValue(null));
                Assert.False((bool)StorageMember(completedReceipt!, "Published")!);
                var receiptImage = StorageMember(completedReceipt!, "Image")!;
                Assert.Same(owners.OriginalOrigin, StorageMember(receiptImage, "StorageCpuOrigin"));
                Assert.Equal(StorageMember(completedReceipt!, "Version"), StorageMember(receiptImage, "ContentVersion"));
                FinishWriter();
                Assert.Equal(CpuMappedReadWatchStatus.Current, ((ICpuMappedReadWatchSource)memory).GetMappedCopyWriteWatchStatus(watched));
                foreach (var origin in volumeOriginals) Call("ValidateSingletonVolumeOriginalContent", origin);
            }));
            if (scenario == "overlap")
            {
                var failed = Assert.Throws<TargetInvocationException>(() => state.DrainWithControlledBudget());
                var fatal = Assert.IsType<GpuFatalException>(failed.InnerException);
                Assert.Contains("lost its ORIGINAL", fatal.InnerException!.Message);
                Assert.Equal(0, waited); Assert.Equal(expected, canonical());
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
                BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(imageBytes), 0x13579BDF);
                Assert.Equal(expected, canonical());
                var image = ((IDictionary)Field("_guestImages").GetValue(null)!)[address]!;
                AssertFiniteDebtSettled(image); state.AssertFinitePipelineCompleted(shader);
                Assert.Equal(2, StorageMember(image, "ContentVersion"));
            }
            Assert.True(started); Assert.Equal(1, completions); Assert.Equal(extraExpected, ReadExtra());
            for (var i = 0; i < owners.VolumeImages.Count; i++)
            {
                Assert.Same(volumeOriginals[i], StorageMember(owners.VolumeImages[i], "StorageCpuOrigin"));
                Assert.Equal(volumeTextures[i], StorageMember(owners.VolumeImages[i], "Texture"));
                Assert.Equal(volumeVersions[i], StorageMember(owners.VolumeImages[i], "ContentVersion"));
                Assert.Equal(volumeNative[i], ReadNativeVolume(owners.VolumeImages[i]));
            }
            output.WriteLine($"finite-precommit-native scenario={scenario}; actual_status=4; full_completed_staging_bytes=true; distinct_backings=true; real_admission=2; staging_copies=1; original_native_owners_unchanged=true; publication={(scenario == "overlap" ? "none" : "one-canonical-version")}; EOP={(scenario == "overlap" ? "unacknowledged" : "after-publication")}");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            Exception? cleanup = null; pause.Resume.Set();
            try
            {
                if (pause.Thread is { } writer && !writer.Join(TimeSpan.FromSeconds(5)))
                { owners.OrdinaryWriterUnconfirmed = true; cleanup = new TimeoutException("precommit writer live; exact owners quarantined"); }
            }
            catch (Exception failure) { owners.OrdinaryWriterUnconfirmed = true; cleanup = failure; }
            for (var i = 0; i < fields.Length; i++) { var index = i; Attempt(() => Field(fields[index]).SetValue(null, saved[index])); }
            Attempt(() => writeHook.SetValue(volumeViews, oldWrite));
            if (!owners.OrdinaryWriterUnconfirmed) Attempt(pause.Dispose);
            if (cleanup is not null)
            {
                if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                try { primary.Data["FinitePrecommitFixtureCleanup"] = cleanup; } catch { }
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
