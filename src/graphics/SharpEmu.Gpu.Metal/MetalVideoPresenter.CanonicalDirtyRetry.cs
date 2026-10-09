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
        OriginalCanonicalDirtyBusy? originalBodyBusy, CanonicalDirtyPendingWork? originalPending)
    {
        var pending = originalPending is not null;
        if (pending && (originalPending!.Owner.Address != address || !ReferenceEquals(originalPending.Owner.Image, image) ||
            !ReferenceEquals(originalPending.Owner.Debt, debt) || !ReferenceEquals(originalPending.Owner.Origin, origin) ||
            originalPending.Owner.Version != version || originalPending.Owner.Texture != texture)) return false;
        StorageCpuSeedOrigin[] volumes;
        lock (_gate)
        {
            if (_deferredCanonicalDirty is not null &&
                !IsExactDeferredCanonicalDirtyLocked(address, image, debt, origin, version, texture)) return false;
            var status = GetExactCanonicalDirtyOwnerStatusLocked(address, image, debt, origin, version, texture,
                out _, out _, classifyBusy: true, allowPendingWork: pending);
            if (pending ? status != CpuMappedReadWatchStatus.Current :
                status != CpuMappedReadWatchStatus.Busy &&
                !(originalBodyBusy is not null && status == CpuMappedReadWatchStatus.Current)) return false;
            volumes = _guestImages.Values.Select(other => other.StorageCpuOrigin)
                .Where(other => other?.VolumeWatch is not null).Cast<StorageCpuSeedOrigin>().ToArray();
            if (originalBodyBusy is SingletonVolumeOriginalBusy volumeBusy &&
                !volumes.Any(volume => ReferenceEquals(volume, volumeBusy.Original))) return false;
            if (originalBodyBusy is CanonicalDirtyOwnerBusy ownerBusy &&
                (ownerBusy.Owner.Address != address || !ReferenceEquals(ownerBusy.Owner.Image, image) ||
                 !ReferenceEquals(ownerBusy.Owner.Debt, debt) || !ReferenceEquals(ownerBusy.Owner.Origin, origin) ||
                 ownerBusy.Owner.Version != version || ownerBusy.Owner.Texture != texture)) return false;
        }
        if (!origin.Observation.Observed || origin.Observation.ByteCount != origin.Layout.PhysicalBytes ||
            !origin.Source.IsMappedCopyObservationCurrent(origin.Mapping) ||
            !GuestImageWriteTracker.TryGetProtectedObservedContentToken(address, origin.Layout.PhysicalBytes, out var token) ||
            token != origin.Observation.Token) return false;
        foreach (var volume in volumes)
        {
            var watch = volume.VolumeWatch!;
            if (!ReferenceEquals(volume.Memory, origin.Memory) || volume.Session != origin.Session ||
                !volume.Source.IsMappedCopyObservationCurrent(volume.Mapping) ||
                !GuestImageWriteTracker.IsReadSnapshotStable(watch.Read) || !volume.Observation.Observed ||
                !GuestImageWriteTracker.TryGetProtectedObservedContentToken(volume.Layout.Address,
                    volume.Layout.PhysicalBytes, out var current) || current != volume.Observation.Token) return false;
            var status = watch.Source.GetMappedCopyWriteWatchStatus(watch.Mapping);
            if (pending ? status != CpuMappedReadWatchStatus.Current :
                status is not CpuMappedReadWatchStatus.Current and not CpuMappedReadWatchStatus.Busy) return false;
        }
        lock (_gate)
        {
            // Do not certify a changed inventory with tokens from the old one.
            var currentVolumes = 0;
            foreach (var other in _guestImages.Values)
                if (other.StorageCpuOrigin is { VolumeWatch: not null } original)
                {
                    currentVolumes++;
                    if (!volumes.Any(prior => ReferenceEquals(prior, original))) return false;
                }
            if (currentVolumes != volumes.Length) return false;
            var finalStatus = GetExactCanonicalDirtyOwnerStatusLocked(address, image, debt, origin, version, texture,
                out _, out _, classifyBusy: true, allowPendingWork: pending);
            if (pending ? finalStatus != CpuMappedReadWatchStatus.Current :
                finalStatus == CpuMappedReadWatchStatus.Stale) return false;
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
        var result = TryAcknowledgeFiniteCanonicalDirty(retry.Address, retry);
        if (result == CanonicalDirtyResult.NotApplicable)
            throw new InvalidOperationException("Deferred canonical dirty ORIGINAL receipt could not be revalidated.");
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
                _selectedNativeOriginalCleanupFailure is not null || _r16SelectedRetainCleanupFailure is not null ||
                _nativeDepthBatches.Count != 0 || retry.Origin.Session == CurrentStorageCpuSeedSession ||
                retry.Debt.Failed || retry.Debt.Pending || retry.Debt.CanonicalVersion != retry.Version ||
                !ReferenceEquals(retry.Debt.CanonicalOrigin, retry.Origin)) return false;
            _deferredCanonicalDirty = null;
            return true;
        }
    }
}
