// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Host.MacOS;

internal sealed class MacHostThreading : IHostThreading
{
    public uint AllocateTlsSlot() => MacHostStubs.TlsAlloc();

    public bool FreeTlsSlot(uint slot) => MacHostStubs.TlsFree(slot);

    public bool SetTlsValue(uint slot, nint value) => MacHostStubs.TlsSetValue(slot, value);

    public nint GetTlsValue(uint slot) => MacHostStubs.TlsGetValue(slot);

    public uint CurrentThreadId => MacHostStubs.GetCurrentThreadId();

    public void RequestTimerResolution()
    {
        // Darwin sleep primitives are already high-resolution; there is no
        // timeBeginPeriod equivalent to request.
    }

    // macOS offers no pthread-level affinity API (thread affinity sets are
    // hints the scheduler may ignore), so callers treat false as
    // "not applied".
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
        return MacHostStubs.CreateWorkerThread(entry, parameter, stackReserveBytes, out threadId);
    }

    public bool WaitForThreadExit(nint threadHandle, uint timeoutMilliseconds)
    {
        return MacHostStubs.WaitForWorkerThreadExit(threadHandle, timeoutMilliseconds);
    }

    public void CloseThreadHandle(nint threadHandle)
    {
        MacHostStubs.CloseWorkerThreadHandle(threadHandle);
    }

    public bool TryCaptureThreadRegisters(uint threadId, out HostCapturedRegisters registers)
    {
        _ = threadId;
        registers = default;
        return false;
    }
}
