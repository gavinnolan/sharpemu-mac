// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Scalar query attribution only; the synthetic provider grants no GPU or
    // physical mapping authority. Real writer/ledger tests are in Host.Memory.
    [Theory]
    [InlineData("current")] [InlineData("busy")] [InlineData("stale")]
    [InlineData("released")] [InlineData("unknown")] [InlineData("provider-error")]
    public void OriginalReservationFirstStatusKeepsOneSourceQueryAndUnreachedTicketOutcomes(string kind)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot);
        MetalVideoPresenter.AttachGuestMemory(memory); var watch = VolumeWatch(origin);
        var sourceQueries = 0; var cause = new IOException("exact first status failure");
        memory.ReadStatus = () =>
        {
            sourceQueries++;
            if (kind == "provider-error") throw cause;
            return kind switch { "current" => CpuMappedReadWatchStatus.Current, "busy" => CpuMappedReadWatchStatus.Busy, _ => CpuMappedReadWatchStatus.Stale };
        };
        if (kind == "unknown") watch.GetType().GetField("UnknownProducer", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(watch, true);
        if (kind == "released") watch.GetType().GetMethod("Release", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(watch, [false, null]);
        var mappingQueries = memory.MappingQueries; var booleanQueries = memory.WatchQueries;
        try
        {
            var reservationType = Presenter.GetNestedType("StorageWatchReservation", BindingFlags.NonPublic)!;
            var reservation = Activator.CreateInstance(reservationType)!;
            var method = reservationType.GetMethod("GetStatusWithDiagnostic", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object?[] args = [watch, null];
            if (kind == "provider-error")
            {
                Assert.Same(cause, Assert.Throws<TargetInvocationException>(() => method.Invoke(reservation, args)).InnerException);
                Assert.Equal(1, sourceQueries);
            }
            else
            {
                var returned = (CpuMappedReadWatchStatus)method.Invoke(reservation, args)!;
                var diagnostic = args[1]!; var type = diagnostic.GetType();
                var provider = (CpuMappedReadWatchDiagnostic)type.GetProperty("Provider")!.GetValue(diagnostic)!;
                Assert.Equal(kind switch { "current" => CpuMappedReadWatchStatus.Current, "busy" => CpuMappedReadWatchStatus.Busy, _ => CpuMappedReadWatchStatus.Stale }, returned);
                Assert.Equal(kind is "released" or "unknown" ? 0 : 1, sourceQueries);
                Assert.Equal(kind switch { "released" => "watch-consumed", "unknown" => "watch-unknown-producer", _ => null }, type.GetProperty("LocalGuard")!.GetValue(diagnostic));
                Assert.Null(type.GetProperty("OriginalRead")!.GetValue(diagnostic));
                Assert.Null(type.GetProperty("Publication")!.GetValue(diagnostic));
                Assert.Null(type.GetProperty("Validation")!.GetValue(diagnostic));
                if (kind is "released" or "unknown") Assert.Null(provider.ProviderStatus);
                else { Assert.Equal(returned, provider.ProviderStatus); Assert.Equal("source-status-unattributed", provider.WatchGuard); }
                Assert.Null(provider.PrimaryAdmission); // unsupported participant is explicit, never guessed
            }
            Assert.Equal(mappingQueries, memory.MappingQueries); Assert.Equal(booleanQueries, memory.WatchQueries);
            Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
            Assert.Null(Field("_guestWorkFailure").GetValue(null)); Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
        }
        finally { MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
        Assert.Equal(1, memory.Releases);
    }
}
