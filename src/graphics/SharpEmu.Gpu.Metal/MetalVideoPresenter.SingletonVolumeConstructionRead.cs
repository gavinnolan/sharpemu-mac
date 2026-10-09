// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private const int SingletonVolumeConstructionBusyMilliseconds = 500;
    private static Action? _singletonVolumeConstructionReadReleasedForControl = null;
    private static Action? _singletonVolumeConstructionBeforeFinalContentForControl = null;
    private static Action? _singletonVolumeConstructionBeforeFinalOwnerForControl = null;
    private static Action<CpuMappedReadValidation, CpuMappedCopyObservation>? _singletonVolumeConstructionReadPreparedForControl = null;
    private static Action? _singletonVolumeConstructionBusyForControl = null;

    // Only private ORIGINAL CPU preparation can acquire this exclusion. It
    // cannot refresh a live owner or retry an already encoded dispatch.
    private static SingletonVolumeConstructionRead? PrepareSingletonVolumeConstructionRead(GuestDrawTexture descriptor)
    {
        var origin = StorageCpuOrigin(descriptor);
        if (origin?.VolumeWatch is not { } watch || Volatile.Read(ref watch.State) != 0) return null;
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Volume construction must prepare outside presenter gates.");
        // An existing native owner is validated by the unchanged live lookup.
        // Its admission must never acquire or adopt a newly captured read.
        lock (_gate) if (_guestImages.ContainsKey(descriptor.Address)) return null;
        RequireSingletonVolumeConstructionOriginal(origin, descriptor);
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * SingletonVolumeConstructionBusyMilliseconds / 1000;
        while (true)
        {
            var result = watch.Source.TryPrepareMappedReadValidation(watch.Mapping, out var read);
            if (result == CpuMappedPublicationPreparationResult.Prepared && read is not null)
            {
                var scope = new SingletonVolumeConstructionRead(origin, read);
                try
                {
                    RequireSingletonVolumeConstructionOriginal(origin, descriptor);
                    ValidateSingletonVolumeOriginalContentWithReservation(origin, scope.Reservation);
                    if (!read.IsOriginalCurrent(watch.Mapping))
                        throw new InvalidOperationException("Volume construction ORIGINAL read reservation changed during admission.");
                    _singletonVolumeConstructionReadPreparedForControl?.Invoke(read, watch.Mapping);
                    return scope;
                }
                catch (Exception failure) { scope.Finish(failure); throw; }
            }
            // A declined provider must not leak a candidate ticket, nor does
            // Unsupported borrow a legacy bool. No retry after an unknown release.
            if (read is not null)
            {
                read.Dispose();
                throw new InvalidOperationException("Volume construction provider returned an unaccepted original read receipt.");
            }
            if (result != CpuMappedPublicationPreparationResult.Rejected ||
                watch.Source.GetMappedCopyWriteWatchStatus(watch.Mapping) != CpuMappedReadWatchStatus.Busy)
                throw new InvalidOperationException("Volume construction ORIGINAL read validation was refused.");
            RequireSingletonVolumeConstructionOriginal(origin, descriptor);
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("Volume construction ORIGINAL participating writer remained busy before native allocation.");
            Thread.Sleep(1); // no presenter/provider/PVM gate held; no bytes or native work admitted
        }
    }

    private static void RequireSingletonVolumeConstructionOriginal(StorageCpuSeedOrigin origin, GuestDrawTexture descriptor)
    {
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Volume construction content check requires a released presenter gate.");
        if (_closed || _guestWorkFailure is not null || _singletonVolumeOriginalCleanupFailure is not null ||
            !ReferenceEquals(_guestMemory, origin.Memory) || origin.Session != CurrentStorageCpuSeedSession ||
            origin.VolumeWatch is not { } watch || Volatile.Read(ref watch.State) != 0 || watch.Image is not null ||
            Volatile.Read(ref watch.UnknownProducer) || !origin.Layout.Matches(descriptor) ||
            origin.Mapping.FootprintAddress != origin.Layout.Address || origin.Mapping.FootprintByteCount != origin.Layout.PhysicalBytes ||
            origin.Mapping.SourceAddress != origin.Layout.Address || origin.Mapping.SourceByteCount != origin.Layout.PhysicalBytes ||
            !ReferenceEquals(watch.Mapping, origin.Mapping) || !origin.Source.IsMappedCopyObservationCurrent(origin.Mapping) ||
            !GuestImageWriteTracker.IsReadSnapshotStable(watch.Read) || !origin.Observation.Observed ||
            origin.Observation.ByteCount != origin.Layout.PhysicalBytes ||
            !GuestImageWriteTracker.TryGetProtectedObservedContentToken(origin.Layout.Address, origin.Layout.PhysicalBytes, out var token) ||
            token != origin.Observation.Token)
            throw new InvalidOperationException("Volume construction lost its exact ORIGINAL CPU preparation/session/mapping/token.");
    }

    private sealed class SingletonVolumeConstructionRead(StorageCpuSeedOrigin origin, CpuMappedReadValidation read)
    {
        internal StorageWatchReservation Reservation => new(read);
        private GuestImage? native;
        private bool consumed;
        internal void SetNativeOwner(GuestImage image) => native = image;

        internal void Finish(Exception? primary)
        {
            if (consumed) return;
            consumed = true; // a release failure is unknown, never retried
            Exception? cleanup = null;
            try { read.Dispose(); }
            catch (Exception failure)
            {
                cleanup = failure;
                Interlocked.CompareExchange(ref _singletonVolumeOriginalCleanupFailure, failure, null);
            }
            if (primary is null && cleanup is null && native is not null)
            {
                try
                {
                    _singletonVolumeConstructionReadReleasedForControl?.Invoke();
                    WaitForOrdinaryOriginalBeforeEncoding();
                }
                catch (Exception failure) { cleanup = failure; }
            }
            if (cleanup is not null)
            {
                if (origin.VolumeWatch is { } watch) Volatile.Write(ref watch.UnknownProducer, true);
                if (primary is null) ExceptionDispatchInfo.Capture(cleanup).Throw();
                try { primary.Data["SingletonVolumeConstructionReadCleanup"] = cleanup; } catch { }
            }
        }

        private void WaitForOrdinaryOriginalBeforeEncoding()
        {
            var watch = origin.VolumeWatch!;
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * SingletonVolumeConstructionBusyMilliseconds / 1000;
            while (true)
            {
                if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Volume construction cannot wait under presenter gates.");
                // Original token/mapping and exact transferred native owner
                // are mandatory on EVERY tick; Busy alone never supplies content.
                if (!OriginalTransferredObservationCurrent())
                    throw new InvalidOperationException("Volume construction ORIGINAL observation changed after reservation release.");
                lock (_gate)
                    if (!_guestImages.TryGetValue(origin.Layout.Address, out var current) || !ReferenceEquals(current, native) ||
                        native!.Texture == 0 || !ReferenceEquals(native.StorageCpuOrigin, origin) ||
                        !ReferenceEquals(watch.Image, native) || watch.Texture != native.Texture || watch.Version != native.ContentVersion ||
                        Volatile.Read(ref watch.State) != 1 || Volatile.Read(ref watch.UnknownProducer) ||
                        origin.Session != CurrentStorageCpuSeedSession || _closed || _guestWorkFailure is not null)
                        throw new InvalidOperationException("Volume construction ORIGINAL transferred owner changed before encoding.");
                var status = watch.Source.GetMappedCopyWriteWatchStatus(watch.Mapping);
                if (status == CpuMappedReadWatchStatus.Current)
                {
                    _singletonVolumeConstructionBeforeFinalContentForControl?.Invoke();
                    try { ValidateSingletonVolumeOriginalContent(origin); }
                    catch (InvalidOperationException failure)
                    {
                        // The writer may begin AFTER the status read. It may
                        // delay only this retained constructor, never hide any
                        // changed original token/mapping/session/health.
                        var originalBusy = false;
                        try
                        {
                            originalBusy = OriginalTransferredObservationCurrent() &&
                                watch.Source.GetMappedCopyWriteWatchStatus(watch.Mapping) == CpuMappedReadWatchStatus.Busy;
                        }
                        catch { ExceptionDispatchInfo.Capture(failure).Throw(); }
                        if (!originalBusy) throw;
                        status = CpuMappedReadWatchStatus.Busy;
                    }
                    if (status == CpuMappedReadWatchStatus.Current)
                    {
                        _singletonVolumeConstructionBeforeFinalOwnerForControl?.Invoke();
                        var accepted = false;
                        lock (_gate)
                        {
                            if (OriginalTransferredOwnerIdentityLocked() && IsSingletonVolumeNativeOwnerCurrentLocked(native!, origin))
                                accepted = true;
                            else if (!OriginalTransferredOwnerIdentityLocked() ||
                                watch.Source.GetMappedCopyWriteWatchStatus(watch.Mapping) != CpuMappedReadWatchStatus.Busy)
                                throw new InvalidOperationException("Volume construction ORIGINAL owner changed at its final encoding boundary.");
                        }
                        if (accepted) return; // ordinary guard succeeds; no disposed-ticket borrowing
                        // Token/mapping checks remain outside the gate. Busy
                        // never turns a failed strict owner check into success.
                        if (!OriginalTransferredObservationCurrent())
                            throw new InvalidOperationException("Volume construction ORIGINAL observation changed at its final encoding boundary.");
                        status = CpuMappedReadWatchStatus.Busy;
                    }
                }
                if (status != CpuMappedReadWatchStatus.Busy)
                    throw new InvalidOperationException("Volume construction ORIGINAL watch became stale before encoding.");
                _singletonVolumeConstructionBusyForControl?.Invoke();
                if (Stopwatch.GetTimestamp() >= deadline)
                    throw new TimeoutException("Volume construction ORIGINAL participating writer remained busy before shader encoding.");
                Thread.Sleep(1);
            }
        }

        private bool OriginalTransferredObservationCurrent()
        {
            var watch = origin.VolumeWatch!;
            return !_closed && _guestWorkFailure is null && _singletonVolumeOriginalCleanupFailure is null &&
                ReferenceEquals(_guestMemory, origin.Memory) && origin.Session == CurrentStorageCpuSeedSession &&
                origin.Source.IsMappedCopyObservationCurrent(origin.Mapping) &&
                GuestImageWriteTracker.IsReadSnapshotStable(watch.Read) && origin.Observation.Observed &&
                GuestImageWriteTracker.TryGetProtectedObservedContentToken(origin.Layout.Address, origin.Layout.PhysicalBytes, out var token) &&
                token == origin.Observation.Token;
        }

        private bool OriginalTransferredOwnerIdentityLocked()
        {
            var watch = origin.VolumeWatch!;
            return !_closed && _guestWorkFailure is null && _singletonVolumeOriginalCleanupFailure is null &&
                ReferenceEquals(_guestMemory, origin.Memory) && origin.Session == CurrentStorageCpuSeedSession &&
                _guestImages.TryGetValue(origin.Layout.Address, out var current) && ReferenceEquals(current, native) &&
                native is not null && native.Texture != 0 && native.Initialized &&
                ReferenceEquals(native.StorageCpuOrigin, origin) && ReferenceEquals(native.StorageCpuLayout, origin.Layout) &&
                ReferenceEquals(watch.Image, native) && watch.Texture == native.Texture && watch.Version == native.ContentVersion &&
                Volatile.Read(ref watch.State) == 1 && !Volatile.Read(ref watch.UnknownProducer) &&
                native.TextureType == MtlTextureType3D && native.Depth == origin.Layout.Depth && native.ArrayLength == 1 &&
                native.Width == origin.Layout.Width && native.Height == origin.Layout.Height && native.Format == origin.Layout.NativeFormat.Format;
        }
    }
}
