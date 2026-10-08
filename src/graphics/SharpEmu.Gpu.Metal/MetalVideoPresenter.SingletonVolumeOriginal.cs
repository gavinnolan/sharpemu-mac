// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using System.Runtime.CompilerServices;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    internal static bool SingletonVolumeOriginalCaptureEnabled =>
        (_rgbaStorageRuntimePublicationRequested || _finiteStorageWritebackEnabled) && !TextureSourceGraphEnabled;
    private static Exception? _singletonVolumeOriginalCleanupFailure;
    private static readonly bool _volumeOwnerDeclineDiagnostics =
        Environment.GetEnvironmentVariable("SHARPEMU_CANONICAL_PUBLICATION_DIAGNOSTICS") == "1";
    private static Action? _singletonVolumeAfterOrdinaryQueueInsert; // optional ordinary-path fixture seam
    private static readonly HashSet<SingletonVolumeOriginalWatch> _singletonVolumeOriginalWatches = new(ReferenceEqualityComparer.Instance);

    private static void TrackSingletonVolumeOriginalWatch(SingletonVolumeOriginalWatch watch, long originalSession)
    {
        lock (_gate)
        {
            if (_closed || _guestWorkFailure is not null || _singletonVolumeOriginalCleanupFailure is not null ||
                originalSession != CurrentStorageCpuSeedSession ||
                _singletonVolumeOriginalWatches.Count >= 128 || Volatile.Read(ref watch.State) != 0)
                throw new InvalidOperationException("Singleton volume original watch inventory is closed, failed or full.");
            _singletonVolumeOriginalWatches.Add(watch);
        }
    }

    // An ORIGINAL participating-writer veto, not a native backing/permission
    // lease. All mutation authorization and raw/JIT coverage stay separate.
    private sealed class SingletonVolumeOriginalWatch(ICpuMappedReadWatchSource source,
        CpuMappedCopyObservation mapping, GuestImageWriteTracker.ReadSnapshot read)
    {
        internal readonly ICpuMappedReadWatchSource Source = source;
        internal readonly CpuMappedCopyObservation Mapping = mapping;
        internal readonly GuestImageWriteTracker.ReadSnapshot Read = read;
        internal int State; // 0 private CPU preparation; 1 exact native owner; 2 consumed
        internal GuestImage? Image;
        internal nint Texture;
        internal int Version;
        internal bool UnknownProducer;
        internal string? DiagnosticUnknownProducerReason;
        internal bool PreparationReleased; // presenter gate
        internal int QueuedReferences; // presenter gate; separate from native acceptance

        internal bool IsCurrent => Volatile.Read(ref State) != 2 && !Volatile.Read(ref UnknownProducer) &&
            Source.IsMappedCopyWriteWatchCurrent(Mapping);

        internal Exception? Release(bool preparationOnly, Exception? primary)
        {
            lock (_gate)
            {
                if (preparationOnly)
                {
                    PreparationReleased = true;
                    if (QueuedReferences != 0) return null;
                }
                var state = Volatile.Read(ref State);
                if (state == 2 || preparationOnly && state == 1) return null;
                if (Interlocked.CompareExchange(ref State, 2, state) != state) return null; // no retry/stealing a transfer
            }
            try
            {
                Source.ReleaseMappedCopyWriteWatch(Mapping);
                lock (_gate) _singletonVolumeOriginalWatches.Remove(this);
                return null;
            }
            catch (Exception cleanup)
            {
                Interlocked.CompareExchange(ref _singletonVolumeOriginalCleanupFailure, cleanup, null);
                if (primary is not null)
                    try { primary.Data["MetalSingletonVolumeOriginalCleanup"] = cleanup; } catch { }
                return cleanup; // consumed before callback; unknown is never retried
            }
        }
    }

    private sealed class QueuedSingletonVolumeOriginals(SingletonVolumeOriginalWatch[] watches)
    {
        internal readonly SingletonVolumeOriginalWatch[] Watches = watches;
        internal bool Released;
    }
    private static readonly ConditionalWeakTable<object, QueuedSingletonVolumeOriginals> _queuedSingletonVolumeOriginals = new();
    private const int MaximumSingletonVolumeQueuedOwners = MaxPendingGuestWork + 1; // pending capacity plus the taken consumer
    private static readonly HashSet<object> _pendingSingletonVolumeOriginalWorks = new(ReferenceEqualityComparer.Instance);

    private static bool RetainQueuedSingletonVolumeOriginalsLocked(object work)
    {
        if (!SingletonVolumeOriginalCaptureEnabled) return false;
        IReadOnlyList<GuestDrawTexture> textures = work switch
        {
            ComputeGuestDispatch dispatch => dispatch.Textures,
            OffscreenGuestDraw draw => draw.Draw.Textures,
            _ => [],
        };
        if (!textures.Any(texture => StorageCpuOrigin(texture)?.VolumeWatch is not null)) return false;
        if (textures.Count > 128) throw new NotSupportedException("Singleton volume queue preparation exceeds its finite bound.");
        if (_pendingSingletonVolumeOriginalWorks.Count >= MaximumSingletonVolumeQueuedOwners)
            throw new InvalidOperationException("Singleton volume pending work inventory exceeds its finite bound.");
        if (_queuedSingletonVolumeOriginals.TryGetValue(work, out _))
            throw new InvalidOperationException("Singleton volume work already owns its original watches.");
        var watches = textures.Select(texture => StorageCpuOrigin(texture)?.VolumeWatch)
            .Where(watch => watch is not null).Cast<SingletonVolumeOriginalWatch>().Distinct().ToArray();
        if (watches.Length > 128 || watches.Any(watch => !watch.IsCurrent || watch.PreparationReleased && Volatile.Read(ref watch.State) == 0 || watch.QueuedReferences == int.MaxValue))
            throw new InvalidOperationException("Singleton volume queue admission lost original watch preparation.");
        _pendingSingletonVolumeOriginalWorks.Add(work);
        try { _queuedSingletonVolumeOriginals.Add(work, new(watches)); } // allocate before changing counters
        catch { _pendingSingletonVolumeOriginalWorks.Remove(work); throw; }
        foreach (var watch in watches) watch.QueuedReferences++;
        return true;
    }

    private static void ReleaseQueuedSingletonVolumeOriginalsLocked(object work)
    {
        if (!_queuedSingletonVolumeOriginals.TryGetValue(work, out var owner) || owner.Released) return;
        owner.Released = true;
        _pendingSingletonVolumeOriginalWorks.Remove(work);
        Exception? cleanup = null;
        foreach (var watch in owner.Watches)
        {
            if (watch.QueuedReferences <= 0) throw new InvalidOperationException("Unbalanced singleton volume queue ownership.");
            watch.QueuedReferences--;
            if (watch.QueuedReferences == 0 && watch.PreparationReleased)
            {
                var later = watch.Release(preparationOnly: true, cleanup);
                cleanup ??= later;
            }
        }
        if (cleanup is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();
    }

    // Ordinary queued and taken work only; exact consume precedes callbacks.
    // Called on failure/close, never acknowledges canceled work or native bytes.
    private static void CancelQueuedSingletonVolumeOriginalsLocked(Exception? primary = null)
    {
        Exception? cleanup = null;
        while (_pendingSingletonVolumeOriginalWorks.Count != 0)
        {
            // Struct enumerator and scalar removal: cancellation cannot fail
            // allocating a snapshot after the original admission/drain error.
            var iterator = _pendingSingletonVolumeOriginalWorks.GetEnumerator();
            if (!iterator.MoveNext()) break;
            var work = iterator.Current;
            iterator.Dispose();
            _pendingSingletonVolumeOriginalWorks.Remove(work);
            try { ReleaseQueuedSingletonVolumeOriginalsLocked(work); }
            catch (Exception later)
            {
                cleanup ??= later;
                if (primary is not null)
                    try { primary.Data["MetalSingletonVolumeQueueCleanup"] = later; } catch { }
            }
        }
        if (primary is null && cleanup is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();
    }

    internal static void ReleaseUnregisteredSingletonVolumeWatch(ICpuMappedCopySource source,
        CpuMappedCopyObservation? mapping, Exception? primary)
    {
        if (mapping is null || source is not ICpuMappedReadWatchSource watched) return;
        SingletonVolumeOriginalWatch? tracked;
        lock (_gate) tracked = _singletonVolumeOriginalWatches.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Source, source) && ReferenceEquals(candidate.Mapping, mapping));
        if (tracked is not null)
        {
            var cleanup = tracked.Release(preparationOnly: true, primary);
            if (primary is null && cleanup is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();
            return;
        }
        try { watched.ReleaseMappedCopyWriteWatch(mapping); }
        catch (Exception cleanup)
        {
            Interlocked.CompareExchange(ref _singletonVolumeOriginalCleanupFailure, cleanup, null);
            if (primary is not null)
            { try { primary.Data["MetalSingletonVolumeUnregisteredCleanup"] = cleanup; } catch { } }
            else System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();
        }
    }

    // Content sampling is ordinary and always OUTSIDE the presenter/provider
    // gates. Original tokens/read/session are never refreshed after the copy.
    private static void ValidateSingletonVolumeOriginalContent(StorageCpuSeedOrigin? origin) =>
        ValidateSingletonVolumeOriginalContentWithReservation(origin, default);

    private static void ValidateSingletonVolumeOriginalContentWithReservation(StorageCpuSeedOrigin? origin,
        StorageWatchReservation reservation)
    {
        if (origin?.VolumeWatch is not { } watch) return;
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Singleton volume content validation requires a released presenter gate.");
        if (_closed || _guestWorkFailure is not null || !ReferenceEquals(_guestMemory, origin.Memory) ||
            _singletonVolumeOriginalCleanupFailure is not null || origin.Session != CurrentStorageCpuSeedSession ||
            !reservation.IsCurrent(watch) || !origin.Source.IsMappedCopyObservationCurrent(origin.Mapping) ||
            !GuestImageWriteTracker.IsReadSnapshotStable(watch.Read) || !origin.Observation.Observed ||
            !GuestImageWriteTracker.TryGetProtectedObservedContentToken(origin.Layout.Address, origin.Layout.PhysicalBytes, out var token) ||
            token != origin.Observation.Token)
            throw new InvalidOperationException("Metal singleton volume lost its ORIGINAL full mapping/read/watch/protected token/session.");
    }

    private static void ValidateSingletonVolumeForMaterialization(GuestDrawTexture descriptor, StorageCpuSeedOrigin? incoming)
    {
        ValidateSingletonVolumeOriginalContent(incoming);
        StorageCpuSeedOrigin? existing = null;
        lock (_gate)
            if (_guestImages.TryGetValue(descriptor.Address, out var image) && image.StorageCpuOrigin?.VolumeWatch is not null)
                existing = image.StorageCpuOrigin;
        ValidateSingletonVolumeOriginalContent(existing);
    }

    private static bool IsSingletonVolumeNativeOwnerCurrentLocked(GuestImage image, StorageCpuSeedOrigin origin) =>
        IsSingletonVolumeNativeOwnerCurrentWithReservationLocked(image, origin, default);

    private static bool IsSingletonVolumeNativeOwnerCurrentWithReservationLocked(GuestImage image,
        StorageCpuSeedOrigin origin, StorageWatchReservation reservation) =>
        !_closed && _guestWorkFailure is null && _singletonVolumeOriginalCleanupFailure is null &&
        ReferenceEquals(_guestMemory, origin.Memory) &&
        origin.VolumeWatch is { } watch && Volatile.Read(ref watch.State) == 1 && reservation.IsCurrent(watch) &&
        ReferenceEquals(watch.Image, image) && ReferenceEquals(image.StorageCpuOrigin, origin) &&
        image.Initialized && ReferenceEquals(image.StorageCpuLayout, origin.Layout) &&
        image.Texture != 0 && image.Texture == watch.Texture && image.ContentVersion == watch.Version &&
        image.TextureType == MtlTextureType3D && image.Depth == origin.Layout.Depth && image.ArrayLength == 1 &&
        image.Width == origin.Layout.Width && image.Height == origin.Layout.Height && image.Format == origin.Layout.NativeFormat.Format &&
        origin.Session == CurrentStorageCpuSeedSession;

    private static void TransferSingletonVolumeOriginalWatchLocked(GuestImage image, StorageCpuSeedOrigin origin)
    {
        if (origin.VolumeWatch is not { } watch) return;
        if (_closed || _guestWorkFailure is not null || _singletonVolumeOriginalCleanupFailure is not null ||
            !ReferenceEquals(_guestMemory, origin.Memory) || !watch.IsCurrent || watch.Image is not null ||
            !image.Initialized || image.Texture == 0 || image.ContentVersion != 1 ||
            image.TextureType != MtlTextureType3D || image.Format != origin.Layout.NativeFormat.Format ||
            image.Width != origin.Layout.Width || image.Height != origin.Layout.Height || image.Depth != origin.Layout.Depth || image.ArrayLength != 1 ||
            !ReferenceEquals(image.StorageCpuLayout, origin.Layout) ||
            !ReferenceEquals(image.StorageCpuOrigin, origin) || origin.Session != CurrentStorageCpuSeedSession ||
            _guestImages.Count > 128 || !_guestImages.TryGetValue(origin.Layout.Address, out var current) ||
            !ReferenceEquals(current, image))
            throw new InvalidOperationException("Metal singleton volume has no exact original construction watch to transfer.");
        watch.Image = image; watch.Texture = image.Texture; watch.Version = image.ContentVersion;
        if (Interlocked.CompareExchange(ref watch.State, 1, 0) != 0)
            throw new InvalidOperationException("Metal singleton volume original watch has already been consumed.");
    }

    private static void NoteSingletonVolumeOriginalVersion(GuestImage image, string kind)
    {
        if (image.StorageCpuOrigin?.VolumeWatch is not { } watch) return;
        // Preserve each original guard and its evaluation order. Capture the
        // primary failing branch before setting the existing sticky veto.
        var reason = Volatile.Read(ref watch.State) != 1 ? "state" :
            !ReferenceEquals(watch.Image, image) ? "image" : image.Texture != watch.Texture ? "texture" :
            watch.Version == int.MaxValue ? "version-exhausted" :
            image.ContentVersion != watch.Version + 1 ? "version-step" : kind != "storage-dispatch" ? "producer-kind" :
            _executingGuestWorkSequence <= 0 ? "work-sequence" : !watch.IsCurrent ? "original-watch-not-current" : null;
        if (reason is not null)
        {
            if (_volumeOwnerDeclineDiagnostics && watch.DiagnosticUnknownProducerReason is null)
                watch.DiagnosticUnknownProducerReason = reason;
            Volatile.Write(ref watch.UnknownProducer, true); return;
        }
        watch.Version = image.ContentVersion; // exact known queued producer, never new CPU provenance
    }

    private static void ValidateSingletonVolumeOriginalCohortContent(StorageWatchReservation reservation = default)
    {
        StorageCpuSeedOrigin[] originals;
        lock (_gate)
        {
            if (_guestImages.Count > 128) throw new NotSupportedException("Singleton volume original cohort exceeds its finite bound.");
            originals = _guestImages.Values.Select(image => image.StorageCpuOrigin)
                .Where(origin => origin?.VolumeWatch is not null).Cast<StorageCpuSeedOrigin>().ToArray();
        }
        foreach (var original in originals) ValidateSingletonVolumeOriginalContentWithReservation(original, reservation);
    }

    internal static void ReleaseFailedPreparedSingletonVolumeOriginals(IReadOnlyList<GuestDrawTexture> textures,
        GuestDrawTexture? unreturned, Exception primary)
    {
        _ = ReleasePreparedSingletonVolumeOriginals(textures, primary);
        if (unreturned is not null && StorageCpuOrigin(unreturned)?.VolumeWatch is { } watch)
            _ = watch.Release(preparationOnly: true, primary);
    }

    private static Exception? ReleasePreparedSingletonVolumeOriginals(IReadOnlyList<GuestDrawTexture> textures, Exception? primary)
    {
        Exception? cleanup = null;
        foreach (var texture in textures)
            if (StorageCpuOrigin(texture)?.VolumeWatch is { } watch)
            {
                var later = watch.Release(preparationOnly: true, primary ?? cleanup);
                cleanup ??= later;
            }
        return cleanup;
    }

    private static void ReleaseSingletonVolumeNativeOriginals()
    {
        SingletonVolumeOriginalWatch[] watches;
        lock (_gate)
            watches = _singletonVolumeOriginalWatches.ToArray(); // includes untaken queued/private originals
        Exception? cleanup = null;
        foreach (var watch in watches)
        {
            var later = watch.Release(preparationOnly: false, cleanup);
            cleanup ??= later;
        }
        if (cleanup is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();
    }
}
