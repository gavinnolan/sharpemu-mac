// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// FreeBSD 11 clock ids (<c>sys/sys/time.h</c>) and their readings, shared by
/// <c>clock_gettime</c>, <c>clock_getres</c>, <c>sceKernelClockGettime</c>
/// and <c>sceKernelClockGetres</c>.
/// </summary>
internal static class KernelClocks
{
    public const int Realtime = 0;
    public const int Virtual = 1;
    public const int Prof = 2;
    public const int Monotonic = 4;
    public const int Uptime = 5;
    public const int UptimePrecise = 7;
    public const int UptimeFast = 8;
    public const int RealtimePrecise = 9;
    public const int RealtimeFast = 10;
    public const int MonotonicPrecise = 11;
    public const int MonotonicFast = 12;
    public const int Second = 13;
    public const int ThreadCputime = 14;
    public const int ProcessCputime = 15;

    private const long NanosPerSecond = 1_000_000_000L;

    // FreeBSD kern_clock_getres: the timecounter clocks report
    // 1e9 / tc_frequency + 1, which is 1 ns for the console's GHz-class TSC;
    // VIRTUAL/PROF report howmany(1e9, hz) with hz = 1000; the CPU-time clocks
    // report 1000 ns once 1e6 / cpu_tickrate rounds to zero.
    private const long TimecounterResolutionNanos = 1;
    private const long StatClockResolutionNanos = 1_000_000;
    private const long CpuTimeResolutionNanos = 1_000;

    public static bool IsValid(int clockId) => clockId is
        Realtime or Virtual or Prof or Monotonic or Uptime or UptimePrecise or UptimeFast or
        RealtimePrecise or RealtimeFast or MonotonicPrecise or MonotonicFast or Second or
        ThreadCputime or ProcessCputime;

    /// <summary>Reads <paramref name="clockId"/>; EINVAL for an unknown id.</summary>
    public static int TryGetTime(int clockId, out long seconds, out long nanoseconds)
    {
        long nanos;
        switch (clockId)
        {
            case Realtime:
            case RealtimePrecise:
            case RealtimeFast:
            case Second:
                ReadRealtime(out seconds, out nanoseconds);
                if (clockId == Second)
                {
                    nanoseconds = 0;
                }

                return 0;
            case Monotonic:
            case MonotonicPrecise:
            case MonotonicFast:
            case Uptime:
            case UptimePrecise:
            case UptimeFast:
                ReadMonotonic(out seconds, out nanoseconds);
                return 0;
            case Virtual:
                nanos = Process.GetCurrentProcess().UserProcessorTime.Ticks * 100L;
                break;
            case Prof:
            case ProcessCputime:
                nanos = ReadProcessCpuNanos();
                break;
            case ThreadCputime:
                nanos = ReadThreadCpuNanos();
                break;
            default:
                seconds = 0;
                nanoseconds = 0;
                return OrbisErrno.EINVAL;
        }

        seconds = nanos / NanosPerSecond;
        nanoseconds = nanos % NanosPerSecond;
        return 0;
    }

    /// <summary>Resolution of <paramref name="clockId"/>; EINVAL for an unknown id.</summary>
    public static int TryGetResolution(int clockId, out long seconds, out long nanoseconds)
    {
        seconds = 0;
        nanoseconds = 0;
        switch (clockId)
        {
            case Second:
                seconds = 1;
                return 0;
            case Virtual:
            case Prof:
                nanoseconds = StatClockResolutionNanos;
                return 0;
            case ThreadCputime:
            case ProcessCputime:
                nanoseconds = CpuTimeResolutionNanos;
                return 0;
            default:
                if (!IsValid(clockId))
                {
                    return OrbisErrno.EINVAL;
                }

                nanoseconds = TimecounterResolutionNanos;
                return 0;
        }
    }

    private static void ReadRealtime(out long seconds, out long nanoseconds)
    {
        var now = DateTimeOffset.UtcNow;
        seconds = now.ToUnixTimeSeconds();
        nanoseconds = (now.Ticks % TimeSpan.TicksPerSecond) * 100;
    }

    private static void ReadMonotonic(out long seconds, out long nanoseconds) =>
        KernelRuntimeCompatExports.ReadMonotonicSinceStart(out seconds, out nanoseconds);

    private static long ReadProcessCpuNanos()
    {
        if (TryHostClock(HostProcessCpuClockId, out var nanos))
        {
            return nanos;
        }

        return Process.GetCurrentProcess().TotalProcessorTime.Ticks * 100L;
    }

    private static long ReadThreadCpuNanos()
    {
        if (TryHostClock(HostThreadCpuClockId, out var nanos))
        {
            return nanos;
        }

        // No per-thread CPU clock on this host: process CPU time is the closest
        // monotonic stand-in.
        return ReadProcessCpuNanos();
    }

    // Host clock ids: macOS CLOCK_PROCESS_CPUTIME_ID 12 / CLOCK_THREAD_CPUTIME_ID 16,
    // Linux 2 / 3. Windows has no clock_gettime.
    private static readonly int HostProcessCpuClockId =
        OperatingSystem.IsMacOS() ? 12 : OperatingSystem.IsLinux() ? 2 : -1;

    private static readonly int HostThreadCpuClockId =
        OperatingSystem.IsMacOS() ? 16 : OperatingSystem.IsLinux() ? 3 : -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct HostTimespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int HostClockGettime(int clockId, out HostTimespec value);

    private static bool TryHostClock(int clockId, out long nanos)
    {
        nanos = 0;
        if (clockId < 0)
        {
            return false;
        }

        try
        {
            if (HostClockGettime(clockId, out var value) != 0)
            {
                return false;
            }

            nanos = value.Seconds * NanosPerSecond + value.Nanoseconds;
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes a FreeBSD <c>struct timespec</c> (two 64-bit fields).
    /// </summary>
    public static bool TryWriteTimespec(CpuContext ctx, ulong address, long seconds, long nanoseconds) =>
        ctx.TryWriteUInt64(address, unchecked((ulong)seconds)) &&
        ctx.TryWriteUInt64(address + sizeof(long), unchecked((ulong)nanoseconds));
}
