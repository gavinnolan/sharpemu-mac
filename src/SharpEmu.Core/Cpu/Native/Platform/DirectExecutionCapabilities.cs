// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Native.Platform;

/// <summary>
/// What an execution platform supports. These replace the
/// <c>OperatingSystem.Is*</c> policy checks that the shared direct-execution
/// engine used to make: the engine asks what the platform can do, and the
/// per-OS package answers for itself.
/// </summary>
/// <remarks>
/// Each flag names a behaviour, not an operating system. Several are currently
/// set only by <c>SharpEmu.Cpu.DirectExecution.Windows.X64</c>, which is a
/// statement about what that integration implements today, not a licence for
/// shared code to spell the flag "Windows".
/// </remarks>
[Flags]
public enum DirectExecutionCapabilities
{
    None = 0,

    /// <summary>
    /// The platform delivers faults to a first-chance handler that already
    /// receives a Win64 <c>EXCEPTION_POINTERS</c>/<c>CONTEXT</c> pair, so the
    /// shared recovery chain runs without a context conversion. POSIX
    /// integrations rebuild the same view inside their signal bridge instead,
    /// and therefore do not set this.
    /// </summary>
    VectoredFaultDispatch = 1 << 0,

    /// <summary>
    /// Per-thread stack bounds live in a thread block that emitted code can
    /// read and swap (the Windows TEB at <c>gs:[8]</c>/<c>gs:[16]</c>), so the
    /// engine can tell the runtime which stack guest code is using. Platforms
    /// without it keep the guest stack invisible to the runtime and move the
    /// fault handler onto the host stack instead.
    /// </summary>
    SwappableThreadStackBounds = 1 << 1,

    /// <summary>
    /// Guest worker threads can run the emitted native wait loop
    /// (<c>NativeGuestExecutor</c>).
    /// </summary>
    NativeGuestWorkers = 1 << 2,

    /// <summary>
    /// The emitted worker wait loop uses the platform's own event handles
    /// rather than the worker-event primitives on this interface.
    /// </summary>
    Win64WorkerEventHandles = 1 << 3,

    /// <summary>
    /// Diagnostics can suspend another thread, snapshot its registers into a
    /// Win64 CONTEXT record and resume it as one operation.
    /// </summary>
    ThreadContextCapture = 1 << 4,

    /// <summary>
    /// Import stubs can read guest TLS from emitted code, so the managed
    /// gateway is not needed on the fast path.
    /// </summary>
    EmittedTlsFastPath = 1 << 5,

    /// <summary>
    /// Managed callbacks are already compiled for the Win64 ABI the emitted
    /// call sites use, so no Win64-to-SysV thunk is needed.
    /// </summary>
    Win64ManagedCallbackAbi = 1 << 6,

    /// <summary>
    /// The fault context handed to the recovery chain always carries the
    /// thread's live vector registers. Where it does not, the per-OS fault
    /// bridge reports per fault whether it managed to bridge them (see
    /// <see cref="DirectExecutionFaultContext.VectorRegistersBridged"/>).
    /// </summary>
    FaultContextCarriesVectorRegisters = 1 << 7,

    /// <summary>
    /// An <c>INT n</c> the guest may not execute arrives as a
    /// general-protection fault reported as an access violation with an
    /// all-ones fault address, which lets the engine skip the opcode probe for
    /// ordinary page faults. Platforms without it must probe every fault.
    /// </summary>
    GeneralProtectionFaultsReportAllOnesAddress = 1 << 8,

    /// <summary>
    /// Guest RIP sampling can suspend guest threads to sample them.
    /// </summary>
    GuestRipSampling = 1 << 9,

    /// <summary>
    /// Host region queries are cheap enough to run per import argument read.
    /// Platforms without it cache the last readable range per thread.
    /// </summary>
    CheapHostRegionQueries = 1 << 10,

    /// <summary>
    /// A stray read of an inaccessible address surfaces as a managed exception
    /// a <c>catch</c> can absorb. Where it does not — because the engine's own
    /// signal handler owns SIGSEGV — a probe must consult the region table
    /// instead, or the nested fault kills the process mid-diagnostic.
    /// </summary>
    StrayReadsSurfaceAsManagedExceptions = 1 << 11,
}
