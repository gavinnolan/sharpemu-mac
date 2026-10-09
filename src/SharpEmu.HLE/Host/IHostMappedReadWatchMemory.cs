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
    bool IsMappedCopyWriteWatchCurrent(HostBackingObject backing, CpuMappedCopyObservation observation,
        out CpuMappedReadWatchDiagnostic diagnostic)
    {
        var current = IsMappedCopyWriteWatchCurrent(backing, observation);
        diagnostic = new() { WatchGuard = "host-boolean-unattributed", BooleanCurrent = current };
        return current;
    }
    CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(HostBackingObject backing,
        CpuMappedCopyObservation observation) => CpuMappedReadWatchStatus.Stale;
    CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(HostBackingObject backing,
        CpuMappedCopyObservation observation, out CpuMappedReadWatchDiagnostic diagnostic)
    {
        var status = GetMappedCopyWriteWatchStatus(backing, observation);
        diagnostic = new() { WatchGuard = "host-status-unattributed", ProviderStatus = status };
        return status;
    }
    void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation observation);
}
