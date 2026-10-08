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
        internal bool IsCurrent(SingletonVolumeOriginalWatch watch) =>
            Volatile.Read(ref watch.State) != 2 && !Volatile.Read(ref watch.UnknownProducer) &&
            (originalRead is not null ? originalRead.IsOriginalCurrent(watch.Mapping) :
                watch.Source.IsMappedCopyWriteWatchCurrent(watch.Mapping) ||
                publication?.IsOriginalDisjointReadWatchCurrent(watch.Mapping) == true ||
                validation?.IsOriginalDisjointReadWatchCurrent(watch.Mapping) == true);
    }
}
