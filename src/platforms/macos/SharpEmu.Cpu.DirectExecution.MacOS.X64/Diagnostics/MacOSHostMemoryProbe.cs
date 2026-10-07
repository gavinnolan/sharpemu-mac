// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Cpu.DirectExecution.MacOS;

/// <summary>
/// Reads host memory for fault diagnostics through Mach, so an unmapped or
/// inaccessible page cannot fault the reader.
/// </summary>
/// <remarks>
/// A stray read inside a signal handler would raise a nested SIGSEGV and kill
/// the process before the diagnostics finish, and the host memory table does
/// not contain all guest code mappings, so try/catch over a direct read is not
/// an option. Moved verbatim out of
/// <c>SharpEmu.Core/Cpu/Native/DirectExecutionBackend.Exceptions.cs</c> by W10.
/// </remarks>
internal static unsafe class MacOSHostMemoryProbe
{
    internal static bool TryRead(ulong address, byte* destination, int byteCount)
    {
        if (byteCount < 0 || !IsCanonicalUserAddress(address) ||
            address > 0x0000_8000_0000_0000UL - (ulong)byteCount)
        {
            return false;
        }

        if (byteCount == 0)
        {
            return true;
        }

        // The host memory table does not contain all guest code mappings.
        // Use Mach to read the memory and report access errors without another fault.
        return ReadMachVirtualMemory(GetCurrentMachTask(), address, (ulong)byteCount,
            (ulong)destination, out var bytesRead) == 0 && bytesRead == (ulong)byteCount;
    }

    /// <summary>
    /// Forces the Mach memory functions to be bound before the first signal uses
    /// them: resolving a lazy P/Invoke inside a signal frame would need the JIT.
    /// </summary>
    internal static void WarmUp()
    {
        byte probe = 0;
        _ = TryRead((ulong)(nint)(&probe), &probe, 1);
    }

    private static bool IsCanonicalUserAddress(ulong address) =>
        address >= 0x10000 && address < 0x0000_8000_0000_0000UL;

    [DllImport("libSystem.B.dylib", EntryPoint = "mach_task_self")]
    private static extern uint GetCurrentMachTask();

    [DllImport("libSystem.B.dylib", EntryPoint = "mach_vm_read_overwrite")]
    private static extern int ReadMachVirtualMemory(
        uint taskHandle, ulong sourceAddress, ulong byteCount, ulong destinationAddress, out ulong bytesRead);
}
