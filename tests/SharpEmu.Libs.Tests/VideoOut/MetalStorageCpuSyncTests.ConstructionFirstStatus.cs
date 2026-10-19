// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Scalar provider controls: no manufactured native execution authority.
    [Theory]
    [InlineData("content-busy-clears")] [InlineData("owner-busy-clears")]
    [InlineData("content-stale-then-busy")] [InlineData("owner-stale-then-busy")]
    [InlineData("content-overlap")] [InlineData("content-unknown")]
    [InlineData("content-mapping-rights")] [InlineData("content-token")]
    [InlineData("owner-session")] [InlineData("owner-version")]
    public void ConstructorFinalBoundaryUsesFirstClassifiedOriginalWithoutLaterStatusRescue(string variant)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot);
        MetalVideoPresenter.AttachGuestMemory(memory);
        var control = new ConstructionReadControl(memory.LastMapping!);
        memory.ReadPreparation = CpuMappedPublicationPreparationResult.Prepared; memory.ReadValidation = new(control);
        var scope = InvokeVolume("PrepareSingletonVolumeConstructionRead", snapshot)!;
        var reservation = scope.GetType().GetProperty("Reservation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scope);
        var image = VolumeImage(origin, memory); Images.Remove(Address);
        var entry = Field(variant.StartsWith("owner") ? "_singletonVolumeConstructionBeforeFinalOwnerForControl" :
            "_singletonVolumeConstructionBeforeFinalContentForControl");
        var firstBusy = Field("_singletonVolumeConstructionFirstBusyForControl");
        var wait = Field("_singletonVolumeConstructionBusyForControl");
        var previousEntry = entry.GetValue(null); var previousFirst = firstBusy.GetValue(null); var previousWait = wait.GetValue(null);
        var queries = 0; var caught = 0; var waited = 0; var entered = false;
        try
        {
            lock (Field("_gate").GetValue(null)!)
                InvokeVolume("PublishStorageImageWithOriginalReadLocked", snapshot, null, image, (1u, 1u, 4UL), reservation);
            scope.GetType().GetMethod("SetNativeOwner", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(scope, [image]);
            entry.SetValue(null, (Action)(() =>
            {
                if (entered) return; entered = true;
                Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                Assert.Equal(1, control.Releases);
                memory.WatchCurrent = false;
                memory.ReadStatus = () =>
                {
                    ++queries;
                    if (variant.Contains("stale-then-busy")) return queries == 1 ? CpuMappedReadWatchStatus.Stale : CpuMappedReadWatchStatus.Busy;
                    if (variant == "content-overlap") return CpuMappedReadWatchStatus.Stale;
                    if (variant == "content-mapping-rights") memory.Current = false;
                    if (variant == "content-token") protection.Generation(1);
                    if (variant == "content-unknown") VolumeWatch(origin).GetType().GetField("UnknownProducer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(VolumeWatch(origin), true);
                    return queries == 1 ? CpuMappedReadWatchStatus.Busy : CpuMappedReadWatchStatus.Current;
                };
                if (variant == "owner-session") Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentStorageCpuSeedSession + 1);
                if (variant == "owner-version") image.GetType().GetField("ContentVersion", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(image, 2);
            }));
            firstBusy.SetValue(null, (Action)(() =>
            {
                Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
                Assert.Equal(2, queries); // first Busy, independent health recheck Current
                Assert.False(memory.WatchCurrent); caught++;
                memory.WatchCurrent = true;
            }));
            wait.SetValue(null, (Action)(() => { Assert.Equal(1, caught); waited++; }));
            var booleanQueries = memory.WatchQueries;
            var finish = scope.GetType().GetMethod("Finish", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var success = variant.EndsWith("busy-clears");
            if (success) finish.Invoke(scope, [null]);
            else Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => finish.Invoke(scope, [null])).InnerException);
            Assert.True(entered); Assert.Equal(success ? 1 : 0, caught); Assert.Equal(success ? 1 : 0, waited);
            Assert.Equal(booleanQueries, memory.WatchQueries); // no Boolean then Status rescue
            if (variant.Contains("stale-then-busy")) Assert.Equal(1, queries);
            Assert.Equal(1, control.Releases); finish.Invoke(scope, [null]); Assert.Equal(1, control.Releases);
            Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
            Assert.Equal(!success, VolumeWatch(origin).GetType().GetField("UnknownProducer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(VolumeWatch(origin)));
        }
        finally
        {
            entry.SetValue(null, previousEntry); firstBusy.SetValue(null, previousFirst); wait.SetValue(null, previousWait);
            Images.Remove(Address); InvokeVolume("ReleaseSingletonVolumeNativeOriginals");
            MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]);
        }
    }
}
