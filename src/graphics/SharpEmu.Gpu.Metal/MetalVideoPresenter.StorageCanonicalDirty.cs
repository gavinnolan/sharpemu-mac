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
    // Observes only the saved first pending refusal after known cancellation;
    // cannot supply a status, receipt or completion.
    private static Action? _canonicalDirtyAfterPendingWorkRefusalForControl = null;
    // Ordinary race boundaries only; neither can supply a provider receipt,
    // replacement observation or native completion.
    private static Action<bool>? _canonicalDirtyBeforeBodyValidationForControl = null;
    // Ordinary refusal race boundary only. It cannot supply a status, mapping,
    // receipt or completion; runs after the first original check, gate-free.
    private static Action? _canonicalDirtyAfterInitialOwnerCheckForControl = null;
    private abstract class OriginalCanonicalDirtyBusy(string message) : InvalidOperationException(message) { }
    private sealed class CanonicalDirtyOwnerBusy(ulong address, GuestImage image, FiniteStorageDebt debt,
        StorageCpuSeedOrigin origin, int version, nint texture) : OriginalCanonicalDirtyBusy(
            "Canonical dirty ORIGINAL exact native cohort is busy with a participating ordinary writer.")
    {
        internal readonly DeferredCanonicalDirty Owner = new(address, image, debt, origin, version, texture);
    }
    private enum CanonicalDirtyWorkGuard { None, Pending, Batch, Taken, Executing }
    private sealed class CanonicalDirtyPendingWork(ulong address, GuestImage image, FiniteStorageDebt debt,
        StorageCpuSeedOrigin origin, int version, nint texture) : InvalidOperationException(
            "Canonical dirty ordinary post-release owner changed: pending-work-or-batch")
    {
        internal readonly DeferredCanonicalDirty Owner = new(address, image, debt, origin, version, texture);
    }

    private static bool InspectCanonicalDirtyOwnerForAcknowledgmentLocked(ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture, out string reason,
        out FiniteStorageCohortOffender? offender, StorageWatchReservation reservation = default,
        bool classifyPendingWork = false)
    {
        var status = GetExactCanonicalDirtyOwnerStatusCoreLocked(address, image, debt, origin, version, texture,
            out reason, out offender, out var workGuard, reservation, classifyBusy: true);
        if (classifyPendingWork && workGuard != CanonicalDirtyWorkGuard.None)
        {
            InvalidOperationException failure = workGuard == CanonicalDirtyWorkGuard.Pending
                ? new CanonicalDirtyPendingWork(address, image, debt, origin, version, texture)
                : new InvalidOperationException("Canonical dirty ordinary post-release owner changed: pending-work-or-batch");
            failure.Data["CanonicalDirtyFirstWorkGuard"] = workGuard.ToString();
            throw failure;
        }
        if (status == CpuMappedReadWatchStatus.Busy)
            throw new CanonicalDirtyOwnerBusy(address, image, debt, origin, version, texture);
        return status == CpuMappedReadWatchStatus.Current;
    }

    private static bool IsExactCanonicalDirtyOwnerLocked(ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture, out string reason,
        out FiniteStorageCohortOffender? offender, StorageWatchReservation reservation = default) =>
        GetExactCanonicalDirtyOwnerStatusLocked(address, image, debt, origin, version, texture,
            out reason, out offender, reservation) == CpuMappedReadWatchStatus.Current;

    private static CpuMappedReadWatchStatus GetExactCanonicalDirtyOwnerStatusLocked(ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture, out string reason,
        out FiniteStorageCohortOffender? offender, StorageWatchReservation reservation = default, bool classifyBusy = false,
        bool allowPendingWork = false) =>
        GetExactCanonicalDirtyOwnerStatusCoreLocked(address, image, debt, origin, version, texture,
            out reason, out offender, out _, reservation, classifyBusy, allowPendingWork);

    private static CpuMappedReadWatchStatus GetExactCanonicalDirtyOwnerStatusCoreLocked(ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture, out string reason,
        out FiniteStorageCohortOffender? offender, out CanonicalDirtyWorkGuard workGuard,
        StorageWatchReservation reservation = default, bool classifyBusy = false, bool allowPendingWork = false)
    {
        offender = null;
        workGuard = CanonicalDirtyWorkGuard.None;
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
        // Preserve the first reached scalar guard, including its original
        // order. Pending is bookkeeping, never native/content authority.
        if (!allowPendingWork && _pendingGuestWorkCount != 0 &&
            !IsExactDeferredCanonicalDirtyLocked(address, image, debt, origin, version, texture))
            workGuard = CanonicalDirtyWorkGuard.Pending;
        else if (_batchOpen) workGuard = CanonicalDirtyWorkGuard.Batch;
        else if (_takenGuestWorkSequence != 0) workGuard = CanonicalDirtyWorkGuard.Taken;
        else if (_executingGuestWorkSequence != 0) workGuard = CanonicalDirtyWorkGuard.Executing;
        if (workGuard != CanonicalDirtyWorkGuard.None) return CpuMappedReadWatchStatus.Stale;
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
        CpuMappedReadWatchStatus firstOwnerStatus;
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
            // Preserve the first complete ORIGINAL classification. An ordinary
            // writer can finish before deferral rechecks this same owner. A
            // younger Current does not turn that healthy Busy into Stale.
            firstOwnerStatus = GetExactCanonicalDirtyOwnerStatusLocked(address, image, debt, origin,
                version, texture, out reason, out offender, classifyBusy: true);
            ownerCurrent = firstOwnerStatus == CpuMappedReadWatchStatus.Current;
        }
        if (!ownerCurrent)
        {
            _canonicalDirtyAfterInitialOwnerCheckForControl?.Invoke();
            var firstBusy = firstOwnerStatus == CpuMappedReadWatchStatus.Busy
                ? new CanonicalDirtyOwnerBusy(address, image, debt, origin, version, texture) : null;
            if (firstBusy is not null &&
                TryDeferCanonicalDirtyBusy(address, image, debt, origin, version, texture, firstBusy))
                return CanonicalDirtyResult.Deferred;
            bool retained;
            lock (_gate) retained = _deferredCanonicalDirty is not null;
            if (retained)
            {
                // The first owner check already detached this offender. Keep
                // the original refusal and format only after releasing _gate;
                // a later provider/watch query cannot explain that first check.
                TraceFiniteStorageRuntime("dirty-retained-owner-refused", address, reason, origin, offender);
                throw new InvalidOperationException("Deferred canonical dirty ORIGINAL owner became stale: " + reason);
            }
            TraceFiniteStorageRuntime("dirty-refused", address, reason, origin, offender);
            return CanonicalDirtyResult.NotApplicable;
        }
        CpuMappedPublicationValidation? validation = null;
        Exception? primary = null;
        OriginalCanonicalDirtyBusy? bodyBusy = null;
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
            _canonicalDirtyBeforeBodyValidationForControl?.Invoke(false);
            ValidateFiniteStorageOrigin(origin, debt.CanonicalDescriptor!, reservation, classifyOrdinaryBusy: true);
            if (!validation.IsCurrent)
            { TraceFiniteStorageRuntime("dirty-refused", address, "validation-revoked", origin); return CanonicalDirtyResult.NotApplicable; }
            lock (_gate)
                ownerCurrent = InspectCanonicalDirtyOwnerForAcknowledgmentLocked(address, image, debt, origin, version, texture, out reason, out offender, reservation);
            if (!ownerCurrent) { TraceFiniteStorageRuntime("dirty-refused", address, reason, origin, offender); return CanonicalDirtyResult.NotApplicable; }
            consumed = GuestImageWriteTracker.TryConsumeDirtyAtProtectedToken(address, origin.Layout.PhysicalBytes, origin.Observation.Token);
            if (!consumed)
            { TraceFiniteStorageRuntime("dirty-refused", address, "original-protected-token-or-armed-owner", origin); return CanonicalDirtyResult.NotApplicable; }
            _canonicalDirtyBeforeBodyValidationForControl?.Invoke(true);
            ValidateFiniteStorageOrigin(origin, debt.CanonicalDescriptor!, reservation, classifyOrdinaryBusy: true);
            if (!validation.IsCurrent) throw new InvalidOperationException("Canonical dirty acknowledgment mapping was revoked.");
            lock (_gate)
                ownerCurrent = InspectCanonicalDirtyOwnerForAcknowledgmentLocked(address, image, debt, origin, version, texture, out reason, out offender, reservation);
            if (!ownerCurrent)
            {
                // The first failed query and detached offender are saved under
                // the gate. Failure-only output uses its own bounded budget.
                if (_volumeOwnerDeclineDiagnostics)
                    TraceFiniteStorageRuntime("dirty-body-owner-refused", address, reason, origin, offender);
                throw new InvalidOperationException("Canonical dirty acknowledgment exact native owner changed: " + reason);
            }
            outcome = CanonicalDirtyResult.Acknowledged;
        }
        catch (OriginalCanonicalDirtyBusy busy)
        {
            // Hold the precise failed ORIGINAL, but earn no success. Disposal
            // must complete before installing the whole-drain retry receipt.
            bodyBusy = busy;
            primary = busy;
            // Preserve the consumed scalar dirty bit even if subsequent
            // cancellation is unknown. This is no authority or retry receipt.
            if (consumed) GuestImageWriteTracker.RestoreDirtyAtProtectedOwner(address, origin.Observation.Token);
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
                    ValidateFiniteStorageOrigin(origin, debt.CanonicalDescriptor!, classifyOrdinaryBusy: true);
                    lock (_gate)
                        ownerCurrent = InspectCanonicalDirtyOwnerForAcknowledgmentLocked(address, image, debt, origin, version, texture,
                            out reason, out offender, classifyPendingWork: true);
                    if (!ownerCurrent)
                    {
                        TraceFiniteStorageRuntime("dirty-post-release-refused", address, reason, origin, offender);
                        throw new InvalidOperationException("Canonical dirty ordinary post-release owner changed: " + reason);
                    }
                }
            }
            catch (Exception cleanup)
            {
                if (_volumeOwnerDeclineDiagnostics)
                    try
                    {
                        if (cleanup.Data["CanonicalDirtyFirstWorkGuard"] is string firstGuard)
                            TraceFiniteStorageRuntime("dirty-post-release-refused", address,
                                "pending-work-or-batch; first_work_guard=" + firstGuard, origin);
                    }
                    catch { /* Optional first-scalar output preserves the original cleanup refusal. */ }
                var defer = false;
                if (validationReleased && primary is null && consumed && cleanup is OriginalCanonicalDirtyBusy exactBusy)
                    try { defer = TryDeferCanonicalDirtyBusy(address, image, debt, origin, version, texture, exactBusy); }
                    catch (Exception later) { try { cleanup.Data["CanonicalDirtyRetryClassification"] = later; } catch { } }
                if (validationReleased && primary is null && consumed && cleanup is CanonicalDirtyPendingWork exactPending)
                    try
                    {
                        _canonicalDirtyAfterPendingWorkRefusalForControl?.Invoke();
                        defer = TryDeferCanonicalDirtyPendingWork(address, image, debt, origin, version, texture, exactPending);
                    }
                    catch (Exception later) { try { cleanup.Data["CanonicalDirtyRetryClassification"] = later; } catch { } }
                if (defer)
                {
                    GuestImageWriteTracker.RestoreDirtyAtProtectedOwner(address, origin.Observation.Token);
                    outcome = CanonicalDirtyResult.Deferred; // no dirty/queue/presentation acknowledgment
                }
                else
                {
                    if (consumed && bodyBusy is null) GuestImageWriteTracker.RestoreDirtyAtProtectedOwner(address, origin.Observation.Token);
                    lock (_gate) { debt.Failed = true; debt.Pending = true; _textureSourceCleanupFailure ??= cleanup; }
                    if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                    try { primary.Data["CanonicalDirtyValidationCleanup"] = cleanup; } catch { }
                }
            }
        }
        if (bodyBusy is not null)
        {
            if (validationReleased && TryDeferCanonicalDirtyBusy(address, image, debt, origin, version, texture, bodyBusy))
                outcome = CanonicalDirtyResult.Deferred;
            else ExceptionDispatchInfo.Capture(bodyBusy).Throw();
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
