// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Threading;

/// <summary>
/// Guest timeouts → 64-bit Stopwatch deadlines. Absolute timespecs are read against the
/// guest clock they name (CLOCK_REALTIME by default) and converted once, so a deadline
/// hours or days away neither truncates nor wraps.
/// </summary>
public static class GuestTime
{
    public const int ClockRealtime = 0;
    public const int ClockMonotonic = 4;

    /// <summary>
    /// Reads a <c>struct timespec</c> at <paramref name="address"/> as an absolute time on
    /// <paramref name="clockId"/> and returns the Stopwatch deadline. errno EFAULT for an
    /// unreadable pointer, EINVAL for a malformed nanosecond field.
    /// </summary>
    public static bool TryReadAbsoluteDeadline(CpuContext ctx, ulong address, int clockId, out long deadline, out int errno)
    {
        deadline = 0;
        if (!TryReadTimespec(ctx, address, out var seconds, out var nanoseconds, out errno))
        {
            return false;
        }

        KernelRuntimeCompatExports.GetClockTime(clockId, out var nowSeconds, out var nowNanoseconds);
        deadline = DeadlineFromRelativeNanoseconds(seconds - nowSeconds, nanoseconds - nowNanoseconds);
        return true;
    }

    /// <summary>Reads a relative <c>struct timespec</c> and returns the Stopwatch deadline.</summary>
    public static bool TryReadRelativeDeadline(CpuContext ctx, ulong address, out long deadline, out int errno)
    {
        deadline = 0;
        if (!TryReadTimespec(ctx, address, out var seconds, out var nanoseconds, out errno))
        {
            return false;
        }

        if (seconds < 0)
        {
            errno = GuestErrno.EINVAL;
            return false;
        }

        deadline = DeadlineFromRelativeNanoseconds(seconds, nanoseconds);
        return true;
    }

    /// <summary>Reads a timespec, validating 0 ≤ tv_nsec &lt; 1e9.</summary>
    public static bool TryReadTimespec(CpuContext ctx, ulong address, out long seconds, out long nanoseconds, out int errno)
    {
        seconds = 0;
        nanoseconds = 0;
        if (address == 0 ||
            !ctx.TryReadUInt64(address, out var rawSeconds) ||
            !ctx.TryReadUInt64(address + 8, out var rawNanoseconds))
        {
            errno = GuestErrno.EFAULT;
            return false;
        }

        seconds = unchecked((long)rawSeconds);
        nanoseconds = unchecked((long)rawNanoseconds);
        if (nanoseconds < 0 || nanoseconds >= 1_000_000_000L)
        {
            errno = GuestErrno.EINVAL;
            return false;
        }

        errno = 0;
        return true;
    }

    /// <summary>
    /// Now + (<paramref name="seconds"/>·1e9 + <paramref name="nanoseconds"/>) as a Stopwatch
    /// deadline, rounded up to the next microsecond; a past time yields "now" (already
    /// expired), a time beyond the representable range yields <see cref="long.MaxValue"/>.
    /// </summary>
    public static long DeadlineFromRelativeNanoseconds(long seconds, long nanoseconds)
    {
        var now = Stopwatch.GetTimestamp();
        if (seconds > 100L * 365 * 24 * 3600)
        {
            return long.MaxValue;
        }

        if (seconds < -100L * 365 * 24 * 3600)
        {
            return now;
        }

        var totalNanoseconds = seconds * 1_000_000_000L + nanoseconds;
        if (totalNanoseconds <= 0)
        {
            return now;
        }

        return HostPreciseClock.DeadlineAfterMicroseconds((totalNanoseconds + 999) / 1000);
    }

    /// <summary>A relative microsecond timeout as a deadline.</summary>
    public static long DeadlineAfterMicroseconds(ulong microseconds) =>
        microseconds == 0
            ? Stopwatch.GetTimestamp()
            : HostPreciseClock.DeadlineAfterMicroseconds(unchecked((long)Math.Min(microseconds, (ulong)long.MaxValue)));

    /// <summary>Microseconds until <paramref name="deadline"/> (0 when passed), clamped to uint.</summary>
    public static uint RemainingMicrosecondsUInt32(long deadline)
    {
        if (deadline == long.MaxValue)
        {
            return uint.MaxValue;
        }

        var remaining = HostPreciseClock.TicksToMicroseconds(deadline - Stopwatch.GetTimestamp());
        return (uint)Math.Min(remaining, uint.MaxValue);
    }
}
