// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Globalization;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.VideoOut;

internal sealed class AllocationProfileFactAttribute : FactAttribute
{
    public AllocationProfileFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_ALLOCATIONS") != "1")
            Skip = "Requires a fresh process with SHARPEMU_PROFILE_ALLOCATIONS=1; no native activity.";
    }
}

internal sealed class AllocationProfileDisabledFactAttribute : FactAttribute
{
    public AllocationProfileDisabledFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_ALLOCATIONS") == "1" ||
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1" ||
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RENDER") == "1")
            Skip = "Requires a fresh process with allocation and wall profiles disabled.";
    }
}

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalDrainAllocationProfileTests
{
    private readonly ITestOutputHelper _output;
    public MetalDrainAllocationProfileTests(ITestOutputHelper output) => _output = output;
    [AllocationProfileDisabledFact]
    public void DisabledScopesAndCommitDoNotAllocateOrExport()
    {
        Assert.False(MetalPresentationProfile.AllocationsEnabled);
        MetalPresentationProfile.Begin();
        var warm = MetalPresentationProfile.MeasureDrainAllocations(); warm.Finish(true);
        MetalPresentationProfile.RecordCompletedDrainWork(MetalPresentationProfile.DrainWorkKind.Other);
        MetalPresentationProfile.RecordGuestCommit(0); MetalPresentationProfile.End();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
        {
            var scope = MetalPresentationProfile.MeasureDrainAllocations(); scope.Finish(false);
            MetalPresentationProfile.RecordCompletedDrainWork(MetalPresentationProfile.DrainWorkKind.Compute);
            MetalPresentationProfile.RecordGuestCommit(index);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(string.Empty, Report());
    }

    [AllocationProfileFact]
    public void ActualDrainAcknowledgementAndAllocationCorrelateWithGuestCommit()
    {
        using var presenter = new MetalGuestFailureTests.PresenterState();
        MetalPresentationProfile.Begin();
        var sequence = MetalVideoPresenter.SubmitOrderedGuestAction(
            static () => GC.KeepAlive(new byte[32768]), "controlled allocation");
        DrainThrough(sequence);
        Assert.True(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
        MetalPresentationProfile.RecordGuestCommit(43);
        var report = Report();
        _output.WriteLine(report);
        var row = Frame(report);
        Assert.Equal(43, Number(row, "guest_sequence"));
        Assert.True(Number(row, "allocated_bytes") >= 32768);
        Assert.True(Number(row, "drain_calls") >= 1);
        Assert.Equal(1, Number(row, "completed_work"));
        Assert.Equal(0, Number(row, "completed_draws"));
        Assert.Equal(0, Number(row, "completed_compute"));
        Assert.Equal(0, Number(row, "failed_drains"));
        Assert.Equal(0, Number(row, "discarded_scopes"));
    }

    [AllocationProfileFact]
    public void ActualFailedDrainKeepsPartialCompletedWorkInTailWithoutAcknowledgingFailure()
    {
        using var presenter = new MetalGuestFailureTests.PresenterState();
        GpuFatal.OnFatal = static _ => { };
        MetalPresentationProfile.Begin();
        var good = MetalVideoPresenter.SubmitOrderedGuestAction(static () => GC.KeepAlive(new byte[8192]), "good");
        var original = new InvalidOperationException("controlled allocation failure");
        var bad = MetalVideoPresenter.SubmitOrderedGuestAction(() => { GC.KeepAlive(new byte[4096]); throw original; }, "bad");
        var followingRan = false;
        var next = MetalVideoPresenter.SubmitOrderedGuestAction(() => followingRan = true, "not executed");
        GpuFatalException? failure = null;
        for (var count = 0; count < 10 && failure is null; count++)
        {
            try { MetalVideoPresenter.DrainGuestWork(0, 0); }
            catch (GpuFatalException caught) { failure = caught; }
        }
        Assert.NotNull(failure); Assert.Same(original, failure.InnerException);
        Assert.True(MetalVideoPresenter.WaitForGuestWork(good, 0));
        Assert.False(MetalVideoPresenter.WaitForGuestWork(bad, 0));
        Assert.False(MetalVideoPresenter.WaitForGuestWork(next, 0)); Assert.False(followingRan);
        var report = Report();
        _output.WriteLine(report);
        Assert.DoesNotContain("[PERF][METAL_DRAIN_ALLOC_FRAME]", report);
        var tail = Line(report, "METAL_DRAIN_ALLOC_TAIL");
        Assert.True(Number(tail, "allocated_bytes") >= 12288);
        Assert.Equal(1, Number(tail, "completed_work"));
        Assert.Equal(1, Number(tail, "failed_drains"));
        Assert.Equal(0, Number(tail, "discarded_scopes"));
    }

    [AllocationProfileFact]
    public void ActualSkippedDrawAndComputeCompletionCountsAreNotGpuDispatchCounts()
    {
        using var presenter = new MetalGuestFailureTests.PresenterState();
        var shader = new MetalCompiledGuestShader(new Gen5MslShader("", "managed no-op", Gen5MslStage.Compute, 0));
        var cache = (IDictionary)typeof(MetalVideoPresenter).GetField("_computePipelineCache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        cache.Add(shader, (nint)0); // real pipeline-cache decline, no native lookup
        try
        {
            MetalPresentationProfile.Begin();
            MetalVideoPresenter.SubmitOffscreenTranslatedDraw(shader, [], [], 0,
                [new GuestRenderTarget(0, 1, 1, uint.MaxValue, 0)], null, 3, 1, 4, null, null, null, null, 0);
            var compute = MetalVideoPresenter.SubmitComputeDispatch(0, shader, [], [], 1, 1, 1, 0, 0, 0, true, null);
            DrainThrough(compute);
            Assert.True(MetalVideoPresenter.WaitForGuestWork(compute, 0));
            var report = Report();
            _output.WriteLine(report);
            var tail = Line(report, "METAL_DRAIN_ALLOC_TAIL");
            Assert.Equal(2, Number(tail, "completed_work"));
            Assert.Equal(1, Number(tail, "completed_draws"));
            Assert.Equal(1, Number(tail, "completed_compute"));
            Assert.Equal(0, Number(tail, "failed_drains"));
        }
        finally { cache.Remove(shader); }
    }

    [AllocationProfileFact]
    public void NestedMeasurementCountsBytesOnceAndPreservesCompletedCategories()
    {
        MetalPresentationProfile.Begin();
        var warm = MetalPresentationProfile.MeasureDrainAllocations(); warm.Finish(true);
        MetalPresentationProfile.Begin();
        var outer = MetalPresentationProfile.MeasureDrainAllocations();
        GC.KeepAlive(new byte[1024]);
        var inner = MetalPresentationProfile.MeasureDrainAllocations();
        GC.KeepAlive(new byte[2048]);
        MetalPresentationProfile.RecordCompletedDrainWork(MetalPresentationProfile.DrainWorkKind.Compute);
        inner.Finish(true); outer.Finish(true);
        var tail = Line(Report(), "METAL_DRAIN_ALLOC_TAIL");
        Assert.InRange(Number(tail, "allocated_bytes"), 3072, 4096);
        Assert.Equal(2, Number(tail, "drain_calls"));
        Assert.Equal(1, Number(tail, "completed_compute"));
    }

    [AllocationProfileFact]
    public void OtherThreadAllocationIsExcludedAndWrongThreadFinishIsExplicitlyDiscarded()
    {
        MetalPresentationProfile.Begin();
        var scope = MetalPresentationProfile.MeasureDrainAllocations();
        var worker = new Thread(static () => GC.KeepAlive(new byte[1024 * 1024]));
        worker.Start(); Assert.True(worker.Join(TimeSpan.FromSeconds(10)));
        scope.Finish(true);
        var tail = Line(Report(), "METAL_DRAIN_ALLOC_TAIL");
        Assert.InRange(Number(tail, "allocated_bytes"), 0, 65536);
        Assert.Equal(0, Number(tail, "discarded_scopes"));

        MetalPresentationProfile.Begin();
        var wrongThreadScope = MetalPresentationProfile.MeasureDrainAllocations();
        var finishingWorker = new Thread(() => wrongThreadScope.Finish(true));
        finishingWorker.Start(); Assert.True(finishingWorker.Join(TimeSpan.FromSeconds(10)));
        var replacement = MetalPresentationProfile.MeasureDrainAllocations();
        GC.KeepAlive(new byte[4096]); replacement.Finish(true);
        wrongThreadScope.Finish(true); // revoked epoch must not read the new counter
        tail = Line(Report(), "METAL_DRAIN_ALLOC_TAIL");
        Assert.True(Number(tail, "discarded_scopes") >= 2);
        Assert.InRange(Number(tail, "allocated_bytes"), 4096, 8192);
    }

    [AllocationProfileFact]
    public void ConcurrentRejectedScopesSurviveGuestCommitsAndDoNotCancelOwnerBytes()
    {
        const int rounds = 256, rejectedPerRound = 4096;
        using var barrier = new Barrier(2);
        Exception? workerFailure = null;
        var worker = new Thread(() =>
        {
            try
            {
                for (var round = 0; round < rounds; round++)
                {
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("start rejected-scope round");
                    for (var index = 0; index < rejectedPerRound; index++)
                    {
                        var rejected = MetalPresentationProfile.MeasureDrainAllocations();
                        rejected.Finish(true);
                    }
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("finish rejected-scope round");
                }
            }
            catch (Exception exception) { workerFailure = exception; }
        });
        MetalPresentationProfile.Begin();
        var warm = MetalPresentationProfile.MeasureDrainAllocations(); warm.Finish(true);
        MetalPresentationProfile.Begin();
        worker.Start();
        try
        {
            for (var round = 0; round < rounds; round++)
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                var owner = MetalPresentationProfile.MeasureDrainAllocations();
                GC.KeepAlive(new byte[64]); owner.Finish(true);
                MetalPresentationProfile.RecordGuestCommit(round);
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            }
        }
        finally { Assert.True(worker.Join(TimeSpan.FromSeconds(10))); }
        Assert.Null(workerFailure);
        var report = Report();
        var rows = report.Split('\n').Where(static line => line.StartsWith("[PERF][METAL_DRAIN_ALLOC_FRAME]", StringComparison.Ordinal)).ToArray();
        Assert.Equal(rounds, rows.Length);
        Assert.All(rows, static row => Assert.True(Number(row, "allocated_bytes") >= 64));
        var tail = Line(report, "METAL_DRAIN_ALLOC_TAIL");
        Assert.Equal((long)rounds * rejectedPerRound,
            rows.Sum(static row => Number(row, "discarded_scopes")) + Number(tail, "discarded_scopes"));
        Assert.Equal(0, rows.Sum(static row => Number(row, "failed_drains")) + Number(tail, "failed_drains"));
    }

    [AllocationProfileFact]
    public void ClosedDiagnosticOutputCannotFailDrainOrErasePendingCounters()
    {
        MetalPresentationProfile.Begin();
        var scope = MetalPresentationProfile.MeasureDrainAllocations();
        GC.KeepAlive(new byte[4096]); scope.Finish(false);
        var previous = Console.Error;
        try { Console.SetError(new ClosedOutput()); MetalPresentationProfile.End(); }
        finally { Console.SetError(previous); }
        var tail = Line(Report(), "METAL_DRAIN_ALLOC_TAIL");
        Assert.True(Number(tail, "allocated_bytes") >= 4096);
        Assert.Equal(1, Number(tail, "failed_drains"));
    }

    [AllocationProfileFact]
    public void RingIsBoundedAndHostDrainsAccumulateWithoutCreatingGuestRows()
    {
        MetalPresentationProfile.Begin();
        for (var index = 0; index < 3; index++)
        {
            var scope = MetalPresentationProfile.MeasureDrainAllocations(); scope.Finish(true);
        }
        var noCommit = Report();
        Assert.DoesNotContain("[PERF][METAL_DRAIN_ALLOC_FRAME]", noCommit);
        Assert.Equal(3, Number(Line(noCommit, "METAL_DRAIN_ALLOC_TAIL"), "drain_calls"));
        for (var index = 0; index < 4099; index++) MetalPresentationProfile.RecordGuestCommit(100000 + index);
        var report = Report();
        Assert.Equal(4096, report.Split('\n').Count(static line => line.StartsWith("[PERF][METAL_DRAIN_ALLOC_FRAME]", StringComparison.Ordinal)));
        Assert.Contains("retained=4096 overwritten=3", Line(report, "METAL_DRAIN_ALLOC_TRACE"));
        Assert.Contains("sequence=3 guest_sequence=100003", Frame(report));
        var wallEnabled = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1" || Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RENDER") == "1";
        Assert.Equal(wallEnabled, report.Contains("[PERF][METAL_PRESENT_FRAME]", StringComparison.Ordinal));
        MetalPresentationProfile.Begin();
        Assert.Contains("retained=0 overwritten=0", Report());
    }

    private static void DrainThrough(long sequence)
    {
        for (var count = 0; count < 10 && !MetalVideoPresenter.WaitForGuestWork(sequence, 0); count++)
            MetalVideoPresenter.DrainGuestWork(0, 0);
    }
    private sealed class ClosedOutput : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("controlled closed diagnostic sink");
    }
    private static string Report()
    {
        var previous = Console.Error; using var output = new StringWriter(CultureInfo.InvariantCulture);
        try { Console.SetError(output); MetalPresentationProfile.End(); return output.ToString(); }
        finally { Console.SetError(previous); }
    }
    private static string Line(string report, string name) => report.Split('\n').First(line => line.StartsWith($"[PERF][{name}]", StringComparison.Ordinal));
    private static string Frame(string report) => Line(report, "METAL_DRAIN_ALLOC_FRAME");
    private static long Number(string line, string key) => long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Single(value => value.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..], CultureInfo.InvariantCulture);
}
