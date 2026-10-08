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
        FiniteStorageDebt debt, StorageCpuSeedOrigin origin, int version, nint texture)
    {
        StorageCpuSeedOrigin[] volumes;
        lock (_gate)
        {
            if (_deferredCanonicalDirty is not null &&
                !IsExactDeferredCanonicalDirtyLocked(address, image, debt, origin, version, texture)) return false;
            if (GetExactCanonicalDirtyOwnerStatusLocked(address, image, debt, origin, version, texture,
                out _, out _, classifyBusy: true) != CpuMappedReadWatchStatus.Busy) return false;
            volumes = _guestImages.Values.Select(other => other.StorageCpuOrigin)
                .Where(other => other?.VolumeWatch is not null).Cast<StorageCpuSeedOrigin>().ToArray();
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
            if (status is not CpuMappedReadWatchStatus.Current and not CpuMappedReadWatchStatus.Busy) return false;
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
            if (currentVolumes != volumes.Length ||
                GetExactCanonicalDirtyOwnerStatusLocked(address, image, debt, origin, version, texture,
                    out _, out _, classifyBusy: true) == CpuMappedReadWatchStatus.Stale) return false;
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
}
