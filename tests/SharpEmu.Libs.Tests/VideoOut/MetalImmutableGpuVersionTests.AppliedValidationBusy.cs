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
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalImmutableGpuVersionTests
{
    [FiniteStoragePublicationNativeFact]
    public void ActualAppliedValidationWaitsForDisjointWriterWithoutRepeatingGpuOrCommit() => Applied("disjoint");
    [FiniteStoragePublicationNativeFact]
    public void ActualAppliedValidationOverlappingWriterFailsHistoryWithoutFinalizationOrEop() => Applied("overlap");
    [FiniteStoragePublicationNativeFact]
    public void ActualAppliedValidationCannotReplaceItsOriginalAppliedObservation() => Applied("replacement");
    [FiniteStoragePublicationNativeFact]
    public void ActualAppliedValidationCannotBorrowChangedNativeVersion() => Applied("version");
    [FiniteStoragePublicationNativeFact]
    public void ActualAppliedValidationChangedOriginalTokenStillRefuses() => Applied("token");
    [FiniteStoragePublicationNativeFact]
    public void ActualAppliedValidationDeadlineRetainsExactPendingNativeOwners() => Applied("timeout");
    [FiniteStoragePublicationNativeFact]
    public void ActualAppliedValidationUnknownCancellationRetainsExactPendingNativeOwners() => Applied("cancel-unknown");
    private void Applied(string scenario) =>
        RunActualRuntimeTwoTypedPublications(false, rgba: true, originalVolumes: true, appliedValidationWindow: scenario);

    private void RunActualAppliedValidationBusy(NativeState state, PhysicalVirtualMemory physical, TrackedCpuMemory memory,
        ICpuMemory cpuSource, FiniteStorageFixtureOwners owners, ulong address, int imageBytes, int storeOffset,
        byte[] expected, long sequence, long eop, MetalCompiledGuestShader shader, Func<byte[]> canonical, string scenario)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var views = Assert.IsType<SharedBackingViews>(StorageMember(StorageMember(physical, "_backedSpace")!, "_views"));
        var backing = StorageMember(views, "_backing")!; var writers = StorageMember(backing, "_managedWrites")!;
        var volumePhysical = Assert.IsType<PhysicalVirtualMemory>(owners.BodyVolumePhysical);
        var volumeViews = Assert.IsType<SharedBackingViews>(StorageMember(StorageMember(volumePhysical, "_backedSpace")!, "_views"));
        Assert.NotSame(writers, StorageMember(StorageMember(volumeViews, "_backing")!, "_managedWrites"));
        var volumeBytes = owners.VolumeImages.Select(ReadVolume).ToArray();
        var volumeOriginals = owners.VolumeImages.Select(image => StorageMember(image, "StorageCpuOrigin")).ToArray();
        var extraExpected = Enumerable.Range(imageBytes, 3 * 65536).Select(i => (byte)(i * 19 + 7)).ToArray();
        var destination = scenario == "overlap" ? address : address + (ulong)imageBytes + 32768;
        Assert.True(views.TryResolveAlias(destination, 1, out var alias));
        var writeHook = typeof(SharedBackingViews).GetField("_beforeCanonicalAliasCopyForControl", instance)!;
        var oldWrite = writeHook.GetValue(views);
        var beforeHook = Field("_finiteStorageBeforeAppliedValidationForControl"); var oldBefore = beforeHook.GetValue(null);
        var busyHook = Field("_finiteStorageAppliedValidationBusyForControl"); var oldBusy = busyHook.GetValue(null);
        var encoderHook = Field("_computeEncoderCreationOverride"); var oldEncoder = encoderHook.GetValue(null);
        var appliedHook = Field("_finiteStorageAfterAppliedForControl"); var oldApplied = appliedHook.GetValue(null);
        var pause = new BusyOrdinaryWriter(); owners.OrdinaryWriterOwner = pause;
        object? receipt = null; object? applied = null; nint producer = 0, staging = 0, texture = 0;
        var encoders = 0; var appliedEvents = 0; var starts = 0; var waits = 0; var completed = Field("_completedGuestWorkSequence").GetValue(null);
        var published = expected.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(published.AsSpan(storeOffset), 0x11223344);
        Exception? primary = null;
        try
        {
            appliedHook.SetValue(null, (Action)(() =>
            {
                Interlocked.Increment(ref appliedEvents);
                if (oldApplied is Action previous) previous();
            }));
            encoderHook.SetValue(null, (Func<nint, nint>)(command =>
            {
                Interlocked.Increment(ref encoders);
                return oldEncoder is Func<nint, nint> old ? old(command) : MetalNative.Send(command, MetalNative.Selector("computeCommandEncoder"));
            }));
            writeHook.SetValue(views, (Action<ulong, int>)((target, count) =>
            {
                Assert.Equal(alias, target); Assert.Equal(1, count);
                Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                Assert.False(Monitor.IsEntered(StorageMember(views, "_lock")!));
                var gate = Assert.IsType<ReaderWriterLockSlim>(StorageMember(physical, "_gate"));
                Assert.False(gate.IsReadLockHeld || gate.IsWriteLockHeld || gate.IsUpgradeableReadLockHeld);
                Assert.Equal(2L, StorageMember(writers, "admission"));
                writeHook.SetValue(views, oldWrite); pause.Admitted.Set();
                if (!pause.Resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Applied validation writer pause bound");
            }));
            beforeHook.SetValue(null, (Action<object>)(actual =>
            {
                beforeHook.SetValue(null, oldBefore); starts++; Assert.Equal(1, starts); receipt = actual;
                Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                Assert.True((bool)StorageMember(actual, "Published")!); Assert.True((bool)StorageMember(actual, "SuccessfullyCompleted")!);
                Assert.True((bool)StorageMember(actual, "Encoded")!); Assert.Equal(1, encoders); Assert.Equal(1, appliedEvents);
                producer = (nint)StorageMember(actual, "CommandBuffer")!; staging = (nint)StorageMember(actual, "Staging")!;
                texture = (nint)StorageMember(actual, "Texture")!;
                Assert.NotEqual((nint)0, producer); Assert.NotEqual((nint)0, staging); Assert.NotEqual((nint)0, texture);
                Assert.Equal((nint)4, MetalNative.Send(producer, MetalNative.Selector("status")));
                applied = StorageMember(actual, "AppliedObservation"); Assert.NotNull(applied);
                Assert.NotSame(StorageMember(StorageMember(actual, "Origin")!, "Mapping"), applied);
                Assert.Equal(published, canonical()); // actual Applied full bytes BEFORE callback/EOP
                Assert.Null(StorageMember(actual, "PublishedOrigin"));
                pause.Thread = new Thread(() =>
                {
                    try { pause.Written = views.TryWriteBacking(destination, new byte[] { 0xA9 }); }
                    catch (Exception failure) { pause.Failure = failure; }
                }) { IsBackground = true, Name = "post-Applied exact validation writer" };
                pause.Thread.Start(); Assert.True(pause.Admitted.Wait(TimeSpan.FromSeconds(5)));
                Assert.Equal(2L, StorageMember(writers, "admission"));
                if (scenario == "cancel-unknown") Assert.IsType<BodyBusyRoutedMemory>(cpuSource).ArmValidationCancellation();
            }));
            busyHook.SetValue(null, (Action<object>)(actual =>
            {
                waits++; Assert.Same(receipt, actual); Assert.Equal(2L, StorageMember(writers, "admission"));
                Assert.Same(applied, StorageMember(actual, "AppliedObservation"));
                Assert.Equal(producer, StorageMember(actual, "CommandBuffer")); Assert.Equal(staging, StorageMember(actual, "Staging"));
                Assert.Equal(texture, StorageMember(actual, "Texture")); Assert.Equal(1, encoders);
                Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                Assert.Null(StorageMember(actual, "PublishedOrigin")); Assert.Equal(published, canonical());
                Assert.Equal(completed, Field("_completedGuestWorkSequence").GetValue(null));
                Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0)); Assert.False(MetalVideoPresenter.WaitForGuestWork(eop, 0));
                if (scenario == "timeout") return; // actual 500ms bound, no fake outcome
                Assert.Equal(1, waits); busyHook.SetValue(null, oldBusy);
                if (scenario == "replacement") actual.GetType().GetField("AppliedObservation", instance)!.SetValue(actual,
                    StorageMember(StorageMember(actual, "Origin")!, "Mapping")); // real older original, never fabricated authority
                if (scenario == "version")
                {
                    var image = StorageMember(actual, "Image")!;
                    image.GetType().GetField("ContentVersion", instance)!.SetValue(image, (int)StorageMember(image, "ContentVersion")! + 1);
                }
                if (scenario == "token") GuestImageWriteTracker.NotifyManagedWrite(address, 1);
                FinishWriter();
            }));
            if (scenario == "disjoint")
            {
                state.DrainWithControlledBudget(); Assert.Equal(1, waits);
                Assert.True(MetalVideoPresenter.WaitForGuestWork(eop, 0));
                BinaryPrimitives.WriteUInt32LittleEndian(published.AsSpan(imageBytes), 0x13579BDF);
                published[checked((int)(destination - address))] = 0xA9;
                Assert.Equal(published, canonical()); AssertFiniteDebtSettled(StorageMember(receipt!, "Image")!);
                Assert.False((bool)StorageMember(receipt!, "ValidationQuarantined")!);
                state.AssertFinitePipelineCompleted(shader);
            }
            else
            {
                var failure = Assert.Throws<TargetInvocationException>(() => state.DrainWithControlledBudget());
                Assert.IsType<GpuFatalException>(failure.InnerException);
                Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0)); Assert.False(MetalVideoPresenter.WaitForGuestWork(eop, 0));
                Assert.Equal(completed, Field("_completedGuestWorkSequence").GetValue(null));
                Assert.True(waits > 0); FinishWriter();
                published[checked((int)(destination - address))] = 0xA9;
                Assert.Equal(published, canonical()); // Applied happened ONCE; no EOP bytes
                var debt = StorageMember(receipt!, "Debt")!;
                Assert.True((bool)StorageMember(debt, "Failed")!); Assert.True((bool)StorageMember(debt, "Pending")!);
                Assert.Equal(scenario is "timeout" or "cancel-unknown", (bool)StorageMember(receipt!, "ValidationQuarantined")!);
                if (scenario is "timeout" or "cancel-unknown")
                {
                    owners.AppliedValidationQuarantined = true;
                    Assert.Equal(producer, StorageMember(receipt!, "CommandBuffer")); Assert.Equal(staging, StorageMember(receipt!, "Staging"));
                    Assert.Equal(texture, StorageMember(receipt!, "Texture")); Assert.False((bool)StorageMember(receipt!, "Consumed")!);
                    var slot = (int)StorageMember(receipt!, "LifetimeSlot")!;
                    Assert.Same(receipt, Assert.IsAssignableFrom<Array>(Field("_finiteStorageWritebackRoots").GetValue(null)).GetValue(slot));
                    // Unknown/deadline cleanup consumes no new release authority.
                    // The SAME registered root and retained native owners survive
                    // repeated ordinary cleanup requests without a retry.
                    Assert.IsAssignableFrom<IDisposable>(receipt).Dispose();
                    Assert.IsAssignableFrom<IDisposable>(receipt).Dispose();
                    Assert.Same(receipt, Assert.IsAssignableFrom<Array>(Field("_finiteStorageWritebackRoots").GetValue(null)).GetValue(slot));
                    Assert.Equal(producer, StorageMember(receipt!, "CommandBuffer"));
                    Assert.Equal(staging, StorageMember(receipt!, "Staging"));
                    Assert.Equal(texture, StorageMember(receipt!, "Texture"));
                    Assert.False((bool)StorageMember(receipt!, "Consumed")!);
                }
                state.AssertFinitePipelineCompleted(shader);
            }
            Assert.Equal(1, starts); Assert.Equal(1, encoders); Assert.Equal(1, appliedEvents); Assert.Equal(extraExpected, ReadExtra());
            for (var i = 0; i < owners.VolumeImages.Count; i++)
            { Assert.Same(volumeOriginals[i], StorageMember(owners.VolumeImages[i], "StorageCpuOrigin")); Assert.Equal(volumeBytes[i], ReadVolume(owners.VolumeImages[i])); }
            if (scenario is "timeout" or "cancel-unknown") owners.AppliedValidationQuarantineVerified = true;
            output.WriteLine($"finite-storage-applied-validation-native scenario={scenario}; actual_status=4; actual_compute_encoders=1; actual_Applied_bytes_once=true; exact_originals=true; full_volume_native_bytes=true; full_canonical_neighbors=true; callback_retry=false; EOP={(scenario == "disjoint" ? "after-canonical" : "unacknowledged")}; quarantine={(scenario is "timeout" or "cancel-unknown")}");
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            Exception? cleanup = null; pause.Resume.Set();
            try { if (pause.Thread is { } writer && !writer.Join(TimeSpan.FromSeconds(5))) { owners.OrdinaryWriterUnconfirmed = true; cleanup = new TimeoutException("Applied writer still live; owners quarantined"); } }
            catch (Exception failure) { owners.OrdinaryWriterUnconfirmed = true; cleanup = failure; }
            if (receipt is not null && (bool)StorageMember(receipt, "ValidationQuarantined")!) owners.AppliedValidationQuarantined = true;
            Attempt(() => beforeHook.SetValue(null, oldBefore)); Attempt(() => busyHook.SetValue(null, oldBusy));
            Attempt(() => writeHook.SetValue(views, oldWrite)); Attempt(() => encoderHook.SetValue(null, oldEncoder));
            Attempt(() => appliedHook.SetValue(null, oldApplied));
            if (!owners.OrdinaryWriterUnconfirmed) Attempt(pause.Dispose);
            if (cleanup is not null) { if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw(); else try { primary.Data["AppliedValidationFixtureCleanup"] = cleanup; } catch { } }
            void Attempt(Action action) { try { action(); } catch (Exception failure) { cleanup ??= failure; } }
        }
        void FinishWriter()
        {
            pause.Resume.Set(); Assert.True(pause.Thread!.Join(TimeSpan.FromSeconds(5)));
            if (pause.Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw(); Assert.True(pause.Written);
        }
        byte[] ReadExtra() { var bytes = new byte[3 * 65536]; Assert.True(volumePhysical.TryRead(owners.BodyVolumeAddress, bytes)); return bytes; }
        byte[] ReadVolume(object volume) => ReadOriginalVolume((nint)StorageMember(volume, "Texture")!,
            (uint)StorageMember(volume, "Width")!, (uint)StorageMember(volume, "Height")!, (uint)StorageMember(volume, "Depth")!);
    }
}
