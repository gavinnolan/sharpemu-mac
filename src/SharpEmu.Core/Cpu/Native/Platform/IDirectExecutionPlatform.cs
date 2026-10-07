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
    /// Makes the calling thread's guest access faults reach the installed
    /// handler. macOS reroutes the thread's Mach exception port so
    /// <c>EXC_BAD_ACCESS</c> becomes a signal instead of being called a stack
    /// overflow by the runtime; other platforms do nothing.
    /// </summary>
    void RouteGuestFaultsOnCurrentThread();

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
}
