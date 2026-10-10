// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private static bool IsExactDeferredCanonicalDirtyLocked(ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture) =>
        _deferredCanonicalDirty is { } retry && retry.Address == address &&
        ReferenceEquals(retry.Image, image) && ReferenceEquals(retry.Debt, debt) &&
        ReferenceEquals(retry.Origin, origin) && retry.Version == version && retry.Texture == texture;

    private static void RequireExactDeferredCanonicalDirtyOwnerLocked(DeferredCanonicalDirty retry)
    {
        if (!ReferenceEquals(_deferredCanonicalDirty, retry) ||
            !_guestImages.TryGetValue(retry.Address, out var image) || !ReferenceEquals(image, retry.Image) ||
            image.ContentVersion != retry.Version || image.Texture != retry.Texture ||
            !ReferenceEquals(image.StorageCpuOrigin, retry.Origin) ||
            !_finiteStorageDebts.TryGetValue(image, out var debt) || !ReferenceEquals(debt, retry.Debt) ||
            _closed || _guestWorkFailure is not null || _typedCpuSyncCleanupFailure is not null ||
            _textureSourceCleanupFailure is not null || !ReferenceEquals(_guestMemory, retry.Origin.Memory) ||
            retry.Origin.Session != CurrentStorageCpuSeedSession)
            throw new InvalidOperationException("Deferred canonical dirty ORIGINAL receipt owner or version changed.");
    }

    // Detached first-return refusal only. This cannot certify history, bytes,
    // a native owner or acknowledgment; its exact ORIGINAL must still survive
    // every ordinary retention check below before a whole-drain refusal exists.
    private sealed class CanonicalDirtyPreparationBusy(DeferredCanonicalDirty owner,
        CpuMappedCopyObservation mapping, CpuMappedPublicationPreparationResult result,
        CpuMappedPublicationValidationRefusal first) : OriginalCanonicalDirtyBusy(
            "Canonical dirty ORIGINAL validation preparation was busy with an ordinary writer.")
    {
        internal readonly DeferredCanonicalDirty Owner = owner;
        internal readonly CpuMappedCopyObservation Mapping = mapping;
        internal readonly CpuMappedPublicationPreparationResult Result = result;
        internal readonly CpuMappedPublicationValidationRefusal First = first;
    }
    private readonly record struct CanonicalDirtyRefusalDiagnostic(string? Boundary, string? Reason,
        CpuMappedReadWatchStatus? OwnerStatus = null, CpuMappedPublicationPreparationResult? PrepareResult = null,
        CpuMappedPublicationValidationRefusal? PrepareRefusal = null);

    private static bool SameCanonicalDirtyOwner(DeferredCanonicalDirty saved, ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture) =>
        saved.Address == address && ReferenceEquals(saved.Image, image) && ReferenceEquals(saved.Debt, debt) &&
        ReferenceEquals(saved.Origin, origin) && saved.Version == version && saved.Texture == texture;

    private static bool IsOrdinaryCanonicalDirtyPrepareBusy(CpuMappedPublicationPreparationResult result, bool hasTicket,
        CpuMappedPublicationValidationRefusal first) => result == CpuMappedPublicationPreparationResult.Rejected && !hasTicket &&
        first.Kind == CpuMappedPublicationValidationRefusalKind.AdmissionBusy &&
        first.RejectedAdmission > 0 && first.RejectedAdmission < (1L << 32);

    private static CpuMappedPublicationPreparationResult PrepareCanonicalDirtyValidation(ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture,
        out CpuMappedPublicationValidation? validation, out CanonicalDirtyPreparationBusy? firstBusy,
        out CpuMappedPublicationValidationRefusal first)
    {
        // ONE actual preparation/query over the retained ORIGINAL. A later
        // ordinary Current cannot supply this first Busy classification.
        var result = ((ICpuMappedPublicationSource)origin.Memory).TryPrepareMappedPublicationValidation(origin.Mapping,
            out validation, out first);
        firstBusy = IsOrdinaryCanonicalDirtyPrepareBusy(result, validation is not null, first)
            ? new(new(address, image, debt, origin, version, texture), origin.Mapping, result, first) : null;
        return result;
    }

    private static int _canonicalDirtyRetryRefusalRows;
    private static bool TryReserveCanonicalDirtyRetryRefusalTrace()
    {
        var rows = Volatile.Read(ref _canonicalDirtyRetryRefusalRows);
        return rows < 64 && Interlocked.CompareExchange(ref _canonicalDirtyRetryRefusalRows, rows + 1, rows) == rows;
    }
    private static string FormatCanonicalDirtyRetryRefusal(DeferredCanonicalDirty retry, CanonicalDirtyRefusalDiagnostic first) =>
        $"[canonical-dirty-retry-first-refusal] address=0x{retry.Address:X} original_session={retry.Origin.Session} " +
        $"boundary={first.Boundary ?? "not-reached"} reason={first.Reason ?? "not-reached"} " +
        $"owner_status={first.OwnerStatus?.ToString() ?? "not-reached"} prepare_result={first.PrepareResult?.ToString() ?? "not-reached"} " +
        $"prepare_refusal={first.PrepareRefusal?.Kind.ToString() ?? "not-reached"} " +
        $"prepare_original_revision={first.PrepareRefusal?.OriginalRevision.ToString() ?? "not-reached"} " +
        $"prepare_rejected_admission={first.PrepareRefusal?.RejectedAdmission.ToString() ?? "not-reached"} no_later_query=true";
    private static void TraceCanonicalDirtyRetryRefusal(DeferredCanonicalDirty retry, CanonicalDirtyRefusalDiagnostic first)
    {
        // Failure-specific budget remains available after ordinary runtime rows
        // exhaust. Only already-returned/reached values are emitted; no tracker,
        // mapping, watch, provider, status or native query explains them later.
        if (!_rgbaStorageRuntimePublicationRequested || !_volumeOwnerDeclineDiagnostics || Monitor.IsEntered(_gate)) return;
        try
        {
            if (!TryReserveCanonicalDirtyRetryRefusalTrace()) return;
            Console.WriteLine(FormatCanonicalDirtyRetryRefusal(retry, first));
        }
        catch { /* Optional first-refusal output preserves the original failure. */ }
    }

    // All successful content/native/publication routes remain Current-only.
    // This helper can install ONLY a whole-drain refusal, never an ack.
    private static bool TryDeferCanonicalDirtyBusy(ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture,
        OriginalCanonicalDirtyBusy? originalBodyBusy = null) =>
        TryRetainCanonicalDirtyRetry(address, image, debt, origin, version, texture, originalBodyBusy, null);

    private static bool TryDeferCanonicalDirtyPendingWork(ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture,
        CanonicalDirtyPendingWork originalPending) =>
        TryRetainCanonicalDirtyRetry(address, image, debt, origin, version, texture, null, originalPending);

    private static bool TryRetainCanonicalDirtyRetry(ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture,
        OriginalCanonicalDirtyBusy? originalBodyBusy, CanonicalDirtyPendingWork? originalPending) =>
        TryRetainCanonicalDirtyRetryCore(address, image, debt, origin, version, texture, originalBodyBusy, originalPending, out _);

    private static bool TryRetainCanonicalDirtyRetryCore(ulong address, GuestImage image,
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture,
        OriginalCanonicalDirtyBusy? originalBodyBusy, CanonicalDirtyPendingWork? originalPending,
        out CanonicalDirtyRefusalDiagnostic refusal)
    {
        refusal = new("retention", "not-reached");
        var pending = originalPending is not null;
        if (originalBodyBusy is CanonicalDirtyPreparationBusy prepared &&
            (!SameCanonicalDirtyOwner(prepared.Owner, address, image, debt, origin, version, texture) ||
             !ReferenceEquals(prepared.Mapping, origin.Mapping) || !IsOrdinaryCanonicalDirtyPrepareBusy(prepared.Result, false, prepared.First)))
        { refusal = new("retention", "first-prepare-original-mismatch"); return false; }
        if (pending && (originalPending!.Owner.Address != address || !ReferenceEquals(originalPending.Owner.Image, image) ||
            !ReferenceEquals(originalPending.Owner.Debt, debt) || !ReferenceEquals(originalPending.Owner.Origin, origin) ||
            originalPending.Owner.Version != version || originalPending.Owner.Texture != texture))
        { refusal = new("retention", "pending-original-mismatch"); return false; }
        StorageCpuSeedOrigin[] volumes;
        lock (_gate)
        {
            if (_deferredCanonicalDirty is not null &&
                !IsExactDeferredCanonicalDirtyLocked(address, image, debt, origin, version, texture))
            { refusal = new("retention", "different-retained-original"); return false; }
            var status = GetExactCanonicalDirtyOwnerStatusLocked(address, image, debt, origin, version, texture,
                out var reason, out _, classifyBusy: true, allowPendingWork: pending);
            if (pending ? status != CpuMappedReadWatchStatus.Current :
                status != CpuMappedReadWatchStatus.Busy &&
                !(originalBodyBusy is not null && status == CpuMappedReadWatchStatus.Current))
            { refusal = new("retention-initial-owner", reason, status); return false; }
            volumes = _guestImages.Values.Select(other => other.StorageCpuOrigin)
                .Where(other => other?.VolumeWatch is not null).Cast<StorageCpuSeedOrigin>().ToArray();
            if (originalBodyBusy is SingletonVolumeOriginalBusy volumeBusy &&
                !volumes.Any(volume => ReferenceEquals(volume, volumeBusy.Original)))
            { refusal = new("retention", "first-volume-original-mismatch"); return false; }
            if (originalBodyBusy is CanonicalDirtyOwnerBusy ownerBusy &&
                (ownerBusy.Owner.Address != address || !ReferenceEquals(ownerBusy.Owner.Image, image) ||
                 !ReferenceEquals(ownerBusy.Owner.Debt, debt) || !ReferenceEquals(ownerBusy.Owner.Origin, origin) ||
                 ownerBusy.Owner.Version != version || ownerBusy.Owner.Texture != texture))
            { refusal = new("retention", "first-owner-original-mismatch"); return false; }
        }
        var token = default(GuestImageWriteTracker.ContentToken);
        string? originalGuard = !origin.Observation.Observed ? "observation-unavailable" :
            origin.Observation.ByteCount != origin.Layout.PhysicalBytes ? "observation-byte-count" :
            !origin.Source.IsMappedCopyObservationCurrent(origin.Mapping) ? "original-mapping" :
            !GuestImageWriteTracker.TryGetProtectedObservedContentToken(address, origin.Layout.PhysicalBytes, out token) ? "protected-token-unavailable" :
            token != origin.Observation.Token ? "protected-token-changed" : null;
        if (originalGuard is not null)
        { refusal = new("retention-original", originalGuard); return false; }
        foreach (var volume in volumes)
        {
            var watch = volume.VolumeWatch!;
            var current = default(GuestImageWriteTracker.ContentToken);
            string? volumeGuard = !ReferenceEquals(volume.Memory, origin.Memory) ? "memory" :
                volume.Session != origin.Session ? "session" :
                !volume.Source.IsMappedCopyObservationCurrent(volume.Mapping) ? "original-mapping" :
                !GuestImageWriteTracker.IsReadSnapshotStable(watch.Read) ? "original-read" :
                !volume.Observation.Observed ? "observation-unavailable" :
                !GuestImageWriteTracker.TryGetProtectedObservedContentToken(volume.Layout.Address, volume.Layout.PhysicalBytes, out current) ? "protected-token-unavailable" :
                current != volume.Observation.Token ? "protected-token-changed" : null;
            if (volumeGuard is not null)
            { refusal = new("retention-volume-original", volumeGuard); return false; }
            var status = watch.Source.GetMappedCopyWriteWatchStatus(watch.Mapping);
            if (pending ? status != CpuMappedReadWatchStatus.Current :
                status is not CpuMappedReadWatchStatus.Current and not CpuMappedReadWatchStatus.Busy)
            { refusal = new("retention-volume-watch", "original-read-watch", status); return false; }
        }
        lock (_gate)
        {
            // Do not certify a changed inventory with tokens from the old one.
            var currentVolumes = 0;
            foreach (var other in _guestImages.Values)
                if (other.StorageCpuOrigin is { VolumeWatch: not null } original)
                {
                    currentVolumes++;
                    if (!volumes.Any(prior => ReferenceEquals(prior, original)))
                    { refusal = new("retention-inventory", "volume-original-replaced"); return false; }
                }
            if (currentVolumes != volumes.Length)
            { refusal = new("retention-inventory", "volume-count-changed"); return false; }
            var finalStatus = GetExactCanonicalDirtyOwnerStatusLocked(address, image, debt, origin, version, texture,
                out var finalReason, out _, classifyBusy: true, allowPendingWork: pending);
            if (pending ? finalStatus != CpuMappedReadWatchStatus.Current :
                finalStatus == CpuMappedReadWatchStatus.Stale)
            { refusal = new("retention-final-owner", finalReason, finalStatus); return false; }
            _deferredCanonicalDirty ??= new(address, image, debt, origin, version, texture);
            Volatile.Write(ref _cpuWrittenGuestImageSyncRequested, 1);
            return true;
        }
    }

    // Managed refusal-boundary controls only. No status/bytes/receipt can be
    // supplied by this default-null hook to any publication/native consumer.
    private static Func<bool>? _canonicalDirtyDrainReadyForControl = null;
    private static bool CanonicalDirtyDrainReady() =>
        _canonicalDirtyDrainReadyForControl is { } control ? control() :
        RetryDeferredCanonicalDirty() != CanonicalDirtyResult.Deferred;

    private static CanonicalDirtyResult RetryDeferredCanonicalDirty()
    {
        DeferredCanonicalDirty? retry;
        lock (_gate) retry = _deferredCanonicalDirty;
        if (retry is null) return CanonicalDirtyResult.NotApplicable;
        var result = TryAcknowledgeFiniteCanonicalDirtyCore(retry.Address, retry, out var refusal);
        if (result == CanonicalDirtyResult.NotApplicable)
        {
            TraceCanonicalDirtyRetryRefusal(retry, refusal);
            throw new InvalidOperationException("Deferred canonical dirty ORIGINAL receipt could not be revalidated.");
        }
        lock (_gate)
            if (result == CanonicalDirtyResult.Acknowledged && _deferredCanonicalDirty is not null)
                throw new InvalidOperationException("Deferred canonical dirty ORIGINAL receipt was not consumed.");
        return result;
    }

    // Called only after Run's native-depth, volume-watch and selected-owner
    // retirement attempts succeeded. Drop retry bookkeeping, never a native
    // retain or a guest completion. Existing owner/quarantine records survive.
    private static bool DropDeferredCanonicalDirtyAfterShutdown()
    {
        lock (_gate)
        {
            if (_deferredCanonicalDirty is not { } retry) return true;
            if (!_closed || _thread is not null || !ReferenceEquals(_renderOwner, Thread.CurrentThread) ||
                _batchOpen || _takenGuestWorkSequence != 0 || _executingGuestWorkSequence != 0 ||
                _guestWorkFailure is not null || _textureSourceCleanupFailure is not null ||
                _typedCpuSyncCleanupFailure is not null || _singletonVolumeOriginalCleanupFailure is not null ||
                Volatile.Read(ref _selectedNativeOriginalCleanupFailure) is not null || _r16SelectedRetainCleanupFailure is not null ||
                _nativeDepthBatches.Count != 0 || retry.Origin.Session == CurrentStorageCpuSeedSession ||
                retry.Debt.Failed || retry.Debt.Pending || retry.Debt.CanonicalVersion != retry.Version ||
                !ReferenceEquals(retry.Debt.CanonicalOrigin, retry.Origin)) return false;
            _deferredCanonicalDirty = null;
            return true;
        }
    }
}
