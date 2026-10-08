// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

public interface IHostMappedReadWatchMemory : IHostMappedCopyMemory
{
    CpuMappedPublicationPreparationResult TryPrepareMappedReadValidation(HostBackingObject backing,
        CpuMappedCopyObservation original, out CpuMappedReadValidation? validation)
    { validation = null; return CpuMappedPublicationPreparationResult.Unavailable; }
    CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(HostBackingObject backing,
        ulong footprintAddress, ulong footprintByteCount, ulong sourceAddress,
        Span<byte> destination, out CpuMappedCopyObservation? observation);
    bool IsMappedCopyWriteWatchCurrent(HostBackingObject backing, CpuMappedCopyObservation observation);
    CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(HostBackingObject backing,
        CpuMappedCopyObservation observation) => CpuMappedReadWatchStatus.Stale;
    void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation observation);
}
