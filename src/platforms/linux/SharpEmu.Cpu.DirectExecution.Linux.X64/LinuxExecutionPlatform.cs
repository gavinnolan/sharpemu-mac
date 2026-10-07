// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native.Platform;
using SharpEmu.HLE.Host;

namespace SharpEmu.Cpu.DirectExecution.Linux;

/// <summary>
/// The Linux x86-64 direct-execution integration: the OS side of the shared
/// direct-execution engine on Linux.
/// </summary>
/// <remarks>
/// Registered from the application's composition root
/// (<c>src/SharpEmu.CLI/CpuComposition.cs</c>); shared code reaches it only
/// through <see cref="DirectExecutionPlatform.Current"/>.
/// </remarks>
public sealed unsafe class LinuxExecutionPlatform : IDirectExecutionPlatform
{
    /// <summary>Creates the platform. Cheap: native stub pages are emitted on first use.</summary>
    public static IDirectExecutionPlatform Create() => new LinuxExecutionPlatform();

    public string Name => "linux-x64";

    /// <remarks>
    /// None yet. Linux has no swappable thread stack bounds for emitted code,
    /// delivers faults as signals rather than as a Win64 exception record, has
    /// no suspended thread-context capture here, and its emitted TLS read is a
    /// real <c>pthread_getspecific</c> call rather than a single load, so the
    /// import fast path keeps using the managed gateway.
    /// </remarks>
    public DirectExecutionCapabilities Capabilities => DirectExecutionCapabilities.None;

    /// <remarks>
    /// The 0x7FFx window Windows leaves free is not reliably free on Linux, so
    /// guest thread stacks and TLS go below it.
    /// </remarks>
    public ulong GuestThreadStackBaseAddress => 0x6FFF_E000_0000UL;

    public ulong GuestThreadTlsBaseAddress => 0x6FFE_0000_0000UL;

    public uint AllocateTlsSlot() => LinuxExecutionStubs.TlsAlloc();

    public bool FreeTlsSlot(uint slot) => LinuxExecutionStubs.TlsFree(slot);

    public bool SetTlsValue(uint slot, nint value) => LinuxExecutionStubs.TlsSetValue(slot, value);

    public nint GetTlsValue(uint slot) => LinuxExecutionStubs.TlsGetValue(slot);

    public nint GetRuntimeRoutine(HostRuntimeFunction routine) => routine switch
    {
        HostRuntimeFunction.TlsGetValue => LinuxExecutionStubs.TlsGetValueStubAddress,
        HostRuntimeFunction.QueryPerformanceCounter => LinuxExecutionStubs.QueryPerformanceCounterStubAddress,
        HostRuntimeFunction.SwitchToThread => LinuxExecutionStubs.SwitchToThreadStubAddress,
        HostRuntimeFunction.Sleep => LinuxExecutionStubs.SleepStubAddress,
        // The emitted native guest worker loop is not implemented here (see
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
        LinuxExecutionStubs.CreateWin64ToSysVThunk(managedCallback);

    public uint CurrentThreadId => LinuxExecutionStubs.GetCurrentThreadId();

    /// <remarks>Linux has no thread pseudo-handle equivalent.</remarks>
    public nint CurrentThreadHandle => 0;

    /// <remarks>
    /// Affinity is not set from the execution backend here. Reports success so
    /// callers do not treat the absence as a failure, matching the behaviour
    /// before W10.
    /// </remarks>
    public nuint TrySetThreadAffinity(nint threadHandle, nuint affinityMask) => 1;

    public nint CreateWorkerEvent() => LinuxExecutionStubs.CreateWorkerEvent();

    public bool SignalWorkerEvent(nint handle) => LinuxExecutionStubs.SignalWorkerEvent(handle);

    public bool WaitWorkerEvent(nint handle, int timeoutMilliseconds) =>
        LinuxExecutionStubs.WaitWorkerEvent(handle, timeoutMilliseconds);

    public void DestroyWorkerEvent(nint handle) => LinuxExecutionStubs.DestroyWorkerEvent(handle);

    /// <remarks>
    /// Nothing to do: Linux delivers access faults straight to the installed
    /// signal handler. Only macOS has a Mach exception port ahead of signals.
    /// </remarks>
    public void RouteGuestFaultsOnCurrentThread()
    {
    }

    public bool TryReadHostMemory(ulong address, byte* destination, int byteCount) =>
        LinuxHostMemoryProbe.TryRead(address, destination, byteCount);

    /// <remarks>
    /// <c>process_vm_readv</c> ignores page protection, so execute-only guest
    /// code needs no separate path here.
    /// </remarks>
    public bool TryReadExecutableHostMemory(ulong address, byte* destination, int byteCount) =>
        LinuxHostMemoryProbe.TryRead(address, destination, byteCount);
}
