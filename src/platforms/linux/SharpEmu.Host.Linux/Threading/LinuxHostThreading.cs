// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Host.Linux;

internal sealed class LinuxHostThreading : IHostThreading
{
    public uint AllocateTlsSlot() => LinuxHostStubs.TlsAlloc();

    public bool FreeTlsSlot(uint slot) => LinuxHostStubs.TlsFree(slot);

    public bool SetTlsValue(uint slot, nint value) => LinuxHostStubs.TlsSetValue(slot, value);

    public nint GetTlsValue(uint slot) => LinuxHostStubs.TlsGetValue(slot);

    public uint CurrentThreadId => LinuxHostStubs.GetCurrentThreadId();

    public void RequestTimerResolution()
    {
        // Linux sleep primitives are already high-resolution; there is no
        // timeBeginPeriod equivalent to request.
    }

    // Not wired up yet: sched_setaffinity exists on Linux but the Win32
    // affinity-mask semantics the caller passes have not been validated
    // against it, so callers treat false as "not applied".
    public bool TrySetCurrentThreadAffinity(nuint affinityMask)
    {
        _ = affinityMask;
        return false;
    }

    public nint CreateNativeThread(
        nint entry,
        nint parameter,
        nuint stackReserveBytes,
        out uint threadId)
    {
        return LinuxHostStubs.CreateWorkerThread(entry, parameter, stackReserveBytes, out threadId);
    }

    public bool WaitForThreadExit(nint threadHandle, uint timeoutMilliseconds)
    {
        return LinuxHostStubs.WaitForWorkerThreadExit(threadHandle, timeoutMilliseconds);
    }

    public void CloseThreadHandle(nint threadHandle)
    {
        LinuxHostStubs.CloseWorkerThreadHandle(threadHandle);
    }

    public bool TryCaptureThreadRegisters(uint threadId, out HostCapturedRegisters registers)
    {
        _ = threadId;
        registers = default;
        return false;
    }
}
