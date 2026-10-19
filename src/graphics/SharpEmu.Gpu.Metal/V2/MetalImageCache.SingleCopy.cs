// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal.V2;

// What single-copy textures (MetalSingleCopy) need from the cache: its images, and whether
// one image alone holds a guest range.
internal sealed partial class MetalImageCache
{
    /// <summary>The single-copy manager, or null when single-copy textures are off.</summary>
    public MetalSingleCopy? SingleCopy { get; set; }

    // Caller holds the lock.
    internal void ForEachImageLocked(Action<MetalCachedImage> visit) => _slots.ForEach((_, image) => visit(image));

    /// <summary>
    /// Caller holds the lock. True when <paramref name="image"/> is the only image over guest
    /// [address, address + size) by page, and nothing else the cache knows of reads or writes
    /// it: no metadata watch, no staged range, no GPU write still in flight.
    /// </summary>
    internal bool IsSoleSingleCopyOwnerLocked(MetalCachedImage image, ulong address, ulong size)
    {
        var owners = FindImagesInRange(address, size, pageOverlap: true);
        if (owners.Count != 1 || !ReferenceEquals(_slots.TryGet(owners[0]), image))
        {
            return false;
        }

        foreach (var (begin, length) in _metadataWatches)
        {
            if (begin < address + size && address < begin + length)
            {
                return false;
            }
        }

        return StagedRanges?.MayCover(address, size) != true && !_owner.HasPendingGpuWrite(address, size);
    }
}
