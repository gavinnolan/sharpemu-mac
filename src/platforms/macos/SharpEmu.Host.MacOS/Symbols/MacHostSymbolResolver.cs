// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Host.MacOS;

internal sealed class MacHostSymbolResolver : IHostSymbolResolver
{
    public nint GetAddress(HostRuntimeFunction function) => function switch
    {
        HostRuntimeFunction.TlsGetValue => MacHostStubs.TlsGetValueStubAddress,
        HostRuntimeFunction.QueryPerformanceCounter => MacHostStubs.QueryPerformanceCounterStubAddress,
        HostRuntimeFunction.SwitchToThread => MacHostStubs.SwitchToThreadStubAddress,
        HostRuntimeFunction.Sleep => MacHostStubs.SleepStubAddress,
        HostRuntimeFunction.WaitForSingleObject => MacHostStubs.WaitForSingleObjectStubAddress,
        HostRuntimeFunction.SetEvent => MacHostStubs.SetEventStubAddress,
        HostRuntimeFunction.ExitThread => MacHostStubs.ExitThreadStubAddress,
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, null),
    };
}
