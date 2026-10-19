// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Threading;

/// <summary>
/// FreeBSD errno values (sys/sys/errno.h) and the result adapter between them and the
/// two guest conventions: <c>sce*</c> exports return <c>0x8002_0000 | errno</c> (an
/// <see cref="OrbisGen2Result"/>), POSIX-named <c>pthread_*</c> exports return the plain
/// errno, and <c>sem_*</c>/<c>nanosleep</c>-style exports return -1 and set errno.
/// Sync cores compute an errno once; each export maps it with the convention it owns.
/// </summary>
public static class GuestErrno
{
    public const int EPERM = 1;
    public const int ENOENT = 2;
    public const int ESRCH = 3;
    public const int EINTR = 4;
    public const int EDEADLK = 11;
    public const int ENOMEM = 12;
    public const int EACCES = 13;
    public const int EFAULT = 14;
    public const int EBUSY = 16;
    public const int EEXIST = 17;
    public const int EINVAL = 22;
    public const int EAGAIN = 35;
    public const int ENOTSUP = 45;
    public const int ETIMEDOUT = 60;
    public const int ENOSYS = 78;
    public const int EOVERFLOW = 84;
    public const int ECANCELED = 85;

    /// <summary>
    /// The <c>sce*</c> code for <paramref name="errno"/>. Named enum members are used where
    /// one exists for the errno (so a global fix of a member's value applies here too);
    /// any other errno maps to <c>0x8002_0000 | errno</c>.
    /// </summary>
    public static int ToSce(int errno) => errno switch
    {
        0 => (int)OrbisGen2Result.ORBIS_GEN2_OK,
        EPERM => (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED,
        ENOENT => (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
        EINVAL => (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
        EEXIST => (int)OrbisGen2Result.ORBIS_GEN2_ERROR_ALREADY_EXISTS,
        EDEADLK => (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK,
        EACCES => (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED,
        EBUSY => (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY,
        EAGAIN => (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN,
        ECANCELED => (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED,
        ETIMEDOUT => (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
        EFAULT => (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
        _ => unchecked((int)(0x80020000u | (uint)errno)),
    };

    /// <summary>Recovers the errno from an <c>sce*</c> code (inverse of <see cref="ToSce"/>).</summary>
    public static int FromSce(int result) => result switch
    {
        (int)OrbisGen2Result.ORBIS_GEN2_OK => 0,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED => EPERM,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND => ENOENT,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT => EINVAL,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_ALREADY_EXISTS => EEXIST,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK => EDEADLK,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED => EACCES,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY => EBUSY,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN => EAGAIN,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED => ECANCELED,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT => ETIMEDOUT,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT => EFAULT,
        _ when (result & unchecked((int)0xFFFF0000)) == unchecked((int)0x80020000) => result & 0xFFFF,
        _ => EINVAL,
    };

    /// <summary>The errno a finished wait reports.</summary>
    public static int FromWaitStatus(GuestWaitStatus status) => status switch
    {
        GuestWaitStatus.Satisfied => 0,
        GuestWaitStatus.TimedOut => ETIMEDOUT,
        GuestWaitStatus.Canceled => ECANCELED,
        GuestWaitStatus.Deleted => EACCES,
        GuestWaitStatus.Interrupted => EINTR,
        _ => EINVAL,
    };

    /// <summary>Stores an <c>sce*</c> result in RAX and returns it.</summary>
    public static int ReturnSce(CpuContext ctx, int errno)
    {
        var value = ToSce(errno);
        ctx[CpuRegister.Rax] = unchecked((ulong)value);
        return value;
    }

    /// <summary>Stores a POSIX <c>pthread_*</c> result (the errno itself) in RAX and returns it.</summary>
    public static int ReturnPosix(CpuContext ctx, int errno)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)errno);
        return errno;
    }

    /// <summary>libc convention (<c>sem_*</c>, <c>nanosleep</c>): 0, or -1 with errno set.</summary>
    public static int ReturnLibc(CpuContext ctx, int errno)
    {
        if (errno == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        _ = SharpEmu.Libs.Kernel.KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        ctx[CpuRegister.Rax] = unchecked((ulong)-1L);
        return -1;
    }

    /// <summary>Stores a <c>pthread_*</c>-style or <c>sce*</c>-style result by convention.</summary>
    public static int Return(CpuContext ctx, int errno, bool posix) =>
        posix ? ReturnPosix(ctx, errno) : ReturnSce(ctx, errno);
}
