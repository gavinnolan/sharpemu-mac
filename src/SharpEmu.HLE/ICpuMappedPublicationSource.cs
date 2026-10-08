// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE;

/// <summary>Optional ordinary bounded publication into the original provider-owned
/// mapping. This is separate from mapping-only IsCurrent, native guest write
/// exclusion, permission ownership, GPU completion and signal recovery.</summary>
public interface ICpuMappedPublicationSource
{
    /// <summary>Cancel-only reservation from the ORIGINAL mapping and physical
    /// writer baseline. It changes no bytes or revision and certifies no native
    /// guest writer completion. Unsupported providers decline.</summary>
    CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(
        CpuMappedCopyObservation original, out CpuMappedPublicationValidation? validation)
    { validation = null; return CpuMappedPublicationPreparationResult.Unavailable; }

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
    public void Dispose() => transaction.Cancel();
}

internal abstract class CpuMappedPublicationTransaction
{
    internal abstract CpuMappedPublicationResult TryCommit();
    internal abstract void Cancel();
    internal abstract CpuMappedCopyObservation? SuccessfulObservation { get; }
}

/// <summary>Opaque one-use ordinary validation reservation. No commit or byte
/// payload exists. The producer validates its original tracker/image observations
/// outside provider gates while mapping and participating writers are excluded.</summary>
public sealed class CpuMappedPublicationValidation : IDisposable
{
    private readonly CpuMappedPublicationValidationTransaction transaction;
    internal CpuMappedPublicationValidation(CpuMappedPublicationValidationTransaction transaction) => this.transaction = transaction;
    public bool IsCurrent => transaction.IsCurrent;
    public void Dispose() => transaction.Cancel();
}
internal abstract class CpuMappedPublicationValidationTransaction
{
    internal abstract bool IsCurrent { get; }
    internal abstract void Cancel();
}
