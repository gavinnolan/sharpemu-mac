// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native.Platform;
using SharpEmu.HLE.Host;

namespace SharpEmu.Cpu.DirectExecution.MacOS.X64;

/// <summary>
/// The macOS x86-64 direct-execution integration: the OS side of the shared
/// direct-execution engine on Darwin.
/// </summary>
/// <remarks>
/// Registered from the application's composition root
/// (<c>src/SharpEmu.CLI/CpuComposition.cs</c>); shared code reaches it only
/// through <see cref="DirectExecutionPlatform.Current"/>. Guest x86-64 code
/// executes natively, so a macOS process runs this under Rosetta 2 on Apple
/// silicon.
/// </remarks>
public sealed unsafe class MacOSExecutionPlatform : IDirectExecutionPlatform
{
    /// <summary>Creates the platform. Cheap: native stub pages are emitted on first use.</summary>
    public static IDirectExecutionPlatform Create() => new MacOSExecutionPlatform();

    public string Name => "macos-x64";

    /// <remarks>
    /// Only the emitted TLS fast path. Darwin has no swappable thread stack
    /// bounds (gs addresses the pthread TSD, where the Windows TEB keeps its
    /// stack limits), delivers faults as signals rather than as a Win64
    /// exception record, has no suspended thread-context capture, and the
    /// emitted native guest worker loop is not implemented here.
    /// </remarks>
    public DirectExecutionCapabilities Capabilities => DirectExecutionCapabilities.EmittedTlsFastPath;

    /// <remarks>
    /// The 0x7FFx window Windows leaves free is occupied on macOS by the dyld
    /// shared cache and the Rosetta 2 runtime, so guest thread stacks and TLS
    /// go below it.
    /// </remarks>
    public ulong GuestThreadStackBaseAddress => 0x6FFF_E000_0000UL;

    public ulong GuestThreadTlsBaseAddress => 0x6FFE_0000_0000UL;

    public uint AllocateTlsSlot() => MacOSExecutionStubs.TlsAlloc();

    public bool FreeTlsSlot(uint slot) => MacOSExecutionStubs.TlsFree(slot);

    public bool SetTlsValue(uint slot, nint value) => MacOSExecutionStubs.TlsSetValue(slot, value);

    public nint GetTlsValue(uint slot) => MacOSExecutionStubs.TlsGetValue(slot);

    public nint GetRuntimeRoutine(HostRuntimeFunction routine) => routine switch
    {
        HostRuntimeFunction.TlsGetValue => MacOSExecutionStubs.TlsGetValueStubAddress,
        HostRuntimeFunction.QueryPerformanceCounter => MacOSExecutionStubs.QueryPerformanceCounterStubAddress,
        HostRuntimeFunction.SwitchToThread => MacOSExecutionStubs.SwitchToThreadStubAddress,
        HostRuntimeFunction.Sleep => MacOSExecutionStubs.SleepStubAddress,
        // The emitted native guest worker loop is Windows-only here (see
        // Capabilities), so its event and exit routines have no stub.
        HostRuntimeFunction.WaitForSingleObject => 0,
        HostRuntimeFunction.SetEvent => 0,
        HostRuntimeFunction.ExitThread => 0,
        _ => 0,
    };

    /// <remarks>
    /// Managed code is compiled for the SysV ABI on POSIX .NET while the
    /// emitted call sites use Win64, so every managed callback the emitted code
    /// calls goes through a thunk.
    /// </remarks>
    public nint ResolveWin64CallbackAddress(nint managedCallback) =>
        MacOSExecutionStubs.CreateWin64ToSysVThunk(managedCallback);

    public uint CurrentThreadId => MacOSExecutionStubs.GetCurrentThreadId();

    /// <remarks>Darwin has no thread pseudo-handle equivalent.</remarks>
    public nint CurrentThreadHandle => 0;

    /// <remarks>
    /// Darwin exposes no thread affinity control (only scheduling affinity
    /// hints, which are advisory). Reports success so callers do not treat the
    /// absence as a failure, matching the behaviour before W10.
    /// </remarks>
    public nuint TrySetThreadAffinity(nint threadHandle, nuint affinityMask) => 1;

    public nint OpenThreadForContext(uint threadId) => 0;
    public uint GetThreadId(nint threadHandle) => 0;
    public bool CloseThreadHandle(nint threadHandle) => false;
    public nint CreateNativeWorker(nuint stackReservation, nint entry, out uint threadId)
    {
        threadId = 0;
        return 0;
    }
    public uint WaitNativeWorker(nint threadHandle, uint timeoutMilliseconds) => uint.MaxValue;
    public bool TerminateNativeWorker(nint threadHandle, uint exitCode) => false;

    public nint CreateWorkerEvent() => MacOSExecutionStubs.CreateWorkerEvent();

    public bool SignalWorkerEvent(nint handle) => MacOSExecutionStubs.SignalWorkerEvent(handle);

    public bool WaitWorkerEvent(nint handle, int timeoutMilliseconds) =>
        MacOSExecutionStubs.WaitWorkerEvent(handle, timeoutMilliseconds);

    public void DestroyWorkerEvent(nint handle) => MacOSExecutionStubs.DestroyWorkerEvent(handle);

    public void InstallFaultHandling(in DirectExecutionFaultHandlers handlers)
    {
        PosixFaultBridge.WarmUpPlatformEntries(this);
        PosixFaultBridge.Install(handlers);
    }

    public void RemoveFaultHandling() => PosixFaultBridge.Remove();

    public void RouteGuestFaultsOnCurrentThread() => MacOSGuestFaultRouting.RouteGuestAccessFaultsToSignals();

    public bool TryReadHostMemory(ulong address, byte* destination, int byteCount) =>
        MacOSHostMemoryProbe.TryRead(address, destination, byteCount);

    /// <remarks>
    /// Mach reads ignore page protection, so execute-only guest code needs no
    /// separate path here.
    /// </remarks>
    public bool TryReadExecutableHostMemory(ulong address, byte* destination, int byteCount) =>
        MacOSHostMemoryProbe.TryRead(address, destination, byteCount);

    public bool TryResolveHostImageSymbol(ulong address, out HostImageSymbol symbol) =>
        MacOSHostSymbolizer.TryResolve(address, out symbol);

    public double HostLoadAverage => MacOSExecutionStubs.ReadLoadAverage();

    // ---------------------------------------------------------------------
    // Thread stack bounds
    // ---------------------------------------------------------------------

    /// <remarks>
    /// No equivalent of the Windows memory-information query that reports a
    /// thread stack's whole reservation; the engine's neutral host region
    /// lookup answers instead.
    /// </remarks>
    public bool TryGetCommittedStackRegion(ulong stackPointer, out ulong bottom, out ulong top)
    {
        bottom = 0;
        top = 0;
        return false;
    }

    /// <remarks>
    /// Emits nothing. There is no thread block whose stack bounds emitted code
    /// may swap: gs addresses the pthread TSD, not a TEB, and writing stack
    /// bounds into it corrupts unrelated thread-specific slots. The fault
    /// bridge moves the handler onto the host stack instead.
    /// </remarks>
    public void EmitHostStackBounds(byte* code, ref int offset, bool save)
    {
    }

    /// <inheritdoc cref="EmitHostStackBounds"/>
    public void EmitFiberStackBounds(byte* code, ref int offset)
    {
    }

    /// <inheritdoc cref="EmitHostStackBounds"/>
    public void EmitGuestStackBounds(byte* code, ref int offset, ulong stackBottom, ulong stackTop)
    {
    }

    /// <inheritdoc cref="EmitHostStackBounds"/>
    public void EmitSavedStackBounds(byte* code, ref int offset, bool restore)
    {
    }

}
