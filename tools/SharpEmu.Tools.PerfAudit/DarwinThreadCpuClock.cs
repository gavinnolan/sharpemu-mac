// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.PerfAudit;

// Shared by optional component benchmarks. Rosetta on the measured host returns
// unscaled ticks from CLOCK_THREAD_CPUTIME_ID, including clock_gettime_nsec_np.
// Mach THREAD_BASIC_INFO explicitly reports seconds and microseconds in both ABIs.
internal static class DarwinThreadCpuClock
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadBasicInfo
    {
        public int UserSeconds, UserMicroseconds, SystemSeconds, SystemMicroseconds;
        public int CpuUsage, Policy, RunState, Flags, SuspendCount, SleepTime;
    }

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern nint pthread_self();
    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern uint pthread_mach_thread_np(nint thread);
    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int thread_info(uint thread, int flavor, out ThreadBasicInfo info, ref uint count);

    internal static ulong Nanoseconds()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Thread CPU diagnostics require macOS.");
        uint count = 10;
        // pthread_mach_thread_np borrows the existing port; mach_thread_self would
        // create a send right requiring deallocation at every sample boundary.
        if (thread_info(pthread_mach_thread_np(pthread_self()), 3, out var info, ref count) != 0 || count != 10)
            throw new InvalidOperationException("Cannot read THREAD_BASIC_INFO.");
        return ((ulong)info.UserSeconds + (ulong)info.SystemSeconds) * 1_000_000_000 +
            ((ulong)info.UserMicroseconds + (ulong)info.SystemMicroseconds) * 1_000;
    }
}
