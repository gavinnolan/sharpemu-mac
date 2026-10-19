// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Synthetic scalar provider/native metadata exercises refusal policy only.
    // It supplies no native completion, permission receipt or GPU byte oracle.
    [Theory]
    [InlineData("busy-clears")] [InlineData("busy")] [InlineData("current")]
    [InlineData("stale-then-busy")] [InlineData("strict-default")]
    [InlineData("unknown")] [InlineData("version")] [InlineData("mapping")]
    [InlineData("overlap")] [InlineData("provider-error")]
    public void NativeCohortKeepsFirstOriginalStatusWithoutRelaxingOtherOwnerVetoes(string variant)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory);
        var original = GetOrigin(snapshot); var volume = VolumeImage(original, memory);
        var target = Activator.CreateInstance(Presenter.GetNestedType("GuestImage", BindingFlags.NonPublic)!, true)!;
        var targetOrigin = original.GetType().GetMethod("<Clone>$")!.Invoke(original, null)!;
        var targetMapping = new CpuMappedCopyObservation(memory, Address, 65536, Address, 65536,
            [new(Address, 65536, Address, 65536, 1, variant == "overlap" ? 1 : 2,
                65536, 0, 16384, GuestPageProtection.Read | GuestPageProtection.Write)]);
        targetOrigin.GetType().GetProperty("Mapping")!.SetValue(targetOrigin, targetMapping);
        var statusQueries = 0;
        var providerFailure = new IOException("cohort original status failed");
        try
        {
            PublishVolume(snapshot, volume);
            memory.WatchQueries = 0;
            memory.WatchCurrent = false;
            memory.ReadStatus = () =>
            {
                statusQueries++;
                if (variant == "provider-error") throw providerFailure;
                if (variant == "current") return CpuMappedReadWatchStatus.Current;
                if (variant == "stale-then-busy")
                    return statusQueries == 1 ? CpuMappedReadWatchStatus.Stale : CpuMappedReadWatchStatus.Busy;
                if (variant == "busy-clears")
                    return statusQueries == 1 ? CpuMappedReadWatchStatus.Busy : CpuMappedReadWatchStatus.Current;
                return CpuMappedReadWatchStatus.Busy;
            };
            if (variant == "mapping") memory.Current = false;
            if (variant == "version") Set(volume, "ContentVersion", 2);
            if (variant == "unknown") VolumeWatch(original).GetType()
                .GetField("UnknownProducer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(VolumeWatch(original), true);
            var reservation = Activator.CreateInstance(Presenter.GetNestedType("StorageWatchReservation", BindingFlags.NonPublic)!)!;
            object?[] args = [target, targetOrigin, null, null, false, reservation, variant != "strict-default"];
            lock (Field("_gate").GetValue(null)!)
            {
                if (variant == "provider-error")
                {
                    var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("InspectFiniteStorageNativeCohortStatusLocked", args));
                    Assert.Same(providerFailure, failure.InnerException);
                }
                else
                {
                    var actual = Assert.IsType<CpuMappedReadWatchStatus>(InvokeVolume("InspectFiniteStorageNativeCohortStatusLocked", args));
                    Assert.Equal(variant switch
                    {
                        "busy" or "busy-clears" => CpuMappedReadWatchStatus.Busy,
                        "current" => CpuMappedReadWatchStatus.Current,
                        _ => CpuMappedReadWatchStatus.Stale,
                    }, actual);
                    if (variant == "overlap") Assert.Equal("other-image-physical-overlap", args[2]);
                }
            }
            Assert.Equal(variant is "strict-default" or "unknown" or "version" or "mapping" ? 0 : 1, statusQueries);
            Assert.Equal(variant == "strict-default" ? 1 : 0, memory.WatchQueries);
            Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
            Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
            Assert.Null(Field("_guestWorkFailure").GetValue(null));
        }
        finally
        {
            Images.Remove(Address);
            InvokeVolume("ReleaseSingletonVolumeNativeOriginals");
            MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]);
        }
        Assert.Equal(1, memory.Releases);
    }
}
