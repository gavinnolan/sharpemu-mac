// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Text;

namespace SharpEmu.GpuRemote;

/// <summary>
/// The Darwin calls the transport makes.
/// </summary>
/// <remarks>
/// Ordinary imports for everything that runs on a normal thread. The calls a
/// <see cref="SyncCallTable"/> request can make from inside a signal handler - the two
/// wait-on-address entry points, <c>nanosleep</c> and the monotonic clock - are resolved once
/// into raw function pointers instead: the first call through a <c>DllImport</c> binds and may
/// generate its marshalling stub, which allocates, and nothing may allocate in signal context.
/// A call through a function pointer is compiled into the caller and needs nothing at run time.
/// </remarks>
internal static unsafe class RemoteNative
{
    public const int O_RDWR = 0x0002;
    public const int O_CREAT = 0x0200;
    public const int O_EXCL = 0x0800;
    public const int PROT_READ = 0x01;
    public const int PROT_WRITE = 0x02;
    public const int MAP_SHARED = 0x0001;

    public const int ENOENT = 2;
    public const int ESRCH = 3;
    public const int EINTR = 4;
    public const int ECHILD = 10;
    public const int EEXIST = 17;
    public const int ETIMEDOUT = 60;

    public const int SIGKILL = 9;
    public const int SIGTERM = 15;
    public const int SIGSTOP = 17;
    public const int WNOHANG = 1;

    public const short POSIX_SPAWN_SETSIGDEF = 0x0004;
    public const short POSIX_SPAWN_SETSIGMASK = 0x0008;
    public const short POSIX_SPAWN_CLOEXEC_DEFAULT = 0x4000;

    // CPU_TYPE_ARM | CPU_ARCH_ABI64, and CPU_SUBTYPE_ANY.
    public const int CPU_TYPE_ARM64 = 0x0100_000C;
    public const int CPU_SUBTYPE_ANY = -1;

    public const short EVFILT_PROC = -5;
    public const ushort EV_ADD = 0x0001;
    public const ushort EV_ONESHOT = 0x0010;
    public const uint NOTE_EXIT = 0x8000_0000;

    // CLOCK_MONOTONIC_RAW: nanoseconds, unaffected by clock adjustments; Rosetta passes it through.
    private const int CLOCK_MONOTONIC_RAW = 4;

    public static readonly nint MapFailed = -1;

    // Signal-path entry points; see the type remarks. Null when the symbol is missing.
    public static readonly delegate* unmanaged<uint, void*, ulong, ulong, ulong, int> UlockWait2;
    public static readonly delegate* unmanaged[SuppressGCTransition]<uint, void*, ulong, int> UlockWake;
    public static readonly delegate* unmanaged<Timespec*, Timespec*, int> NanoSleep;
    private static readonly delegate* unmanaged[SuppressGCTransition]<int, ulong> ClockGetTimeNsec;

    static RemoteNative()
    {
        if (!OperatingSystem.IsMacOS() || !NativeLibrary.TryLoad("/usr/lib/libSystem.B.dylib", out var system))
        {
            return;
        }

        if (NativeLibrary.TryGetExport(system, "__ulock_wait2", out var wait) &&
            NativeLibrary.TryGetExport(system, "__ulock_wake", out var wake))
        {
            UlockWait2 = (delegate* unmanaged<uint, void*, ulong, ulong, ulong, int>)wait;
            UlockWake = (delegate* unmanaged[SuppressGCTransition]<uint, void*, ulong, int>)wake;
        }

        if (NativeLibrary.TryGetExport(system, "nanosleep", out var sleep))
        {
            NanoSleep = (delegate* unmanaged<Timespec*, Timespec*, int>)sleep;
        }

        if (NativeLibrary.TryGetExport(system, "clock_gettime_nsec_np", out var clock))
        {
            ClockGetTimeNsec = (delegate* unmanaged[SuppressGCTransition]<int, ulong>)clock;
        }
    }

    /// <summary>Monotonic nanoseconds; async-signal-safe (a commpage read through a pointer).</summary>
    public static long MonotonicNanoseconds()
    {
        if (ClockGetTimeNsec != null)
        {
            return (long)ClockGetTimeNsec(CLOCK_MONOTONIC_RAW);
        }

        // Only off macOS, where nothing runs in signal context.
        return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
    }

    /// <summary>
    /// <c>shm_open</c>, which is variadic: <c>(const char *name, int oflag, ...)</c>.
    /// </summary>
    /// <remarks>
    /// The x86-64 System V convention passes a variadic int in the register a fixed third
    /// argument would use, so the plain three-argument import is right there (and is what
    /// SharpEmu.Host.MacOS uses). Apple's arm64 convention passes every variadic argument on the
    /// stack, so a fixed third argument leaves <c>shm_open</c> reading the mode from whatever is
    /// at [sp] and the object is created with garbage permissions. Padding the call out to the
    /// eight argument registers puts the ninth argument, the mode, in the first stack slot -
    /// exactly where the callee's <c>va_arg</c> looks.
    /// </remarks>
    public static int ShmOpen(string name, int flags, int mode)
    {
        var bytes = NullTerminated(name);
        fixed (byte* path = bytes)
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? shm_open_arm64(path, flags, 0, 0, 0, 0, 0, 0, mode)
                : shm_open(path, flags, mode);
        }
    }

    public static int ShmUnlink(string name)
    {
        var bytes = NullTerminated(name);
        fixed (byte* path = bytes)
        {
            return shm_unlink(path);
        }
    }

    /// <summary>Reads <c>st_mode</c> and <c>st_size</c> with the 64-bit-inode <c>struct stat</c>.</summary>
    public static bool TryGetFileStatus(int descriptor, out int mode, out long size)
    {
        // arm64 only has the 64-bit-inode layout; on x86-64 the plain "fstat" symbol is the
        // legacy 32-bit-inode one and the modern layout is "fstat$INODE64".
        var status = stackalloc byte[StatSize];
        var result = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? fstat(descriptor, status)
            : fstat_inode64(descriptor, status);
        if (result != 0)
        {
            mode = 0;
            size = 0;
            return false;
        }

        mode = *(ushort*)(status + StatModeOffset);
        size = *(long*)(status + StatSizeOffset);
        return true;
    }

    /// <summary>
    /// 1 when this process runs translated by Rosetta, 0 when native, -1 when unknown
    /// (<c>sysctl.proc_translated</c> does not exist on Intel Macs, which are never translated).
    /// </summary>
    public static int ProcessTranslated()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return -1;
        }

        int value = 0;
        nuint length = sizeof(int);
        fixed (byte* name = "sysctl.proc_translated\0"u8)
        {
            if (sysctlbyname(name, &value, &length, null, 0) != 0)
            {
                return Marshal.GetLastPInvokeError() == ENOENT ? 0 : -1;
            }
        }

        return value;
    }

    public static byte[] NullTerminated(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    // struct stat (64-bit inodes): st_dev(4) st_mode(2) st_nlink(2) st_ino(8) st_uid st_gid
    // st_rdev(4 each, plus 4 of padding) then four timespecs (16 each), st_size at 96; 144 in all.
    private const int StatSize = 144;
    private const int StatModeOffset = 4;
    private const int StatSizeOffset = 96;

    [StructLayout(LayoutKind.Sequential)]
    public struct Timespec
    {
        public nint Seconds;
        public nint Nanoseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEvent
    {
        public nuint Ident;
        public short Filter;
        public ushort Flags;
        public uint FilterFlags;
        public nint Data;
        public nint UserData;
    }

    [DllImport("libc", EntryPoint = "shm_open", SetLastError = true)]
    private static extern int shm_open(byte* name, int flags, int mode);

    [DllImport("libc", EntryPoint = "shm_open", SetLastError = true)]
    private static extern int shm_open_arm64(byte* name, int flags, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, nint variadicMode);

    [DllImport("libc", SetLastError = true)]
    private static extern int shm_unlink(byte* name);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int descriptor, byte* status);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int fstat_inode64(int descriptor, byte* status);

    [DllImport("libc", SetLastError = true)]
    public static extern int ftruncate(int descriptor, long length);

    [DllImport("libc", SetLastError = true)]
    public static extern nint mmap(nint address, nuint length, int protection, int flags, int descriptor, long offset);

    [DllImport("libc", SetLastError = true)]
    public static extern int munmap(nint address, nuint length);

    [DllImport("libc", SetLastError = true)]
    public static extern int close(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int sysctlbyname(byte* name, void* oldValue, nuint* oldLength, void* newValue, nuint newLength);

    [DllImport("libc")]
    public static extern int getppid();

    [DllImport("libc", SetLastError = true)]
    public static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true)]
    public static extern int waitpid(int pid, int* status, int options);

    [DllImport("libc", SetLastError = true)]
    public static extern int kqueue();

    [DllImport("libc", SetLastError = true)]
    public static extern int kevent(int kq, KEvent* changes, int changeCount, KEvent* events, int eventCount, Timespec* timeout);

    // posix_spawn returns the error number instead of setting errno.
    [DllImport("libc")]
    public static extern int posix_spawn(int* pid, byte* path, nint* fileActions, nint* attributes, byte** argv, byte** envp);

    [DllImport("libc")]
    public static extern int posix_spawnattr_init(nint* attributes);

    [DllImport("libc")]
    public static extern int posix_spawnattr_destroy(nint* attributes);

    [DllImport("libc")]
    public static extern int posix_spawnattr_setflags(nint* attributes, short flags);

    [DllImport("libc")]
    public static extern int posix_spawnattr_setsigmask(nint* attributes, uint* mask);

    [DllImport("libc")]
    public static extern int posix_spawnattr_setsigdefault(nint* attributes, uint* mask);

    [DllImport("libc")]
    public static extern int posix_spawnattr_setarchpref_np(nint* attributes, nuint count, int* cpuTypes, int* cpuSubtypes, nuint* copied);

    [DllImport("libc")]
    public static extern int posix_spawn_file_actions_init(nint* actions);

    [DllImport("libc")]
    public static extern int posix_spawn_file_actions_destroy(nint* actions);

    [DllImport("libc")]
    public static extern int posix_spawn_file_actions_addinherit_np(nint* actions, int descriptor);
}
