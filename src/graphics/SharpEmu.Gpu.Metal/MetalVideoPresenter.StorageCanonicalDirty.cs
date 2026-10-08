// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private static bool IsExactCanonicalDirtyOwnerLocked(ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture, out string reason)
    {
        reason = "presenter-owner-session-or-version";
        if (_closed || _guestWorkFailure is not null || _typedCpuSyncCleanupFailure is not null ||
            _textureSourceCleanupFailure is not null || !ReferenceEquals(_guestMemory, origin.Memory) ||
            origin.Session != CurrentStorageCpuSeedSession ||
            !_guestImages.TryGetValue(address, out var current) || !ReferenceEquals(current, image) ||
            !ReferenceEquals(image.StorageCpuOrigin, origin) || !ReferenceEquals(image.StorageCpuLayout, origin.Layout) ||
            image.ContentVersion != version || image.Texture != texture || texture == 0 || !image.Initialized ||
            !image.GpuWritten || image.TextureType != MtlTextureType2DArray || image.Depth != 1 || image.ArrayLength != 1 ||
            image.Format != origin.Layout.NativeFormat.Format) return false;
        reason = "pending-work-or-batch";
        if (_pendingGuestWorkCount != 0 || _batchOpen) return false;
        reason = "canonical-debt-or-version";
        if (debt.Failed || debt.Pending || debt.CanonicalVersion != version || !ReferenceEquals(debt.CanonicalOrigin, origin) ||
            debt.CanonicalDescriptor is not { } descriptor || !MetalBgra1024ArrayStorageAdmission.IsQualified(descriptor) ||
            !origin.Layout.Matches(descriptor) || image.Width != origin.Layout.Width || image.Height != origin.Layout.Height ||
            image.Format != origin.Layout.NativeFormat.Format) return false;
        reason = "selected-owner-overlap";
        if (_selectedStorageMipOwners.Keys.Any(key => SelectedStorageMipOverlaps(key, address, origin.Layout.PhysicalBytes))) return false;
        reason = "unknown-texture-source-or-depth-alias";
        if (_latestTextureSources.Count != 0 || _guestDepthImages.Count != 0 || _guestDepthReadAliases.Count != 0) return false;
        reason = "image-inventory-bound";
        if (_guestImages.Count > 128) return false;
        foreach (var other in _guestImages.Values)
        {
            if (ReferenceEquals(other, image)) continue;
            // Unknown physical native aliases cannot be made disjoint by address.
            reason = "other-image-original-provenance-unavailable-or-stale";
            if (other.StorageCpuOrigin is not { } otherOrigin ||
                !ReferenceEquals(otherOrigin.Memory, origin.Memory) ||
                otherOrigin.Session != origin.Session ||
                !otherOrigin.Source.IsMappedCopyObservationCurrent(otherOrigin.Mapping)) return false;
            reason = "other-image-physical-overlap";
            if (PhysicalMappingOverlaps(origin.Mapping, otherOrigin.Mapping)) return false;
        }
        reason = "qualified";
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
        string reason;
        bool ownerCurrent;
        lock (_gate)
        {
            if (!_guestImages.TryGetValue(address, out image!) || !_finiteStorageDebts.TryGetValue(image, out debt!) ||
                image.StorageCpuOrigin is not { } found) return false;
            origin = found; version = image.ContentVersion; texture = image.Texture;
            ownerCurrent = IsExactCanonicalDirtyOwnerLocked(address, image, debt, origin, version, texture, out reason);
        }
        if (!ownerCurrent) { TraceFiniteStorageRuntime("dirty-refused", address, reason, origin); return false; }
        CpuMappedPublicationValidation? validation = null;
        Exception? primary = null;
        var consumed = false;
        try
        {
            var result = ((ICpuMappedPublicationSource)origin.Memory).TryPrepareMappedPublicationValidation(origin.Mapping, out validation);
            if (result != CpuMappedPublicationPreparationResult.Prepared || validation is null)
            { TraceFiniteStorageRuntime("dirty-refused", address, "original-physical-history-reservation", origin); return false; }
            ValidateFiniteStorageOrigin(origin, debt.CanonicalDescriptor!);
            if (!validation.IsCurrent)
            { TraceFiniteStorageRuntime("dirty-refused", address, "validation-revoked", origin); return false; }
            lock (_gate)
                ownerCurrent = IsExactCanonicalDirtyOwnerLocked(address, image, debt, origin, version, texture, out reason);
            if (!ownerCurrent) { TraceFiniteStorageRuntime("dirty-refused", address, reason, origin); return false; }
            consumed = GuestImageWriteTracker.TryConsumeDirtyAtProtectedToken(address, origin.Layout.PhysicalBytes, origin.Observation.Token);
            if (!consumed)
            { TraceFiniteStorageRuntime("dirty-refused", address, "original-protected-token-or-armed-owner", origin); return false; }
            ValidateFiniteStorageOrigin(origin, debt.CanonicalDescriptor!);
            if (!validation.IsCurrent) throw new InvalidOperationException("Canonical dirty acknowledgment mapping was revoked.");
            lock (_gate)
                ownerCurrent = IsExactCanonicalDirtyOwnerLocked(address, image, debt, origin, version, texture, out reason);
            if (!ownerCurrent) throw new InvalidOperationException("Canonical dirty acknowledgment exact native owner changed: " + reason);
            TraceFiniteStorageRuntime("dirty-acknowledged", address, "original-token-unchanged", origin);
            return true;
        }
        catch (Exception failure)
        {
            primary = failure;
            TraceFiniteStorageRuntime("dirty-refused", address, "original-observation-or-final-owner-veto", origin);
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
