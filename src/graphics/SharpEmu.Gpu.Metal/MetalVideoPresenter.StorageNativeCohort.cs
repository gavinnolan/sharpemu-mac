// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private const int MaximumFiniteRetiredOrigins = 128;
    private static StorageCpuSeedOrigin?[]? _finiteStorageRetiredOrigins;
    private static int _finiteStorageRetiredOriginCount;
    private static bool _finiteStorageRetiredUnknown;

    // Called BEFORE a native owner leaves/replaces its registry entry. Retain
    // immutable original physical facts, not a native handle or a fresh probe.
    // Missing/overflow debt is process-sticky: session reset is not teardown.
    private static void NoteFiniteStorageOwnerRetirementLocked(GuestImage retired)
    {
        RetireNativeColorImageLocked(retired);
        if (_rgbaStorageRuntimePublicationRequested) RecordFiniteStorageOwnerRetirementLocked(retired);
        retired.StorageCpuOrigin?.VolumeWatch?.Release(preparationOnly: false, primary: _guestWorkFailure);
    }

    // Shared ordinary bookkeeping core allows managed refusal controls without
    // enabling the runtime/native route. It never supplies positive authority.
    private static void RecordFiniteStorageOwnerRetirementLocked(GuestImage retired)
    {
        if (!retired.GpuWritten) return;
        if (_finiteStorageDebts.TryGetValue(retired, out var debt) &&
            !debt.Failed && !debt.Pending && debt.CanonicalVersion == retired.ContentVersion &&
            debt.CanonicalOrigin is { } canonical &&
            ReferenceEquals(canonical, retired.StorageCpuOrigin)) return;
        if (retired.StorageCpuOrigin is not { } original ||
            original.Mapping.FootprintAddress != original.Layout.Address ||
            original.Mapping.FootprintByteCount != original.Layout.PhysicalBytes ||
            original.Mapping.SourceAddress != original.Layout.Address ||
            original.Mapping.SourceByteCount != original.Layout.PhysicalBytes)
        {
            if (!RecordRetiredDisplayOriginalLocked(retired)) _finiteStorageRetiredUnknown = true;
            return;
        }
        for (var index = 0; index < _finiteStorageRetiredOriginCount; index++)
            if (ReferenceEquals(_finiteStorageRetiredOrigins![index], original)) return;
        if (_finiteStorageRetiredOriginCount + _finiteRetiredDisplayOriginCount >= MaximumFiniteRetiredOrigins)
        { _finiteStorageRetiredUnknown = true; return; }
        try { _finiteStorageRetiredOrigins ??= new StorageCpuSeedOrigin?[MaximumFiniteRetiredOrigins]; }
        catch { _finiteStorageRetiredUnknown = true; return; } // optional bookkeeping must preserve an in-flight primary
        _finiteStorageRetiredOrigins[_finiteStorageRetiredOriginCount++] = original;
    }

    // Only retained ORIGINAL mapping facts prove physical disjointness. Never
    // probe a new mapping, query tracker content under _gate, or use distinct
    // virtual addresses as evidence that two native resources cannot alias.
    private static bool IsFiniteStorageNativeCohortCurrentLocked(GuestImage image,
        StorageCpuSeedOrigin origin, out string reason) =>
        InspectFiniteStorageNativeCohortLocked(image, origin, out reason, out _, captureOffender: false);

    private static bool InspectFiniteStorageNativeCohortLocked(GuestImage image,
        StorageCpuSeedOrigin origin, out string reason, out FiniteStorageCohortOffender? offender,
        bool captureOffender, StorageWatchReservation reservation = default) =>
        InspectFiniteStorageNativeCohortStatusLocked(image, origin, out reason, out offender,
            captureOffender, reservation) == CpuMappedReadWatchStatus.Current;

    private static CpuMappedReadWatchStatus InspectFiniteStorageNativeCohortStatusLocked(GuestImage image,
        StorageCpuSeedOrigin origin, out string reason, out FiniteStorageCohortOffender? offender,
        bool captureOffender, StorageWatchReservation reservation = default, bool classifyBusy = false)
    {
        offender = null;
        var busy = false;
        reason = "sticky-native-cleanup-failure";
        if (_singletonVolumeOriginalCleanupFailure is not null || _textureSourceCleanupFailure is not null || _typedCpuSyncCleanupFailure is not null ||
            _r16SelectedRetainCleanupFailure is not null || _selectedNativeOriginalCleanupFailure is not null) return CpuMappedReadWatchStatus.Stale;
        reason = "unordered-presentation-write-debt";
        if (_unorderedPresentationWriteSequence != 0 ||
            _latestPresentation is { } presentation && PresentationHasUnorderedGuestWrites(presentation)) return CpuMappedReadWatchStatus.Stale;
        reason = "retired-native-owner-unknown-or-overflow";
        if (_finiteStorageRetiredUnknown) return CpuMappedReadWatchStatus.Stale;
        for (var index = 0; index < _finiteRetiredDisplayOriginCount; index++)
        {
            var retired = _finiteRetiredDisplayOrigins![index]!;
            reason = "retired-display-original-provenance-stale";
            if (!ReferenceEquals(retired.Memory, origin.Memory) || retired.Session != origin.Session ||
                !retired.Source.IsMappedCopyObservationCurrent(retired.Mapping)) return CpuMappedReadWatchStatus.Stale;
            reason = "retired-display-original-physical-overlap";
            if (PhysicalMappingOverlaps(origin.Mapping, retired.Mapping)) return CpuMappedReadWatchStatus.Stale;
        }
        for (var index = 0; index < _finiteStorageRetiredOriginCount; index++)
        {
            var retired = _finiteStorageRetiredOrigins![index]!;
            reason = "retired-native-owner-original-provenance-stale";
            if (!ReferenceEquals(retired.Memory, origin.Memory) || retired.Session != origin.Session ||
                !retired.Source.IsMappedCopyObservationCurrent(retired.Mapping)) return CpuMappedReadWatchStatus.Stale;
            reason = "retired-native-owner-physical-overlap";
            if (PhysicalMappingOverlaps(origin.Mapping, retired.Mapping)) return CpuMappedReadWatchStatus.Stale;
        }
        reason = "unknown-texture-source-or-depth-alias";
        if (_latestTextureSources.Count != 0 || _guestDepthImages.Count != 0 || _guestDepthReadAliases.Count != 0) return CpuMappedReadWatchStatus.Stale;
        reason = "image-inventory-bound";
        if (_guestImages.Count > 128 || _selectedStorageMipOwners.Count > 128 || _selectedStorageMipImages.Count > 128) return CpuMappedReadWatchStatus.Stale;
        reason = "selected-owner-pending-or-unknown";
        if (_selectedNativeOriginalCleanupFailure is not null || _selectedStorageMipPending.Count != 0 ||
            _selectedStorageMipOwners.Count != _selectedStorageMipImages.Count) return CpuMappedReadWatchStatus.Stale;
        foreach (var pair in _selectedStorageMipOwners)
        {
            var owner = pair.Value;
            reason = "selected-owner-original-provenance-unavailable-or-stale";
            if (!_selectedNativeOriginalProofs.TryGetValue(pair.Key, out var proof) ||
                !_selectedStorageMipImages.TryGetValue(pair.Key, out var selected) ||
                !ReferenceEquals(owner, proof.Owner) || !ReferenceEquals(selected, proof.Image) ||
                owner.Session != origin.Session || selected.Texture != proof.Texture || selected.Texture == 0 ||
                !selected.Initialized || selected.ContentVersion != proof.Version ||
                !ReferenceEquals(proof.Seed.Source, origin.Memory) ||
                proof.Seed.Mapping.FootprintAddress != owner.Key.Resource.Address ||
                proof.Seed.Mapping.FootprintByteCount != owner.Observation.ByteCount ||
                !proof.Seed.Source.IsMappedCopyObservationCurrent(proof.Seed.Mapping)) return CpuMappedReadWatchStatus.Stale;
            reason = "selected-owner-physical-overlap";
            if (PhysicalMappingOverlaps(origin.Mapping, proof.Seed.Mapping)) return CpuMappedReadWatchStatus.Stale;
        }
        foreach (var pair in _guestImages)
        {
            var other = pair.Value;
            if (ReferenceEquals(other, image)) continue;
            reason = "other-image-original-provenance-unavailable-or-stale";
            var otherOrigin = other.StorageCpuOrigin;
            if (otherOrigin is null && _displayOriginalOwners.TryGetValue(other, out var display))
            {
                reason = "other-display-original-owner-or-provenance-stale";
                if (!IsDisplayOriginalOwnerCurrentLocked(other, display, origin.Memory, origin.Session)) return CpuMappedReadWatchStatus.Stale;
                reason = "other-display-original-physical-overlap";
                if (PhysicalMappingOverlaps(origin.Mapping, display.Copy.Mapping)) return CpuMappedReadWatchStatus.Stale;
                continue;
            }
            string? branch = otherOrigin is null ? "origin-missing" :
                !ReferenceEquals(otherOrigin.Memory, origin.Memory) ? "memory-mismatch" :
                otherOrigin.Session != origin.Session ? "session-mismatch" :
                otherOrigin.Mapping.FootprintAddress != otherOrigin.Layout.Address ? "footprint-address" :
                otherOrigin.Mapping.FootprintByteCount != otherOrigin.Layout.PhysicalBytes ? "footprint-bytes" :
                otherOrigin.Mapping.SourceAddress != otherOrigin.Layout.Address ? "source-address" :
                otherOrigin.Mapping.SourceByteCount != otherOrigin.Layout.PhysicalBytes ? "source-bytes" :
                !otherOrigin.Source.IsMappedCopyObservationCurrent(otherOrigin.Mapping) ? "mapping-stale" : null;
            if (branch is not null)
            {
                if (captureOffender && _rgbaStorageRuntimePublicationRequested)
                    offender = CaptureFiniteStorageCohortOffender(pair.Key, other, branch);
                return CpuMappedReadWatchStatus.Stale;
            }
            reason = "other-image-original-volume-watch-or-version";
            if (other.TextureType == MtlTextureType3D &&
                (otherOrigin!.VolumeWatch is null || !IsSingletonVolumeNativeOwnerCurrentWithReservationLocked(other, otherOrigin, reservation)))
            {
                // Classification only: Busy never makes this cohort Current.
                if (classifyBusy && IsSingletonVolumeNativeOwnerMetadataCurrentLocked(other, otherOrigin!) &&
                    otherOrigin!.VolumeWatch!.Source.GetMappedCopyWriteWatchStatus(otherOrigin.VolumeWatch.Mapping) == CpuMappedReadWatchStatus.Busy)
                    busy = true;
                else
                {
                    if (captureOffender && _rgbaStorageRuntimePublicationRequested)
                        offender = CaptureFiniteStorageCohortOffender(pair.Key, other, "original-volume-watch-or-version");
                    return CpuMappedReadWatchStatus.Stale;
                }
            }
            reason = "other-image-physical-overlap";
            if (PhysicalMappingOverlaps(origin.Mapping, otherOrigin!.Mapping))
            {
                if (captureOffender && _rgbaStorageRuntimePublicationRequested)
                    offender = CaptureFiniteStorageCohortOffender(pair.Key, other, "physical-overlap");
                return CpuMappedReadWatchStatus.Stale;
            }
        }
        reason = busy ? "original-volume-watch-busy" : "qualified";
        return busy ? CpuMappedReadWatchStatus.Busy : CpuMappedReadWatchStatus.Current;
    }

    private static void RequireFiniteStorageNativeCohortLocked(GuestImage image, StorageCpuSeedOrigin origin,
        StorageWatchReservation reservation = default)
    {
        if (!InspectFiniteStorageNativeCohortLocked(image, origin, out var reason, out var offender,
            captureOffender: _rgbaStorageRuntimePublicationRequested, reservation: reservation))
        {
            var message = "Metal finite canonical publication native cohort is unavailable: " + reason;
            if (offender is { } captured) throw new FiniteStorageCohortRefusal(message, captured);
            throw new NotSupportedException(message);
        }
    }
}
