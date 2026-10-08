// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private static bool IsExactCanonicalDirtyOwnerLocked(ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture)
    {
        if (_closed || _guestWorkFailure is not null || _typedCpuSyncCleanupFailure is not null ||
            _textureSourceCleanupFailure is not null || !ReferenceEquals(_guestMemory, origin.Memory) ||
            origin.Session != CurrentStorageCpuSeedSession || _pendingGuestWorkCount != 0 || _batchOpen ||
            !_guestImages.TryGetValue(address, out var current) || !ReferenceEquals(current, image) ||
            !ReferenceEquals(image.StorageCpuOrigin, origin) || !ReferenceEquals(image.StorageCpuLayout, origin.Layout) ||
            image.ContentVersion != version || image.Texture != texture || texture == 0 || !image.Initialized ||
            !image.GpuWritten || image.TextureType != MtlTextureType2DArray || image.Depth != 1 || image.ArrayLength != 1 ||
            debt.Failed || debt.Pending || debt.CanonicalVersion != version || !ReferenceEquals(debt.CanonicalOrigin, origin) ||
            debt.CanonicalDescriptor is not { } descriptor || !MetalBgra1024ArrayStorageAdmission.IsQualified(descriptor) ||
            !origin.Layout.Matches(descriptor) || image.Width != origin.Layout.Width || image.Height != origin.Layout.Height ||
            image.Format != origin.Layout.NativeFormat.Format) return false;
        if (_selectedStorageMipOwners.Keys.Any(key => SelectedStorageMipOverlaps(key, address, origin.Layout.PhysicalBytes)) ||
            _latestTextureSources.Count != 0 || _guestDepthImages.Count != 0 || _guestDepthReadAliases.Count != 0 ||
            _guestImages.Count > 128) return false;
        foreach (var other in _guestImages.Values)
        {
            if (ReferenceEquals(other, image)) continue;
            // Unknown physical native aliases cannot be made disjoint by address.
            if (other.StorageCpuOrigin is not { } otherOrigin ||
                !ReferenceEquals(otherOrigin.Memory, origin.Memory) ||
                otherOrigin.Session != origin.Session ||
                !otherOrigin.Source.IsMappedCopyObservationCurrent(otherOrigin.Mapping) ||
                PhysicalMappingOverlaps(origin.Mapping, otherOrigin.Mapping)) return false;
        }
        return true;
    }

    // Before the ordinary dirty consumer. Historical GpuWritten is never cleared;
    // only dirty already represented by the ORIGINAL accepted seed token may be
    // acknowledged after exact completed canonical publication. New gens refuse.
    private static bool TryAcknowledgeFiniteCanonicalDirty(ulong address)
    {
        if (!_rgbaStorageRuntimePublicationRequested && !_finiteStorageWritebackEnabled) return false;
        GuestImage image; FiniteStorageDebt debt; StorageCpuSeedOrigin origin;
        int version; nint texture;
        lock (_gate)
        {
            if (!_guestImages.TryGetValue(address, out image!) || !_finiteStorageDebts.TryGetValue(image, out debt!) ||
                image.StorageCpuOrigin is not { } found) return false;
            origin = found; version = image.ContentVersion; texture = image.Texture;
            if (!IsExactCanonicalDirtyOwnerLocked(address, image, debt, origin, version, texture)) return false;
        }
        CpuMappedPublicationValidation? validation = null;
        Exception? primary = null;
        var consumed = false;
        try
        {
            var result = ((ICpuMappedPublicationSource)origin.Memory).TryPrepareMappedPublicationValidation(origin.Mapping, out validation);
            if (result != CpuMappedPublicationPreparationResult.Prepared || validation is null) return false;
            ValidateFiniteStorageOrigin(origin, debt.CanonicalDescriptor!);
            if (!validation.IsCurrent) return false;
            lock (_gate)
                if (!IsExactCanonicalDirtyOwnerLocked(address, image, debt, origin, version, texture)) return false;
            consumed = GuestImageWriteTracker.TryConsumeDirtyAtProtectedToken(address, origin.Layout.PhysicalBytes, origin.Observation.Token);
            if (!consumed) return false;
            ValidateFiniteStorageOrigin(origin, debt.CanonicalDescriptor!);
            if (!validation.IsCurrent) throw new InvalidOperationException("Canonical dirty acknowledgment mapping was revoked.");
            lock (_gate)
                if (!IsExactCanonicalDirtyOwnerLocked(address, image, debt, origin, version, texture))
                    throw new InvalidOperationException("Canonical dirty acknowledgment exact native owner changed.");
            return true;
        }
        catch (Exception failure)
        {
            primary = failure;
            if (consumed) GuestImageWriteTracker.RestoreDirtyAtProtectedOwner(address, origin.Observation.Token);
            throw;
        }
        finally
        {
            try { validation?.Dispose(); }
            catch (Exception cleanup)
            {
                if (consumed) GuestImageWriteTracker.RestoreDirtyAtProtectedOwner(address, origin.Observation.Token);
                lock (_gate) { debt.Failed = true; debt.Pending = true; _textureSourceCleanupFailure ??= cleanup; }
                if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                try { primary.Data["CanonicalDirtyValidationCleanup"] = cleanup; } catch { }
            }
        }
    }
}
