// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;

namespace SharpEmu.Cpu.DirectExecution.Windows;

/// <summary>
/// Reads host memory for fault diagnostics without faulting the reader.
/// </summary>
/// <remarks>
/// Diagnostic and operand addresses go through <c>ReadProcessMemory</c>,
/// because they can become invalid while an exception is being reported.
/// Moved verbatim out of
/// <c>SharpEmu.Core/Cpu/Native/DirectExecutionBackend.Exceptions.cs</c> by W10;
/// the readability pre-check stays shared, since it runs on the neutral host
/// region query rather than on a Win32 call.
/// </remarks>
internal static unsafe class WindowsHostMemoryProbe
{
    internal static bool TryRead(ulong address, byte* destination, int byteCount)
    {
        if (!DirectExecutionBackend.IsReadableHostRange(address, byteCount))
        {
            return false;
        }

        if (byteCount == 0)
        {
            return true;
        }

        return ReadProcessMemory(
            (nint)(-1),
            (nint)address,
            destination,
            (nuint)byteCount,
            out var bytesRead) != 0 &&
            bytesRead == (nuint)byteCount;
    }

    internal static bool TryReadExecutable(ulong address, byte* destination, int byteCount)
    {
        // Faulting x64 code can have execute-only protection. Data probes still require read access.
        if (!DirectExecutionBackend.IsReadableHostRange(address, byteCount, allowExecuteOnly: true))
        {
            return false;
        }

        if (byteCount == 0)
        {
            return true;
        }

        // RIP identifies code that Windows was already executing when the
        // exception occurred. These mappings are stable for the duration of
        // dispatch, so read them directly.
        new ReadOnlySpan<byte>((void*)address, byteCount).CopyTo(new Span<byte>(destination, byteCount));
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern int ReadProcessMemory(
        nint process,
        nint baseAddress,
        void* buffer,
        nuint byteCount,
        out nuint bytesRead);
}
