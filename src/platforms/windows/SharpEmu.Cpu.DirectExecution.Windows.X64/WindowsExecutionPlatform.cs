// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native.Platform;
using SharpEmu.HLE.Host;

namespace SharpEmu.Cpu.DirectExecution.Windows;

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
}
