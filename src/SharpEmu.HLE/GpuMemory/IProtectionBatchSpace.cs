// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.GpuMemory;

/// <summary>
/// An address space whose protections that take rights away are slow to confirm (the two-process
/// split's GPU process asks the guest process and waits for its acknowledgement). A
/// <see cref="PageGuard"/> watch change brackets all of its protections with these calls, so the
/// space can send them all and wait once, after the guard released its block locks, instead of
/// once per protection under a lock.
/// </summary>
public interface IProtectionBatchSpace
{
    /// <summary>A watch change starts on this thread. Calls nest; only the outermost end waits.</summary>
    void BeginProtectionBatch();

    /// <summary>
    /// The watch change is over: every protection it asked for that takes rights away must be
    /// confirmed before this returns.
    /// </summary>
    void EndProtectionBatch();
}
