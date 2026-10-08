// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE;

/// <summary>Optional copied-read with an ORIGINAL participating physical-writer
/// veto. It excludes neither native/JIT stores nor subsequent mapping effects.
/// No fresh observation may be substituted for an invalidated original watch.</summary>
public interface ICpuMappedReadWatchSource : ICpuMappedCopySource
{
    CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong footprintAddress,
        ulong footprintByteCount, ulong sourceAddress, Span<byte> destination,
        out CpuMappedCopyObservation? observation);
    // Bounded scalar check; no gate, allocation, query, callback or wait.
    bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation observation);
    // Exact one-use bookkeeping release only, never native ownership release.
    void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation observation);
}
