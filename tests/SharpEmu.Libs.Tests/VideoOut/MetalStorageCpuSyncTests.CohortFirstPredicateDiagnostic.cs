// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Scalar metadata/query ordering only. These synthetic handles never reach
    // native selectors, and no status or retained snapshot grants GPU authority.
    [Theory]
    [InlineData("first-stale-later-busy")] [InlineData("first-busy-later-current")]
    [InlineData("strict-watch")] [InlineData("version")] [InlineData("unknown")]
    [InlineData("session")] [InlineData("texture")] [InlineData("width")]
    [InlineData("watch-image")] [InlineData("current")] [InlineData("provider-error")]
    public void NativeCohortDiagnosticReportsExactFirstPredicateWithoutBorrowingLaterStatus(string kind)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory);
        var original = GetOrigin(snapshot); var volume = VolumeImage(original, memory);
        var queries = 0; var cause = new IOException("first native cohort status failed");
        try
        {
            PublishVolume(snapshot, volume); memory.WatchQueries = 0; memory.WatchCurrent = false;
            memory.ReadStatus = () =>
            {
                queries++;
                if (kind == "provider-error") throw cause;
                if (kind == "first-stale-later-busy") return queries == 1 ? CpuMappedReadWatchStatus.Stale : CpuMappedReadWatchStatus.Busy;
                if (kind == "first-busy-later-current") return queries == 1 ? CpuMappedReadWatchStatus.Busy : CpuMappedReadWatchStatus.Current;
                return CpuMappedReadWatchStatus.Current;
            };
            if (kind == "version") Set(volume, "ContentVersion", 2);
            if (kind == "texture") Set(volume, "Texture", (nint)124);
            if (kind == "width") Set(volume, "Width", 2U);
            if (kind == "unknown") VolumeWatch(original).GetType().GetField("UnknownProducer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(VolumeWatch(original), true);
            if (kind == "session") original.GetType().GetProperty("Session")!.SetValue(original, (long)original.GetType().GetProperty("Session")!.GetValue(original)! + 1);
            if (kind == "watch-image") VolumeWatch(original).GetType().GetField("Image", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(VolumeWatch(original), null);
            var reservation = Activator.CreateInstance(Presenter.GetNestedType("StorageWatchReservation", BindingFlags.NonPublic)!)!;
            object?[] args = [volume, original, reservation, kind != "strict-watch", null, null];
            lock (Field("_gate").GetValue(null)!)
            {
                if (kind == "provider-error") Assert.Same(cause, Assert.Throws<TargetInvocationException>(() => InvokeVolume("InspectSingletonVolumeCohortWithDiagnosticLocked", args)).InnerException);
                else
                {
                    Assert.Equal(kind switch { "first-busy-later-current" => CpuMappedReadWatchStatus.Busy, "current" => CpuMappedReadWatchStatus.Current, _ => CpuMappedReadWatchStatus.Stale },
                        Assert.IsType<CpuMappedReadWatchStatus>(InvokeVolume("InspectSingletonVolumeCohortWithDiagnosticLocked", args)));
                    Assert.Equal(kind switch
                    {
                        "first-stale-later-busy" or "first-busy-later-current" or "strict-watch" => "original-read-watch",
                        "version" or "texture" or "width" or "session" or "watch-image" => kind,
                        "unknown" => "watch-unknown-producer", _ => null,
                    }, args[4]);
                    var first = args[5];
                    if (kind is "first-stale-later-busy" or "first-busy-later-current" or "strict-watch" or "current")
                    {
                        Assert.NotNull(first);
                        var provider = (CpuMappedReadWatchDiagnostic)first.GetType().GetProperty("Provider")!.GetValue(first)!;
                        if (kind == "strict-watch") { Assert.Equal(false, provider.BooleanCurrent); Assert.Null(provider.ProviderStatus); }
                        else
                        {
                            Assert.Equal(kind switch { "current" => CpuMappedReadWatchStatus.Current, "first-busy-later-current" => CpuMappedReadWatchStatus.Busy, _ => CpuMappedReadWatchStatus.Stale }, provider.ProviderStatus);
                            Assert.Null(provider.BooleanCurrent);
                        }
                        Assert.Null(first.GetType().GetProperty("Publication")!.GetValue(first));
                        Assert.Null(first.GetType().GetProperty("Validation")!.GetValue(first));
                    }
                    else Assert.Null(first);
                }
            }
            Assert.Equal(kind is "first-stale-later-busy" or "first-busy-later-current" or "current" or "provider-error" ? 1 : 0, queries);
            Assert.Equal(kind == "strict-watch" ? 1 : 0, memory.WatchQueries);
            Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
            Assert.Null(Field("_guestWorkFailure").GetValue(null)); Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
        }
        finally
        {
            Images.Remove(Address); InvokeVolume("ReleaseSingletonVolumeNativeOriginals");
            MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]);
        }
        Assert.Equal(1, memory.Releases);
    }

    [Theory]
    [InlineData("current")] [InlineData("stale")] [InlineData("released")] [InlineData("unknown")]
    public void OriginalReservationBooleanDiagnosticKeepsOneBooleanAndNoStatusQuery(string kind)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory); var original = GetOrigin(snapshot);
        MetalVideoPresenter.AttachGuestMemory(memory); var watch = VolumeWatch(original);
        memory.WatchCurrent = kind == "current";
        memory.ReadStatus = () => throw new IOException("Boolean attribution must not classify Status");
        if (kind == "unknown") watch.GetType().GetField("UnknownProducer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(watch, true);
        if (kind == "released") watch.GetType().GetMethod("Release", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(watch, [false, null]);
        var mappingQueries = memory.MappingQueries; var booleanQueries = memory.WatchQueries;
        try
        {
            var type = Presenter.GetNestedType("StorageWatchReservation", BindingFlags.NonPublic)!;
            object?[] args = [watch, null];
            Assert.Equal(kind == "current", type.GetMethod("IsCurrentWithDiagnostic", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Activator.CreateInstance(type), args));
            var first = args[1]!; var provider = (CpuMappedReadWatchDiagnostic)first.GetType().GetProperty("Provider")!.GetValue(first)!;
            Assert.Equal(kind == "current", first.GetType().GetProperty("BooleanCurrent")!.GetValue(first));
            Assert.Null(provider.ProviderStatus); Assert.Null(provider.PrimaryAdmission);
            Assert.Null(first.GetType().GetProperty("Publication")!.GetValue(first)); Assert.Null(first.GetType().GetProperty("Validation")!.GetValue(first));
            Assert.Equal(booleanQueries + (kind is "released" or "unknown" ? 0 : 1), memory.WatchQueries);
            Assert.Equal(mappingQueries, memory.MappingQueries); Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
        }
        finally { MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
        Assert.Equal(1, memory.Releases);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void StrictNativeOwnerDiagnosticMatchesOriginalBooleanPredicate(bool current)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory); var original = GetOrigin(snapshot);
        var image = VolumeImage(original, memory);
        try
        {
            PublishVolume(snapshot, image); memory.WatchCurrent = current;
            memory.ReadStatus = () => throw new IOException("Strict native owner must not query Status");
            var mappingQueries = memory.MappingQueries; var watchQueries = memory.WatchQueries;
            var reservation = Activator.CreateInstance(Presenter.GetNestedType("StorageWatchReservation", BindingFlags.NonPublic)!)!;
            object?[] args = [image, original, reservation, false, null, null];
            lock (Field("_gate").GetValue(null)!)
            {
                Assert.Equal(current, InvokeVolume("IsSingletonVolumeNativeOwnerCurrentWithReservationLocked", image, original, reservation));
                Assert.Equal(current ? CpuMappedReadWatchStatus.Current : CpuMappedReadWatchStatus.Stale,
                    InvokeVolume("InspectSingletonVolumeCohortWithDiagnosticLocked", args));
            }
            Assert.Equal(watchQueries + 2, memory.WatchQueries); Assert.Equal(mappingQueries, memory.MappingQueries);
            Assert.Equal(current ? null : "original-read-watch", args[4]);
            Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
            Assert.Null(Field("_guestWorkFailure").GetValue(null)); Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
        }
        finally
        {
            Images.Remove(Address); InvokeVolume("ReleaseSingletonVolumeNativeOriginals");
            MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]);
        }
        Assert.Equal(1, memory.Releases);
    }
}
