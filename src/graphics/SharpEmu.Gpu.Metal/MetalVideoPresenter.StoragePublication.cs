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
        (uint Width, uint Height, ulong ByteCount) extent)
    {
        var ownerAdded = false;
        var nativeAdded = false;
        var hadExtent = _guestImageExtents.TryGetValue(descriptor.Address, out var previousExtent);
        try
        {
            if (selected is not null)
            {
                ValidateSelectedStorageMipOwner(selected);
                _selectedStorageMipImages.Add(selected.Key, image);
                nativeAdded = true;
                _storageImageBeforeMetadataPublication?.Invoke(image.Texture);
                ownerAdded = _selectedStorageMipOwners.TryAdd(selected.Key, selected);
            }
            else
            {
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
                if (nativeAdded) _selectedStorageMipImages.Remove(selected.Key);
                if (ownerAdded) _selectedStorageMipOwners.Remove(selected.Key);
            }
            else if (nativeAdded)
            {
                _guestImages.Remove(descriptor.Address);
                if (hadExtent) _guestImageExtents[descriptor.Address] = previousExtent;
                else _guestImageExtents.Remove(descriptor.Address);
            }
            throw;
        }
    }
}
