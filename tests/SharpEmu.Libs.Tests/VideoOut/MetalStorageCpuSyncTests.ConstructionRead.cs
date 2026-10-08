// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Synthetic opaque read-ticket owner tests only. Real provider exclusion
    // is independently exercised by MappedCopyReadWatchTests.ReadValidation.
    private sealed class ConstructionReadControl(CpuMappedCopyObservation original) : CpuMappedReadValidationTransaction
    {
        internal int Releases;
        internal bool Current = true;
        internal Exception? ReleaseFailure;
        internal Action? AfterRelease;
        internal override bool IsOriginalCurrent(CpuMappedCopyObservation candidate) =>
            Releases == 0 && Current && ReferenceEquals(candidate, original);
        internal override void Cancel()
        {
            if (Releases != 0) return;
            Releases++;
            AfterRelease?.Invoke();
            if (ReleaseFailure is not null) throw ReleaseFailure;
        }
    }

    [Theory]
    [InlineData("unavailable")] [InlineData("stale")] [InlineData("provider-error")]
    [InlineData("token-during-prepare")] [InlineData("session-during-prepare")]
    public void SingletonVolumeConstructionRefusalPrecedesAnyNativeAllocationAndPreservesOriginal(string cause)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory);
        MetalVideoPresenter.AttachGuestMemory(memory);
        var control = new ConstructionReadControl(memory.LastMapping!);
        var providerError = new InvalidOperationException("exact original provider error");
        if (cause is "token-during-prepare" or "session-during-prepare")
        {
            memory.ReadPreparation = CpuMappedPublicationPreparationResult.Prepared;
            memory.ReadValidation = new(control);
            memory.BeforeReadPreparation = () =>
            {
                if (cause == "token-during-prepare") protection.Generation(1);
                else Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentStorageCpuSeedSession + 1);
            };
        }
        if (cause == "stale") { memory.ReadPreparation = CpuMappedPublicationPreparationResult.Rejected; memory.WatchCurrent = false; }
        if (cause == "provider-error") memory.ReadPreparationFailure = providerError;
        try
        {
            var outer = Assert.Throws<TargetInvocationException>(() => InvokeVolume("EnsureStorageImage", (nint)0, snapshot));
            var error = Assert.IsType<InvalidOperationException>(outer.InnerException);
            if (cause == "provider-error") Assert.Same(providerError, error);
            else Assert.Contains("ORIGINAL", error.Message);
            Assert.False(Images.Contains(Address)); Assert.Equal(1, memory.ReadPreparations);
            Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
            Assert.Equal(cause is "token-during-prepare" or "session-during-prepare" ? 1 : 0, control.Releases);
        }
        finally { MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
    }

    [Theory]
    [InlineData("healthy")] [InlineData("replacement")] [InlineData("session")] [InlineData("release-error")]
    [InlineData("content-busy")] [InlineData("owner-busy")]
    [InlineData("content-busy-token")] [InlineData("owner-busy-replacement")] [InlineData("owner-busy-session")]
    public void SingletonVolumeConstructionTransferUsesSameTicketAndRevalidatesOnlyExactOwnerAfterRelease(string cause)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot);
        MetalVideoPresenter.AttachGuestMemory(memory);
        var control = new ConstructionReadControl(memory.LastMapping!);
        var cleanup = new InvalidOperationException("original read release uncertain");
        memory.ReadPreparation = CpuMappedPublicationPreparationResult.Prepared; memory.ReadValidation = new(control);
        memory.WatchCurrent = false; // ordinary guard may NOT bless the held ticket
        var scope = InvokeVolume("PrepareSingletonVolumeConstructionRead", snapshot)!;
        var reservation = scope.GetType().GetProperty("Reservation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope);
        var image = VolumeImage(origin, memory); Images.Remove(Address);
        var contentHook = Field("_singletonVolumeConstructionBeforeFinalContentForControl");
        var ownerHook = Field("_singletonVolumeConstructionBeforeFinalOwnerForControl");
        var oldContentHook = contentHook.GetValue(null); var oldOwnerHook = ownerHook.GetValue(null);
        var paused = false; var busyObserved = 0;
        try
        {
            lock (Field("_gate").GetValue(null)!)
                InvokeVolume("PublishStorageImageWithOriginalReadLocked", snapshot, null, image, (1u, 1u, 4UL), reservation);
            Assert.Same(image, Images[Address]); Assert.Equal(0, control.Releases);
            Assert.Same(memory.LastMapping, origin.GetType().GetProperty("Mapping")!.GetValue(origin));
            scope.GetType().GetMethod("SetNativeOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scope, [image]);
            control.AfterRelease = () =>
            {
                memory.WatchCurrent = true;
                if (cause == "replacement") Images.Remove(Address);
                if (cause == "session") Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentStorageCpuSeedSession + 1);
            };
            if (cause == "release-error") control.ReleaseFailure = cleanup;
            if (cause.Contains("busy"))
            {
                var hook = cause.StartsWith("content") ? contentHook : ownerHook;
                hook.SetValue(null, (Action)(() =>
                {
                    if (paused) return; paused = true;
                    Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                    Assert.Equal(1, control.Releases); // original read receipt was consumed once
                    memory.WatchCurrent = false;
                    memory.ReadStatus = () =>
                    {
                        if (busyObserved++ == 0) { memory.WatchCurrent = true; return CpuMappedReadWatchStatus.Busy; }
                        return CpuMappedReadWatchStatus.Current;
                    };
                    if (cause == "content-busy-token") protection.Generation(1);
                    if (cause == "owner-busy-replacement") Images.Remove(Address);
                    if (cause == "owner-busy-session") Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentStorageCpuSeedSession + 1);
                }));
            }
            var finish = scope.GetType().GetMethod("Finish", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var success = cause is "healthy" or "content-busy" or "owner-busy";
            if (success) finish.Invoke(scope, [null]);
            else
            {
                var error = Assert.Throws<TargetInvocationException>(() => finish.Invoke(scope, [null])).InnerException;
                if (cause == "release-error") Assert.Same(cleanup, error);
                else Assert.IsType<InvalidOperationException>(error);
            }
            Assert.Equal(1, control.Releases); finish.Invoke(scope, [null]); Assert.Equal(1, control.Releases);
            Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
            Assert.Equal(!success, VolumeWatch(origin).GetType().GetField("UnknownProducer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(VolumeWatch(origin)));
            if (cause is "content-busy" or "owner-busy") { Assert.True(paused); Assert.True(busyObserved > 0); }
        }
        finally
        {
            contentHook.SetValue(null, oldContentHook); ownerHook.SetValue(null, oldOwnerHook);
            Images.Remove(Address); InvokeVolume("ReleaseSingletonVolumeNativeOriginals");
            MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]);
        }
    }
}
