// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// How a libkernel export reports failure to its caller.
/// </summary>
public enum KernelAbiFlavor
{
    /// <summary>
    /// <c>sceKernel*</c>: return <c>0x80020000 | errno</c>, leave errno alone.
    /// </summary>
    Sce,

    /// <summary>
    /// POSIX name (<c>open</c>, <c>stat</c>, ...): return -1 and store errno
    /// in the calling thread's <c>__error()</c> slot.
    /// </summary>
    Posix,
}

/// <summary>
/// The one adapter from an errno to an export's return value. Handlers decide
/// the errno; this decides how the flavour presents it, so the sceKernel* and
/// POSIX forms of one function cannot drift apart (SPEC §5.1).
/// </summary>
public static class KernelErrno
{
    /// <summary>
    /// Reports <paramref name="errno"/> in <paramref name="flavor"/>. Returns
    /// the value the export should return; rax is written to match.
    /// </summary>
    public static int Fail(CpuContext ctx, KernelAbiFlavor flavor, int errno)
    {
        if (flavor == KernelAbiFlavor.Posix)
        {
            KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
            ctx[CpuRegister.Rax] = ulong.MaxValue;
            return -1;
        }

        var result = OrbisErrno.ToSceKernelError(errno);
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)result);
        return result;
    }

    /// <summary>
    /// POSIX failure from an errno: errno is stored and -1 returned.
    /// </summary>
    public static int Posix(CpuContext ctx, int errno) =>
        Fail(ctx, KernelAbiFlavor.Posix, errno);

    /// <summary>
    /// <c>SCE_KERNEL_ERROR_*</c> for <paramref name="errno"/>.
    /// </summary>
    public static int Sce(int errno) => OrbisErrno.ToSceKernelError(errno);

    /// <summary>
    /// Re-presents the result of a <c>sceKernel*</c>-flavoured core in
    /// <paramref name="flavor"/>. Success passes through untouched (the core
    /// may already have written a value such as an fd into rax). For POSIX,
    /// an errno-backed failure becomes -1 plus that errno; a result with no
    /// errno becomes <paramref name="fallbackErrno"/>.
    /// </summary>
    public static int FromSceResult(
        CpuContext ctx,
        KernelAbiFlavor flavor,
        int sceResult,
        int fallbackErrno = OrbisErrno.EINVAL)
    {
        if (sceResult >= 0 || flavor == KernelAbiFlavor.Sce)
        {
            return sceResult;
        }

        return Posix(ctx, OrbisErrno.ToErrno(sceResult, fallbackErrno));
    }

    /// <summary>
    /// The errno the calling thread currently sees, or 0 when the TLS slot is
    /// unavailable.
    /// </summary>
    public static int Current(CpuContext ctx) =>
        KernelRuntimeCompatExports.TryGetErrno(ctx, out var value) ? value : 0;
}
