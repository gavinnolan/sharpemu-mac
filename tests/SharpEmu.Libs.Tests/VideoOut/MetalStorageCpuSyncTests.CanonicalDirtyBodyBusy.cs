// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // These are refusal classification controls with the existing synthetic
    // scalar tracker/provider fixture. They mint no syscall/native authority.
    [Theory]
    [InlineData("busy")] [InlineData("strict-default")] [InlineData("mapping")]
    [InlineData("token")] [InlineData("session")] [InlineData("memory")]
    [InlineData("unknown")] [InlineData("released")] [InlineData("stale")]
    [InlineData("provider-error")]
    public void OriginalDirtyBodyBusyIsTypedOnlyForHealthySameOriginalAndOptIn(string variant)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot);
        MetalVideoPresenter.AttachGuestMemory(memory);
        var watch = VolumeWatch(origin);
        memory.WatchCurrent = false;
        var providerFailure = new IOException("exact actual status failure");
        memory.ReadStatus = () => variant == "provider-error" ? throw providerFailure :
            variant == "stale" ? CpuMappedReadWatchStatus.Stale : CpuMappedReadWatchStatus.Busy;
        switch (variant)
        {
            case "mapping": memory.Current = false; break;
            case "token": protection.Generation(1); break;
            case "session": Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentStorageCpuSeedSession + 1); break;
            case "memory": Field("_guestMemory").SetValue(null, new VolumeMemory(VolumePhysical())); break;
            case "unknown": watch.GetType().GetField("UnknownProducer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(watch, true); break;
            case "released": watch.GetType().GetMethod("Release", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(watch, [false, null]); break;
        }
        try
        {
            var reservation = Activator.CreateInstance(Presenter.GetNestedType("StorageWatchReservation", BindingFlags.NonPublic)!)!;
            var failure = Assert.Throws<TargetInvocationException>(() =>
                InvokeVolume("ValidateSingletonVolumeOriginalContentWithReservation", origin, reservation, variant != "strict-default"));
            if (variant == "busy")
            {
                Assert.Equal("SingletonVolumeOriginalBusy", failure.InnerException!.GetType().Name);
                Assert.Same(origin, failure.InnerException.GetType().GetField("Original", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(failure.InnerException));
            }
            else if (variant == "provider-error") Assert.Same(providerFailure, failure.InnerException);
            else Assert.IsType<InvalidOperationException>(failure.InnerException);
            Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
            Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
            Assert.Null(Field("_guestWorkFailure").GetValue(null));
        }
        finally { MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
        Assert.Equal(1, memory.Releases);
    }
}
