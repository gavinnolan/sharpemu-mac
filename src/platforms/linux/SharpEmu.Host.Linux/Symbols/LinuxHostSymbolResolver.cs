// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Host.Linux;

internal sealed class LinuxHostSymbolResolver : IHostSymbolResolver
{
    public nint GetAddress(HostRuntimeFunction function) => function switch
    {
        HostRuntimeFunction.TlsGetValue => LinuxHostStubs.TlsGetValueStubAddress,
        HostRuntimeFunction.QueryPerformanceCounter => LinuxHostStubs.QueryPerformanceCounterStubAddress,
        HostRuntimeFunction.SwitchToThread => LinuxHostStubs.SwitchToThreadStubAddress,
        HostRuntimeFunction.Sleep => LinuxHostStubs.SleepStubAddress,
        HostRuntimeFunction.WaitForSingleObject => LinuxHostStubs.WaitForSingleObjectStubAddress,
        HostRuntimeFunction.SetEvent => LinuxHostStubs.SetEventStubAddress,
        HostRuntimeFunction.ExitThread => LinuxHostStubs.ExitThreadStubAddress,
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, null),
    };
}
