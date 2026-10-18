// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE;

// Busy is a retry-only refusal. It authorizes no bytes, GPU work or content.
public enum CpuMappedReadWatchStatus { Stale = 0, Current, Busy }

// Detached SAME-query observations only. Unreached predicates remain null;
// none of these fields certify content, lifetime or permission authority.
public enum CpuMappedAdmissionOwnerKind { Unspecified, ReadWatchEnrollment, Publication, PublicationValidation, OriginalReadValidation }
public enum CpuMappedAdmissionOwnerPhase { Unspecified, Reserved, Prepared, Enrolling, Committing, Releasing, AdmissionReleased, Unknown }
public enum CpuMappedAdmissionOwnerOutcome { None, AdmissionReleased, Unknown }

// Detached scalars: no retained owner handle, wait capability or authority.
public readonly record struct CpuMappedAdmissionOwnerDiagnostic(long? Identity,
    CpuMappedAdmissionOwnerKind Kind, int? AcquiringThread, CpuMappedAdmissionOwnerPhase Phase,
    CpuMappedAdmissionOwnerOutcome Outcome, string? Refusal);

public readonly record struct CpuMappedReadWatchDiagnostic
{
    public string? MappingGuard { get; init; }
    public string? MappingCheck { get; init; }
    public string? WatchGuard { get; init; }
    public long? PrimaryAdmission { get; init; }
    public CpuMappedAdmissionOwnerDiagnostic? AdmissionOwner { get; init; }
    public string? ClassificationGuard { get; init; }
    public CpuMappedReadWatchStatus? WatchStatus { get; init; }
    public CpuMappedReadWatchStatus? ProviderStatus { get; init; }
    public bool? BooleanCurrent { get; init; }
    public string? SharedClosure { get; init; }
    public string? OwnerClosure { get; init; }
    public string? VirtualClosure { get; init; }
}

/// <summary>Optional copied-read with an ORIGINAL participating physical-writer
/// veto. It excludes neither native/JIT stores nor subsequent mapping effects.
/// No fresh observation may be substituted for an invalidated original watch.</summary>
public interface ICpuMappedReadWatchSource : ICpuMappedCopySource
{
    // Optional same-ORIGINAL scalar status waiting. Grants no enrollment, byte
    // copy, publication, retry or authority; unknown decorators refuse.
    bool CanWaitForMappedReadWatchStatus => false;

    // Optional cold PRE-BYTE admission wait. Unknown wrappers decline: no
    // observation or bytes may be obtained by retrying a fresh generic copy.
    bool CanWaitForMappedReadWatchEnrollment => false;
    CpuMappedCopyResult TryReadMappedCopyWithWriteWatchAdmissionWait(ulong footprintAddress,
        ulong footprintByteCount, ulong sourceAddress, Span<byte> destination,
        Func<bool> originalStillCurrent, out CpuMappedCopyObservation? observation)
    { observation = null; return CpuMappedCopyResult.Unavailable; }
    // Cancel-only exclusion for the SAME original watched source. It is not a
    // publication/disjoint-watch reservation and has no bytes or continuity.
    CpuMappedPublicationPreparationResult TryPrepareMappedReadValidation(
        CpuMappedCopyObservation original, out CpuMappedReadValidation? validation)
    { validation = null; return CpuMappedPublicationPreparationResult.Unavailable; }
    CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong footprintAddress,
        ulong footprintByteCount, ulong sourceAddress, Span<byte> destination,
        out CpuMappedCopyObservation? observation);
    // Bounded scalar check; no gate, allocation, query, callback or wait.
    bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation observation);
    bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation observation,
        out CpuMappedReadWatchDiagnostic diagnostic)
    {
        var current = IsMappedCopyWriteWatchCurrent(observation); // ONE original Boolean call, no status reclassification
        diagnostic = new() { WatchGuard = "source-boolean-unattributed", BooleanCurrent = current };
        return current;
    }
    CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(CpuMappedCopyObservation observation) => CpuMappedReadWatchStatus.Stale;
    CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(CpuMappedCopyObservation observation,
        out CpuMappedReadWatchDiagnostic diagnostic)
    {
        var status = GetMappedCopyWriteWatchStatus(observation); // ONE original call; never borrow Boolean currentness
        diagnostic = new() { WatchGuard = "source-status-unattributed", ProviderStatus = status };
        return status;
    }
    // Exact one-use bookkeeping release only, never native ownership release.
    void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation observation);
}
