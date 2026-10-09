// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Pad;

/// <summary>Observes the pad bytes already submitted to guest memory, without polling input again.</summary>
internal static class PadReadDiagnostics
{
    internal static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_PAD_READS"), "1", StringComparison.Ordinal);

    private static int _initialRows;
    private static int _activeRows;
    private static int _periodicRows;
    private static long _nextPeriodicTimestamp;
    private static long _reads;
    private static readonly long Started = Stopwatch.GetTimestamp();

    internal static void Record(ulong address, bool written, uint buttons,
        byte leftX, byte leftY, byte rightX, byte rightY, byte l2, byte r2)
    {
        if (!Enabled) return;

        var read = Interlocked.Increment(ref _reads);
        var now = Stopwatch.GetTimestamp();
        var active = buttons != 0 || leftX != 128 || leftY != 128 ||
            rightX != 128 || rightY != 128 || l2 != 0 || r2 != 0;
        var emit = read <= 16 && Interlocked.Increment(ref _initialRows) <= 16;
        if (active)
        {
            emit |= Interlocked.Increment(ref _activeRows) <= 64;
        }
        else
        {
            // One observer wins each five-second interval. These periodic neutral
            // samples reveal whether the guest polls across an input-plan window.
            var next = Volatile.Read(ref _nextPeriodicTimestamp);
            if (now >= next && Interlocked.CompareExchange(
                    ref _nextPeriodicTimestamp, now + 5 * Stopwatch.Frequency, next) == next)
            {
                emit |= Interlocked.Increment(ref _periodicRows) <= 64;
            }
        }

        if (!emit) return;
        try
        {
            var elapsed = (now - Started) / (double)Stopwatch.Frequency;
            Console.Error.WriteLine(FormattableString.Invariant($"[PAD][READ] seconds={elapsed:F3} read={read} thread={Environment.CurrentManagedThreadId} address=0x{address:X16} written={written} buttons=0x{buttons:X8} left={leftX},{leftY} right={rightX},{rightY} triggers={l2},{r2}"));
        }
        catch
        {
            // Diagnostics cannot change the already-completed guest write result.
        }
    }
}
