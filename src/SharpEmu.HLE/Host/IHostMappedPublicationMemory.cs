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

    // Diagnostic-only overload: legacy participants call the same original
    // capture once and explicitly leave their internal refusal unclassified.
    HostMappedCopyLedger.OrdinaryWriteIdentity? CaptureOrdinaryWriteIdentity(
        HostBackingObject backing, ulong address, ulong size, out string? firstRefusal)
    {
        var original = CaptureOrdinaryWriteIdentity(backing, address, size);
        firstRefusal = original.HasValue ? null : "provider-capture-unclassified";
        return original;
    }

    CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(
        HostBackingObject backing, CpuMappedCopyObservation original, out CpuMappedPublicationValidation? validation)
    { validation = null; return CpuMappedPublicationPreparationResult.Unavailable; }

    CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(
        HostBackingObject backing, CpuMappedCopyObservation original, out CpuMappedPublicationValidation? validation,
        out CpuMappedPublicationValidationRefusal refusal)
    {
        refusal = default;
        return TryPrepareMappedPublicationValidation(backing, original, out validation);
    }

    CpuMappedPublicationPreparationResult TryPrepareMappedPublication(
        HostBackingObject backing, CpuMappedCopyObservation original,
        IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication);

    // Diagnostic only: legacy providers remain Unknown and execute once.
    CpuMappedPublicationPreparationResult TryPrepareMappedPublication(
        HostBackingObject backing, CpuMappedCopyObservation original,
        IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication,
        out CpuMappedPublicationPreparationRefusal refusal)
    {
        refusal = default;
        return TryPrepareMappedPublication(backing, original, runs, out publication);
    }
}
