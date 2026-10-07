// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Cpu.DirectExecution.Linux.X64;

/// <summary>
/// Reads host memory for fault diagnostics through the kernel, so an untracked
/// or inaccessible page cannot fault the reader.
/// </summary>
/// <remarks>
/// A stray read inside a signal handler would raise a nested SIGSEGV and kill
/// the process before the diagnostics finish, so try/catch over a direct read
/// is not an option. Moved verbatim out of
/// <c>SharpEmu.Core/Cpu/Native/DirectExecutionBackend.Exceptions.cs</c> by W10.
/// </remarks>
internal static unsafe class LinuxHostMemoryProbe
{
    internal static bool TryRead(ulong address, byte* destination, int byteCount)
    {
        if (byteCount < 0 || !IsCanonicalUserAddress(address) ||
            address > 0x0000_8000_0000_0000UL - (ulong)byteCount)
        {
            return false;
        }

        if (byteCount == 0) return true;

        // Read through the kernel so untracked or inaccessible pages cannot fault the reader.
        var local = new DiagnosticMemoryVector { Address = (nint)destination, Length = (nuint)byteCount };
        var remote = new DiagnosticMemoryVector { Address = (nint)address, Length = (nuint)byteCount };
        return ReadLinuxProcessMemory(Environment.ProcessId, &local, 1, &remote, 1, 0) == byteCount;
    }

    /// <summary>
    /// Forces the kernel read to be bound before the first signal uses it:
    /// resolving a lazy P/Invoke inside a signal frame would need the JIT.
    /// </summary>
    internal static void WarmUp()
    {
        byte probe = 0;
        _ = TryRead((ulong)(nint)(&probe), &probe, 1);
    }

    private static bool IsCanonicalUserAddress(ulong address) =>
        address >= 0x10000 && address < 0x0000_8000_0000_0000UL;

    [StructLayout(LayoutKind.Sequential)]
    private struct DiagnosticMemoryVector
    {
        public nint Address;
        public nuint Length;
    }

    [DllImport("libc", EntryPoint = "process_vm_readv")]
    private static extern nint ReadLinuxProcessMemory(
        int processId, DiagnosticMemoryVector* local, nuint localCount,
        DiagnosticMemoryVector* remote, nuint remoteCount, nuint flags);
}
