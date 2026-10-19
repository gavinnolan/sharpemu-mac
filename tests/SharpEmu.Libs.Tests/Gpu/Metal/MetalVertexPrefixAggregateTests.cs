// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using Xunit;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
namespace SharpEmu.Libs.Tests.Gpu.Metal;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalVertexPrefixAggregateTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void ProofProcessIdentityIsActualAndExpectedWhenRequested()
    {
        var actual = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        output.WriteLine($"Vertex-profile proof actual architecture={actual} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} profile={MetalVertexPrefixProfile.Enabled} policy={MetalVertexPrefixProfile.PrefixEnabled}");
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH") is { Length: > 0 } expected)
            Assert.Equal(expected, actual, ignoreCase: true);
    }

    [Fact]
    public async Task ConcurrentScopesAndSnapshotsRetainOneCoherentAccountingBoundary()
    {
        var aggregate = new MetalVertexPrefixProfile.Aggregate();
        const int count = 4000;
        var worker = Task.Run(() => Parallel.For(0, count, i =>
        {
            var scope = new MetalVertexPrefixProfile.CopyScope(aggregate, true);
            scope.Qualify(); scope.Stream(256, 184);
            if (i % 2 == 0) scope.Complete(true);
            scope.Finish(); scope.Finish();
        }));
        while (!worker.IsCompleted)
        {
            var value = aggregate.Snapshot();
            Assert.Equal(value.Attempted, value.Copied + value.Failed + value.Active);
            Assert.Equal(value.Copied * 256, value.FullBytes);
            Assert.Equal(value.Copied * 184, value.ActualBytes);
            Assert.Equal(value.Copied, value.Applied);
            await Task.Yield();
        }
        await worker.WaitAsync(TimeSpan.FromSeconds(10));
        var final = aggregate.Snapshot();
        Assert.Equal(count, final.Attempted);
        Assert.Equal(count, final.Qualified);
        Assert.Equal(count / 2, final.Copied);
        Assert.Equal(count / 2, final.Failed);
        Assert.Equal(0, final.Active);
        Assert.False(final.CoverageComplete);
    }

    [Fact]
    public void ActiveTailAndUnknownByteCountsAreExplicit()
    {
        var aggregate = new MetalVertexPrefixProfile.Aggregate();
        var pending = new MetalVertexPrefixProfile.CopyScope(aggregate, false);
        Assert.Equal(1, aggregate.Snapshot().Active);
        Assert.False(aggregate.Snapshot().CoverageComplete);
        pending.Stream(ulong.MaxValue, 1); pending.Complete(false); pending.Finish();
        var result = aggregate.Snapshot();
        Assert.Equal(1, result.Copied); Assert.Equal(1, result.RejectedBytes);
        Assert.Equal(0, result.FullBytes); Assert.Equal(0, result.ActualBytes);
        Assert.False(result.CoverageComplete);
    }

    [Theory]
    [InlineData("_attempted", true)]
    [InlineData("_full", false)]
    public void OverflowIsStickyAndNeverPublishesPartialOrNegativeTotals(string fieldName, bool beginOverflow)
    {
        var aggregate = new MetalVertexPrefixProfile.Aggregate();
        typeof(MetalVertexPrefixProfile.Aggregate).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(aggregate, long.MaxValue);
        var scope = new MetalVertexPrefixProfile.CopyScope(aggregate, true);
        scope.Qualify(); scope.Stream(1, 1); scope.Complete(true); scope.Finish();
        var result = aggregate.Snapshot();
        Assert.True(result.Overflow); Assert.False(result.CoverageComplete);
        Assert.Equal(0, result.Active); Assert.Equal(0, result.Copied);
        Assert.Equal(beginOverflow ? long.MaxValue : 1, result.Attempted);
        Assert.Equal(beginOverflow ? 0 : long.MaxValue, result.FullBytes);
        Assert.Equal(0, result.ActualBytes);
    }

    private sealed class ProfileDisabledFactAttribute : FactAttribute
    {
        public ProfileDisabledFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE") == "1") Skip = "Requires a fresh profiling-disabled process.";
        }
    }

    [ProfileDisabledFact]
    public void DisabledScopesAndReportHaveZeroMeasuredManagedAllocations()
    {
        MetalVertexPrefixProfile.WarmUp();
        void Run()
        {
            for (var i = 0; i < 10000; i++)
            {
                var scope = MetalVertexPrefixProfile.BeginCopy(true);
                scope.Qualify(); scope.Stream(256, 184); scope.Complete(true); scope.Finish();
                MetalVertexPrefixProfile.EndReport();
            }
        }
        Run();
        var before = GC.GetAllocatedBytesForCurrentThread(); Run();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, bytes); Assert.Equal(default, MetalVertexPrefixProfile.Snapshot());
    }
}
