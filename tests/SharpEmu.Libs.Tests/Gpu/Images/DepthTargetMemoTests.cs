// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class DepthTargetMemoTests(ITestOutputHelper output)
{
    private sealed class FormatSupport : IImageFormatSupport
    {
        public int Queries;
        public bool TryGetImageFormatProperties(GpuFormat format, GpuImageType type, GpuImageTiling tiling,
            GpuImageUsage usage, GpuImageCreateFlags flags, out GpuImageFormatProperties properties)
        {
            Queries++;
            properties = new GpuImageFormatProperties { SampleCounts = GpuSampleCount.Count1Bit };
            return true;
        }
    }

    private static DepthTargetWords[] Targets() => Enumerable.Range(0, 4)
        .Select(i => RegisterWords.Depth(0x61000000UL + (ulong)i * 0x800000, 64, 64)).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlternatingEqualReadWriteBasesRetainsAllFourTargets(bool sameAddress)
    {
        using var reuse = WorkerScratch.Override(true);
        var device = new FormatSupport();
        var targets = sameAddress
            ? Enumerable.Range(1, 4).Select(i => RegisterWords.Depth(0x61000000, (uint)i * 64, 64)).ToArray()
            : Targets();
        foreach (var words in targets) Assert.NotNull(ImageRequestBuilders.DepthTarget(words, device));
        var queries = device.Queries;
        var before = GC.GetAllocatedBytesForCurrentThread();
        ulong sum = 0;
        for (var repeat = 0; repeat < 128; repeat++)
            foreach (var words in targets)
                sum += ImageRequestBuilders.DepthTarget(words, device)!.Value.DepthSize;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(sum > 0);
        Assert.Equal(queries, device.Queries);
        Assert.True(bytes <= 512, $"Warm alternating targets allocated {bytes} bytes.");
    }

    [Fact]
    public void DeviceStateAndCopiedLayoutsRemainIndependent()
    {
        using var reuse = WorkerScratch.Override(true);
        var first = new FormatSupport();
        var second = new FormatSupport();
        var words = Targets()[0];
        var original = ImageRequestBuilders.DepthTarget(words, first)!.Value;
        Assert.NotNull(ImageRequestBuilders.DepthTarget(words, second));
        Assert.True(first.Queries > 0 && second.Queries > 0);
        var changed = ImageRequestBuilders.DepthTarget(RegisterWords.Depth(words.ZReadBase, 128, 32, depthClear: true), first)!.Value;
        Assert.Equal(128u, changed.Width);
        Assert.Equal(32u, changed.Height);
        Assert.True(changed.DepthClearEnabled);
        var copied = original.Request.Description;
        copied.MipLayout[0] = default;
        var again = ImageRequestBuilders.DepthTarget(words, first)!.Value;
        Assert.Equal(64u, again.Width);
        Assert.False(again.DepthClearEnabled);
        Assert.NotEqual(0UL, again.Request.Description.MipLayout[0].Size);
        Assert.Null(ImageRequestBuilders.DepthTarget(RegisterWords.Depth(words.ZReadBase, 64, 64, depthTest: false, depthWrite: false), first));
    }

    [Fact]
    public void AlternatingDepthTargetBenchmark()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_DEPTH_MEMO_BENCH") != "1") return;
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        // The native clock agrees with elapsed CPU time on arm64. Rosetta's result was not
        // calibrated, so do not label its raw values as nanoseconds in benchmark reports.
        var cpuTimingSupported = OperatingSystem.IsMacOS() &&
            architecture == System.Runtime.InteropServices.Architecture.Arm64;
        output.WriteLine($"process_arch={architecture} cpu_clock_supported={cpuTimingSupported}");
        using var reuse = WorkerScratch.Override(true);
        var device = new FormatSupport();
        foreach (var count in new[] { 1, 4 })
        {
            var targets = Targets().Take(count).ToArray();
            ulong sum = 0;
            for (var i = 0; i < 20000; i++) sum += ImageRequestBuilders.DepthTarget(targets[i % count], device)!.Value.DepthSize;
            const int iterations = 100000;
            for (var batch = 0; batch < 7; batch++)
            {
                var queries = device.Queries;
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var cpuStart = cpuTimingSupported ? SharpEmu.HLE.DsTrace.DsTrace.SelfCpuNs() : 0;
                var start = Stopwatch.GetTimestamp();
                for (var i = 0; i < iterations; i++) sum += ImageRequestBuilders.DepthTarget(targets[i % count], device)!.Value.DepthSize;
                var elapsed = Stopwatch.GetTimestamp() - start;
                var cpu = cpuTimingSupported ? SharpEmu.HLE.DsTrace.DsTrace.SelfCpuNs() - cpuStart : 0;
                bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
                output.WriteLine($"depth_memo count={count} batch={batch} ns={elapsed * 1e9 / Stopwatch.Frequency / iterations:F2} bytes={bytes / (double)iterations:F2} format_queries={device.Queries - queries} cpu_ns={cpu / (double)iterations:F2}");
            }
            Assert.True(sum > 0);
        }
    }
}
