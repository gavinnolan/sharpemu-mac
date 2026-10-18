// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// The guest errno space and its single mapping onto the libkernel result ABI.
/// </summary>
/// <remarks>
/// The PS5 kernel is FreeBSD 11, so errno values follow
/// <c>sys/sys/errno.h</c>. libkernel reports a failed <c>sceKernel*</c> call as
/// <c>SCE_KERNEL_ERROR_* = 0x80020000 | errno</c>; the POSIX-named exports of
/// the same function return <c>-1</c> and store the errno in the per-thread
/// <c>__error()</c> slot; a raw <c>syscall</c> returns the errno in rax with the
/// carry flag set. Every one of those is derived from one errno here, so the
/// three flavours can never disagree.
/// </remarks>
public static class OrbisErrno
{
    public const int EPERM = 1;
    public const int ENOENT = 2;
    public const int ESRCH = 3;
    public const int EINTR = 4;
    public const int EIO = 5;
    public const int ENXIO = 6;
    public const int E2BIG = 7;
    public const int ENOEXEC = 8;
    public const int EBADF = 9;
    public const int ECHILD = 10;
    public const int EDEADLK = 11;
    public const int ENOMEM = 12;
    public const int EACCES = 13;
    public const int EFAULT = 14;
    public const int EBUSY = 16;
    public const int EEXIST = 17;
    public const int EXDEV = 18;
    public const int ENODEV = 19;
    public const int ENOTDIR = 20;
    public const int EISDIR = 21;
    public const int EINVAL = 22;
    public const int ENFILE = 23;
    public const int EMFILE = 24;
    public const int ENOTTY = 25;
    public const int EFBIG = 27;
    public const int ENOSPC = 28;
    public const int ESPIPE = 29;
    public const int EROFS = 30;
    public const int EMLINK = 31;
    public const int EPIPE = 32;
    public const int EDOM = 33;
    public const int ERANGE = 34;
    public const int EAGAIN = 35;
    public const int EINPROGRESS = 36;
    public const int EALREADY = 37;
    public const int ENOTSOCK = 38;
    public const int EOPNOTSUPP = 45;
    public const int ENOTCONN = 57;
    public const int ETIMEDOUT = 60;
    public const int ELOOP = 62;
    public const int ENAMETOOLONG = 63;
    public const int ENOTEMPTY = 66;
    public const int ENOSYS = 78;
    public const int EOVERFLOW = 84;
    public const int ECANCELED = 85;
    public const int EILSEQ = 86;

    /// <summary>Largest errno FreeBSD 11 defines (<c>ELAST</c>).</summary>
    public const int ELAST = 96;

    /// <summary>The <c>SCE_KERNEL_ERROR_*</c> facility base.</summary>
    public const int SceKernelErrorBase = unchecked((int)0x80020000);

    /// <summary>
    /// <c>SCE_KERNEL_ERROR_UNKNOWN</c>: the kernel facility's catch-all for a
    /// failure that has no errno.
    /// </summary>
    public const int SceKernelErrorUnknown = unchecked((int)0x80020000);

    /// <summary>Maps an errno to its <c>SCE_KERNEL_ERROR_*</c> value; 0 stays 0.</summary>
    public static int ToSceKernelError(int errno) =>
        errno == 0 ? 0 : SceKernelErrorBase | (errno & 0xFFFF);

    /// <summary>
    /// Recovers the errno a kernel-facility result encodes. Only values of the
    /// form <c>0x80020000 | errno</c> with errno in 1..ELAST qualify; anything
    /// else (another library's facility, a synthetic emulator code) does not.
    /// </summary>
    public static bool TryGetErrno(int sceResult, out int errno)
    {
        errno = 0;
        if ((sceResult & unchecked((int)0xFFFF0000)) != SceKernelErrorBase)
        {
            return false;
        }

        var low = sceResult & 0xFFFF;
        if (low < 1 || low > ELAST)
        {
            return false;
        }

        errno = low;
        return true;
    }

    /// <summary>
    /// The errno a POSIX-flavoured export reports for a failed kernel result.
    /// Results that carry no errno (another facility, the emulator's
    /// NOT_IMPLEMENTED or CPU_TRAP codes) become <paramref name="fallback"/>.
    /// </summary>
    public static int ToErrno(int sceResult, int fallback = EINVAL)
    {
        if (sceResult == (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED)
        {
            return ENOSYS;
        }

        return TryGetErrno(sceResult, out var errno) ? errno : fallback;
    }
}
