// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Native.Platform;

/// <summary>
/// The shared fault-recovery entry points a per-OS execution platform installs
/// its fault interception in front of.
/// </summary>
/// <remarks>
/// Prepublished managed function pointers to static recovery entries, not
/// delegates or interface members: a fault handler runs on an arbitrary thread
/// in an arbitrary runtime state, including inside the garbage collector, and
/// must not allocate, take a lock or make a virtual call that could need the
/// JIT. POSIX keeps the existing direct managed call after its native signal
/// entry; Windows creates its existing reverse-P/Invoke delegate thunk during
/// installation. No callable is created while handling a fault.
///
/// <para>Every entry takes a Win64 <c>EXCEPTION_POINTERS*</c> and returns an
/// exception disposition: <c>-1</c> means the fault was resolved and the thread
/// may resume with the (possibly edited) context, anything else means it was
/// not. A POSIX platform rebuilds that Win64 view from its signal context, so
/// the recovery logic itself stays shared and single-sourced.</para>
/// </remarks>
public readonly unsafe struct DirectExecutionFaultHandlers
{
    /// <summary>
    /// First-chance recovery that must run before any managed recovery:
    /// unresolved-import trap sentinels. Allocation-free by construction.
    /// </summary>
    public required delegate* managed<void*, int> RawRecovery { get; init; }

    /// <summary>
    /// The full shared recovery chain (sentinels, demand paging of lazily
    /// committed guest pages, GPU and image write tracking, instruction
    /// emulation, fault diagnostics).
    /// </summary>
    public required delegate* managed<void*, int> Recovery { get; init; }

    /// <summary>
    /// Last-resort report for a fault nothing recovered, where the platform has
    /// such a hook. Always returns 0 (do not handle).
    /// </summary>
    public required delegate* managed<void*, int> UnhandledReport { get; init; }

    /// <summary>Runs shared recovery warm-up before OS handlers are installed.</summary>
    public required delegate* managed<void> WarmUp { get; init; }

    /// <summary>
    /// Queries normalized guest stack bounds for Windows exception resumption.
    /// Published before interception starts, so no interface dispatch is needed.
    /// </summary>
    public required delegate* managed<ulong, ulong*, ulong*, bool> TryGetStackBounds { get; init; }

    /// <summary>
    /// Native TLS slot holding each thread's saved host stack pointer from when
    /// it entered guest code, so a handler that must not run on the guest stack
    /// can find the host stack. <see cref="uint.MaxValue"/> while unknown.
    /// </summary>
    public required uint HostRspSlotTlsIndex { get; init; }

    /// <summary>
    /// Win64-ABI <c>TlsGetValue</c> address an emitted handler prologue may call
    /// to read <see cref="HostRspSlotTlsIndex"/> without entering managed code.
    /// </summary>
    public required nint TlsGetValueAddress { get; init; }

    /// <summary>
    /// Emitted stub a native guest worker is redirected to when it faults
    /// unrecoverably, instead of taking the whole process down. 0 when the
    /// engine built none.
    /// </summary>
    public required nint WorkerAbortStub { get; init; }

    /// <summary>
    /// Native TLS slot marking a thread as eligible for the
    /// <see cref="WorkerAbortStub"/> redirect. <see cref="uint.MaxValue"/> when
    /// the platform runs no native guest workers.
    /// </summary>
    public required uint WorkerAbortEligibleTlsIndex { get; init; }
}
