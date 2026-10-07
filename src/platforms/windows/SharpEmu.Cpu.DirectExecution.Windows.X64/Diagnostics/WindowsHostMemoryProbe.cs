// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;

namespace SharpEmu.Cpu.DirectExecution.Windows.X64;

/// <summary>
/// Reads host memory for fault diagnostics without faulting the reader.
/// </summary>
/// <remarks>
/// Diagnostic and operand addresses go through <c>ReadProcessMemory</c>,
/// because they can become invalid while an exception is being reported.
/// Moved verbatim out of
/// <c>SharpEmu.Core/Cpu/Native/DirectExecutionBackend.Exceptions.cs</c> by W10;
/// the readability pre-check now lives with the Windows probe, using the
/// same neutral host-region query and protection checks as before.
/// </remarks>
internal static unsafe class WindowsHostMemoryProbe
{
    internal static bool TryRead(ulong address, byte* destination, int byteCount)
    {
        if (!IsReadableRange(address, byteCount))
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
        if (!IsReadableRange(address, byteCount, allowExecuteOnly: true))
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

    private static bool IsReadableRange(ulong address, int byteCount, bool allowExecuteOnly = false)
    {
        if (byteCount < 0 || address < 0x10000 || address >= 0x0000_8000_0000_0000UL ||
            address > 0x0000_8000_0000_0000UL - (ulong)byteCount) return false;
        var end = address + (ulong)byteCount;
        for (var cursor = address; cursor < end;)
        {
            if (HostMemory.Query((void*)cursor, out var region) == 0 ||
                region.State != HostMemory.MEM_COMMIT ||
                (region.Protect & (0x100u | HostMemory.PAGE_NOACCESS)) != 0 ||
                ((region.Protect & 0xEE) == 0 && !(allowExecuteOnly && region.Protect == HostMemory.PAGE_EXECUTE)))
                return false;
            var regionEnd = region.BaseAddress + region.RegionSize;
            if (regionEnd < region.BaseAddress || regionEnd <= cursor) return false;
            cursor = Math.Min(regionEnd, end);
        }
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
