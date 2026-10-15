// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Kernel-facility result codes used by the Gen5 runtime. Every errno-backed
/// member is <c>SCE_KERNEL_ERROR_* = 0x80020000 | FreeBSD errno</c>
/// (<c>sys/sys/errno.h</c>); see <see cref="OrbisErrno"/> for the single
/// errno mapping shared by the sceKernel*, POSIX and raw-syscall flavours.
/// Only <see cref="ORBIS_GEN2_ERROR_NOT_IMPLEMENTED"/> and
/// <see cref="ORBIS_GEN2_ERROR_CPU_TRAP"/> are emulator-synthetic.
/// Prefixed with ORBIS_GEN2 to distinguish from PS4-oriented ORBIS_* codes
/// used by other emulators such as shadPS4.
/// </summary>
/// <remarks>
/// These are kernel codes. A library with its own facility (Pad, AudioOut,
/// SaveData, ...) documents its own SCE_xxx_ERROR_* values; using a member
/// here from such a library is a known approximation, not the SDK contract.
/// </remarks>
public enum OrbisGen2Result : int
{
    /// <summary>
    /// Indicates successful completion.
    /// </summary>
    ORBIS_GEN2_OK = 0,

    /// <summary>
    /// EPERM (1): the operation is not permitted for the calling thread.
    /// </summary>
    ORBIS_GEN2_ERROR_PERMISSION_DENIED = unchecked((int)0x80020001),

    /// <summary>
    /// ENOENT (2): the requested file, object or export was not found.
    /// </summary>
    ORBIS_GEN2_ERROR_NOT_FOUND = unchecked((int)0x80020002),

    /// <summary>ESRCH (3): no such process, thread or symbol.</summary>
    ORBIS_GEN2_ERROR_ESRCH = unchecked((int)0x80020003),

    /// <summary>EINTR (4): interrupted call.</summary>
    ORBIS_GEN2_ERROR_EINTR = unchecked((int)0x80020004),

    /// <summary>EIO (5): input/output error.</summary>
    ORBIS_GEN2_ERROR_EIO = unchecked((int)0x80020005),

    /// <summary>ENOEXEC (8): exec format error.</summary>
    ORBIS_GEN2_ERROR_ENOEXEC = unchecked((int)0x80020008),

    /// <summary>EBADF (9): bad file descriptor.</summary>
    ORBIS_GEN2_ERROR_EBADF = unchecked((int)0x80020009),

    /// <summary>ENOMEM (12): cannot allocate memory.</summary>
    ORBIS_GEN2_ERROR_ENOMEM = unchecked((int)0x8002000C),

    /// <summary>
    /// EINVAL (22): one or more arguments were invalid.
    /// </summary>
    ORBIS_GEN2_ERROR_INVALID_ARGUMENT = unchecked((int)0x80020016),

    /// <summary>
    /// EEXIST (17): an item already exists.
    /// </summary>
    ORBIS_GEN2_ERROR_ALREADY_EXISTS = unchecked((int)0x80020011),

    /// <summary>ENODEV (19): operation not supported by device.</summary>
    ORBIS_GEN2_ERROR_ENODEV = unchecked((int)0x80020013),

    /// <summary>ENOTDIR (20): a path component is not a directory.</summary>
    ORBIS_GEN2_ERROR_ENOTDIR = unchecked((int)0x80020014),

    /// <summary>EISDIR (21): the target is a directory.</summary>
    ORBIS_GEN2_ERROR_EISDIR = unchecked((int)0x80020015),

    /// <summary>EMFILE (24): the descriptor table is full.</summary>
    ORBIS_GEN2_ERROR_EMFILE = unchecked((int)0x80020018),

    /// <summary>ENOTTY (25): inappropriate ioctl for device.</summary>
    ORBIS_GEN2_ERROR_ENOTTY = unchecked((int)0x80020019),

    /// <summary>EFBIG (27): file too large.</summary>
    ORBIS_GEN2_ERROR_EFBIG = unchecked((int)0x8002001B),

    /// <summary>ENOSPC (28): no space left on device.</summary>
    ORBIS_GEN2_ERROR_ENOSPC = unchecked((int)0x8002001C),

    /// <summary>ESPIPE (29): illegal seek.</summary>
    ORBIS_GEN2_ERROR_ESPIPE = unchecked((int)0x8002001D),

    /// <summary>EROFS (30): read-only filesystem.</summary>
    ORBIS_GEN2_ERROR_EROFS = unchecked((int)0x8002001E),

    /// <summary>EPIPE (32): broken pipe.</summary>
    ORBIS_GEN2_ERROR_EPIPE = unchecked((int)0x80020020),

    /// <summary>ERANGE (34): result too large.</summary>
    ORBIS_GEN2_ERROR_ERANGE = unchecked((int)0x80020022),

    /// <summary>EOPNOTSUPP (45): operation not supported.</summary>
    ORBIS_GEN2_ERROR_EOPNOTSUPP = unchecked((int)0x8002002D),

    /// <summary>ELOOP (62): too many levels of symbolic links.</summary>
    ORBIS_GEN2_ERROR_ELOOP = unchecked((int)0x8002003E),

    /// <summary>ENAMETOOLONG (63): file name too long.</summary>
    ORBIS_GEN2_ERROR_ENAMETOOLONG = unchecked((int)0x8002003F),

    /// <summary>ENOTEMPTY (66): directory not empty.</summary>
    ORBIS_GEN2_ERROR_ENOTEMPTY = unchecked((int)0x80020042),

    /// <summary>ENOSYS (78): function not implemented.</summary>
    ORBIS_GEN2_ERROR_ENOSYS = unchecked((int)0x8002004E),

    /// <summary>EOVERFLOW (84): value too large for the data type.</summary>
    ORBIS_GEN2_ERROR_EOVERFLOW = unchecked((int)0x80020054),

    /// <summary>
    /// EDEADLK (11): completing the operation would deadlock.
    /// </summary>
    ORBIS_GEN2_ERROR_DEADLOCK = unchecked((int)0x8002000B),

    /// <summary>
    /// EACCES (13): the waited-on object was deleted while the caller was
    /// blocked on it. Matches the SCE kernel EACCES code that waiters of a
    /// deleted semaphore observe.
    /// </summary>
    ORBIS_GEN2_ERROR_DELETED = unchecked((int)0x8002000D),

    /// <summary>
    /// EBUSY (16): the target resource is busy.
    /// </summary>
    ORBIS_GEN2_ERROR_BUSY = unchecked((int)0x80020010),

    /// <summary>
    /// EAGAIN (35): the operation should be retried later.
    /// </summary>
    ORBIS_GEN2_ERROR_TRY_AGAIN = unchecked((int)0x80020023),

    /// <summary>
    /// ECANCELED (85): a blocked wait was canceled (e.g. sceKernelCancelSema).
    /// Matches the SCE kernel ECANCELED code that canceled semaphore waiters
    /// observe.
    /// </summary>
    ORBIS_GEN2_ERROR_CANCELED = unchecked((int)0x80020055),

    /// <summary>
    /// Emulator-synthetic: behavior is recognized but not implemented yet.
    /// Not an errno; POSIX exports report it as ENOSYS.
    /// </summary>
    ORBIS_GEN2_ERROR_NOT_IMPLEMENTED = unchecked((int)0x8002FFFF),

    /// <summary>
    /// ETIMEDOUT (60): the operation timed out.
    /// </summary>
    ORBIS_GEN2_ERROR_TIMED_OUT = unchecked((int)0x8002003C),

    /// <summary>
    /// EFAULT (14): a guest pointer argument could not be read or written.
    /// </summary>
    ORBIS_GEN2_ERROR_MEMORY_FAULT = unchecked((int)0x8002000E),

    /// <summary>
    /// Emulator-synthetic: CPU execution trapped on an unsupported instruction.
    /// </summary>
    ORBIS_GEN2_ERROR_CPU_TRAP = unchecked((int)0x80020102),
}
