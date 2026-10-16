// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel.Syscalls;

/// <summary>Outcome of one syscall in the FreeBSD sense: a value pair, or an errno.</summary>
internal readonly record struct SyscallOutcome(bool Failed, ulong Value, ulong SecondValue)
{
    public int Errno => Failed ? unchecked((int)Value) : 0;

    public static SyscallOutcome Error(int errno, ulong secondValue) =>
        new(true, unchecked((ulong)errno), secondValue);
}

/// <summary>
/// Services a syscall number against <see cref="OrbisSyscallTable"/>. Both the
/// trapped raw <c>syscall</c> instruction and the libc <c>syscall()</c> export
/// come through here.
/// </summary>
/// <remarks>
/// A forwarded slot is serviced by the first of its libkernel exports the
/// module manager has registered, looked up by NID on each call, so a wrapper
/// added to the HLE later serves the raw syscall too. The export runs on the
/// calling guest context with the syscall arguments in the SysV argument
/// registers, and its result is decoded according to its error flavour.
/// </remarks>
internal static class KernelSyscallDispatcher
{
    /// <summary>Register arguments a syscall reads; the rest come from the guest stack.</summary>
    public const int RegisterArgumentCount = 6;

    /// <summary>Arguments handed to an export: six registers plus six stack slots.</summary>
    public const int ArgumentCount = 12;

    private const int IndirectSyscall = 0x000;
    private const int IndirectQuadSyscall = 0x0C6;

    private static readonly ConcurrentDictionary<long, byte> s_reported = new();

    /// <summary>
    /// Resolves an export by NID. Published by the loader from the live module
    /// manager; null until then, which leaves every forwarded slot unserviced.
    /// </summary>
    public static Func<string, ExportedFunction?>? ExportLookup { get; set; }

    /// <summary>
    /// Runs syscall <paramref name="number"/>.
    /// </summary>
    /// <param name="ctx">The calling guest thread's context.</param>
    /// <param name="number">The syscall number (rax for the raw form).</param>
    /// <param name="arguments">
    /// <see cref="ArgumentCount"/> values: the six register arguments in syscall
    /// order (rdi, rsi, rdx, r10, r8, r9), then the qwords above the caller's
    /// return address on its stack.
    /// </param>
    /// <param name="incomingRdx">
    /// The caller's rdx, which FreeBSD returns as the second value unless the
    /// syscall sets one.
    /// </param>
    /// <remarks>
    /// The thread's TLS errno is left as it was: the kernel never writes it,
    /// so an export's errno store is undone. The libc wrapper sets errno from
    /// the outcome itself.
    /// </remarks>
    public static SyscallOutcome Invoke(
        CpuContext ctx,
        long number,
        ReadOnlySpan<ulong> arguments,
        ulong incomingRdx)
    {
        if (arguments.Length != ArgumentCount)
        {
            throw new ArgumentException($"Expected {ArgumentCount} arguments.", nameof(arguments));
        }

        if (!OrbisSyscallTable.TryGet(number, out var entry))
        {
            // FreeBSD maps an out-of-range number to sysent[0], which is nosys.
            Report(number, $"#{number}", "out of range: nosys, SIGSYS not delivered (docs/deviations.md)");
            return SyscallOutcome.Error(OrbisErrno.ENOSYS, incomingRdx);
        }

        if (entry.Kind == OrbisSyscallKind.Indirect)
        {
            // cpu_fetch_syscall_args unwraps once: the number is the first
            // argument and the rest shift down. A nested indirect number lands
            // on sysent[0]/[198] themselves, which are nosys.
            var inner = unchecked((long)arguments[0]);
            Span<ulong> shifted = stackalloc ulong[ArgumentCount];
            arguments[1..].CopyTo(shifted);
            if (OrbisSyscallTable.TryGet(inner, out var innerEntry) && innerEntry.Kind == OrbisSyscallKind.Indirect)
            {
                Report(inner, innerEntry.Name, "nested indirect syscall: nosys, SIGSYS not delivered (docs/deviations.md)");
                return SyscallOutcome.Error(OrbisErrno.ENOSYS, incomingRdx);
            }

            return Invoke(ctx, inner, shifted, incomingRdx);
        }

        switch (entry.Kind)
        {
            case OrbisSyscallKind.NoSys:
                Report(number, entry.Name, "nosys slot: ENOSYS, SIGSYS not delivered (docs/deviations.md)");
                return SyscallOutcome.Error(OrbisErrno.ENOSYS, incomingRdx);
            case OrbisSyscallKind.Compat:
                Report(number, entry.Name, "FreeBSD compat shim is not modelled: ENOSYS");
                return SyscallOutcome.Error(OrbisErrno.ENOSYS, incomingRdx);
            case OrbisSyscallKind.AbiMismatch:
                Report(number, entry.Name, "no export with the raw syscall ABI: ENOSYS");
                return SyscallOutcome.Error(OrbisErrno.ENOSYS, incomingRdx);
            case OrbisSyscallKind.Unmapped:
                Report(number, entry.Name, "libkernel-internal syscall with no HLE equivalent: ENOSYS");
                return SyscallOutcome.Error(OrbisErrno.ENOSYS, incomingRdx);
        }

        if (!TryResolve(entry, ctx.TargetGeneration, out var export, out var target))
        {
            Report(number, entry.Name, "no registered HLE export (" +
                string.Join(", ", entry.Targets.Select(t => $"{t.ExportName}/{t.Nid}")) + "): ENOSYS");
            return SyscallOutcome.Error(OrbisErrno.ENOSYS, incomingRdx);
        }

        return Call(ctx, export, target.Flavor, arguments, incomingRdx);
    }

    private static bool TryResolve(
        OrbisSyscallEntry entry,
        Generation generation,
        out ExportedFunction export,
        out OrbisSyscallTarget target)
    {
        var lookup = ExportLookup;
        if (lookup is not null)
        {
            foreach (var candidate in entry.Targets)
            {
                if (lookup(candidate.Nid) is { } found && (found.Target & generation) != 0)
                {
                    export = found;
                    target = candidate;
                    return true;
                }
            }
        }

        export = null!;
        target = default;
        return false;
    }

    private static SyscallOutcome Call(
        CpuContext ctx,
        ExportedFunction export,
        KernelAbiFlavor flavor,
        ReadOnlySpan<ulong> arguments,
        ulong incomingRdx)
    {
        // The export runs on the caller's context: park the registers it uses
        // as arguments and the stack-argument view, and put them back after.
        var savedRdi = ctx[CpuRegister.Rdi];
        var savedRsi = ctx[CpuRegister.Rsi];
        var savedRdx = ctx[CpuRegister.Rdx];
        var savedRcx = ctx[CpuRegister.Rcx];
        var savedR8 = ctx[CpuRegister.R8];
        var savedR9 = ctx[CpuRegister.R9];
        var savedRax = ctx[CpuRegister.Rax];
        var savedRaxWritten = ctx.WasRaxWritten;
        Span<ulong> savedStack = stackalloc ulong[6];
        var hadStack = ctx.TryGetImportStackArgument(0, out savedStack[0]);
        for (var i = 1; hadStack && i < savedStack.Length; i++)
        {
            _ = ctx.TryGetImportStackArgument(i, out savedStack[i]);
        }

        var errnoAvailable = KernelRuntimeCompatExports.TryGetErrno(ctx, out var savedErrno);
        if (errnoAvailable)
        {
            // Start from 0 so a -1 that set no errno is not blamed on a stale one.
            KernelRuntimeCompatExports.TrySetErrno(ctx, 0);
        }

        ulong value;
        ulong second;
        int errnoAfter = 0;
        try
        {
            ctx[CpuRegister.Rdi] = arguments[0];
            ctx[CpuRegister.Rsi] = arguments[1];
            ctx[CpuRegister.Rdx] = arguments[2];
            ctx[CpuRegister.Rcx] = arguments[3];
            ctx[CpuRegister.R8] = arguments[4];
            ctx[CpuRegister.R9] = arguments[5];
            ctx.SetImportStackArguments(
                arguments[6], arguments[7], arguments[8], arguments[9], arguments[10], arguments[11]);

            ctx.ClearRaxWriteFlag();
            var returned = export.Function(ctx);
            value = ctx.WasRaxWritten ? ctx[CpuRegister.Rax] : unchecked((ulong)(long)returned);
            var rdxAfter = ctx[CpuRegister.Rdx];
            second = rdxAfter == arguments[2] ? incomingRdx : rdxAfter;
            if (errnoAvailable)
            {
                _ = KernelRuntimeCompatExports.TryGetErrno(ctx, out errnoAfter);
            }
        }
        finally
        {
            ctx[CpuRegister.Rdi] = savedRdi;
            ctx[CpuRegister.Rsi] = savedRsi;
            ctx[CpuRegister.Rdx] = savedRdx;
            ctx[CpuRegister.Rcx] = savedRcx;
            ctx[CpuRegister.R8] = savedR8;
            ctx[CpuRegister.R9] = savedR9;
            ctx[CpuRegister.Rax] = savedRax;
            if (!savedRaxWritten)
            {
                ctx.ClearRaxWriteFlag();
            }

            if (hadStack)
            {
                ctx.SetImportStackArguments(
                    savedStack[0], savedStack[1], savedStack[2], savedStack[3], savedStack[4], savedStack[5]);
            }
            else
            {
                ctx.ClearImportStackArguments();
            }

            if (errnoAvailable)
            {
                KernelRuntimeCompatExports.TrySetErrno(ctx, savedErrno);
            }
        }

        if (TryDecodeFailure(flavor, value, errnoAfter, out var errno))
        {
            return SyscallOutcome.Error(errno, incomingRdx);
        }

        return new SyscallOutcome(false, value, second);
    }

    /// <summary>
    /// Whether an export's result is a failure, and its errno. POSIX flavour
    /// fails with -1 and the errno it stored; sceKernel flavour fails with any
    /// negative 32-bit result, decoded by <see cref="OrbisErrno.ToErrno"/>. A
    /// POSIX export that returns an SCE kernel code (or NOT_IMPLEMENTED) is
    /// decoded the same way, so a mis-flavoured HLE export still gives the
    /// guest a real errno instead of a "successful" 0xFFFFFFFF8002xxxx.
    /// </summary>
    internal static bool TryDecodeFailure(KernelAbiFlavor flavor, ulong value, int storedErrno, out int errno)
    {
        var signed = unchecked((long)value);
        if (flavor == KernelAbiFlavor.Posix && signed == -1)
        {
            errno = storedErrno > 0 ? storedErrno : OrbisErrno.EINVAL;
            return true;
        }

        if (signed is < 0 and >= int.MinValue)
        {
            var code = unchecked((int)signed);
            if (flavor == KernelAbiFlavor.Sce ||
                OrbisErrno.TryGetErrno(code, out _) ||
                code == (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED)
            {
                errno = OrbisErrno.ToErrno(code);
                return true;
            }
        }

        errno = 0;
        return false;
    }

    private static void Report(long number, string name, string what)
    {
        if (s_reported.TryAdd(number, 0))
        {
            Console.Error.WriteLine($"[LOADER][WARN] syscall 0x{number:X} {name}: {what}");
        }
    }

    internal static void ResetReportsForTests() => s_reported.Clear();
}
