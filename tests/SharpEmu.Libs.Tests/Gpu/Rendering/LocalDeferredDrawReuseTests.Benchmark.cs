// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

public sealed partial class LocalDeferredDrawReuseTests
{
    private readonly record struct Measurement(double NanosecondsPerDraw, double RenderThreadBytesPerDraw, long Taken);

    // Exercise the real translation queue and background resolver. Unavailable fake programs
    // keep native compilation/rendering out of this component benchmark; every job must still
    // resolve and be consumed. Allocations measure this thread, not the background resolver.
    private static Measurement Measure(Fixture fixture, int batches, int burst)
    {
        fixture.Prefetch.TakeCounters();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var batch = 0; batch < batches; batch++)
        {
            for (var draw = 0; draw < burst; draw++) fixture.Draw(3);
            fixture.Drain();
            fixture.Host.Calls.Clear();
            fixture.Host.DrawTargets.Clear();
            fixture.Pipelines.Calls.Clear();
            fixture.Pipelines.ExportMappings.Clear();
        }
        var elapsed = Stopwatch.GetTimestamp() - started;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var counters = fixture.Prefetch.TakeCounters();
        Assert.Equal((long)batches * burst, counters.Taken);
        Assert.Equal(0, counters.Refused);
        Assert.Equal(0, counters.Abandoned);
        return new(elapsed * 1e9 / Stopwatch.Frequency / (batches * burst),
            (double)bytes / (batches * burst), counters.Taken);
    }

    [Fact]
    public void WarmLocalQueueAvoidsWrapperAllocations()
    {
        using var baseline = new Fixture(false);
        using var reused = new Fixture(true);
        baseline.Pipelines.Graphics = reused.Pipelines.Graphics = new GraphicsPrograms { Available = false };
        Measure(baseline, 16, 16); Measure(reused, 16, 16);
        var before = Measure(baseline, 64, 16);
        var after = Measure(reused, 64, 16);
        Assert.True(before.RenderThreadBytesPerDraw - after.RenderThreadBytesPerDraw >= 128,
            $"baseline={before.RenderThreadBytesPerDraw:F2} B/draw; reused={after.RenderThreadBytesPerDraw:F2} B/draw");
        reused.AssertCleared();
    }

    [Fact]
    public void MeasureLocalDrawReuseWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("SHARPEMU_LOCAL_DRAW_REUSE_BENCH_OUTPUT");
        if (string.IsNullOrEmpty(path)) return;
        var cases = new List<object>();
        foreach (var burst in new[] { 1, 5, 32, 512 })
        {
            using var baseline = new Fixture(false);
            using var reused = new Fixture(true);
            baseline.Pipelines.Graphics = reused.Pipelines.Graphics = new GraphicsPrograms { Available = false };
            Measure(baseline, 32, burst); Measure(reused, 32, burst);
            var batches = Math.Max(16, 2048 / burst);
            var rounds = new List<object>();
            for (var round = 0; round < 9; round++)
            {
                Measurement before, after;
                if ((round & 1) == 0)
                { before = Measure(baseline, batches, burst); after = Measure(reused, batches, burst); }
                else
                { after = Measure(reused, batches, burst); before = Measure(baseline, batches, burst); }
                Assert.Equal(before.Taken, after.Taken);
                rounds.Add(new { round, before, after });
            }
            reused.AssertCleared();
            cases.Add(new { burst, batches, rounds });
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            caveat = "Real translation queue/resolver with unavailable fake programs; current-thread allocation only; no GPU or FPS claim.",
            cases,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
