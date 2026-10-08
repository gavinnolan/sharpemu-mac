// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private static bool IsExactCanonicalDirtyOwnerLocked(ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture, out string reason,
        out FiniteStorageCohortOffender? offender, StorageWatchReservation reservation = default)
    {
        offender = null;
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
        return InspectFiniteStorageNativeCohortLocked(image, origin, out reason, out offender,
            captureOffender: _volumeOwnerDeclineDiagnostics, reservation: reservation);
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
        FiniteStorageCohortOffender? offender;
        lock (_gate)
        {
            if (!_guestImages.TryGetValue(address, out image!) || !_finiteStorageDebts.TryGetValue(image, out debt!) ||
                image.StorageCpuOrigin is not { } found) return false;
            origin = found; version = image.ContentVersion; texture = image.Texture;
            ownerCurrent = IsExactCanonicalDirtyOwnerLocked(address, image, debt, origin, version, texture, out reason, out offender);
        }
        if (!ownerCurrent) { TraceFiniteStorageRuntime("dirty-refused", address, reason, origin, offender); return false; }
        CpuMappedPublicationValidation? validation = null;
        Exception? primary = null;
        var consumed = false;
        try
        {
            var result = ((ICpuMappedPublicationSource)origin.Memory).TryPrepareMappedPublicationValidation(origin.Mapping, out validation);
            if (result != CpuMappedPublicationPreparationResult.Prepared || validation is null)
            { TraceFiniteStorageRuntime("dirty-refused", address, "original-physical-history-reservation", origin); return false; }
            var reservation = new StorageWatchReservation(validation);
            ValidateFiniteStorageOrigin(origin, debt.CanonicalDescriptor!, reservation);
            if (!validation.IsCurrent)
            { TraceFiniteStorageRuntime("dirty-refused", address, "validation-revoked", origin); return false; }
            lock (_gate)
                ownerCurrent = IsExactCanonicalDirtyOwnerLocked(address, image, debt, origin, version, texture, out reason, out offender, reservation);
            if (!ownerCurrent) { TraceFiniteStorageRuntime("dirty-refused", address, reason, origin, offender); return false; }
            consumed = GuestImageWriteTracker.TryConsumeDirtyAtProtectedToken(address, origin.Layout.PhysicalBytes, origin.Observation.Token);
            if (!consumed)
            { TraceFiniteStorageRuntime("dirty-refused", address, "original-protected-token-or-armed-owner", origin); return false; }
            ValidateFiniteStorageOrigin(origin, debt.CanonicalDescriptor!, reservation);
            if (!validation.IsCurrent) throw new InvalidOperationException("Canonical dirty acknowledgment mapping was revoked.");
            lock (_gate)
                ownerCurrent = IsExactCanonicalDirtyOwnerLocked(address, image, debt, origin, version, texture, out reason, out offender, reservation);
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
            try
            {
                validation?.Dispose();
                if (primary is null && consumed)
                {
                    ValidateFiniteStorageOrigin(origin, debt.CanonicalDescriptor!);
                    lock (_gate)
                        ownerCurrent = IsExactCanonicalDirtyOwnerLocked(address, image, debt, origin, version, texture, out reason, out offender);
                    if (!ownerCurrent)
                    {
                        TraceFiniteStorageRuntime("dirty-post-release-refused", address, reason, origin, offender);
                        throw new InvalidOperationException("Canonical dirty ordinary post-release owner changed: " + reason);
                    }
                }
            }
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
