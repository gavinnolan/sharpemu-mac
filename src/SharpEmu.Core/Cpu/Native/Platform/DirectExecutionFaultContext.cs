// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Native.Platform;

/// <summary>
/// The small amount of per-fault state that a per-OS fault bridge publishes and
/// the shared recovery chain reads.
/// </summary>
/// <remarks>
/// A plain static with a <c>[ThreadStatic]</c> field rather than anything on
/// <see cref="IDirectExecutionPlatform"/>: both sides touch it from inside a
/// fault handler, where a virtual call is not allowed. Static field access
/// compiles to a direct load with no allocation and no lock.
/// </remarks>
public static class DirectExecutionFaultContext
{
    [ThreadStatic]
    private static bool _vectorRegistersBridged;

    /// <summary>
    /// Set by a per-OS fault bridge when the Win64 CONTEXT it built carries the
    /// faulting thread's live vector registers, so instruction recovery may
    /// change them and have the change take effect. Cleared when it could not
    /// locate them in the platform's fault context.
    /// </summary>
    public static bool VectorRegistersBridged
    {
        get => _vectorRegistersBridged;
        set => _vectorRegistersBridged = value;
    }

    /// <summary>
    /// Whether the fault context the recovery chain is looking at has usable
    /// vector registers: either the platform always provides them, or this
    /// fault's bridge managed to map them in.
    /// </summary>
    public static bool HasVectorRegisters(DirectExecutionCapabilities capabilities) =>
        (capabilities & DirectExecutionCapabilities.FaultContextCarriesVectorRegisters) != 0 ||
        _vectorRegistersBridged;
}
