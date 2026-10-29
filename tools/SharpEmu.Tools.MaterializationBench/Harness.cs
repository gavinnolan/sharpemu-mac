// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.PerfAudit;

/// <summary>One synthetic benchmark: per-operation latency and allocation.</summary>
internal sealed record BenchResult(
    string Id, string Name, double MedianNs, double P95Ns, double MinNs, double BytesPerOp, double OpsPerFrame, string Unit, string Note);

/// <summary>
/// Hand-rolled Stopwatch harness: untimed warmup, <see cref="Runs"/> timed runs, median and p95 of
/// the per-run ns/op, and managed allocation measured on the benchmarking thread (or process-wide
/// for the cross-thread cases).
/// </summary>
internal static class Harness
{
    public static int Runs = int.TryParse(Environment.GetEnvironmentVariable("PA_RUNS"), out var runs) ? runs : 5;
    public static double TargetRunMs = double.TryParse(Environment.GetEnvironmentVariable("PA_RUN_MS"), out var runMs) ? runMs : 150;
    public static double WarmupMs = 1500;
    public static readonly string[]? OnlyScenarios = Environment.GetEnvironmentVariable("PA_SCENARIOS")?.Split(',');
    public static readonly List<BenchResult> Results = [];

    /// <param name="opsPerInvoke">operations the body performs per call (ns/op = elapsed / calls / ops).</param>
    /// <param name="opsPerFrame">Historical workload weighting, unused in timing results.</param>
    /// <param name="body">the measured work; receives a running iteration index.</param>
    /// <param name="processWideAllocations">count every thread's allocations (for benchmarks that cross threads).</param>
    public static BenchResult Measure(string id, string name, double opsPerInvoke, double opsPerFrame, Action<int> body,
        string unit = "draw", string note = "", int warmupInvokes = 3, int maxInvokesPerRun = int.MaxValue,
        bool processWideAllocations = false, Action? beforeEachRun = null)
    {
        if (OnlyScenarios is not null && !OnlyScenarios.Contains(id))
            return new BenchResult(id, name, 0, 0, 0, 0, opsPerFrame, unit, note);
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
        var index = 0;
        for (var run = 0; run < Runs; run++)
        {
            beforeEachRun?.Invoke();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var allocatedBefore = processWideAllocations ? GC.GetTotalAllocatedBytes(true) : GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < invokes; i++) body(index++);
            var elapsed = Stopwatch.GetElapsedTime(start);
            var allocatedAfter = processWideAllocations ? GC.GetTotalAllocatedBytes(true) : GC.GetAllocatedBytesForCurrentThread();
            var ops = (double)invokes * opsPerInvoke;
            times[run] = elapsed.TotalNanoseconds / ops;
            bytes[run] = (allocatedAfter - allocatedBefore) / ops;
        }

        Array.Sort(times);
        Array.Sort(bytes);
        var median = times[Runs / 2];
        var p95 = times[(int)Math.Ceiling(0.95 * Runs) - 1];
        var result = new BenchResult(id, name, median, p95, times[0], bytes[Runs / 2], opsPerFrame, unit, note);
        Results.Add(result);
        Console.WriteLine(Format(result));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"MIN {id} {times[0]:F1} {times[Runs / 4]:F1}"));
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
            $"{r.Id,-6} {r.Name,-62} {r.MedianNs,12:N1} ns/op  p95 {r.P95Ns,12:N1}  {r.BytesPerOp,10:N1} B/op");

    public static string Table()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| ID | Benchmark | median ns/op | p95 ns/op | B/op |");
        sb.AppendLine("|---|---|---:|---:|---:|");
        foreach (var r in Results)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {r.Id} | {r.Name} | {r.MedianNs:N1} | {r.P95Ns:N1} | {r.BytesPerOp:N1} |"));
        return sb.ToString();
    }
}
