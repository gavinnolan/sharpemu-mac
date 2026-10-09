// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE;

/// <summary>Optional ordinary bounded publication into the original provider-owned
/// mapping. This is separate from mapping-only IsCurrent, native guest write
/// exclusion, permission ownership, GPU completion and signal recovery.</summary>
public interface ICpuMappedPublicationSource
{
    // Unknown wrappers cannot authorize waiting under hidden inherited gates.
    bool CanWaitForMappedPublicationValidation => false;

    /// <summary>Cancel-only reservation from the ORIGINAL mapping and physical
    /// writer baseline. It changes no bytes or revision and certifies no native
    /// guest writer completion. Unsupported providers decline.</summary>
    CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(
        CpuMappedCopyObservation original, out CpuMappedPublicationValidation? validation)
    { validation = null; return CpuMappedPublicationPreparationResult.Unavailable; }

    /// <summary>Optional exact first-return classification. Busy is a refusal,
    /// NOT copied history/content authority. Unknown participants stay strict.</summary>
    CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(
        CpuMappedCopyObservation original, out CpuMappedPublicationValidation? validation,
        out CpuMappedPublicationValidationRefusal refusal)
    {
        refusal = default;
        return TryPrepareMappedPublicationValidation(original, out validation);
    }

    /// <summary>Resolve the original opaque observation and detached byte runs,
    /// acquire exact mapping/backing lifetime and participating-writer exclusion,
    /// then return a one-use transaction. No bytes change during preparation.
    /// The caller validates its own original content/native-owner observations
    /// while the transaction is held, outside all provider/mapping gates.</summary>
    CpuMappedPublicationPreparationResult TryPrepareMappedPublication(
        CpuMappedCopyObservation original, IReadOnlyList<CpuMappedWriteRun> runs,
        out CpuMappedPublication? publication);
}

/// <summary>Detached source bytes for an exact original virtual subrange. The
/// provider clones bounded input before admission; these scalar values never
/// substitute for the original opaque provider/backing observation.</summary>
public readonly record struct CpuMappedWriteRun(ulong Address, ReadOnlyMemory<byte> Bytes);

public enum CpuMappedPublicationPreparationResult
{
    Unavailable = 0,
    Rejected,
    Prepared,
}

// Detached first-refusal provenance; no constructor/fields confer authority.
public enum CpuMappedPublicationValidationRefusalKind { UnknownRejected = 0, AdmissionBusy }
public readonly record struct CpuMappedPublicationValidationRefusal(
    CpuMappedPublicationValidationRefusalKind Kind, long OriginalRevision, long RejectedAdmission);

public enum CpuMappedPublicationResult
{
    Rejected = 0,
    Applied,
    PartialUnknown,
}

/// <summary>Opaque one-use provider transaction. No public constructor, pointer,
/// mutable native receipt or caller-generated outcome. Commit writes only its
/// prepared detached runs outside provider gates; Dispose cancels an operation
/// that has not begun. Neither action releases an uncertain in-flight operation.</summary>
public sealed class CpuMappedPublication : IDisposable
{
    private readonly CpuMappedPublicationTransaction transaction;
    internal CpuMappedPublication(CpuMappedPublicationTransaction transaction) => this.transaction = transaction;
    public CpuMappedPublicationResult TryCommit() => transaction.TryCommit();
    /// <summary>Provider-owned continuity from this exact Applied transaction.
    /// Mapping identities are the unchanged original ones; only participating
    /// writer history advances for this known own write. No content token,
    /// external/native writer coverage or mapping recapture is certified.</summary>
    public CpuMappedCopyObservation? SuccessfulObservation => transaction.SuccessfulObservation;
    /// <summary>Scalar check of an ORIGINAL disjoint read watch while this exact
    /// prepared ticket owns participating-writer exclusion. No new watch/content
    /// observation is minted; committing or consumed tickets decline.</summary>
    public bool IsOriginalDisjointReadWatchCurrent(CpuMappedCopyObservation original) =>
        transaction.IsOriginalDisjointReadWatchCurrent(original);
    public void Dispose() => transaction.Cancel();
}

internal abstract class CpuMappedPublicationTransaction
{
    internal abstract CpuMappedPublicationResult TryCommit();
    internal virtual bool IsOriginalDisjointReadWatchCurrent(CpuMappedCopyObservation original) => false;
    internal abstract void Cancel();
    internal abstract CpuMappedCopyObservation? SuccessfulObservation { get; }
}

// Detached FIRST local proof attribution only; no handle, recapture or wait
// authority. Nullable attribution fields remain unset when no corresponding
// refusing lower guard supplies a value.
public readonly record struct CpuMappedDisjointReadWatchDiagnostic
{
    public string? Guard { get; init; }
    public string? MappingGuard { get; init; }
    public string? WatchGuard { get; init; }
    // Actual held-watch admission only when that scalar check refuses.
    public long? PrimaryAdmission { get; init; }
    public long? LocalAdmissionOwnerIdentity { get; init; }
    public long? ReservedBackingIdentity { get; init; }
    public long? WatchedBackingIdentity { get; init; }
    public ulong? WatchedOffset { get; init; }
    public ulong? WatchedCount { get; init; }
    public ulong? ReservedOffset { get; init; }
    public ulong? ReservedCount { get; init; }
    public bool? BooleanCurrent { get; init; }
}

/// <summary>Opaque one-use ordinary validation reservation. No commit or byte
/// payload exists. The producer validates its original tracker/image observations
/// outside provider gates while mapping and participating writers are excluded.</summary>
public sealed class CpuMappedPublicationValidation : IDisposable
{
    private readonly CpuMappedPublicationValidationTransaction transaction;
    internal CpuMappedPublicationValidation(CpuMappedPublicationValidationTransaction transaction) => this.transaction = transaction;
    public bool IsCurrent => transaction.IsCurrent;
    /// <summary>Scalar check of an ORIGINAL disjoint read watch while this exact
    /// prepared ticket owns participating-writer exclusion. No new watch/content
    /// observation is minted; committing or consumed tickets decline.</summary>
    public bool IsOriginalDisjointReadWatchCurrent(CpuMappedCopyObservation original) =>
        transaction.IsOriginalDisjointReadWatchCurrent(original);
    public bool IsOriginalDisjointReadWatchCurrent(CpuMappedCopyObservation original,
        out CpuMappedDisjointReadWatchDiagnostic diagnostic) =>
        transaction.IsOriginalDisjointReadWatchCurrent(original, out diagnostic);
    public void Dispose() => transaction.Cancel();
}
internal abstract class CpuMappedPublicationValidationTransaction
{
    internal abstract bool IsCurrent { get; }
    internal virtual bool IsOriginalDisjointReadWatchCurrent(CpuMappedCopyObservation original) => false;
    internal virtual bool IsOriginalDisjointReadWatchCurrent(CpuMappedCopyObservation original,
        out CpuMappedDisjointReadWatchDiagnostic diagnostic)
    {
        var current = IsOriginalDisjointReadWatchCurrent(original); // ONE exact original call
        diagnostic = new() { Guard = "provider-unattributed", BooleanCurrent = current };
        return current;
    }
    internal abstract void Cancel();
}
