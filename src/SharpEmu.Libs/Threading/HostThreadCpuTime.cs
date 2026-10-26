// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Threading;

/// <summary>
/// CPU time (user + system) consumed by the calling host thread. On macOS this is
/// <c>thread_info(THREAD_BASIC_INFO)</c>, not <c>CLOCK_THREAD_CPUTIME_ID</c>: under Rosetta
/// the clock charges a translated thread only a few percent of the CPU it really used.
/// Elsewhere it is the host's per-thread CPU clock, so a guest that profiles its own
/// threads sees monotonic time that tracks the work it did.
/// </summary>
internal static unsafe partial class HostThreadCpuTime
{
    private const int ThreadBasicInfoFlavor = 3; // THREAD_BASIC_INFO
    private const int ThreadBasicInfoCount = 10; // sizeof(thread_basic_info) / sizeof(natural_t)

    [ThreadStatic]
    private static uint _machThread;

    /// <summary>Microseconds of CPU time the calling thread has used, or -1 when the host cannot say.</summary>
    internal static long ReadCurrentMicroseconds()
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                if (_machThread == 0)
                {
                    _machThread = mach_thread_self();
                }

                var info = stackalloc int[ThreadBasicInfoCount];
                var count = (uint)ThreadBasicInfoCount;
                if (thread_info(_machThread, ThreadBasicInfoFlavor, info, &count) == 0)
                {
                    // user_time {seconds, microseconds}, system_time {seconds, microseconds}
                    return ((long)info[0] + info[2]) * 1_000_000L + info[1] + info[3];
                }
            }
            catch (Exception)
            {
            }
        }

        return KernelClocksThreadMicroseconds();
    }

    private static long KernelClocksThreadMicroseconds() =>
        Kernel.KernelClocks.TryGetTime(Kernel.KernelClocks.ThreadCputime, out var seconds, out var nanoseconds) == 0
            ? seconds * 1_000_000L + nanoseconds / 1_000L
            : -1;

    [LibraryImport("libSystem.B.dylib")]
    private static partial uint mach_thread_self();

    [LibraryImport("libSystem.B.dylib")]
    private static partial int thread_info(uint thread, int flavor, int* info, uint* count);
}
