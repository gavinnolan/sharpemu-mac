// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal.V2;

// Registration in the page owner index, page watches, CPU-write invalidation,
// eviction under memory pressure, and the surface metadata (DCC and HTile) state.
internal sealed partial class MetalImageCache
{
    private void AddToIndex(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        if (image.Registered)
        {
            return;
        }

        if (!MetalImagePageOwners.TryGetPageRange(
                image.Description.Data.Address, image.Description.Data.Size, out var first, out var lastExclusive))
        {
            throw GpuFatal.Fatal(
                $"The image registration is outside the guest address space: address=0x{image.Description.Data.Address:X16} " +
                $"size=0x{image.Description.Data.Size:X}.");
        }

        for (var page = first; page < lastExclusive; page++)
        {
            _pageOwners.GetOrCreate(page).Add(imageIdentifier);
        }

        image.Registered = true;
        _totalUsedMemory += image.AccountedSize;
    }

    private void RemoveFromIndex(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots.TryGet(imageIdentifier);
        if (image is null || !image.Registered)
        {
            return;
        }

        UnwatchImage(imageIdentifier);
        if (MetalImagePageOwners.TryGetPageRange(
                image.Description.Data.Address, image.Description.Data.Size, out var first, out var lastExclusive))
        {
            for (var page = first; page < lastExclusive; page++)
            {
                _pageOwners.Find(page)?.Remove(imageIdentifier);
            }
        }

        var accounted = image.AccountedSize;
        _totalUsedMemory -= Math.Min(accounted, _totalUsedMemory);
        image.Registered = false;
    }

    // Publishes a GPU-written image back into guest memory, then drops it. Guest
    // memory is the single source of truth, so nothing is lost by dropping a texture.
    private void ReleaseImage(ResourceSlotIdentifier imageIdentifier, MetalImageReleaseCause cause)
    {
        _releaseCauses[(int)cause]++;
        if (_slots.TryGet(imageIdentifier) is not { } image)
        {
            return;
        }

        if (image.IsGpuModified)
        {
            WriteBackToGuest(image, MetalWritebackCause.Release);
        }

        if (image.Description.HasMetadata)
        {
            _surfaceMetadata.Remove(image.Description.Metadata.Range.Address);
        }

        RemoveFromIndex(imageIdentifier);
        _slots.Erase(imageIdentifier);
        RetireImage(image);
    }

    // A command buffer names a texture through an argument buffer, which holds a
    // gpuResourceID that Metal does not retain: releasing the texture while a
    // recorded-but-unfinished submission still names it leaves the GPU driver
    // messaging freed memory. So an evicted image's Metal objects wait for the
    // submission that last bound them, and the slot is free either way.
    private void RetireImage(MetalCachedImage image)
    {
        if (image.LastUseSerial <= _owner.CompletedSerial)
        {
            image.Dispose();
            return;
        }

        _retiring.Add((image.LastUseSerial, image));
    }

    // Drops the Metal objects of every evicted image the GPU has finished with.
    private void DrainRetiredImages()
    {
        if (_retiring.Count == 0)
        {
            return;
        }

        var completed = _owner.CompletedSerial;
        var kept = 0;
        for (var index = 0; index < _retiring.Count; index++)
        {
            var entry = _retiring[index];
            if (entry.Serial <= completed)
            {
                entry.Image.Dispose();
                continue;
            }

            _retiring[kept++] = entry;
        }

        _retiring.RemoveRange(kept, _retiring.Count - kept);
    }

    private void WatchImage(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots[imageIdentifier];
        if (_watchingStopped || !image.Registered || ImageDescription.IsEmptyRange(image.Description.Data))
        {
            return;
        }

        var begin = image.Description.Data.Address;
        var end = image.Description.Data.End;
        if (begin == image.WatchBegin && end == image.WatchEnd)
        {
            return;
        }

        if (image.IsWatched)
        {
            UnwatchImage(imageIdentifier);
        }

        image.WatchBegin = begin;
        image.WatchEnd = end;
        _pages.AddWatch(begin, image.Description.Data.Size, blockReads: false);
    }

    private void UnwatchImage(ResourceSlotIdentifier imageIdentifier)
    {
        var image = _slots.TryGet(imageIdentifier);
        if (image is null || !image.IsWatched)
        {
            return;
        }

        var address = image.WatchBegin;
        var size = image.WatchEnd - image.WatchBegin;
        image.WatchBegin = 0;
        image.WatchEnd = 0;
        if (size != 0)
        {
            _pages.RemoveWatch(address, size, blockReads: false);
        }
    }

    // Caller holds the lock; the query epoch marks each image at most once.
    private ImageQueryResults FindImagesInRange(ulong address, ulong size, bool pageOverlap)
    {
        var result = new ImageQueryResults();
        if (!MetalImagePageOwners.TryGetPageRange(address, size, out var first, out var lastExclusive))
        {
            return result;
        }

        var queryEpoch = ++_queryEpoch;
        if (queryEpoch == 0)
        {
            _slots.ForEach(static (_, image) => image.QueryEpoch = 0);
            queryEpoch = ++_queryEpoch;
        }

        for (var page = first; page < lastExclusive; page++)
        {
            var owners = _pageOwners.Find(page);
            if (owners is null)
            {
                continue;
            }

            for (var index = 0; index < owners.Count; index++)
            {
                var imageIdentifier = owners[index];
                var image = _slots.TryGet(imageIdentifier);
                if (image is null || image.QueryEpoch == queryEpoch)
                {
                    continue;
                }

                image.QueryEpoch = queryEpoch;
                if (image.Overlaps(address, size, pageOverlap))
                {
                    result.Add(imageIdentifier);
                }
            }
        }

        return result;
    }

    // A CPU write on the faulting thread. Most writes (command building, labels)
    // touch no image page, and the owner bitmap answers those without the lock.
    bool IGuestImageStore.MarkCpuWrite(ulong address, ulong size)
    {
        if (!IsValidRange(address, size) || !_pageOwners.MayHaveOwners(address, size))
        {
            return false;
        }

        using var held = _lock.Hold();
        return InvalidateAliases(address, size);
    }

    public void InvalidateMemory(ulong address, ulong size)
    {
        if (!IsValidRange(address, size))
        {
            return;
        }

        using var held = _lock.Hold();
        InvalidateAliases(address, size);
    }

    private bool InvalidateAliases(ulong address, ulong size)
    {
        var covered = false;
        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: true))
        {
            if (_slots.TryGet(imageIdentifier) is not { } image)
            {
                continue;
            }

            covered = true;
            image.InvalidateCpuWrite(address, size);
            UnwatchImage(imageIdentifier);
        }

        return covered;
    }

    // The guest unmapped the range: drop its metadata and its images without a writeback.
    void IGuestImageStore.Unregister(ulong address, ulong size)
    {
        if (!IsValidRange(address, size))
        {
            return;
        }

        using var held = _lock.Hold();
        var end = address + size;
        var stale = new List<ulong>();
        foreach (var metadataAddress in _surfaceMetadata.Keys)
        {
            if (metadataAddress >= address && metadataAddress < end)
            {
                stale.Add(metadataAddress);
            }
        }

        foreach (var metadataAddress in stale)
        {
            _surfaceMetadata.Remove(metadataAddress);
        }

        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: false))
        {
            if (_slots.TryGet(imageIdentifier) is not { } image)
            {
                continue;
            }

            // The pages are gone; there is nowhere to write the contents back to.
            image.ClearGpuModified();
            ReleaseImage(imageIdentifier, MetalImageReleaseCause.Unmapped);
        }
    }

    /// <summary>
    /// Releases every page watch and takes no new ones. The runtime frees the page
    /// guard as soon as the guest has left, and it refuses to be freed while a watch
    /// is outstanding, so this runs from the shutting-down thread rather than waiting
    /// for the GPU worker to notice.
    /// </summary>
    public void StopWatching()
    {
        using var held = _lock.Hold();
        _watchingStopped = true;
        var watched = new List<ResourceSlotIdentifier>();
        _slots.ForEach((imageIdentifier, image) =>
        {
            if (image.IsWatched)
            {
                watched.Add(imageIdentifier);
            }
        });
        foreach (var imageIdentifier in watched)
        {
            UnwatchImage(imageIdentifier);
        }
    }

    /// <summary>Advances the collection tick and evicts stale images over the budget.</summary>
    public void Collect()
    {
        using var held = _lock.Hold();
        _collectionTick++;
        DrainRetiredImages();
        CollectIfOverBudget();
    }

    // Eviction under memory pressure: the least recently used clean images go first,
    // and a GPU-modified image is written back before its texture is dropped.
    private void CollectIfOverBudget()
    {
        if (_totalUsedMemory <= _memoryBudgetBytes)
        {
            return;
        }

        var candidates = new List<(ulong Tick, ResourceSlotIdentifier Image)>();
        _slots.ForEach((imageIdentifier, image) =>
        {
            if (image.Registered && _collectionTick - Math.Min(_collectionTick, image.LastAccessTick) > TicksBeforeRemoval)
            {
                candidates.Add((image.LastAccessTick, imageIdentifier));
            }
        });
        candidates.Sort(static (left, right) => left.Tick.CompareTo(right.Tick));
        var target = _memoryBudgetBytes - _memoryBudgetBytes / 8;
        foreach (var (_, imageIdentifier) in candidates)
        {
            if (_totalUsedMemory <= target)
            {
                break;
            }

            ReleaseImage(imageIdentifier, MetalImageReleaseCause.Budget);
            Interlocked.Increment(ref _evictions);
        }

        if (_totalUsedMemory > _memoryBudgetBytes)
        {
            MetalV2Diagnostics.InfoOnce(
                "image-budget",
                $"The image cache is over its budget with no evictable image: used={_totalUsedMemory / MiB}MB " +
                $"budget={_memoryBudgetBytes / MiB}MB images={_slots.Count}.");
        }
    }

    // SHARPEMU_METAL_V2_DCC_MATERIALIZED_SET=0 turns the memory off and takes the
    // repeated materialization back, for comparing one against the other.
    private static readonly bool UseMaterializedSet = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DCC_MATERIALIZED_SET"),
        "0",
        StringComparison.Ordinal);

    // Metadata slices whose clear has already been materialized, by address and clear
    // code. Keyed on the code as well as the address so a different clear colour at the
    // same metadata is a new clear rather than a skipped one.
    private readonly HashSet<(ulong Address, byte Code)> _materializedDccSlices = new();
    private long _dccRepeatsSkipped;

    /// <summary>Materializations skipped because the clear was already applied.</summary>
    public long TakeDccRepeatsSkipped() => Interlocked.Exchange(ref _dccRepeatsSkipped, 0);

    /// <summary>
    /// Claims this slice and clear code for materialization, or reports that it is
    /// already done. The scan around it runs outside the cache lock — it reads guest
    /// backing — so the claim takes the lock itself.
    /// </summary>
    private bool TryClaimMaterializedDcc(ulong address, byte code)
    {
        using var held = _lock.Hold();
        return _materializedDccSlices.Add((address, code));
    }

    private void ReArmMaterializedDccLocked(ulong address, ulong size)
    {
        if (_materializedDccSlices.Count != 0)
        {
            _materializedDccSlices.RemoveWhere(
                entry => entry.Address >= address && entry.Address - address < size);
        }
    }

    private void RegisterDccMetadata(in ImageDescription description)
    {
        var address = description.Metadata.Range.Address;
        if (!_surfaceMetadata.TryGetValue(address, out var metadata))
        {
            metadata = new SurfaceMetadata { Kind = SurfaceMetadataKind.Dcc };
            _surfaceMetadata.Add(address, metadata);
        }
        else if (metadata.Kind == SurfaceMetadataKind.PendingDcc)
        {
            metadata.Kind = SurfaceMetadataKind.Dcc;
        }
        else if (metadata.Kind != SurfaceMetadataKind.Dcc)
        {
            return;
        }

        metadata.Size = Math.Max(metadata.Size, description.DccSliceSize * description.TransferLayers);
    }

    private void RegisterHtileMetadata(in ImageDescription description)
    {
        var address = description.Metadata.Range.Address;
        if (!_surfaceMetadata.TryGetValue(address, out var metadata))
        {
            _surfaceMetadata.Add(address, new SurfaceMetadata
            {
                Kind = SurfaceMetadataKind.HTile,
                ClearMask = description.HtileClearMask,
            });
            return;
        }

        if (metadata.Kind == SurfaceMetadataKind.PendingDcc)
        {
            metadata.Kind = SurfaceMetadataKind.HTile;
            metadata.ClearMask = description.HtileClearMask;
            metadata.FillValue = 0xffffffff;
            metadata.FillSize = 0;
        }
    }

    public bool IsMetadata(ulong address)
    {
        using var held = _lock.Hold();
        return _surfaceMetadata.TryGetValue(address, out var found) && found.Kind != SurfaceMetadataKind.PendingDcc;
    }

    public bool IsMetadataCleared(ulong address, uint slice, out uint fillValue)
    {
        using var held = _lock.Hold();
        return IsMetadataClearedLocked(address, slice, out fillValue);
    }

    /// <summary>As above, for a caller that already holds the lock.</summary>
    private bool IsMetadataClearedLocked(ulong address, uint slice, out uint fillValue)
    {
        fillValue = 0;
        if (!_surfaceMetadata.TryGetValue(address, out var found) ||
            found.Kind == SurfaceMetadataKind.PendingDcc || slice >= 32)
        {
            return false;
        }

        fillValue = found.FillValue;
        return (found.ClearMask & (1u << (int)slice)) != 0;
    }

    public bool IsMetadataCleared(ulong address, uint slice) => IsMetadataCleared(address, slice, out _);

    public bool SetMetadataSlice(ulong address, uint slice, bool isClear)
    {
        using var held = _lock.Hold();
        return SetMetadataSliceLocked(address, slice, isClear);
    }

    /// <summary>As above, for a caller that already holds the lock.</summary>
    private bool SetMetadataSliceLocked(ulong address, uint slice, bool isClear)
    {
        if (!_surfaceMetadata.TryGetValue(address, out var found) ||
            found.Kind == SurfaceMetadataKind.PendingDcc || slice >= 32)
        {
            return false;
        }

        if (isClear)
        {
            found.ClearMask |= 1u << (int)slice;
        }
        else
        {
            found.ClearMask &= ~(1u << (int)slice);
        }

        return true;
    }

    // A broad clear applies to CMask, FMask and HTile; DCC needs a validated fill value.
    public bool ClearMetadata(ulong address)
    {
        using var held = _lock.Hold();
        if (!_surfaceMetadata.TryGetValue(address, out var found) ||
            found.Kind is SurfaceMetadataKind.PendingDcc or SurfaceMetadataKind.Dcc)
        {
            return false;
        }

        found.ClearMask = uint.MaxValue;
        return true;
    }

    public bool OverlapsDccMetadata(ulong address, ulong size)
    {
        using var held = _lock.Hold();
        foreach (var (start, metadata) in _surfaceMetadata)
        {
            if (metadata.Kind == SurfaceMetadataKind.Dcc && metadata.Size != 0 &&
                address < start + metadata.Size && start < address + size)
            {
                return true;
            }
        }

        return false;
    }

    public bool TryAbsorbDccFill(ulong address, ulong size, uint fillValue)
    {
        if (!IsValidRange(address, size))
        {
            return false;
        }

        // A DCC fill repeats one byte code; only the known deferred-clear codes count as clear.
        var code = (byte)fillValue;
        var dccClearMask = fillValue != code * 0x01010101u ? 0u : code switch
        {
            0x00 or 0x20 or 0x40 or 0x80 or 0xc0 => uint.MaxValue,
            _ => 0u,
        };
        using var held = _lock.Hold();
        // A guest fill of this metadata is the guest asking for the clear again.
        ReArmMaterializedDccLocked(address, size);
        if (!_surfaceMetadata.TryGetValue(address, out var found))
        {
            // The fill may precede color-target discovery; a pending entry stays
            // invisible to the metadata queries until the target classifies it.
            _surfaceMetadata.Add(address, new SurfaceMetadata
            {
                Kind = SurfaceMetadataKind.PendingDcc,
                ClearMask = dccClearMask,
                FillValue = fillValue,
                FillSize = size,
            });
            return false;
        }

        if (found.Kind == SurfaceMetadataKind.PendingDcc)
        {
            found.ClearMask = dccClearMask;
            found.FillValue = fillValue;
            found.FillSize = size;
            return false;
        }

        if (found.Kind == SurfaceMetadataKind.Dcc)
        {
            found.ClearMask = dccClearMask;
            found.FillValue = fillValue;
            found.FillSize = size;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A uniform DCC key over a whole slice means the surface is logically cleared to
    /// one color. The texture takes that color and the metadata becomes uncompressed,
    /// which is what makes the following draws and texture reads see the clear.
    /// </summary>
    private void MaterializeDccClear(ResourceSlotIdentifier imageIdentifier, in ImageRequest request, uint metadataBaseLayer)
    {
        if (request.Description.Metadata.Kind != MetadataKind.Dcc)
        {
            return;
        }

        var description = request.Description;
        var range = description.Metadata.Range;
        using (var held = _lock.Hold())
        {
            if (_slots.TryGet(imageIdentifier) is not { } found)
            {
                return;
            }

            found.Description.Metadata = description.Metadata;
            if (range.Size == 0 || description.Resources.Levels != 1 || found.Description.Resources.Levels != 1)
            {
                return;
            }

            _surfaceMetadata.Remove(range.Address);
        }

        var layers = description.TransferLayers;
        const ulong MetadataBlockSize = 0x1000;
        if (!ImageDescription.IsValidRange(range) || range.Address % MetadataBlockSize != 0 || layers == 0 ||
            range.Size % layers != 0 || (range.Size / layers) % MetadataBlockSize != 0)
        {
            MetalV2Diagnostics.WarnOnce(
                "dcc-slices",
                $"DCC metadata is not aligned 4 KiB slices; the deferred clear is ignored: address=0x{range.Address:X16} " +
                $"size=0x{range.Size:X} layers={layers}.");
            return;
        }

        var view = request.View;
        var volume = description.IsVolume && view.Type == GpuImageViewType.Type3D;
        var first = volume ? 0u : metadataBaseLayer;
        var imageFirst = volume ? 0u : view.BaseLayer;
        var count = volume ? description.Extent.Depth : view.LayerCount;
        if (first >= layers || count == 0 || count > layers - first)
        {
            return;
        }

        var sliceSize = range.Size / layers;
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(
            checked((int)Math.Min(sliceSize, 64 * 1024)));
        try
        {
            Span<byte> firstByte = stackalloc byte[1];
            for (var slice = 0u; slice < count; slice++)
            {
                var address = range.Address + sliceSize * (first + slice);
                if (!_backing.TryReadBacking(address, firstByte))
                {
                    continue;
                }

                var code = firstByte[0];
                if (!PackedClearValue.TryDecodeDccColor(
                        view.Format,
                        code * 0x01010101u,
                        description.Metadata,
                        allowClearToRegister: request.Role == ImageRole.ColorTarget,
                        out var clear))
                {
                    continue;
                }

                if (!IsUniformSlice(address, sliceSize, code, buffer))
                {
                    continue;
                }

                // The sentinel that stops this repeating is written by the GPU, while
                // the uniform-slice check above is a host read of those same bytes.
                // Between the fill being recorded and the GPU running it, every lookup
                // of this surface sees the old clear code and materializes again — in
                // The Oregon Trail twenty-four times a frame, two render-pass teardowns
                // apiece. Remembering what has been done closes that window without
                // touching guest memory any earlier than before; a guest fill of the
                // same metadata re-arms it, so a surface the game clears every frame
                // keeps working.
                if (UseMaterializedSet && !TryClaimMaterializedDcc(address, code))
                {
                    Interlocked.Increment(ref _dccRepeatsSkipped);
                    continue;
                }

                using (var held = _lock.Hold())
                {
                    if (_slots.TryGet(imageIdentifier) is not { } image)
                    {
                        return;
                    }

                    ClearImageRange(
                        image,
                        new SubresourceRange(view.BaseLevel, view.LevelCount, imageFirst + slice, 1),
                        in clear);
                }

                if (request.Role != ImageRole.DisplaySurface)
                {
                    _owner.FillGuestRange(address, sliceSize, uint.MaxValue);
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool IsUniformSlice(ulong address, ulong size, byte code, byte[] buffer)
    {
        for (var position = 0UL; position < size;)
        {
            var span = buffer.AsSpan(0, (int)Math.Min(size - position, (ulong)buffer.Length));
            if (!_backing.TryReadBacking(address + position, span) || span.IndexOfAnyExcept(code) >= 0)
            {
                return false;
            }

            position += (ulong)span.Length;
        }

        return true;
    }

    // Clears one subresource range of an image to a color and takes GPU ownership.
    private void ClearImageRange(MetalCachedImage image, in SubresourceRange range, in GpuClearColor clear)
    {
        if (image.Texture == 0)
        {
            return;
        }

        var width = Math.Max(image.Description.Extent.Width >> (int)range.BaseLevel, 1);
        var height = Math.Max(image.Description.Extent.Height >> (int)range.BaseLevel, 1);
        _owner.ClearTextureColor(image.Texture, image.Shape.PixelFormat, in range, width, height, in clear);
        image.RefreshComplete();
        image.MarkGpuModified();
        image.GpuWriteSerial = _owner.CurrentSerial;
    }

    /// <summary>
    /// A guest fill whose range is exactly one cached image clears that image on the
    /// GPU instead of writing its bytes: the clear is what the title meant, and the
    /// image stays resident.
    /// </summary>
    public bool TryClearImageFromBuffer(ulong address, ulong size, uint packedClear)
    {
        if (!IsValidRange(address, size))
        {
            return false;
        }

        using var held = _lock.Hold();
        var selected = ResourceSlotIdentifier.Invalid;
        foreach (var imageIdentifier in FindImagesInRange(address, size, pageOverlap: false))
        {
            if (_slots.TryGet(imageIdentifier) is not { } candidate ||
                candidate.Description.Data.Address != address || candidate.Description.Data.Size != size ||
                candidate.Texture == 0)
            {
                continue;
            }

            if (selected.IsValid && selected != imageIdentifier)
            {
                return false;
            }

            selected = imageIdentifier;
        }

        if (!selected.IsValid)
        {
            return false;
        }

        var image = _slots[selected];
        ref readonly var description = ref image.Description;
        var range = new SubresourceRange(0, image.Shape.MipLevels, 0, image.Shape.ArrayLength);
        if (description.IsDepth)
        {
            if (!PackedClearValue.TryDecodeDepth(description.PixelFormat, packedClear, out var depth))
            {
                return false;
            }

            WatchImage(selected);
            _owner.ClearTextureDepthStencil(
                image.Texture,
                image.Shape.PixelFormat,
                in range,
                description.Extent.Width,
                description.Extent.Height,
                depth,
                0,
                clearDepth: true,
                clearStencil: false);
        }
        else
        {
            if (!PackedClearValue.TryDecodeColor(description.PixelFormat, packedClear, out var color))
            {
                return false;
            }

            WatchImage(selected);
            _owner.ClearTextureColor(
                image.Texture,
                image.Shape.PixelFormat,
                in range,
                description.Extent.Width,
                description.Extent.Height,
                in color);
        }

        image.RefreshComplete();
        image.MarkGpuModified();
        image.GpuWriteSerial = _owner.CurrentSerial;
        return true;
    }

    /// <summary>
    /// The deferred clear a DCC attachment should load, decoded from the metadata
    /// state; consuming it hands the clear to the render pass.
    /// </summary>
    public bool TryConsumeDccAttachmentClear(
        in ImageRequest request,
        in ColorTargetResolution resolution,
        out GpuClearColor clearValue)
    {
        clearValue = default;
        if (request.Description.Metadata.Kind != MetadataKind.Dcc || request.Description.Metadata.Range.Size != 0)
        {
            return false;
        }

        var view = request.View;
        var metadataAddress = request.Description.Metadata.Range.Address;
        if (!IsMetadataCleared(metadataAddress, view.BaseLayer, out var metadataValue) ||
            !TryDecodeDccClear(
                (byte)metadataValue,
                resolution.MetadataClearSupported,
                resolution.MetadataFixedClearSupported,
                resolution.ColorClearValue,
                out clearValue))
        {
            return false;
        }

        for (var layer = 1u; layer < view.LayerCount; layer++)
        {
            if (!IsMetadataCleared(metadataAddress, view.BaseLayer + layer))
            {
                return false;
            }
        }

        // Consume only after every layer of the view can be materialized together.
        for (var layer = 0u; layer < view.LayerCount; layer++)
        {
            _ = SetMetadataSlice(metadataAddress, view.BaseLayer + layer, false);
        }

        return true;
    }

    private static bool TryDecodeDccClear(
        byte code,
        bool registerClearSupported,
        bool fixedClearSupported,
        in GpuClearColor registerClear,
        out GpuClearColor clearValue)
    {
        clearValue = default;
        switch (code)
        {
            case 0x00:
                return true;
            case 0x20:
                clearValue = registerClear;
                return registerClearSupported;
            case 0x40:
                clearValue.Float32_3 = 1f;
                return fixedClearSupported;
            case 0x80:
                clearValue.Float32_0 = 1f;
                clearValue.Float32_1 = 1f;
                clearValue.Float32_2 = 1f;
                return fixedClearSupported;
            case 0xc0:
                clearValue.Float32_0 = 1f;
                clearValue.Float32_1 = 1f;
                clearValue.Float32_2 = 1f;
                clearValue.Float32_3 = 1f;
                return fixedClearSupported;
            default:
                return false;
        }
    }
}
