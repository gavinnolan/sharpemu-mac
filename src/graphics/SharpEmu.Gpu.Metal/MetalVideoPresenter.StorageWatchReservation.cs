// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Stack-only context threaded from the exact local provider ticket. It
    // never replaces ordinary admission/currentness or acquires a provider gate.
    private readonly struct StorageWatchReservation
    {
        private readonly CpuMappedPublication? publication;
        private readonly CpuMappedPublicationValidation? validation;
        private readonly CpuMappedReadValidation? originalRead;
        internal StorageWatchReservation(CpuMappedPublication publication) => this.publication = publication;
        internal StorageWatchReservation(CpuMappedPublicationValidation validation) => this.validation = validation;
        internal StorageWatchReservation(CpuMappedReadValidation originalRead) => this.originalRead = originalRead;
        // Classification uses the status of the actual refusing check. A
        // writer can finish before a later scalar query; that must not turn a
        // healthy Busy refusal into an unrelated fatal error. This still
        // grants Current only through the same exact local reservation.
        internal CpuMappedReadWatchStatus GetStatus(SingletonVolumeOriginalWatch watch)
        {
            if (Volatile.Read(ref watch.State) == 2 || Volatile.Read(ref watch.UnknownProducer))
                return CpuMappedReadWatchStatus.Stale;
            if (originalRead is not null)
                return originalRead.IsOriginalCurrent(watch.Mapping) ? CpuMappedReadWatchStatus.Current : CpuMappedReadWatchStatus.Stale;
            var status = watch.Source.GetMappedCopyWriteWatchStatus(watch.Mapping);
            if (status == CpuMappedReadWatchStatus.Current ||
                publication?.IsOriginalDisjointReadWatchCurrent(watch.Mapping) == true ||
                validation?.IsOriginalDisjointReadWatchCurrent(watch.Mapping) == true)
                return CpuMappedReadWatchStatus.Current;
            return status;
        }
        internal bool IsCurrent(SingletonVolumeOriginalWatch watch) =>
            Volatile.Read(ref watch.State) != 2 && !Volatile.Read(ref watch.UnknownProducer) &&
            (originalRead is not null ? originalRead.IsOriginalCurrent(watch.Mapping) :
                watch.Source.IsMappedCopyWriteWatchCurrent(watch.Mapping) ||
                publication?.IsOriginalDisjointReadWatchCurrent(watch.Mapping) == true ||
                validation?.IsOriginalDisjointReadWatchCurrent(watch.Mapping) == true);
    }
}
