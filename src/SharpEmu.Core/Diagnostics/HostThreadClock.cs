// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Core.Diagnostics;

/// <summary>
/// Host thread name and CPU clock for the HLE profiler (macOS only; elsewhere the
/// name falls back to the managed id and the clock reports -1).
/// </summary>
internal static partial class HostThreadClock
{
    [ThreadStatic] private static uint _machThread;

    /// <summary>The calling thread's Mach port, or 0 where there is none.</summary>
    internal static uint CurrentThread()
    {
        if (!OperatingSystem.IsMacOS()) return 0;
        return _machThread != 0 ? _machThread : _machThread = mach_thread_self();
    }

    internal static unsafe string CurrentThreadName()
    {
        var managed = Thread.CurrentThread.Name;
        if (!string.IsNullOrEmpty(managed)) return managed;
        if (OperatingSystem.IsMacOS())
        {
            var buffer = stackalloc byte[64];
            if (pthread_getname_np(pthread_self(), buffer, 64) == 0 && buffer[0] != 0)
                return Marshal.PtrToStringUTF8((nint)buffer) ?? "<unnamed>";
        }
        return $"<tid {Environment.CurrentManagedThreadId}>";
    }

    /// <summary>
    /// A thread's user + system CPU time in ns, or -1 where it is unavailable.
    /// thread_info, not CLOCK_THREAD_CPUTIME_ID: under Rosetta the clock charges a
    /// translated thread only a few percent of the CPU it really used. It costs a
    /// couple of microseconds there, so callers sample it.
    /// </summary>
    internal static unsafe long ThreadCpuNs(uint machThread)
    {
        if (machThread == 0 || !OperatingSystem.IsMacOS()) return -1;
        var info = stackalloc int[ThreadBasicInfoCount];
        var count = (uint)ThreadBasicInfoCount;
        if (thread_info(machThread, ThreadBasicInfoFlavor, info, &count) != 0) return -1;
        // user_time {seconds, microseconds}, system_time {seconds, microseconds}
        return ((long)info[0] + info[2]) * 1_000_000_000L + ((long)info[1] + info[3]) * 1_000L;
    }

    private const int ThreadBasicInfoFlavor = 3; // THREAD_BASIC_INFO
    private const int ThreadBasicInfoCount = 10; // sizeof(thread_basic_info) / sizeof(natural_t)

    [LibraryImport("libSystem.dylib")]
    private static partial uint mach_thread_self();

    [LibraryImport("libSystem.dylib")]
    private static unsafe partial int thread_info(uint thread, int flavor, int* info, uint* count);

    [LibraryImport("libSystem.dylib")]
    private static partial nint pthread_self();

    [LibraryImport("libSystem.dylib")]
    private static unsafe partial int pthread_getname_np(nint thread, byte* name, nuint length);
}
