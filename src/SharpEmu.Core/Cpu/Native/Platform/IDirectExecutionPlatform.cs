// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Core.Cpu.Native.Platform;

/// <summary>
/// The OS integration the direct-execution engine needs: fault interception,
/// native TLS, the Win64-ABI helper routines emitted code calls, raw thread
/// primitives, stack-bounds handling and host-memory probing.
/// </summary>
/// <remarks>
/// This is the W10 seam. One implementation per OS lives in
/// <c>src/platforms/&lt;os&gt;/SharpEmu.Cpu.DirectExecution.&lt;OS&gt;.X64</c> and is
/// registered at startup through <see cref="DirectExecutionPlatform"/>; shared
/// code never names a concrete implementation, and a macOS build never carries
/// the Windows or Linux one.
///
/// <para>Scope rule: a member belongs here when its implementation differs by
/// OS in a way that is not expressible as data — a native declaration, a struct
/// layout, a calling convention, a kernel interface. Policy that happens to
/// differ today belongs in <see cref="DirectExecutionCapabilities"/> instead, and
/// the shared engine keeps deciding what to do with it.</para>
///
/// <para>Nothing on this interface may be called from a fault handler: the
/// handler path uses the raw function pointers in
/// <see cref="DirectExecutionFaultHandlers"/>, because an interface call can need
/// the JIT.</para>
/// </remarks>
public unsafe interface IDirectExecutionPlatform
{
    /// <summary>Short identity for logs and diagnostics, e.g. "macos-x64".</summary>
    string Name { get; }

    DirectExecutionCapabilities Capabilities { get; }

    // ---------------------------------------------------------------------
    // Guest address-space policy
    // ---------------------------------------------------------------------

    /// <summary>
    /// Base of the window guest thread stacks are mapped into. Platform-owned
    /// because what is already taken differs: the dyld shared cache and the
    /// Rosetta 2 runtime occupy ranges Windows leaves free.
    /// </summary>
    ulong GuestThreadStackBaseAddress { get; }

    /// <summary>Base of the window guest thread TLS blocks are mapped into.</summary>
    ulong GuestThreadTlsBaseAddress { get; }

    // ---------------------------------------------------------------------
    // Native TLS
    // ---------------------------------------------------------------------

    /// <summary>Allocates a native TLS slot; <see cref="uint.MaxValue"/> on failure.</summary>
    uint AllocateTlsSlot();

    bool FreeTlsSlot(uint slot);

    bool SetTlsValue(uint slot, nint value);

    nint GetTlsValue(uint slot);

    // ---------------------------------------------------------------------
    // Win64-ABI helper routines that emitted guest stubs call
    // ---------------------------------------------------------------------

    /// <summary>
    /// Address of a routine the emitted stubs call with the Win64 convention
    /// (first argument in ECX, result in RAX, Win64 non-volatile registers
    /// preserved). Returns 0 when this platform has no such routine.
    /// </summary>
    nint GetRuntimeRoutine(HostRuntimeFunction routine);

    /// <summary>
    /// Makes a managed callback callable with the Win64 ABI the emitted call
    /// sites use. Identity where managed code already uses that ABI; a
    /// Win64-to-SysV thunk where it does not.
    /// </summary>
    nint ResolveWin64CallbackAddress(nint managedCallback);

    // ---------------------------------------------------------------------
    // Raw thread primitives
    // ---------------------------------------------------------------------

    uint CurrentThreadId { get; }

    /// <summary>Pseudo-handle for the calling thread, or 0 where there is none.</summary>
    nint CurrentThreadHandle { get; }

    /// <summary>
    /// Pins a thread to <paramref name="affinityMask"/>; returns the previous
    /// mask, or 0 on failure. Returns 1 where affinity is not controllable.
    /// </summary>
    nuint TrySetThreadAffinity(nint threadHandle, nuint affinityMask);

    /// <summary>Opens a thread for context capture; 0 when unsupported.</summary>
    nint OpenThreadForContext(uint threadId);
    uint GetThreadId(nint threadHandle);
    bool CloseThreadHandle(nint threadHandle);

    /// <summary>Starts an emitted native worker; 0 when unsupported.</summary>
    nint CreateNativeWorker(nuint stackReservation, nint entry, out uint threadId);
    uint WaitNativeWorker(nint threadHandle, uint timeoutMilliseconds);
    bool TerminateNativeWorker(nint threadHandle, uint exitCode);

    // ---------------------------------------------------------------------
    // Worker events for the emitted guest worker wait loop
    // ---------------------------------------------------------------------

    /// <summary>
    /// Creates a binary event signalable and waitable both from managed code
    /// and from emitted native code (through
    /// <see cref="HostRuntimeFunction.WaitForSingleObject"/> and
    /// <see cref="HostRuntimeFunction.SetEvent"/>). Returns 0 on failure, and 0
    /// unconditionally on platforms that set
    /// <see cref="DirectExecutionCapabilities.Win64WorkerEventHandles"/> and use
    /// their own handles instead.
    /// </summary>
    nint CreateWorkerEvent();

    bool SignalWorkerEvent(nint handle);

    /// <summary>Waits for the event; a negative timeout waits forever.</summary>
    bool WaitWorkerEvent(nint handle, int timeoutMilliseconds);

    void DestroyWorkerEvent(nint handle);

    // ---------------------------------------------------------------------
    // Fault interception
    // ---------------------------------------------------------------------

    /// <summary>
    /// Installs this platform's process-wide fault interception in front of the
    /// shared recovery entries in <paramref name="handlers"/>, chaining to
    /// whatever was installed before so the .NET runtime keeps turning its own
    /// faults into managed exceptions. Idempotent: a second call republishes
    /// the handlers without reinstalling.
    /// </summary>
    void InstallFaultHandling(in DirectExecutionFaultHandlers handlers);

    /// <summary>
    /// Removes what <see cref="InstallFaultHandling"/> installed, where that is
    /// safe. An implementation that cannot prove no guest thread will fault
    /// again deliberately leaves the hooks in place.
    /// </summary>
    void RemoveFaultHandling();


    /// <summary>
    /// Makes the calling thread's guest access faults reach the installed
    /// handler. macOS reroutes the thread's Mach exception port so
    /// <c>EXC_BAD_ACCESS</c> becomes a signal instead of being called a stack
    /// overflow by the runtime; other platforms do nothing.
    /// </summary>
    void RouteGuestFaultsOnCurrentThread();

    // ---------------------------------------------------------------------
    // Thread stack bounds
    // ---------------------------------------------------------------------

    /// <summary>
    /// Bounds of the committed allocation holding <paramref name="stackPointer"/>,
    /// where the platform can report a thread stack's whole reservation rather
    /// than one committed region of it. False leaves the engine to fall back on
    /// the neutral host region table.
    /// </summary>
    bool TryGetCommittedStackRegion(ulong stackPointer, out ulong bottom, out ulong top);

    /// <summary>
    /// Emits the thread-block stack-bounds save (<paramref name="save"/>) or
    /// restore into the stub at <paramref name="code"/>, advancing
    /// <paramref name="offset"/>. Emits nothing where the platform has no
    /// swappable bounds.
    /// </summary>
    void EmitHostStackBounds(byte* code, ref int offset, bool save);

    /// <summary>
    /// Emits the fiber-switch bounds sync, which takes the target stack top and
    /// bottom from the context-transfer state in R11.
    /// </summary>
    void EmitFiberStackBounds(byte* code, ref int offset);

    /// <summary>
    /// Emits a switch to the supplied guest stack bounds, saving the host
    /// bounds first.
    /// </summary>
    void EmitGuestStackBounds(byte* code, ref int offset, ulong stackBottom, ulong stackTop);

    /// <summary>
    /// Emits the save (<paramref name="restore"/> false) or restore of bounds
    /// held in R14/R15 across a guest entry.
    /// </summary>
    void EmitSavedStackBounds(byte* code, ref int offset, bool restore);

    // ---------------------------------------------------------------------
    // Diagnostics
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reads host memory for diagnostics without letting an unmapped or
    /// inaccessible page fault the reader — a stray read inside a fault handler
    /// would kill the process before the diagnostics finish. Implemented
    /// through the kernel (<c>mach_vm_read_overwrite</c>,
    /// <c>process_vm_readv</c>, <c>ReadProcessMemory</c>).
    /// </summary>
    bool TryReadHostMemory(ulong address, byte* destination, int byteCount);

    /// <summary>
    /// Reads host memory that may be mapped execute-only, which a plain data
    /// probe cannot touch on every platform. Used for the faulting
    /// instruction's own bytes, whose mapping is stable for the duration of
    /// fault dispatch.
    /// </summary>
    bool TryReadExecutableHostMemory(ulong address, byte* destination, int byteCount);

    /// <summary>
    /// The host's one-minute load average, or a negative value where the host
    /// has none. The stall watchdog stretches its period by it, so a guest phase
    /// slowed by other processes is not taken for a hang.
    /// </summary>
    double HostLoadAverage => -1.0;

    /// <summary>
    /// Resolves a host address that lives inside an OS-loaded image (system
    /// library, GPU driver, runtime) for crash symbolization. Platforms with no
    /// such facility return false.
    /// </summary>
    bool TryResolveHostImageSymbol(ulong address, out HostImageSymbol symbol)
    {
        symbol = default;
        return false;
    }
}

/// <summary>A host address resolved to its loaded image and nearest symbol.</summary>
public readonly record struct HostImageSymbol(string ImagePath, string? SymbolName, ulong SymbolAddress, ulong ImageBase);
