// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

/// <summary>Optional provider-owned short publication contract. Source identity
/// and offsets resolve only from an original opaque mapping observation.</summary>
public interface IHostMappedPublicationMemory
{
    CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(
        HostBackingObject backing, CpuMappedCopyObservation original, out CpuMappedPublicationValidation? validation)
    { validation = null; return CpuMappedPublicationPreparationResult.Unavailable; }

    CpuMappedPublicationPreparationResult TryPrepareMappedPublication(
        HostBackingObject backing, CpuMappedCopyObservation original,
        IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication);
}
