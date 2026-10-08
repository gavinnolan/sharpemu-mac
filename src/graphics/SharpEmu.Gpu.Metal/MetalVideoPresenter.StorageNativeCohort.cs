// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Only retained ORIGINAL mapping facts prove physical disjointness. Never
    // probe a new mapping, query tracker content under _gate, or use distinct
    // virtual addresses as evidence that two native resources cannot alias.
    private static bool IsFiniteStorageNativeCohortCurrentLocked(GuestImage image,
        StorageCpuSeedOrigin origin, out string reason)
    {
        reason = "unordered-presentation-write-debt";
        if (_unorderedPresentationWriteSequence != 0 ||
            _latestPresentation is { } presentation && PresentationHasUnorderedGuestWrites(presentation)) return false;
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
