// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private sealed record StorageCpuSeedOrigin(MetalStorageCpuLayout Layout, ICpuMemory Memory,
        ICpuMappedCopySource Source, CpuMappedCopyObservation Mapping,
        CpuTextureSnapshotObservation Observation, long Session);
    private static readonly ConditionalWeakTable<GuestDrawTexture, StorageCpuSeedOrigin> _storageCpuSeedOrigins = new();
    internal static long CurrentStorageCpuSeedSession => Volatile.Read(ref _selectedStorageMipSession);

    internal static void RegisterStorageCpuSeedOrigin(GuestDrawTexture texture, MetalStorageCpuLayout layout,
        ICpuMemory memory, ICpuMappedCopySource source, CpuMappedCopyObservation mapping,
        CpuTextureSnapshotObservation observation, long originalSession)
    {
        if (!layout.Matches(texture) || !ReferenceEquals(memory, source) ||
            mapping.FootprintAddress != layout.Address || mapping.FootprintByteCount != layout.PhysicalBytes ||
            mapping.SourceAddress != layout.Address || mapping.SourceByteCount != layout.PhysicalBytes)
            throw new InvalidOperationException("Typed storage original copy does not match its immutable creation footprint.");
        if (originalSession != CurrentStorageCpuSeedSession)
            throw TypedCpuSyncRejected(layout.Address, "session-changed-during-original-copy");
        _storageCpuSeedOrigins.Add(texture, new(layout, memory, source, mapping, observation, originalSession));
        if (originalSession != CurrentStorageCpuSeedSession)
        {
            _storageCpuSeedOrigins.Remove(texture);
            throw TypedCpuSyncRejected(layout.Address, "session-changed-during-original-registration");
        }
    }

    private static void CopyStorageCpuSeedOrigin(GuestDrawTexture source, GuestDrawTexture copy)
    {
        if (!_storageCpuSeedOrigins.TryGetValue(source, out var origin)) return;
        if (!origin.Layout.Matches(copy))
            throw new InvalidOperationException("Typed storage copy changed the original creation layout.");
        _storageCpuSeedOrigins.Add(copy, origin);
    }

    private static StorageCpuSeedOrigin? StorageCpuOrigin(GuestDrawTexture descriptor) =>
        _storageCpuSeedOrigins.TryGetValue(descriptor, out var origin) && origin.Layout.Matches(descriptor) ? origin : null;

    private static void ValidateStorageCpuSeedSession(StorageCpuSeedOrigin? origin)
    {
        if (origin is not null && origin.Session != CurrentStorageCpuSeedSession)
            throw TypedCpuSyncRejected(origin.Layout.Address, "original-seed-session-changed");
    }

    private static readonly bool _storageDirtyCausalityEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGE_DIRTY") == "1";

    private static void TraceStorageDirtyBeforeConsume(ulong address)
    {
        if (!_storageDirtyCausalityEnabled) return;
        try
        {
            StorageCpuSeedOrigin? origin;
            int version;
            long session;
            lock (_gate)
            {
                _guestImages.TryGetValue(address, out var image);
                origin = image?.StorageCpuOrigin;
                version = image?.ContentVersion ?? -1;
                session = CurrentStorageCpuSeedSession;
            }
            // No presenter gate while observing tracker scalars. These separately
            // sampled diagnostics do not certify mapping/content currentness.
            GuestImageWriteTracker.TraceDirtyBeforeConsume(address, session,
                origin?.Observation.Token, origin?.Observation.MappingEpoch ?? -1,
                HostMemory.MappingGeneration, origin?.Session ?? -1,
                origin?.Observation.Observed ?? false, origin?.Mapping.FootprintByteCount ?? 0, version);
        }
        catch { } // Optional causality diagnostics cannot replace the original sync result.
    }

    private static InvalidOperationException TypedCpuSyncRejected(ulong address, string reason) =>
        new($"Metal typed storage CPU synchronization rejected at 0x{address:X}: reason={reason}. " +
            "A dirty flag is not full CPU replacement coverage or completion of GPU/native guest writes.");

    // Run under the presenter gate; this performs no provider query/copy.
    private static void ValidateStorageCpuSyncOwnerLocked(ulong address, GuestImage image, StorageCpuSeedOrigin origin,
        int version, nint texture)
    {
        if (_typedCpuSyncCleanupFailure is { } cleanupFailure)
            throw new InvalidOperationException("Typed CPU storage cleanup has an unknown native outcome; reuse is quarantined.", cleanupFailure);
        if (_closed || !ReferenceEquals(_guestMemory, origin.Memory) ||
            origin.Session != Volatile.Read(ref _selectedStorageMipSession))
            throw TypedCpuSyncRejected(address, "memory-or-session-changed");
        if (!_guestImages.TryGetValue(address, out var current) || !ReferenceEquals(current, image) ||
            !ReferenceEquals(image.StorageCpuOrigin, origin) || image.ContentVersion != version || image.Texture != texture ||
            !ReferenceEquals(image.StorageCpuLayout, origin.Layout) ||
            !image.Initialized || image.TextureType != MtlTextureType2DArray || image.Depth != 1 || image.ArrayLength != 1 ||
            image.Width != origin.Layout.Width || image.Height != origin.Layout.Height || image.Format != origin.Layout.NativeFormat.Format)
            throw TypedCpuSyncRejected(address, "exact-image-or-version-changed");
        if (image.GpuWritten || _pendingGuestWorkCount != 0 || _batchOpen)
            throw TypedCpuSyncRejected(address, "gpu-produced-or-pending-write-debt");
        if (_selectedStorageMipOwners.Keys.Any(key => SelectedStorageMipOverlaps(key, address, origin.Layout.PhysicalBytes)) ||
            _latestTextureSources.Count != 0 || _guestDepthImages.Count != 0 || _guestDepthReadAliases.Count != 0)
            throw TypedCpuSyncRejected(address, "selected-or-native-source-alias");
        if (_guestImages.Count > 128) throw TypedCpuSyncRejected(address, "bounded-alias-inventory-exceeded");
        foreach (var entry in _guestImages)
        {
            if (ReferenceEquals(entry.Value, image)) continue;
            var bytes = entry.Value.StorageCpuLayout?.PhysicalBytes ??
                (_guestImageExtents.TryGetValue(entry.Key, out var extent) ? extent.ByteCount : 0);
            if (bytes == 0 || (UInt128)entry.Key < (UInt128)address + origin.Layout.PhysicalBytes &&
                (UInt128)address < (UInt128)entry.Key + bytes)
                throw TypedCpuSyncRejected(address, "overlapping-or-unknown-native-image-extent");
        }
    }

    // Conservative scalar vetoes are retained in addition to the actual original
    // mapping observation. This is not raw/native writer completion authority.
    private static void ValidateStorageCpuSyncOrigin(ulong address, StorageCpuSeedOrigin origin)
    {
        if (origin.Observation.ByteCount != origin.Layout.PhysicalBytes || !origin.Observation.Observed ||
            origin.Observation.MappingEpoch != HostMemory.MappingGeneration)
            throw TypedCpuSyncRejected(address, "unobserved-origin-or-mapping-epoch-changed");
        if (!origin.Source.IsMappedCopyObservationCurrent(origin.Mapping))
            throw TypedCpuSyncRejected(address, "original-mapping-incarnation-retired");
        if (!GuestImageWriteTracker.TryGetContentToken(address, out var current) ||
            current.ProtectedIdentity != origin.Observation.Token.ProtectedIdentity ||
            current.WatchIdentity != origin.Observation.Token.WatchIdentity ||
            current.ProtectedByteCount != origin.Observation.Token.ProtectedByteCount ||
            current.WatchByteCount != origin.Observation.Token.WatchByteCount)
            throw TypedCpuSyncRejected(address, "original-content-owner-replaced");
    }

    private static void SynchronizeTypedStorageCpuSeed(nint device, ulong address, GuestImage image)
    {
        var captured = CaptureTypedStorageCpuSeed(address, image);
        var origin = captured.Origin;
        var version = captured.Version;
        var previous = captured.Previous;
        var upload = captured.Upload;
        nint replacement = 0;
        Exception? primary = null;
        try
        {
            replacement = CreateTypedStorageCpuTexture(device, upload, origin.Layout.NativeFormat);
            ValidateTypedStorageCpuCopy(address, origin, captured.Snapshot, captured.Observation, captured.Mapping);
            nint previousSnapshot;
            lock (_gate)
            {
                ValidateStorageCpuSyncOwnerLocked(address, image, origin, version, previous);
                // Publish the fresh typed native object only after every copy and
                // exact-owner check. Command buffers independently retain old texture.
                image.Texture = replacement; replacement = 0;
                image.ContentVersion++;
                image.SnapshotVersion = -1;
                previousSnapshot = image.SnapshotTexture;
                image.SnapshotTexture = 0; // consume before uncertain native return; never release it twice
            }
            // Seed sync is not GPU production/full explicit guest replacement.
            // Leave GpuWritten false rather than manufacture native write debt.
            Exception? releaseFailure = null;
            try { if (previousSnapshot != 0) MetalNative.SendVoid(previousSnapshot, MetalNative.Selector("release")); }
            catch (Exception cleanup) { releaseFailure = cleanup; }
            try { MetalNative.SendVoid(previous, MetalNative.Selector("release")); }
            catch (Exception cleanup)
            {
                if (releaseFailure is null) releaseFailure = cleanup;
                else try { releaseFailure.Data["TypedStorageCpuPreviousTextureCleanup"] = cleanup; } catch { }
            }
            if (releaseFailure is not null)
            {
                lock (_gate) _typedCpuSyncCleanupFailure ??= releaseFailure;
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(releaseFailure).Throw();
            }
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            if (replacement != 0)
            {
                var owned = replacement; replacement = 0; // uncertain release is consumed, never retried
                try { MetalNative.SendVoid(owned, MetalNative.Selector("release")); }
                catch (Exception cleanup) when (primary is not null)
                {
                    lock (_gate) _typedCpuSyncCleanupFailure ??= cleanup;
                    try { primary.Data["TypedStorageCpuSyncCleanup"] = cleanup; } catch { }
                }
            }
        }
    }

    private sealed record TypedStorageCpuCopy(StorageCpuSeedOrigin Origin, int Version, nint Previous,
        GuestDrawTexture Upload, GuestImageWriteTracker.ReadSnapshot Snapshot,
        CpuTextureSnapshotObservation Observation, CpuMappedCopyObservation Mapping);
    private static Exception? _typedCpuSyncCleanupFailure;

    private static TypedStorageCpuCopy CaptureTypedStorageCpuSeed(ulong address, GuestImage image)
    {
        var origin = image.StorageCpuOrigin ?? throw TypedCpuSyncRejected(address, "original-mapped-copy-unavailable");
        int version;
        nint previous;
        lock (_gate)
        {
            version = image.ContentVersion; previous = image.Texture;
            ValidateStorageCpuSyncOwnerLocked(address, image, origin, version, previous);
        }
        ValidateStorageCpuSyncOrigin(address, origin);
        var epoch = HostMemory.MappingGeneration;
        var snapshot = GuestImageWriteTracker.BeginReadSnapshot(address, origin.Layout.PhysicalBytes, source: "metal.typed-cpu-dirty");
        var observation = ObserveCpuTextureSnapshot(address, origin.Layout.PhysicalBytes, epoch);
        if (!snapshot.Active || !observation.Observed)
            throw TypedCpuSyncRejected(address, "copy-content-observation-unavailable");
        var physical = new byte[checked((int)origin.Layout.PhysicalBytes)];
        if (origin.Source.TryReadMappedCopy(address, origin.Layout.PhysicalBytes, address, physical, out var copied) != CpuMappedCopyResult.Copied ||
            copied is null)
            throw TypedCpuSyncRejected(address, "mapping-bound-copy-declined");
        var upload = origin.Layout.Upload(origin.Layout.Decode(physical)); // valid zero clears are complete owned bytes
        ValidateTypedStorageCpuCopy(address, origin, snapshot, observation, copied);
        lock (_gate) ValidateStorageCpuSyncOwnerLocked(address, image, origin, version, previous);

        return new(origin, version, previous, upload, snapshot, observation, copied);
    }

    private static void ValidateTypedStorageCpuCopy(ulong address, StorageCpuSeedOrigin origin,
        GuestImageWriteTracker.ReadSnapshot snapshot, CpuTextureSnapshotObservation observation, CpuMappedCopyObservation copied)
    {
        ValidateStorageCpuSyncOrigin(address, origin);
        if (!origin.Source.IsMappedCopyObservationCurrent(copied) || !GuestImageWriteTracker.IsReadSnapshotStable(snapshot) ||
            !IsCpuTextureSnapshotObservationStable(address, observation))
            throw TypedCpuSyncRejected(address, "mapping-or-content-changed-during-copy-upload");
    }

    private static nint CreateTypedStorageCpuTexture(nint device, GuestDrawTexture upload, MetalTextureFormat format)
    {
        var layout = MetalStorageCpuLayout.TryCreate(upload) ??
            throw new NotSupportedException("Typed CPU upload lost its exact qualified creation layout.");
        if (format != layout.NativeFormat)
            throw new NotSupportedException("Typed CPU upload cannot substitute a different native format.");
        if (upload.RgbaPixels.Length != layout.LinearBytes)
            throw new InvalidOperationException("Typed CPU upload is incomplete.");
        var descriptor = MetalNative.SendTextureDescriptor(MetalNative.Class("MTLTextureDescriptor"),
            MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
            (nuint)format.Format, upload.Width, upload.Height, false);
        MetalNative.Send(descriptor, MetalNative.Selector("setStorageMode:"), (nint)0);
        MetalNative.Send(descriptor, MetalNative.Selector("setTextureType:"), (nint)MtlTextureType2DArray);
        MetalNative.Send(descriptor, MetalNative.Selector("setDepth:"), (nint)1);
        MetalNative.Send(descriptor, MetalNative.Selector("setArrayLength:"), (nint)1);
        MetalNative.Send(descriptor, MetalNative.Selector("setUsage:"), (nint)(UsageShaderRead | UsageShaderWrite));
        var texture = MetalNative.Send(device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
        if (texture == 0) throw new InvalidOperationException("Metal cannot allocate a fresh typed CPU synchronization texture.");
        try
        {
            ReplaceDrawTextureContents(texture, upload, layout.Pitch, format);
            return texture;
        }
        catch (Exception primary)
        {
            try { MetalNative.SendVoid(texture, MetalNative.Selector("release")); }
            catch (Exception cleanup)
            {
                lock (_gate) _typedCpuSyncCleanupFailure ??= cleanup;
                try { primary.Data["TypedStorageCpuAllocationCleanup"] = cleanup; } catch { }
            }
            throw;
        }
    }
}
