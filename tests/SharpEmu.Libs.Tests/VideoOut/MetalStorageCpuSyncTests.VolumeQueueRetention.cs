// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void VolumePublicQueueRetainsExactOriginalWithoutQueryingConstructorExclusion(bool draw)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar(); var memory = new VolumeMemory(VolumePhysical());
        var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot); var watch = VolumeWatch(origin);
        MetalVideoPresenter.AttachGuestMemory(memory);
        var mapQueries = memory.MappingQueries; var watchQueries = memory.WatchQueries;
        memory.WatchCurrent = false; memory.RejectCurrentnessQueries = true;
        var sequence = SubmitVolume([snapshot], draw);
        Assert.True(sequence > 0); Assert.Equal(mapQueries, memory.MappingQueries); Assert.Equal(watchQueries, memory.WatchQueries);
        MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); Assert.Equal(0, memory.Releases);
        memory.RejectCurrentnessQueries = false;
        // The queue retain did not authorize native construction or rebase any
        // observation. The same original is still refused while excluded.
        Assert.Throws<TargetInvocationException>(() => InvokeVolume("ValidateSingletonVolumeOriginalContent", origin));
        Assert.Same(origin, GetOrigin(snapshot)); Assert.Equal(1, memory.Copies);
        memory.WatchCurrent = true;
        InvokeVolume("ValidateSingletonVolumeOriginalContent", origin);
        _ = TakeVolumeWork();
        InvokeVolume("CompletePresenterShutdown");
        Assert.Equal(1, memory.Releases); Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        Assert.Equal(2, watch.GetType().GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watch));
    }

    [Theory]
    [InlineData("mapping")] [InlineData("watch")] [InlineData("token")]
    public void VolumeQueueRetentionCannotTurnStaleOriginalIntoNativeConstruction(string change)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar(); var memory = new VolumeMemory(VolumePhysical());
        var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot);
        MetalVideoPresenter.AttachGuestMemory(memory);
        memory.WatchCurrent = false;
        _ = SubmitVolume([snapshot]); MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]);
        memory.WatchCurrent = true;
        if (change == "mapping") memory.Current = false;
        if (change == "watch") memory.WatchCurrent = false;
        if (change == "token") GuestImageWriteTracker.NotifyManagedWrite(Address, 1);
        // Real Ensure entry rejects before any native allocation; null native
        // arguments are deliberately unreachable after the original veto.
        Assert.Throws<TargetInvocationException>(() => InvokeVolume("EnsureStorageImage", (nint)0, snapshot));
        Assert.Same(origin, GetOrigin(snapshot)); Assert.Equal(1, memory.Copies);
        InvokeVolume("CompletePresenterShutdown");
        Assert.Equal(1, memory.Releases); Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
    }

    [Theory]
    [InlineData("session")] [InlineData("unknown")] [InlineData("released")]
    [InlineData("untracked")] [InlineData("shape")]
    public void VolumeQueueOwnershipRejectsLostOriginalAssociation(string change)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar(); var memory = new VolumeMemory(VolumePhysical());
        var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot); var watch = VolumeWatch(origin);
        try
        {
            if (change == "session") Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentStorageCpuSeedSession + 1);
            if (change == "unknown") watch.GetType().GetField("UnknownProducer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(watch, true);
            if (change == "released") MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]);
            if (change == "untracked") Field("_singletonVolumeOriginalWatches").GetValue(null)!.GetType().GetMethod("Remove")!.Invoke(Field("_singletonVolumeOriginalWatches").GetValue(null), [watch]);
            if (change == "shape")
            {
                var changed = snapshot with { Depth = snapshot.Depth + 1 };
                Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.CopyCapturedTextureSource(snapshot, changed));
                return; // malformed clones cannot acquire the original association
            }
            Assert.Throws<InvalidOperationException>(() => SubmitVolume([snapshot]));
            Assert.Empty((IEnumerable)Field("_pendingSingletonVolumeOriginalWorks").GetValue(null)!);
            Assert.Equal(0L, Field("_enqueuedGuestWorkSequence").GetValue(null));
        }
        finally { MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
        Assert.Equal(1, memory.Releases);
    }
}
