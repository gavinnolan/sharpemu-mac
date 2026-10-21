// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Kernel.Syscalls;

/// <summary>What a Prospero syscall slot is.</summary>
internal enum OrbisSyscallKind
{
    /// <summary>Unimplemented slot (FreeBSD <c>nosys</c>): SIGSYS, then ENOSYS.</summary>
    NoSys,

    /// <summary><c>syscall</c> / <c>__syscall</c>: the number is the first argument.</summary>
    Indirect,

    /// <summary>A real syscall routed to the libkernel export(s) of the same ABI.</summary>
    Forwarded,

    /// <summary>A real syscall with no libkernel export of its ABI (thr_*, dynlib, PS5 private).</summary>
    Unmapped,

    /// <summary>A FreeBSD compat shim (<c>sys_compat*.x</c>); not modelled.</summary>
    Compat,

    /// <summary>A real syscall whose same-named export has a different ABI (pipe, fork, contexts).</summary>
    AbiMismatch,
}

/// <summary>A libkernel export a forwarded syscall can be serviced by.</summary>
internal readonly record struct OrbisSyscallTarget(string ExportName, string Nid, KernelAbiFlavor Flavor);

/// <summary>One slot of the generated table.</summary>
internal sealed record OrbisSyscallEntry(int Number, string Name, OrbisSyscallKind Kind, OrbisSyscallTarget[] Targets);

/// <summary>
/// The Prospero sysent, generated from psdevwiki by
/// <c>scripts/gen_syscall_table.py</c> into <c>OrbisSyscallTable.g.cs</c>.
/// </summary>
internal static partial class OrbisSyscallTable
{
    public static int Count => s_entries.Length;

    /// <summary>The slot for <paramref name="number"/>, or false past the end of the table.</summary>
    public static bool TryGet(long number, out OrbisSyscallEntry entry)
    {
        if (number < 0 || number >= s_entries.Length)
        {
            entry = null!;
            return false;
        }

        entry = s_entries[number];
        return true;
    }
}
