// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    private sealed class LocalDisjointDiagnosticControl(bool result) : CpuMappedPublicationValidationTransaction
    {
        internal int Queries, Releases;
        internal override bool IsCurrent => true;
        internal override bool IsOriginalDisjointReadWatchCurrent(CpuMappedCopyObservation original)
        { Queries++; return result; }
        internal override void Cancel() => Releases++;
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnattributedLocalValidationDiagnosticDelegatesExactlyOneOriginalBooleanCall(bool result)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory);
        var control = new LocalDisjointDiagnosticControl(result); using var receipt = new CpuMappedPublicationValidation(control);
        try
        {
            Assert.Equal(result, receipt.IsOriginalDisjointReadWatchCurrent(memory.LastMapping!, out var first));
            Assert.Equal(1, control.Queries); Assert.Equal(result, first.BooleanCurrent);
            Assert.Equal("provider-unattributed", first.Guard); Assert.Null(first.LocalAdmissionOwnerIdentity);
            Assert.Null(first.ReservedBackingIdentity); Assert.Null(first.WatchedBackingIdentity);
            Assert.Null(first.PrimaryAdmission); Assert.Null(first.MappingGuard); Assert.Null(first.WatchGuard);
            Assert.Null(first.WatchedOffset); Assert.Null(first.ReservedOffset); Assert.Equal(0, control.Releases);
        }
        finally { MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
    }

    // Pure synthetic provider delegation/reporting, no physical writer or native
    // authority. The real managed provider proof is in Host.Memory controls.
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PresenterLocalDisjointAttributionKeepsFirstProviderStaleAndOneLocalProof(bool localResult)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot);
        MetalVideoPresenter.AttachGuestMemory(memory); var watch = VolumeWatch(origin);
        var providerQueries = 0; memory.ReadStatus = () => { providerQueries++; return CpuMappedReadWatchStatus.Stale; };
        var control = new LocalDisjointDiagnosticControl(localResult); using var receipt = new CpuMappedPublicationValidation(control);
        var type = Presenter.GetNestedType("StorageWatchReservation", BindingFlags.NonPublic)!;
        var reservation = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, [receipt], null)!;
        object?[] args = [watch, null];
        try
        {
            var result = (CpuMappedReadWatchStatus)type.GetMethod("GetStatusWithDiagnostic", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(reservation, args)!;
            Assert.Equal(localResult ? CpuMappedReadWatchStatus.Current : CpuMappedReadWatchStatus.Stale, result);
            Assert.Equal(1, providerQueries); Assert.Equal(1, control.Queries);
            var first = args[1]!; var firstType = first.GetType();
            var provider = (CpuMappedReadWatchDiagnostic)firstType.GetProperty("Provider")!.GetValue(first)!;
            var local = (CpuMappedDisjointReadWatchDiagnostic)firstType.GetProperty("LocalValidation")!.GetValue(first)!;
            Assert.Equal(CpuMappedReadWatchStatus.Stale, provider.ProviderStatus); // first query never overwritten
            Assert.Equal(localResult, firstType.GetProperty("Validation")!.GetValue(first));
            Assert.Equal("validation-local-ticket-no-wait-authority", firstType.GetProperty("LocalTicket")!.GetValue(first));
            Assert.Equal("provider-unattributed", local.Guard); Assert.Equal(localResult, local.BooleanCurrent);
            Assert.Null(local.LocalAdmissionOwnerIdentity); Assert.Null(local.WatchedOffset);
            Assert.Equal(0, control.Releases); Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
        }
        finally { MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
    }
}
