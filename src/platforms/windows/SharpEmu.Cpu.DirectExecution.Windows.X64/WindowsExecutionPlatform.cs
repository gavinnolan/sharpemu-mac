// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native.Platform;
using SharpEmu.HLE.Host;

namespace SharpEmu.Cpu.DirectExecution.Windows.X64;

/// <summary>
/// The Windows x86-64 direct-execution integration: the OS side of the shared
/// direct-execution engine on Windows.
/// </summary>
/// <remarks>
/// Registered from the application's composition root
/// (<c>src/SharpEmu.CLI/CpuComposition.cs</c>); shared code reaches it only
/// through <see cref="DirectExecutionPlatform.Current"/>. This is the platform
/// the engine's emitted call sites were originally written against, so most
/// members here are the kernel32 routine itself rather than a replacement.
/// </remarks>
public sealed unsafe class WindowsExecutionPlatform : IDirectExecutionPlatform
{
    /// <summary>Creates the platform.</summary>
    public static IDirectExecutionPlatform Create() => new WindowsExecutionPlatform();

    public string Name => "windows-x64";

    /// <remarks>
    /// Everything except <see cref="DirectExecutionCapabilities.EmittedTlsFastPath"/>:
    /// a Win32 TLS read from emitted code is a real call, so the import fast
    /// path keeps using the managed gateway here.
    /// </remarks>
    public DirectExecutionCapabilities Capabilities =>
        DirectExecutionCapabilities.VectoredFaultDispatch |
        DirectExecutionCapabilities.SwappableThreadStackBounds |
        DirectExecutionCapabilities.NativeGuestWorkers |
        DirectExecutionCapabilities.Win64WorkerEventHandles |
        DirectExecutionCapabilities.ThreadContextCapture |
        DirectExecutionCapabilities.Win64ManagedCallbackAbi |
        DirectExecutionCapabilities.FaultContextCarriesVectorRegisters |
        DirectExecutionCapabilities.GeneralProtectionFaultsReportAllOnesAddress |
        DirectExecutionCapabilities.GuestRipSampling |
        DirectExecutionCapabilities.CheapHostRegionQueries |
        DirectExecutionCapabilities.StrayReadsSurfaceAsManagedExceptions;

    public ulong GuestThreadStackBaseAddress => 0x7FFF_E000_0000UL;

    public ulong GuestThreadTlsBaseAddress => 0x7FFE_0000_0000UL;

    public uint AllocateTlsSlot() => WindowsExecutionStubs.TlsAlloc();

    public bool FreeTlsSlot(uint slot) => WindowsExecutionStubs.TlsFree(slot);

    public bool SetTlsValue(uint slot, nint value) => WindowsExecutionStubs.TlsSetValue(slot, value);

    public nint GetTlsValue(uint slot) => WindowsExecutionStubs.TlsGetValue(slot);

    public nint GetRuntimeRoutine(HostRuntimeFunction routine)
    {
        var kernel32 = WindowsExecutionStubs.GetModuleHandle("kernel32.dll");
        if (kernel32 == 0)
        {
            return 0;
        }

        return WindowsExecutionStubs.GetProcAddress(kernel32, routine switch
        {
            HostRuntimeFunction.TlsGetValue => "TlsGetValue",
            HostRuntimeFunction.QueryPerformanceCounter => "QueryPerformanceCounter",
            HostRuntimeFunction.SwitchToThread => "SwitchToThread",
            HostRuntimeFunction.Sleep => "Sleep",
            HostRuntimeFunction.WaitForSingleObject => "WaitForSingleObject",
            HostRuntimeFunction.SetEvent => "SetEvent",
            HostRuntimeFunction.ExitThread => "ExitThread",
            HostRuntimeFunction.SuspendThread => "SuspendThread",
            HostRuntimeFunction.GetThreadContext => "GetThreadContext",
            HostRuntimeFunction.ResumeThread => "ResumeThread",
            HostRuntimeFunction.GetStandardErrorHandle => "GetStdHandle",
            HostRuntimeFunction.WriteFile => "WriteFile",
            HostRuntimeFunction.FlushFileBuffers => "FlushFileBuffers",
            _ => throw new ArgumentOutOfRangeException(nameof(routine), routine, null),
        });
    }

    /// <remarks>The runtime already compiles managed callbacks for the Win64 ABI.</remarks>
    public nint ResolveWin64CallbackAddress(nint managedCallback) => managedCallback;

    public uint CurrentThreadId => WindowsExecutionStubs.GetCurrentThreadId();

    public nint CurrentThreadHandle => WindowsExecutionStubs.GetCurrentThread();

    public nuint TrySetThreadAffinity(nint threadHandle, nuint affinityMask) =>
        WindowsExecutionStubs.SetThreadAffinityMask(threadHandle, affinityMask);

    /// <remarks>
    /// The emitted worker wait loop uses Win32 event handles the engine owns
    /// (see <see cref="DirectExecutionCapabilities.Win64WorkerEventHandles"/>), so
    /// this platform creates no separate worker event.
    /// </remarks>
    public nint OpenThreadForContext(uint threadId) => WindowsExecutionStubs.OpenThread(0x0008 | 0x0002 | 0x0800, false, threadId);
    public uint GetThreadId(nint threadHandle) => WindowsExecutionStubs.GetThreadId(threadHandle);
    public bool CloseThreadHandle(nint threadHandle) => WindowsExecutionStubs.CloseHandle(threadHandle);
    public nint CreateNativeWorker(nuint stackReservation, nint entry, out uint threadId) =>
        WindowsExecutionStubs.CreateThread(0, stackReservation, entry, 0, 0x00010000, out threadId);
    public uint WaitNativeWorker(nint threadHandle, uint timeoutMilliseconds) =>
        WindowsExecutionStubs.WaitForSingleObject(threadHandle, timeoutMilliseconds);
    public bool TerminateNativeWorker(nint threadHandle, uint exitCode) =>
        WindowsExecutionStubs.TerminateThread(threadHandle, exitCode);

    public nint CreateWorkerEvent() => 0;

    public bool SignalWorkerEvent(nint handle) => false;

    public bool WaitWorkerEvent(nint handle, int timeoutMilliseconds) => false;

    public void DestroyWorkerEvent(nint handle)
    {
    }

    /// <remarks>
    /// Nothing to do: Windows dispatches every fault to the vectored handlers
    /// on every thread. Only macOS has a Mach exception port ahead of signals.
    /// </remarks>
    public void RouteGuestFaultsOnCurrentThread()
    {
    }

    public bool TryReadHostMemory(ulong address, byte* destination, int byteCount) =>
        WindowsHostMemoryProbe.TryRead(address, destination, byteCount);

    public bool TryReadExecutableHostMemory(ulong address, byte* destination, int byteCount) =>
        WindowsHostMemoryProbe.TryReadExecutable(address, destination, byteCount);

    public void InstallFaultHandling(in DirectExecutionFaultHandlers handlers) =>
        WindowsFaultInterception.Install(handlers);

    public void RemoveFaultHandling() => WindowsFaultInterception.Remove();

    // ---------------------------------------------------------------------
    // Thread stack bounds
    // ---------------------------------------------------------------------

    public bool TryGetCommittedStackRegion(ulong stackPointer, out ulong bottom, out ulong top) =>
        WindowsStackBounds.TryGetCommittedStackRegion(stackPointer, out bottom, out top);

    public void EmitHostStackBounds(byte* code, ref int offset, bool save) =>
        WindowsStackBounds.EmitHostStackBounds(code, ref offset, save);

    public void EmitFiberStackBounds(byte* code, ref int offset) =>
        WindowsStackBounds.EmitFiberStackBounds(code, ref offset);

    public void EmitGuestStackBounds(byte* code, ref int offset, ulong stackBottom, ulong stackTop) =>
        WindowsStackBounds.EmitGuestStackBounds(code, ref offset, stackBottom, stackTop);

    public void EmitSavedStackBounds(byte* code, ref int offset, bool restore) =>
        WindowsStackBounds.EmitSavedStackBounds(code, ref offset, restore);

}
