// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Cpu.DirectExecution.Windows;

/// <summary>
/// The kernel32 TLS, thread and routine-resolution primitives the
/// direct-execution backend uses, both from managed code and baked into
/// emitted x86-64 stubs.
/// </summary>
/// <remarks>
/// Moved verbatim out of <c>SharpEmu.Core/Cpu/Native/DirectExecutionBackend.cs</c>
/// by W10. The emitted call sites were written against this Win64 convention
/// in the first place, so here the "stub addresses" are simply the kernel32
/// exports; the POSIX packages emit equivalents.
/// </remarks>
internal static class WindowsExecutionStubs
{
    [DllImport("kernel32.dll", EntryPoint = "TlsAlloc")]
    internal static extern uint TlsAlloc();

    [DllImport("kernel32.dll", EntryPoint = "TlsFree")]
    internal static extern bool TlsFree(uint dwTlsIndex);

    [DllImport("kernel32.dll", EntryPoint = "TlsSetValue")]
    internal static extern bool TlsSetValue(uint dwTlsIndex, nint lpTlsValue);

    [DllImport("kernel32.dll", EntryPoint = "TlsGetValue")]
    internal static extern nint TlsGetValue(uint dwTlsIndex);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThread")]
    internal static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", EntryPoint = "SetThreadAffinityMask", SetLastError = true)]
    internal static extern nuint SetThreadAffinityMask(nint hThread, nuint dwThreadAffinityMask);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    internal static extern nint GetProcAddress(nint hModule, string procName);
}
