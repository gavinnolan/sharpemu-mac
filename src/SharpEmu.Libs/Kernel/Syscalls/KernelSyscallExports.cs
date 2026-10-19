// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel.Syscalls;

/// <summary>
/// The two ways guest code reaches the syscall table: libkernel's C
/// <c>syscall()</c> and a raw <c>syscall</c> instruction trapped by the loader.
/// </summary>
public static class KernelSyscallExports
{
    /// <summary>
    /// <c>long syscall(int number, ...)</c>. libkernel implements it as the
    /// indirect syscall 0, so the number is in rdi and the arguments follow in
    /// rsi, rdx, rcx, r8, r9 and the stack. Failure is -1 with errno set.
    /// </summary>
    [SysAbiExport(
        Nid = "W0Aa+HAwN6U",
        ExportName = "syscall",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Syscall(CpuContext ctx)
    {
        Span<ulong> arguments = stackalloc ulong[KernelSyscallDispatcher.ArgumentCount];
        arguments[0] = ctx[CpuRegister.Rdi];
        arguments[1] = ctx[CpuRegister.Rsi];
        arguments[2] = ctx[CpuRegister.Rdx];
        arguments[3] = ctx[CpuRegister.Rcx];
        arguments[4] = ctx[CpuRegister.R8];
        arguments[5] = ctx[CpuRegister.R9];
        for (var i = 0; i < KernelSyscallDispatcher.ArgumentCount - KernelSyscallDispatcher.RegisterArgumentCount; i++)
        {
            _ = ctx.TryGetImportStackArgument(i, out arguments[KernelSyscallDispatcher.RegisterArgumentCount + i]);
        }

        var outcome = KernelSyscallDispatcher.Invoke(
            ctx, number: 0, arguments, ctx[CpuRegister.Rdx]);
        if (outcome.Failed)
        {
            return KernelErrno.Posix(ctx, outcome.Errno);
        }

        ctx[CpuRegister.Rax] = outcome.Value;
        ctx[CpuRegister.Rdx] = outcome.SecondValue;
        return unchecked((int)outcome.Value);
    }

#pragma warning disable SHEM006 // Emulator-internal entry, deliberately not a PS5 symbol.
    /// <summary>
    /// Services a trapped raw <c>syscall</c> instruction. rdi points at the
    /// <see cref="GuestSyscallFrame"/> the trap thunk pushed; the number and
    /// arguments are read from it and the FreeBSD return state written back:
    /// rax = result or errno, rdx = second result, CF = failure, rcx = return
    /// RIP, r11 = RFLAGS. The thread's TLS errno is left as it was.
    /// </summary>
    [SysAbiExport(
        Nid = GuestSyscallFrame.TrapNid,
        ExportName = GuestSyscallFrame.TrapExportName,
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int RawSyscallTrap(CpuContext ctx)
    {
        var frame = ctx[CpuRegister.Rdi];
        if (!ctx.TryReadUInt64(frame + GuestSyscallFrame.Rax, out var number) ||
            !ctx.TryReadUInt64(frame + GuestSyscallFrame.Rdi, out var rdi) ||
            !ctx.TryReadUInt64(frame + GuestSyscallFrame.Rsi, out var rsi) ||
            !ctx.TryReadUInt64(frame + GuestSyscallFrame.Rdx, out var rdx) ||
            !ctx.TryReadUInt64(frame + GuestSyscallFrame.R10, out var r10) ||
            !ctx.TryReadUInt64(frame + GuestSyscallFrame.R8, out var r8) ||
            !ctx.TryReadUInt64(frame + GuestSyscallFrame.R9, out var r9) ||
            !ctx.TryReadUInt64(frame + GuestSyscallFrame.Rflags, out var rflags) ||
            !ctx.TryReadUInt64(frame + GuestSyscallFrame.ReturnRip, out var returnRip) ||
            !ctx.TryReadUInt64(frame + GuestSyscallFrame.GuestRsp, out var guestRsp))
        {
            Console.Error.WriteLine($"[LOADER][ERROR] raw syscall trap: unreadable frame at 0x{frame:X16}");
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        Span<ulong> arguments = stackalloc ulong[KernelSyscallDispatcher.ArgumentCount];
        arguments[0] = rdi;
        arguments[1] = rsi;
        arguments[2] = rdx;
        arguments[3] = r10;
        arguments[4] = r8;
        arguments[5] = r9;
        // Arguments past the sixth (and past the fifth for the indirect form)
        // are read from above the wrapper's return address, as amd64
        // cpu_fetch_syscall_args does. An unreadable slot reads as 0.
        for (var i = 0; i < KernelSyscallDispatcher.ArgumentCount - KernelSyscallDispatcher.RegisterArgumentCount; i++)
        {
            _ = ctx.TryReadUInt64(
                guestRsp + sizeof(ulong) * (ulong)(i + 1),
                out arguments[KernelSyscallDispatcher.RegisterArgumentCount + i]);
        }

        var outcome = KernelSyscallDispatcher.Invoke(
            ctx, unchecked((long)number), arguments, rdx);
        var resultFlags = outcome.Failed
            ? rflags | GuestSyscallFrame.CarryFlag
            : rflags & ~GuestSyscallFrame.CarryFlag;
        _ = ctx.TryWriteUInt64(frame + GuestSyscallFrame.Rax, outcome.Value);
        _ = ctx.TryWriteUInt64(frame + GuestSyscallFrame.Rdx, outcome.SecondValue);
        _ = ctx.TryWriteUInt64(frame + GuestSyscallFrame.Rflags, resultFlags);
        _ = ctx.TryWriteUInt64(frame + GuestSyscallFrame.R11, resultFlags);
        _ = ctx.TryWriteUInt64(frame + GuestSyscallFrame.Rcx, returnRip);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
#pragma warning restore SHEM006
}
