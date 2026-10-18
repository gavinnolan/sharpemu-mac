// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

/// <summary>Optional ordinary-thread copy through this provider's canonical backing alias.
/// This is mapping/lifetime validation, not native permission or content certification.</summary>
public interface IHostMappedCopyMemory
{
    CpuMappedCopyResult TryReadMappedCopy(HostBackingObject backing, ulong footprintAddress,
        ulong footprintByteCount, ulong sourceAddress, Span<byte> destination,
        out CpuMappedCopyObservation? observation);

    bool IsMappedCopyObservationCurrent(HostBackingObject backing, CpuMappedCopyObservation observation);
}
