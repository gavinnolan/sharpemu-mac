// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.PerfAudit;

/// <summary>One measured benchmark: per-op time, per-op allocation, and an illustrative scaled workload cost.</summary>
internal sealed record BenchResult(
    string Id, string Name, double MedianNs, double P95Ns, double MinNs, double BytesPerOp, double OpsPerFrame, string Unit, string Note)
{
    public const double BudgetNsPerItem = 1800.0;
    public double FrameMs => MedianNs * OpsPerFrame / 1e6;
    public double BudgetPct => MedianNs / BudgetNsPerItem * 100.0;
}

/// <summary>
/// Hand-rolled Stopwatch harness: untimed warmup, <see cref="Runs"/> timed runs, median and p95 of
/// the per-run ns/op, and managed allocation measured on the benchmarking thread (or process-wide
/// for the cross-thread cases).
/// </summary>
internal static class Harness
{
    public static int Runs = 5;
    public static double TargetRunMs = 150;
    public static double WarmupMs = 700;
    public static readonly List<BenchResult> Results = [];
    private static readonly bool MeasureThreadCpu = Environment.GetEnvironmentVariable("SHARPEMU_BENCH_THREAD_CPU") == "1";

    /// <param name="opsPerInvoke">operations the body performs per call (ns/op = elapsed / calls / ops).</param>
    /// <param name="opsPerFrame">illustrative operation count, not a measured game frame (6331 draws, 2700 dispatches, ...).</param>
    /// <param name="body">the measured work; receives a running iteration index.</param>
    /// <param name="processWideAllocations">count every thread's allocations (for benchmarks that cross threads).</param>
    public static BenchResult Measure(string id, string name, double opsPerInvoke, double opsPerFrame, Action<int> body,
        string unit = "draw", string note = "", int warmupInvokes = 3, int maxInvokesPerRun = int.MaxValue,
        bool processWideAllocations = false, Action? beforeEachRun = null)
    {
        // Warm until both the minimum invoke count and WarmupMs have passed, so tiered JIT / PGO promotion has settled
        // (the shipped app runs R2R then tier-1 after a long session; a cold tier-0 loop would overstate every number).
        var warm = Stopwatch.StartNew();
        for (var i = 0; i < warmupInvokes || warm.Elapsed.TotalMilliseconds < WarmupMs; i++) body(i);
        // Calibrate how many invokes make a ~TargetRunMs run.
        var sw = Stopwatch.StartNew();
        var calibrationInvokes = 0;
        while (sw.Elapsed.TotalMilliseconds < 30 && calibrationInvokes < maxInvokesPerRun)
        {
            body(calibrationInvokes++);
        }

        var perInvokeMs = sw.Elapsed.TotalMilliseconds / Math.Max(calibrationInvokes, 1);
        var invokes = (int)Math.Clamp(TargetRunMs / Math.Max(perInvokeMs, 1e-6), 1, maxInvokesPerRun);
        var times = new double[Runs];
        var bytes = new double[Runs];
        if (MeasureThreadCpu && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Thread CPU diagnostics currently require macOS.");
        var cpuTimes = MeasureThreadCpu ? new double[Runs] : null;
        var index = 0;
        for (var run = 0; run < Runs; run++)
        {
            beforeEachRun?.Invoke();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var allocatedBefore = processWideAllocations ? GC.GetTotalAllocatedBytes(true) : GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            var cpuStart = cpuTimes is null ? 0 : DarwinThreadCpuClock.Nanoseconds();
            for (var i = 0; i < invokes; i++) body(index++);
            var cpuEnd = cpuTimes is null ? 0 : DarwinThreadCpuClock.Nanoseconds();
            var elapsed = Stopwatch.GetElapsedTime(start);
            var allocatedAfter = processWideAllocations ? GC.GetTotalAllocatedBytes(true) : GC.GetAllocatedBytesForCurrentThread();
            var ops = (double)invokes * opsPerInvoke;
            times[run] = elapsed.TotalNanoseconds / ops;
            bytes[run] = (allocatedAfter - allocatedBefore) / ops;
            if (cpuTimes is not null)
            {
                if (cpuStart == 0 || cpuEnd < cpuStart)
                    throw new InvalidOperationException("The thread CPU clock did not return a valid interval.");
                cpuTimes[run] = (cpuEnd - cpuStart) / ops;
            }
        }

        if (cpuTimes is not null)
            Console.WriteLine($"thread_cpu id={id} ns_op=[{string.Join(",", cpuTimes.Select(x => x.ToString("F3", CultureInfo.InvariantCulture)))}] " +
                $"wall_ns_op=[{string.Join(",", times.Select(x => x.ToString("F3", CultureInfo.InvariantCulture)))}]");
        Array.Sort(times);
        Array.Sort(bytes);
        var median = times[Runs / 2];
        var p95 = times[(int)Math.Ceiling(0.95 * Runs) - 1];
        var result = new BenchResult(id, name, median, p95, times[0], bytes[Runs / 2], opsPerFrame, unit, note);
        Results.Add(result);
        Console.WriteLine(Format(result));
        return result;
    }

    /// <summary>Records a result computed elsewhere (a long-running scenario measured once per run).</summary>
    public static BenchResult Record(string id, string name, IReadOnlyList<double> nsPerOp, IReadOnlyList<double> bytesPerOp,
        double opsPerFrame, string unit = "draw", string note = "")
    {
        var times = nsPerOp.OrderBy(v => v).ToArray();
        var bytes = bytesPerOp.OrderBy(v => v).ToArray();
        var result = new BenchResult(id, name, times[times.Length / 2], times[(int)Math.Ceiling(0.95 * times.Length) - 1], times[0],
            bytes[bytes.Length / 2], opsPerFrame, unit, note);
        Results.Add(result);
        Console.WriteLine(Format(result));
        return result;
    }

    public static string Format(BenchResult r) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{r.Id,-6} {r.Name,-62} {r.MedianNs,12:N1} ns/op  p95 {r.P95Ns,12:N1}  {r.BytesPerOp,10:N1} B/op  scaled@{r.OpsPerFrame:N0}/{r.Unit}: {r.FrameMs,8:N2} ms  ({r.BudgetPct,7:N0}% of 1.8us)");

    public static string Table()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| ID | Benchmark | median ns/op | p95 ns/op | B/op | illustrative ops | scaled ms | % of 1.8 us/item |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var r in Results)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {r.Id} | {r.Name} | {r.MedianNs:N1} | {r.P95Ns:N1} | {r.BytesPerOp:N1} | {r.OpsPerFrame:N0} ({r.Unit}) | {r.FrameMs:N2} | {r.BudgetPct:N0}% |"));
        }

        return sb.ToString();
    }
}
