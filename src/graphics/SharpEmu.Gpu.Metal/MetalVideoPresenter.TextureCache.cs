// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

// Render-thread native textures are cached by content identity and evicted
// after observed CPU writes. A cache advertisement carries no lifetime or
// write-generation reservation: producer CPU snapshots retain faithful bytes
// through preparation/queueing instead of relying on this evictable entry.
internal static partial class MetalVideoPresenter
{
    private const int MaxCachedDrawTextures = 2048;

    /// <summary>Render-thread-only cache of decoded draw textures; each value
    /// holds one retain. Committed command buffers retain the textures they
    /// reference, so eviction releases immediately without a GPU drain.</summary>
    private static readonly Dictionary<TextureContentIdentity, nint> _drawTextureCache = new();

    /// <summary>Advertised cache identities. A hit alone does not authorize
    /// discarding a producer snapshot. Read from the submit thread, written
    /// by the render thread.</summary>
    private static readonly ConcurrentDictionary<TextureCacheLookupIdentity, byte> _cachedDrawTextureIdentities = new();

    internal static bool IsTextureContentCached(in TextureCacheLookupIdentity identity) =>
        _cachedDrawTextureIdentities.ContainsKey(identity);

    /// <summary>Builds the same identity the AGC layer checks before skipping
    /// a texel copy; the two must agree field-for-field or skips and cache
    /// entries would never line up.</summary>
    private static TextureContentIdentity GetDrawTextureIdentity(GuestDrawTexture texture) => new(
        texture.Address,
        texture.Width,
        texture.Height,
        texture.Format,
        texture.NumberType,
        texture.DstSelect,
        texture.TileMode,
        texture.Pitch,
        texture.ArrayedView,
        Math.Max(texture.ArrayLayers, 1),
        texture.Type,
        texture.Depth,
        ResourceMipLevels: texture.ResourceMipLevels);

    /// <summary>Storage textures are shader-writable on the GPU, so their
    /// content identity is not stable. CPU rewrites of protected/CPU-backed
    /// images still evict via DrainGuestImageCpuSync when those addresses
    /// are dirty.</summary>
    private static bool IsCacheableDrawTexture(GuestDrawTexture texture) =>
        texture.Address != 0 &&
        !texture.IsStorage &&
        !texture.IsFallback;

    private static bool TryGetCachedDrawTexture(GuestDrawTexture texture, out nint handle)
    {
        var content = GetDrawTextureIdentity(texture);
        if (!_drawTextureCache.TryGetValue(content, out handle))
        {
            return false;
        }

        _cachedDrawTextureIdentities[
            new TextureCacheLookupIdentity(content, texture.Sampler)] = 0;
        return true;
    }

    private static void CacheDrawTexture(GuestDrawTexture texture, nint handle)
    {
        var key = GetDrawTextureIdentity(texture);
        if (_drawTextureCache.Remove(key, out var previous))
        {
            MetalNative.SendVoid(previous, MetalNative.Selector("release"));
        }

        _ = MetalNative.Send(handle, MetalNative.Selector("retain"));
        _drawTextureCache[key] = handle;
        var sourceByteCount = texture.SourceByteCount != 0
            ? texture.SourceByteCount
            : (ulong)(texture.TiledSource?.Length ?? texture.RgbaPixels.Length);
        GuestImageWriteTracker.Track(
            texture.Address,
            sourceByteCount,
            Volatile.Read(ref _executingGuestWorkSequence),
            "metal.texture-cache",
            protect: false);
        _cachedDrawTextureIdentities[
            new TextureCacheLookupIdentity(key, texture.Sampler)] = 0;
    }

    /// <summary>
    /// Single dirty consumer per drain: re-upload CPU-written guest images,
    /// evict matching draw-texture cache entries, then re-arm once per address.
    /// </summary>
    private static bool DrainGuestImageCpuSync(nint device)
    {
        if (RetryDeferredCanonicalDirty() == CanonicalDirtyResult.Deferred) return false;
        _ = Interlocked.Exchange(ref _cpuWrittenGuestImageSyncRequested, 0);

        HashSet<ulong>? dirtyAddresses = null;
        List<(ulong Address, uint Width, uint Height, ulong ByteCount)>? extents = null;
        lock (_gate)
        {
            if (_guestImageExtents.Count > 0)
            {
                extents = new(_guestImageExtents.Count);
                foreach (var entry in _guestImageExtents)
                {
                    extents.Add((
                        entry.Key,
                        entry.Value.Width,
                        entry.Value.Height,
                        entry.Value.ByteCount));
                }
            }
        }

        var memory = _guestMemory;
        HashSet<ulong>? canonicalAddresses = null;
        if (extents is not null)
        {
            // Discover canonical deferral before consuming any unrelated dirty
            // flag or altering caches that would require a later rearm/eviction.
            foreach (var (address, _, _, _) in extents)
            {
                TraceStorageDirtyBeforeConsume(address);
                var canonical = TryAcknowledgeFiniteCanonicalDirty(address);
                if (canonical == CanonicalDirtyResult.Deferred) return false;
                if (canonical == CanonicalDirtyResult.Acknowledged) (canonicalAddresses ??= []).Add(address);
            }
            foreach (var (address, width, height, byteCount) in extents)
            {
                if (canonicalAddresses?.Contains(address) == true) continue;
                if (!GuestImageWriteTracker.ConsumeDirty(address))
                {
                    continue;
                }

                (dirtyAddresses ??= []).Add(address);
                if (memory is null ||
                    byteCount == 0 ||
                    byteCount > 128UL * 1024UL * 1024UL)
                {
                    continue;
                }

                GuestImage? image;
                lock (_gate)
                {
                    _guestImages.TryGetValue(address, out image);
                }

                if (image is null)
                {
                    continue;
                }

                if (image.StorageCpuLayout is not null)
                {
                    SynchronizeTypedStorageCpuSeed(device, address, image);
                    continue;
                }
                if (image.TextureType != MtlTextureType2D || image.Depth != 1)
                    throw CpuDirtyImageShapeMismatch(image, address, width, height, byteCount);
                var pixels = new byte[byteCount];
                if (!memory.TryRead(address, pixels) ||
                    pixels.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                {
                    continue;
                }

                ExecuteGuestImageWrite(
                    device,
                    queue: 0,
                    new GuestImageWrite(address, pixels, 0));
                if (Interlocked.Increment(ref _guestImageCpuSyncTraceCount) <= 64)
                {
                    Console.Error.WriteLine(
                        $"[SYNC] cpu-write-drain addr=0x{address:X} {width}x{height}");
                }
            }
        }

        if (_drawTextureCache.Count == 0)
        {
            if (dirtyAddresses is not null)
            {
                foreach (var address in dirtyAddresses)
                {
                    GuestImageWriteTracker.Rearm(address);
                }
            }

            return true;
        }

        // Evict by address rather than by identity: several identities can
        // share one source address (same texels, different samplers), and
        // ConsumeDirty clears the flag on first read — evicting only the
        // first identity would leave the others sampling stale texels.
        foreach (var entry in _drawTextureCache)
        {
            var address = entry.Key.Address;
            if (dirtyAddresses is not null && dirtyAddresses.Contains(address))
            {
                continue;
            }

            if (GuestImageWriteTracker.ConsumeDirty(address))
            {
                (dirtyAddresses ??= []).Add(address);
            }
        }

        if (dirtyAddresses is null && _drawTextureCache.Count <= MaxCachedDrawTextures)
        {
            return true;
        }

        if (_drawTextureCache.Count > MaxCachedDrawTextures)
        {
            var watchedAddresses = _drawTextureCache.Keys
                .Select(static key => key.Address)
                .Distinct()
                .ToArray();
            foreach (var entry in _drawTextureCache)
            {
                MetalNative.SendVoid(entry.Value, MetalNative.Selector("release"));
            }

            _drawTextureCache.Clear();
            _cachedDrawTextureIdentities.Clear();
            foreach (var address in watchedAddresses)
            {
                GuestImageWriteTracker.UntrackWatchOnly(address);
            }
            if (dirtyAddresses is not null)
            {
                foreach (var address in dirtyAddresses)
                {
                    GuestImageWriteTracker.Rearm(address);
                }
            }

            return true;
        }

        List<TextureContentIdentity>? evicted = null;
        foreach (var entry in _drawTextureCache)
        {
            if (dirtyAddresses!.Contains(entry.Key.Address))
            {
                (evicted ??= []).Add(entry.Key);
            }
        }

        if (evicted is not null)
        {
            foreach (var key in evicted)
            {
                if (_drawTextureCache.Remove(key, out var handle))
                {
                    foreach (var identity in _cachedDrawTextureIdentities.Keys)
                    {
                        if (identity.Content == key)
                        {
                            _cachedDrawTextureIdentities.TryRemove(identity, out _);
                        }
                    }
                    MetalNative.SendVoid(handle, MetalNative.Selector("release"));
                }
            }

            foreach (var address in evicted.Select(static key => key.Address).Distinct())
            {
                if (!_drawTextureCache.Keys.Any(key => key.Address == address))
                {
                    GuestImageWriteTracker.UntrackWatchOnly(address);
                }
            }
        }

        foreach (var address in dirtyAddresses!)
        {
            GuestImageWriteTracker.Rearm(address);
        }
        return true;
    }

    /// <summary>Legacy empty-payload recovery supports only linear base-level
    /// resources. Producer CPU snapshots retain their payload; tiled/mip data
    /// must never be recovered by interpreting raw guest bytes as linear.</summary>
    private static byte[]? TryReadGuestDrawTexturePixels(GuestDrawTexture texture)
    {
        var memory = _guestMemory;
        if (memory is null || texture.Address == 0)
        {
            return null;
        }

        var width = Math.Max(texture.Width, 1u);
        var height = Math.Max(texture.Height, 1u);
        var rowLength = texture.TileMode == 0
            ? Math.Max(texture.Pitch, width)
            : width;
        var format = MetalGuestFormats.DecodeTextureFormat(texture.Format, texture.NumberType);
        // Recovery reads linear base-level resources only. Reading raw tiled
        // bytes as linear texels or ignoring a mip offset corrupts the image.
        if (texture.TileMode != 0 || texture.BaseMipLevel != 0 || texture.MipLevel != 0 ||
            texture.MipUploads is { Length: > 0 })
            throw new NotSupportedException($"Metal cannot recover skipped texture 0x{texture.Address:X}: tiled or mip-range snapshot required.");
        var shape = GuestTextureShape(texture);
        var byteCount = checked(MetalGuestFormats.GetTextureByteCount(format, rowLength, height) *
            Math.Max(shape.ArrayLength, shape.Depth));
        if (byteCount == 0 || byteCount > int.MaxValue)
        {
            return null;
        }

        var pixels = new byte[(int)byteCount];
        return memory.TryRead(texture.Address, pixels) ? pixels : null;
    }
}
