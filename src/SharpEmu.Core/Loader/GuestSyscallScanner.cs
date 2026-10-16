// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Core.Loader;

/// <summary>A raw <c>syscall</c> found in guest code: the offset of its <c>0F 05</c> and its number.</summary>
internal readonly record struct GuestSyscallSite(int Offset, int Number);

/// <summary>
/// Finds raw <c>syscall</c> instructions in a code buffer by the FreeBSD
/// amd64 wrapper idiom, never by the two opcode bytes alone.
/// </summary>
/// <remarks>
/// libkernel's and libc's syscall stubs (FreeBSD <c>SYS.h</c> RSYSCALL) are
/// <c>mov eax|rax, N; mov r10, rcx; syscall; jb cerror; ret</c>. A site is
/// accepted only when the <c>0F 05</c> is directly preceded by a
/// <c>mov eax, imm32</c> (<c>B8</c>) or <c>mov rax, imm32</c>
/// (<c>48 C7 C0</c>) loading a number in the table's range, either right
/// before it or with <c>mov r10, rcx</c> (<c>49 89 CA</c> / <c>4C 8B D1</c>)
/// on either side of the load. A bare load with no r10 move must also be
/// followed by <c>jb</c>/<c>jae</c> (rel8 or rel32) or <c>ret</c>, the only
/// things a stub does next. The upper three bytes of the immediate are then
/// fixed (number &lt;= 0x2D2), so a match pins 7 to 12 specific bytes, which
/// keeps literal pools, jump tables and the inside of other instructions'
/// immediates or displacements from matching. What it cannot prove is the
/// instruction boundary before the load; the trade-off is in
/// <see cref="GuestSyscallPatcher"/>.
/// </remarks>
internal static class GuestSyscallScanner
{
    public static List<GuestSyscallSite> Scan(ReadOnlySpan<byte> code, int maxNumber)
    {
        var sites = new List<GuestSyscallSite>();
        for (var offset = 0; offset + 1 < code.Length; offset++)
        {
            if (code[offset] != 0x0F || code[offset + 1] != 0x05)
            {
                continue;
            }

            if (TryMatch(code, offset, maxNumber, out var number))
            {
                sites.Add(new GuestSyscallSite(offset, number));
                offset++;
            }
        }

        return sites;
    }

    private static bool TryMatch(ReadOnlySpan<byte> code, int syscallOffset, int maxNumber, out int number)
    {
        // mov r10, rcx; syscall  ->  the load sits before the r10 move.
        if (IsMovR10Rcx(code, syscallOffset - 3) &&
            TryLoadEndingAt(code, syscallOffset - 3, maxNumber, out number))
        {
            return true;
        }

        if (TryLoadEndingAt(code, syscallOffset, maxNumber, out number, out var loadStart))
        {
            // mov r10, rcx; mov eax, N; syscall
            if (IsMovR10Rcx(code, loadStart - 3))
            {
                return true;
            }

            // A bare load has to be followed by what a stub does next.
            return IsStubTrailer(code, syscallOffset + 2);
        }

        number = 0;
        return false;
    }

    private static bool TryLoadEndingAt(ReadOnlySpan<byte> code, int end, int maxNumber, out int number) =>
        TryLoadEndingAt(code, end, maxNumber, out number, out _);

    private static bool TryLoadEndingAt(
        ReadOnlySpan<byte> code,
        int end,
        int maxNumber,
        out int number,
        out int start)
    {
        number = 0;
        start = end - 7;
        // mov rax, imm32 (sign-extended): 48 C7 C0 imm32
        if (start >= 0 && code[start] == 0x48 && code[start + 1] == 0xC7 && code[start + 2] == 0xC0 &&
            TryImmediate(code[(start + 3)..end], maxNumber, out number))
        {
            return true;
        }

        start = end - 5;
        // mov eax, imm32: B8 imm32
        if (start >= 0 && code[start] == 0xB8 &&
            TryImmediate(code[(start + 1)..end], maxNumber, out number))
        {
            return true;
        }

        number = 0;
        start = -1;
        return false;
    }

    private static bool TryImmediate(ReadOnlySpan<byte> immediate, int maxNumber, out int number)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(immediate);
        number = (int)Math.Min(value, int.MaxValue);
        return value <= (uint)maxNumber;
    }

    // mov r10, rcx: 49 89 CA (MOV r/m64, r64) or 4C 8B D1 (MOV r64, r/m64).
    private static bool IsMovR10Rcx(ReadOnlySpan<byte> code, int start) =>
        start >= 0 && start + 3 <= code.Length &&
        ((code[start] == 0x49 && code[start + 1] == 0x89 && code[start + 2] == 0xCA) ||
         (code[start] == 0x4C && code[start + 1] == 0x8B && code[start + 2] == 0xD1));

    // jb/jae rel8 (72/73), jb/jae rel32 (0F 82/0F 83), ret (C3).
    private static bool IsStubTrailer(ReadOnlySpan<byte> code, int start)
    {
        if (start >= code.Length)
        {
            return false;
        }

        var opcode = code[start];
        if (opcode is 0x72 or 0x73 or 0xC3)
        {
            return true;
        }

        return opcode == 0x0F && start + 1 < code.Length && code[start + 1] is 0x82 or 0x83;
    }
}
