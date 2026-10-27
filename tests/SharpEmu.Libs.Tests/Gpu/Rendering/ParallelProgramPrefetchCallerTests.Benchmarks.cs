// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.PerfAudit;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

public sealed partial class ParallelProgramPrefetchCallerTests
{
    private readonly record struct Measurement(double WallNs, double CallerCpuNs, double ProcessCpuNs,
        double ProcessAllocatedBytes, long CallerJobs, long Taken, double WaitMs, double WorkerResolveMs, double CallerResolveMs);

    [Fact]
    public void BenchmarkCallerResolutionWhenRequested()
    {
        var output = Environment.GetEnvironmentVariable("SHARPEMU_CALLER_RESOLVE_BENCH_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        object? result = null;
        Exception? failure = null;
        var callerQos = Environment.GetEnvironmentVariable("SHARPEMU_CALLER_RESOLVE_BENCH_QOS") != "0";
        if (callerQos)
        {
            // Match the native Metal GPU worker's class instead of comparing a normal
            // xUnit caller with a UserInteractive resolver on a contended host.
            var thread = HostHelperThread.Start("prefetch caller benchmark", 16 * 1024 * 1024, () =>
            {
                try { result = MeasureBenchmark(callerQos); }
                catch (Exception exception) { failure = exception; }
            });
            Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "Component benchmark exceeded its bound.");
            Assert.True(thread.HasQos, "The requested caller QoS was unavailable.");
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        else result = MeasureBenchmark(callerQos);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object MeasureBenchmark(bool callerQos)
    {
        using var process = Process.GetCurrentProcess();
        var results = new List<object>();
        foreach (var spins in new[] { 0, 512 })
        foreach (var burst in new[] { 1, 5, 32, 256 })
        {
            using var baseline = new Fixture(false);
            using var candidate = new Fixture(true);
            var fixtures = new[] { baseline, candidate };
            foreach (var f in fixtures)
            {
                f.Pipelines.Graphics = new GraphicsPrograms { Available = false };
                f.Pipelines.BeforeGraphicsResolve = _ => { if (spins != 0) Thread.SpinWait(spins); };
            }
            var banks = Enumerable.Range(0, burst).Select(_ => Banks().Snapshot()).ToArray();
            var jobs = new ParallelProgramPrefetch.Job[burst];
            var batches = Math.Max(32, 8192 / burst);
            Measurement Run(int arm, int batchCount)
            {
                var f = fixtures[arm];
                f.Prefetch.WaitIdle();
                f.Prefetch.TakeCounters(); f.Prefetch.TakeCallerResolveCounters(); f.Prefetch.TakeResolveMilliseconds();
                process.Refresh();
                var processBefore = process.TotalProcessorTime.TotalNanoseconds;
                var bytesBefore = GC.GetTotalAllocatedBytes(precise: true);
                var cpuBefore = OperatingSystem.IsMacOS() ? DarwinThreadCpuClock.Nanoseconds() : 0;
                var started = Stopwatch.GetTimestamp();
                for (var batch = 0; batch < batchCount; batch++)
                {
                    for (var i = 0; i < burst; i++) jobs[i] = f.Prefetch.Submit(banks[i]);
                    for (var i = 0; i < burst; i++)
                        if (!f.Take(banks[i], jobs[i])) throw new InvalidOperationException("A benchmark resolution was refused.");
                    f.Prefetch.WaitIdle();
                    f.Pipelines.Calls.Clear(); f.Pipelines.ExportMappings.Clear();
                }
                var elapsed = Stopwatch.GetElapsedTime(started).TotalNanoseconds;
                var cpuAfter = OperatingSystem.IsMacOS() ? DarwinThreadCpuClock.Nanoseconds() : 0;
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - bytesBefore;
                process.Refresh();
                var processAfter = process.TotalProcessorTime.TotalNanoseconds;
                var counters = f.Prefetch.TakeCounters();
                var caller = f.Prefetch.TakeCallerResolveCounters();
                var count = (long)batchCount * burst;
                Assert.Equal(count, counters.Taken);
                Assert.Equal(count, counters.Resolved);
                Assert.Equal(0, counters.Refused); Assert.Equal(0, counters.Abandoned);
                return new(elapsed / count, (cpuAfter - cpuBefore) / (double)count,
                    (processAfter - processBefore) / count, (double)allocated / count,
                    caller.Count, counters.Taken, counters.WaitMilliseconds, f.Prefetch.TakeResolveMilliseconds(), caller.Milliseconds);
            }
            var warm = Stopwatch.StartNew();
            do { Run(0, batches); Run(1, batches); } while (warm.ElapsedMilliseconds < (spins == 0 && burst == 1 ? 5000 : 2000));
            for (var round = 0; round < 7; round++)
                foreach (var arm in round % 2 == 0 ? new[] { 0, 1 } : new[] { 1, 0 })
                    results.Add(new { spins, burst, batches, round, caller_enabled = arm == 1, measurement = Run(arm, batches) });
        }
        return new
        {
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), framework = RuntimeInformation.FrameworkDescription,
            caller_qos = callerQos,
            caveat = "Real prefetch queue/executor with unavailable fake programs; optional fixed work uses SpinWait. Whole-process CPU/allocation includes runtime activity. Caller CPU alone omits background resolution. No GPU/FPS claim.",
            results,
        };
    }
}
