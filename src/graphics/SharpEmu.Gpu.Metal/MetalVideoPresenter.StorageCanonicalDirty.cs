// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private enum CanonicalDirtyResult { NotApplicable, Acknowledged, Deferred }
    private sealed record DeferredCanonicalDirty(ulong Address, GuestImage Image, FiniteStorageDebt Debt,
        StorageCpuSeedOrigin Origin, int Version, nint Texture);
    private static DeferredCanonicalDirty? _deferredCanonicalDirty;
    // Default-null controlled race boundary. Runs only after the actual
    // cancel-only reservation has released; cannot supply content or status.
    private static Action? _canonicalDirtyAfterValidationReleasedForControl = null;

    private static bool IsExactCanonicalDirtyOwnerLocked(ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture, out string reason,
        out FiniteStorageCohortOffender? offender, StorageWatchReservation reservation = default) =>
        GetExactCanonicalDirtyOwnerStatusLocked(address, image, debt, origin, version, texture,
            out reason, out offender, reservation) == CpuMappedReadWatchStatus.Current;

    private static CpuMappedReadWatchStatus GetExactCanonicalDirtyOwnerStatusLocked(ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture, out string reason,
        out FiniteStorageCohortOffender? offender, StorageWatchReservation reservation = default, bool classifyBusy = false)
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
            image.Format != origin.Layout.NativeFormat.Format) return CpuMappedReadWatchStatus.Stale;
        reason = "pending-work-or-batch";
        if ((_pendingGuestWorkCount != 0 && !IsExactDeferredCanonicalDirtyLocked(address, image, debt, origin, version, texture)) ||
            _batchOpen || _takenGuestWorkSequence != 0 || _executingGuestWorkSequence != 0) return CpuMappedReadWatchStatus.Stale;
        reason = "canonical-debt-or-version";
        if (debt.Failed || debt.Pending || debt.CanonicalVersion != version || !ReferenceEquals(debt.CanonicalOrigin, origin) ||
            debt.CanonicalDescriptor is not { } descriptor || !MetalBgra1024ArrayStorageAdmission.IsQualified(descriptor) ||
            !origin.Layout.Matches(descriptor) || image.Width != origin.Layout.Width || image.Height != origin.Layout.Height ||
            image.Format != origin.Layout.NativeFormat.Format) return CpuMappedReadWatchStatus.Stale;
        return InspectFiniteStorageNativeCohortStatusLocked(image, origin, out reason, out offender,
            captureOffender: _volumeOwnerDeclineDiagnostics, reservation: reservation, classifyBusy: classifyBusy);
    }

    // Before the ordinary dirty consumer. Historical GpuWritten is never cleared;
    // only dirty already represented by the ORIGINAL accepted seed token may be
    // acknowledged after exact completed canonical publication. New gens refuse.
    private static CanonicalDirtyResult TryAcknowledgeFiniteCanonicalDirty(ulong address, DeferredCanonicalDirty? expectedRetry = null)
    {
        // A retry is the original receipt, not a new lookup at the old address.
        // Validate before flags, provider reservation or dirty-token consumption.
        if (expectedRetry is not null)
            lock (_gate) RequireExactDeferredCanonicalDirtyOwnerLocked(expectedRetry);
        if (!_rgbaStorageRuntimePublicationRequested && !_finiteStorageWritebackEnabled) return CanonicalDirtyResult.NotApplicable;
        GuestImage image; FiniteStorageDebt debt; StorageCpuSeedOrigin origin;
        int version; nint texture;
        string reason;
        bool ownerCurrent;
        FiniteStorageCohortOffender? offender;
        lock (_gate)
        {
            if (_deferredCanonicalDirty is { } retained)
            {
                if (retained.Address != address)
                    throw new InvalidOperationException("Deferred canonical dirty ORIGINAL receipt must finish before another owner.");
                expectedRetry ??= retained;
            }
            if (expectedRetry is not null) RequireExactDeferredCanonicalDirtyOwnerLocked(expectedRetry);
            if (!_guestImages.TryGetValue(address, out image!) || !_finiteStorageDebts.TryGetValue(image, out debt!) ||
                image.StorageCpuOrigin is not { } found) return CanonicalDirtyResult.NotApplicable;
            origin = found; version = image.ContentVersion; texture = image.Texture;
            ownerCurrent = IsExactCanonicalDirtyOwnerLocked(address, image, debt, origin, version, texture, out reason, out offender);
        }
        if (!ownerCurrent)
        {
            if (TryDeferCanonicalDirtyBusy(address, image, debt, origin, version, texture)) return CanonicalDirtyResult.Deferred;
            lock (_gate)
                if (_deferredCanonicalDirty is not null)
                    throw new InvalidOperationException("Deferred canonical dirty ORIGINAL owner became stale: " + reason);
            TraceFiniteStorageRuntime("dirty-refused", address, reason, origin, offender);
            return CanonicalDirtyResult.NotApplicable;
        }
        CpuMappedPublicationValidation? validation = null;
        Exception? primary = null;
        var consumed = false;
        var validationReleased = false;
        var outcome = CanonicalDirtyResult.NotApplicable;
        try
        {
            var result = ((ICpuMappedPublicationSource)origin.Memory).TryPrepareMappedPublicationValidation(origin.Mapping, out validation);
            if (result != CpuMappedPublicationPreparationResult.Prepared || validation is null)
            {
                if (validation is null && TryDeferCanonicalDirtyBusy(address, image, debt, origin, version, texture))
                    return CanonicalDirtyResult.Deferred;
                TraceFiniteStorageRuntime("dirty-refused", address, "original-physical-history-reservation", origin);
                return CanonicalDirtyResult.NotApplicable;
            }
            var reservation = new StorageWatchReservation(validation);
            ValidateFiniteStorageOrigin(origin, debt.CanonicalDescriptor!, reservation);
            if (!validation.IsCurrent)
            { TraceFiniteStorageRuntime("dirty-refused", address, "validation-revoked", origin); return CanonicalDirtyResult.NotApplicable; }
            lock (_gate)
                ownerCurrent = IsExactCanonicalDirtyOwnerLocked(address, image, debt, origin, version, texture, out reason, out offender, reservation);
            if (!ownerCurrent) { TraceFiniteStorageRuntime("dirty-refused", address, reason, origin, offender); return CanonicalDirtyResult.NotApplicable; }
            consumed = GuestImageWriteTracker.TryConsumeDirtyAtProtectedToken(address, origin.Layout.PhysicalBytes, origin.Observation.Token);
            if (!consumed)
            { TraceFiniteStorageRuntime("dirty-refused", address, "original-protected-token-or-armed-owner", origin); return CanonicalDirtyResult.NotApplicable; }
            ValidateFiniteStorageOrigin(origin, debt.CanonicalDescriptor!, reservation);
            if (!validation.IsCurrent) throw new InvalidOperationException("Canonical dirty acknowledgment mapping was revoked.");
            lock (_gate)
                ownerCurrent = IsExactCanonicalDirtyOwnerLocked(address, image, debt, origin, version, texture, out reason, out offender, reservation);
            if (!ownerCurrent) throw new InvalidOperationException("Canonical dirty acknowledgment exact native owner changed: " + reason);
            outcome = CanonicalDirtyResult.Acknowledged;
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
                validationReleased = true;
                if (primary is null && consumed)
                {
                    _canonicalDirtyAfterValidationReleasedForControl?.Invoke();
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
                var defer = false;
                if (validationReleased && primary is null && consumed)
                    try { defer = TryDeferCanonicalDirtyBusy(address, image, debt, origin, version, texture); }
                    catch (Exception later) { try { cleanup.Data["CanonicalDirtyRetryClassification"] = later; } catch { } }
                if (defer)
                {
                    GuestImageWriteTracker.RestoreDirtyAtProtectedOwner(address, origin.Observation.Token);
                    outcome = CanonicalDirtyResult.Deferred; // no dirty/queue/presentation acknowledgment
                }
                else
                {
                    if (consumed) GuestImageWriteTracker.RestoreDirtyAtProtectedOwner(address, origin.Observation.Token);
                    lock (_gate) { debt.Failed = true; debt.Pending = true; _textureSourceCleanupFailure ??= cleanup; }
                    if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                    try { primary.Data["CanonicalDirtyValidationCleanup"] = cleanup; } catch { }
                }
            }
        }
        if (outcome == CanonicalDirtyResult.Acknowledged)
        {
            lock (_gate)
            {
                if (expectedRetry is not null) RequireExactDeferredCanonicalDirtyOwnerLocked(expectedRetry);
                if (IsExactDeferredCanonicalDirtyLocked(address, image, debt, origin, version, texture))
                    _deferredCanonicalDirty = null;
            }
            TraceFiniteStorageRuntime("dirty-acknowledged", address, "original-token-unchanged", origin);
        }
        return outcome;
    }
}
