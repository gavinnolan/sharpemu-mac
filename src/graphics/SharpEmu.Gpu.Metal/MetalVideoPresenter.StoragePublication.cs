// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Safe-side construction failure seam. Default null, no native ABI/API.
    private static Action<nint>? _storageImageBeforeMetadataPublication;

    private static void PublishStorageImageLocked(GuestDrawTexture descriptor,
        SelectedStorageMipAssociation? selected, GuestImage image,
        (uint Width, uint Height, ulong ByteCount) extent) =>
        PublishStorageImageWithOriginalReadLocked(descriptor, selected, image, extent, default);

    private static void PublishStorageImageWithOriginalReadLocked(GuestDrawTexture descriptor,
        SelectedStorageMipAssociation? selected, GuestImage image,
        (uint Width, uint Height, ulong ByteCount) extent, StorageWatchReservation originalRead)
    {
        var originalNativeProof = selected is null ? null : PrepareSelectedNativeOriginalProofLocked(selected, image);
        var ownerAdded = false;
        var nativeAdded = false;
        var hadExtent = _guestImageExtents.TryGetValue(descriptor.Address, out var previousExtent);
        try
        {
            if (selected is not null)
            {
                ValidateSelectedStorageMipOwner(selected);
                if (originalNativeProof is not null)
                {
                    // Keep the retain private through callbacks/session resets.
                    _storageImageBeforeMetadataPublication?.Invoke(image.Texture);
                    ValidateSelectedStorageMipOwner(selected);
                    if (!OriginalSelectedNativeContentCurrent(selected, originalNativeProof.Seed))
                        throw new InvalidOperationException("Metal original selected publication changed during its callback.");
                }
                _selectedStorageMipImages.Add(selected.Key, image);
                nativeAdded = true;
                if (originalNativeProof is null) _storageImageBeforeMetadataPublication?.Invoke(image.Texture);
                if (originalNativeProof is not null || MetalR16FloatSelectedMip.TryPlacement(selected.Key.Resource, selected.Key.Mip, out _))
                {
                    ValidateSelectedStorageMipOwner(selected);
                    RejectConflictingSelectedStorageConstructionLocked(selected);
                    if (!_selectedStorageMipImages.TryGetValue(selected.Key, out var current) || !ReferenceEquals(current, image))
                        throw new InvalidOperationException("Metal R16Float publication lost its exact construction owner.");
                }
                ownerAdded = _selectedStorageMipOwners.TryAdd(selected.Key, selected);
                if (MetalR16FloatSelectedMip.TryPlacement(selected.Key.Resource, selected.Key.Mip, out _))
                    image.R16SelectedOwnsRetain = true;
                PublishSelectedNativeOriginalProofLocked(selected, image, originalNativeProof);
            }
            else
            {
                if (image.StorageCpuOrigin is { } originalSeed)
                {
                    // Keep this construction's retain private across the safe
                    // callback. Reentrant close must not release it before the
                    // original session is revalidated and ownership transfers.
                    ValidateStorageCpuSeedSession(originalSeed);
                    _storageImageBeforeMetadataPublication?.Invoke(image.Texture);
                    ValidateStorageCpuSeedSession(originalSeed);
                    _guestImages.Add(descriptor.Address, image);
                    nativeAdded = true;
                    _guestImageExtents[descriptor.Address] = extent;
                    TransferSingletonVolumeOriginalWatchLocked(image, originalSeed, originalRead);
                    return;
                }
                _guestImages.Add(descriptor.Address, image);
                nativeAdded = true;
                _storageImageBeforeMetadataPublication?.Invoke(image.Texture);
                _guestImageExtents[descriptor.Address] = extent;
            }
        }
        catch
        {
            // Restore only this construction's exact entries. A prospective
            // admitted owner, or a preexisting extent record, remains intact.
            if (selected is not null)
            {
                if (nativeAdded && (originalNativeProof is null && !MetalR16FloatSelectedMip.TryPlacement(selected.Key.Resource, selected.Key.Mip, out _) ||
                    _selectedStorageMipImages.TryGetValue(selected.Key, out var current) && ReferenceEquals(current, image)))
                    _selectedStorageMipImages.Remove(selected.Key);
                if (ownerAdded) _selectedStorageMipOwners.Remove(selected.Key);
            }
            else if (nativeAdded)
            {
                if (_guestImages.TryGetValue(descriptor.Address, out var retired))
                    NoteFiniteStorageOwnerRetirementLocked(retired);
                _guestImages.Remove(descriptor.Address);
                if (hadExtent) _guestImageExtents[descriptor.Address] = previousExtent;
                else _guestImageExtents.Remove(descriptor.Address);
            }
            throw;
        }
    }
}
