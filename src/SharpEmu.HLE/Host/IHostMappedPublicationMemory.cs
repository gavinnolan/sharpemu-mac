// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

/// <summary>Optional provider-owned short publication contract. Source identity
/// and offsets resolve only from an original opaque mapping observation.</summary>
public interface IHostMappedPublicationMemory
{
    // An unknown wrapper cannot authorize an ordinary wait under hidden gates.
    // Known providers check their CURRENT calling-thread native mapping gates.
    bool CanWaitForOrdinaryWriteAdmission => false;
    HostMappedCopyLedger.OrdinaryWriteIdentity? CaptureOrdinaryWriteIdentity(
        HostBackingObject backing, ulong address, ulong size) => null;

    CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(
        HostBackingObject backing, CpuMappedCopyObservation original, out CpuMappedPublicationValidation? validation)
    { validation = null; return CpuMappedPublicationPreparationResult.Unavailable; }

    CpuMappedPublicationPreparationResult TryPrepareMappedPublication(
        HostBackingObject backing, CpuMappedCopyObservation original,
        IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication);
}
