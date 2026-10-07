// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Host.Posix;

/// <summary>
/// The parts of the POSIX Win64 stub emission that carry no OS-specific
/// declaration, constant or ABI detail: raw x86-64 byte emission and the
/// Win64-to-SysV argument shuffle. Linked by source into SharpEmu.Host.MacOS
/// and SharpEmu.Host.Linux (there is no shared Host.Posix assembly), so each
/// OS package keeps its own libc declarations, semaphore primitives and TLS
/// key widths next to the code that depends on them.
/// </summary>
internal static unsafe class PosixStubEmitter
{
    internal static void Emit(byte* page, ref int offset, params byte[] bytes)
    {
        foreach (var value in bytes)
        {
            page[offset++] = value;
        }
    }

    internal static void EmitMovRaxImm64(byte* page, ref int offset, nint value)
    {
        Emit(page, ref offset, 0x48, 0xB8);
        *(long*)(page + offset) = value;
        offset += sizeof(long);
    }

    /// <summary>
    /// Wraps a managed callback (compiled for the SysV ABI on POSIX .NET) in a
    /// thunk that accepts up to four integer arguments in the Win64 ABI the
    /// emitted x86-64 call sites use. Win64 passes args in rcx/rdx/r8/r9 and
    /// treats rdi/rsi as non-volatile; SysV expects rdi/rsi/rdx/rcx and
    /// clobbers them, so the thunk saves rdi/rsi, shuffles the registers, keeps
    /// the stack 16-byte aligned for the call, and forwards the rax result.
    /// </summary>
    internal static nint CreateWin64ToSysVThunk(IHostMemory memory, nint sysvTarget)
    {
        var page = (byte*)memory.Allocate(
            0,
            4096,
            HostPageProtection.ReadWriteExecute);
        if (page == null)
        {
            throw new OutOfMemoryException("Failed to allocate Win64->SysV thunk page");
        }

        var offset = 0;
        Emit(page, ref offset, 0x57);                   // push rdi
        Emit(page, ref offset, 0x56);                   // push rsi
        Emit(page, ref offset, 0x48, 0x89, 0xCF);       // mov rdi, rcx
        Emit(page, ref offset, 0x48, 0x89, 0xD6);       // mov rsi, rdx
        Emit(page, ref offset, 0x4C, 0x89, 0xC2);       // mov rdx, r8
        Emit(page, ref offset, 0x4C, 0x89, 0xC9);       // mov rcx, r9
        Emit(page, ref offset, 0x48, 0x83, 0xEC, 0x08); // sub rsp, 8 (realign to 16)
        EmitMovRaxImm64(page, ref offset, sysvTarget);  // mov rax, target
        Emit(page, ref offset, 0xFF, 0xD0);             // call rax
        Emit(page, ref offset, 0x48, 0x83, 0xC4, 0x08); // add rsp, 8
        Emit(page, ref offset, 0x5E);                   // pop rsi
        Emit(page, ref offset, 0x5F);                   // pop rdi
        Emit(page, ref offset, 0xC3);                   // ret

        if (!memory.Protect((ulong)page, 4096, HostPageProtection.ReadExecute, out _))
        {
            throw new InvalidOperationException("Failed to protect Win64->SysV thunk page");
        }

        memory.FlushInstructionCache((ulong)page, (ulong)offset);
        return (nint)page;
    }
}
