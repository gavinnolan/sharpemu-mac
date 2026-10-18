// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// The register frame a trapped raw guest <c>syscall</c> instruction is
/// serviced through.
/// </summary>
/// <remarks>
/// A trapped site is redirected (outside the signal handler) to a host thunk
/// that pushes the guest's registers, calls the trap export through an
/// ordinary import stub with <c>rdi</c> pointing at the pushed block, and pops
/// the block back. The export reads the syscall number and arguments from the
/// block and writes the results into it, so the thunk resumes the guest with
/// the FreeBSD amd64 return convention: <c>rax</c> = result or errno,
/// <c>rdx</c> = second result, CF = failure, <c>rcx</c> = return RIP,
/// <c>r11</c> = RFLAGS. Offsets are from the block pointer; the order is the
/// reverse of the thunk's push order.
/// </remarks>
public static class GuestSyscallFrame
{
    /// <summary>Export name of the trap entry (not a PS5 symbol).</summary>
    public const string TrapExportName = "__sharpemu_raw_syscall_trap";

    /// <summary>NID of <see cref="TrapExportName"/> (the PS5 NID hash of that name).</summary>
    public const string TrapNid = "fg6PqcrblCs";

    public const int R15 = 0x00;
    public const int R14 = 0x08;
    public const int R13 = 0x10;
    public const int R12 = 0x18;
    public const int R11 = 0x20;
    public const int R10 = 0x28;
    public const int R9 = 0x30;
    public const int R8 = 0x38;
    public const int Rbp = 0x40;
    public const int Rdi = 0x48;
    public const int Rsi = 0x50;
    public const int Rdx = 0x58;
    public const int Rcx = 0x60;
    public const int Rbx = 0x68;
    public const int Rax = 0x70;
    public const int Rflags = 0x78;

    /// <summary>RIP the guest resumes at (just past the syscall instruction).</summary>
    public const int ReturnRip = 0x80;

    /// <summary>The guest RSP at the syscall instruction.</summary>
    public const int GuestRsp = 0x88;

    public const int Size = 0x90;

    /// <summary>RFLAGS.CF: set on failure by the FreeBSD syscall return path.</summary>
    public const ulong CarryFlag = 1;
}
