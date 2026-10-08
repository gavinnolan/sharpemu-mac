// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE;

/// <summary>One-use cancel-only exclusion for one exact ORIGINAL watched read.
/// No bytes, mapping/content recapture, revision advance, write entitlement or
/// native guest/JIT writer completion is provided by this receipt.</summary>
public sealed class CpuMappedReadValidation : IDisposable
{
    private readonly CpuMappedReadValidationTransaction transaction;
    internal CpuMappedReadValidation(CpuMappedReadValidationTransaction transaction) => this.transaction = transaction;
    // Scalar ORIGINAL identity/incarnation/watch check only. Never a new query
    // or provider gate acquisition while the consumer holds its metadata gate.
    public bool IsOriginalCurrent(CpuMappedCopyObservation original) => transaction.IsOriginalCurrent(original);
    public void Dispose() => transaction.Cancel();
}

internal abstract class CpuMappedReadValidationTransaction
{
    internal abstract bool IsOriginalCurrent(CpuMappedCopyObservation original);
    internal abstract void Cancel();
}
