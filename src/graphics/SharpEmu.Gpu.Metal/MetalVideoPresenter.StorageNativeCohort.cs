// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
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
        if (!_rgbaStorageRuntimePublicationRequested || !retired.GpuWritten) return;
        if (_finiteStorageDebts.TryGetValue(retired, out var debt) &&
            !debt.Failed && !debt.Pending && debt.CanonicalVersion == retired.ContentVersion &&
            debt.CanonicalOrigin is { } canonical &&
            ReferenceEquals(canonical, retired.StorageCpuOrigin)) return;
        if (retired.StorageCpuOrigin is not { } original ||
            original.Mapping.FootprintAddress != original.Layout.Address ||
            original.Mapping.FootprintByteCount != original.Layout.PhysicalBytes ||
            original.Mapping.SourceAddress != original.Layout.Address ||
            original.Mapping.SourceByteCount != original.Layout.PhysicalBytes)
        { _finiteStorageRetiredUnknown = true; return; }
        for (var index = 0; index < _finiteStorageRetiredOriginCount; index++)
            if (ReferenceEquals(_finiteStorageRetiredOrigins![index], original)) return;
        if (_finiteStorageRetiredOriginCount == MaximumFiniteRetiredOrigins)
        { _finiteStorageRetiredUnknown = true; return; }
        try { _finiteStorageRetiredOrigins ??= new StorageCpuSeedOrigin?[MaximumFiniteRetiredOrigins]; }
        catch { _finiteStorageRetiredUnknown = true; throw; }
        _finiteStorageRetiredOrigins[_finiteStorageRetiredOriginCount++] = original;
    }

    // Only retained ORIGINAL mapping facts prove physical disjointness. Never
    // probe a new mapping, query tracker content under _gate, or use distinct
    // virtual addresses as evidence that two native resources cannot alias.
    private static bool IsFiniteStorageNativeCohortCurrentLocked(GuestImage image,
        StorageCpuSeedOrigin origin, out string reason)
    {
        reason = "sticky-native-cleanup-failure";
        if (_textureSourceCleanupFailure is not null || _typedCpuSyncCleanupFailure is not null ||
            _r16SelectedRetainCleanupFailure is not null || _selectedNativeOriginalCleanupFailure is not null) return false;
        reason = "unordered-presentation-write-debt";
        if (_unorderedPresentationWriteSequence != 0 ||
            _latestPresentation is { } presentation && PresentationHasUnorderedGuestWrites(presentation)) return false;
        reason = "retired-native-owner-unknown-or-overflow";
        if (_finiteStorageRetiredUnknown) return false;
        for (var index = 0; index < _finiteStorageRetiredOriginCount; index++)
        {
            var retired = _finiteStorageRetiredOrigins![index]!;
            reason = "retired-native-owner-original-provenance-stale";
            if (!ReferenceEquals(retired.Memory, origin.Memory) || retired.Session != origin.Session ||
                !retired.Source.IsMappedCopyObservationCurrent(retired.Mapping)) return false;
            reason = "retired-native-owner-physical-overlap";
            if (PhysicalMappingOverlaps(origin.Mapping, retired.Mapping)) return false;
        }
        reason = "unknown-texture-source-or-depth-alias";
        if (_latestTextureSources.Count != 0 || _guestDepthImages.Count != 0 || _guestDepthReadAliases.Count != 0) return false;
        reason = "image-inventory-bound";
        if (_guestImages.Count > 128 || _selectedStorageMipOwners.Count > 128 || _selectedStorageMipImages.Count > 128) return false;
        reason = "selected-owner-pending-or-unknown";
        if (_selectedNativeOriginalCleanupFailure is not null || _selectedStorageMipPending.Count != 0 ||
            _selectedStorageMipOwners.Count != _selectedStorageMipImages.Count) return false;
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
                !proof.Seed.Source.IsMappedCopyObservationCurrent(proof.Seed.Mapping)) return false;
            reason = "selected-owner-physical-overlap";
            if (PhysicalMappingOverlaps(origin.Mapping, proof.Seed.Mapping)) return false;
        }
        foreach (var other in _guestImages.Values)
        {
            if (ReferenceEquals(other, image)) continue;
            reason = "other-image-original-provenance-unavailable-or-stale";
            if (other.StorageCpuOrigin is not { } otherOrigin ||
                !ReferenceEquals(otherOrigin.Memory, origin.Memory) || otherOrigin.Session != origin.Session ||
                otherOrigin.Mapping.FootprintAddress != otherOrigin.Layout.Address ||
                otherOrigin.Mapping.FootprintByteCount != otherOrigin.Layout.PhysicalBytes ||
                otherOrigin.Mapping.SourceAddress != otherOrigin.Layout.Address ||
                otherOrigin.Mapping.SourceByteCount != otherOrigin.Layout.PhysicalBytes ||
                !otherOrigin.Source.IsMappedCopyObservationCurrent(otherOrigin.Mapping)) return false;
            reason = "other-image-physical-overlap";
            if (PhysicalMappingOverlaps(origin.Mapping, otherOrigin.Mapping)) return false;
        }
        reason = "qualified";
        return true;
    }

    private static void RequireFiniteStorageNativeCohortLocked(GuestImage image, StorageCpuSeedOrigin origin)
    {
        if (!IsFiniteStorageNativeCohortCurrentLocked(image, origin, out var reason))
            throw new NotSupportedException("Metal finite canonical publication native cohort is unavailable: " + reason);
    }
}
