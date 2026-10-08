// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.Gpu.Metal;

// Observe only successful waits that already exist. Never add a native wait,
// retain a command buffer, or infer GPU time from a caller's wall-clock wait.
internal static class MetalGpuCompletionProfile
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1" ||
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RENDER") == "1";
    private const int Capacity = 4096;
    private static readonly object Gate = new();
    private static class Storage
    {
        internal static readonly (double Start, double End)[] Intervals = new (double, double)[Capacity];
        internal static long Count, Calls, Invalid, Repeated, WaitTicks;
        internal static double LastStart, LastEnd;
    }

    internal static long StartWait() => Enabled ? Stopwatch.GetTimestamp() : 0;

    internal static void Begin()
    {
        if (!Enabled) return;
        lock (Gate)
        {
            Array.Clear(Storage.Intervals);
            Storage.Count = Storage.Calls = Storage.Invalid = Storage.Repeated = Storage.WaitTicks = 0;
            Storage.LastStart = Storage.LastEnd = 0;
        }
    }

    internal static void RecordSuccessfulWait(nint commandBuffer, long started)
    {
        if (!Enabled || started == 0) return;
        var waitTicks = Stopwatch.GetTimestamp() - started;
        double start = 0, end = 0;
        try
        {
            // Apple specifies these timestamps only after native completion.
            // The caller has already established MTLCommandBufferStatusCompleted.
            start = MetalNative.SendDouble(commandBuffer, MetalNative.Selector("GPUStartTime"));
            end = MetalNative.SendDouble(commandBuffer, MetalNative.Selector("GPUEndTime"));
        }
        catch (Exception) { } // A diagnostic query must not change completion.
        lock (Gate)
        {
            Storage.Calls++;
            Storage.WaitTicks += waitTicks;
            if (!ValidInterval(start, end)) { Storage.Invalid++; return; }
            // Existing barriers can wait for the same completed buffer again.
            // Timestamp identity, not a recycled native pointer, removes repeats.
            if (start == Storage.LastStart && end == Storage.LastEnd) { Storage.Repeated++; return; }
            Storage.LastStart = start; Storage.LastEnd = end;
            Storage.Intervals[(int)(Storage.Count % Capacity)] = (start, end);
            Storage.Count++;
        }
    }

    internal static bool ValidInterval(double start, double end) =>
        double.IsFinite(start) && double.IsFinite(end) && start > 0 && end > start;

    internal static (double Union, double Span, int Valid) CalculateUnion((double Start, double End)[] intervals)
    {
        var sorted = intervals.Where(static x => ValidInterval(x.Start, x.End)).OrderBy(static x => x.Start).ThenBy(static x => x.End).ToArray();
        if (sorted.Length == 0) return (0, 0, 0);
        var first = sorted[0].Start; var begin = first; var finish = sorted[0].End; double union = 0;
        foreach (var interval in sorted.AsSpan(1))
        {
            if (interval.Start > finish) { union += finish - begin; begin = interval.Start; }
            finish = Math.Max(finish, interval.End);
        }
        return (union + finish - begin, finish - first, sorted.Length);
    }

    internal static void End()
    {
        if (!Enabled) return;
        try
        {
            (double Start, double End)[] rows;
            long count, calls, invalid, repeated, waits;
            lock (Gate)
            {
                count = Storage.Count; calls = Storage.Calls; invalid = Storage.Invalid;
                repeated = Storage.Repeated; waits = Storage.WaitTicks;
                var first = Math.Max(0, count - Capacity);
                rows = new (double, double)[count - first];
                for (var i = 0; i < rows.Length; i++) rows[i] = Storage.Intervals[(int)((first + i) % Capacity)];
            }
            var totals = CalculateUnion(rows);
            var invariant = CultureInfo.InvariantCulture;
            Console.Error.WriteLine(FormattableString.Invariant($"[PERF][METAL_GPU_WAITS] successful_calls={calls} repeated_intervals={repeated} invalid_timestamps={invalid} caller_wall_ms={waits * 1000.0 / Stopwatch.Frequency:F3} scope=all_existing_successful_wait_calls_not_gpu_busy"));
            Console.Error.WriteLine(FormattableString.Invariant($"[PERF][METAL_GPU_INTERVALS] retained={rows.Length} overwritten={Math.Max(0, count - Capacity)} union_ms={totals.Union * 1000:F3} span_ms={totals.Span * 1000:F3} scope=retained_successfully_waited_native_buffers presentation_buffers=excluded scanout=unmeasured"));
            for (var i = 0; i < rows.Length; i++)
                Console.Error.WriteLine($"[PERF][METAL_GPU_INTERVAL] index={i} start_s={rows[i].Start.ToString("R", invariant)} end_s={rows[i].End.ToString("R", invariant)}");
        }
        catch (Exception) { } // Diagnostic output cannot fail presenter cleanup.
    }
}
