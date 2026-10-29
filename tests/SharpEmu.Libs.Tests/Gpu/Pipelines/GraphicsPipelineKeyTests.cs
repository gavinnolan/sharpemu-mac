// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.PerfAudit;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class GraphicsPipelineKeyTests
{
    private static GraphicsPipelineKey Key(ulong vertexProgram, uint bindings, uint attributes, uint strideSeed = 16)
    {
        var key = new GraphicsPipelineKey
        {
            Rendering = new PipelineRenderingState { ColorCount = 1, DepthFormat = GpuFormat.D32Sfloat },
            VertexInput = new PipelineVertexInputState { BindingCount = (byte)bindings, AttributeCount = (byte)attributes },
            StaticParameters = new PipelineStaticParameters { ColorCount = 1, WithDepth = true },
            VertexProgramId = vertexProgram,
            PixelProgramId = vertexProgram * 3,
        };
        key.Rendering.ColorFormats[0] = GpuFormat.B8G8R8A8Unorm;
        for (var index = 0; index < bindings; index++)
        {
            key.VertexInput.Bindings[index] = new PipelineVertexBinding(strideSeed * (uint)(index + 1), index == 2);
        }

        for (var index = 0; index < attributes; index++)
        {
            key.VertexInput.Attributes[index] = new PipelineVertexAttribute((uint)index * 4, (byte)(index % Math.Max(1, (int)bindings)));
        }

        return key;
    }

    [Fact]
    public void KeysAreEqualWhenProgramsAndTheUsedVertexInputMatch()
    {
        Assert.Equal(Key(7, 3, 5), Key(7, 3, 5));
        Assert.Equal(Key(7, 3, 5).GetHashCode(), Key(7, 3, 5).GetHashCode());
        Assert.NotEqual(Key(7, 3, 5), Key(8, 3, 5));
        Assert.NotEqual(Key(7, 3, 5), Key(7, 3, 5, strideSeed: 32));
        Assert.NotEqual(Key(7, 3, 5), Key(7, 2, 5));
        Assert.NotEqual(Key(7, 3, 5), Key(7, 3, 4));
    }

    [Fact]
    public void AChangeInTheLastUsedBindingOrAttributeMakesKeysDiffer()
    {
        var changedBinding = Key(7, 4, 6);
        changedBinding.VertexInput.Bindings[3] = new PipelineVertexBinding(999, true);
        Assert.NotEqual(Key(7, 4, 6), changedBinding);
        var changedAttribute = Key(7, 4, 6);
        changedAttribute.VertexInput.Attributes[5] = new PipelineVertexAttribute(999, 3);
        Assert.NotEqual(Key(7, 4, 6), changedAttribute);
    }

    [Fact]
    public void AFilledLookupKeyThatWasReusedMatchesAFreshOne()
    {
        // The draw path fills one thread-local key for every draw; the entries a larger draw left past the count
        // are cleared by Reset and never part of the comparison.
        var lookup = Key(7, 6, 6);
        lookup.VertexInput.Reset();
        lookup.VertexInput.BindingCount = 2;
        lookup.VertexInput.AttributeCount = 3;
        var fresh = Key(7, 2, 3);
        for (var index = 0; index < 2; index++) lookup.VertexInput.Bindings[index] = fresh.VertexInput.Bindings[index];
        for (var index = 0; index < 3; index++) lookup.VertexInput.Attributes[index] = fresh.VertexInput.Attributes[index];
        Assert.Equal(fresh.VertexInput, lookup.VertexInput);
        Assert.Equal(fresh.VertexInput.GetHashCode(), lookup.VertexInput.GetHashCode());
    }

    // SHARPEMU_PIPELINE_KEY_BENCH_OUTPUT=<path>: the cost of the per-draw pipeline map lookup - a reused lookup key
    // against a map of a few hundred pipelines, the way ShaderPipelineCache.CreateGraphicsPipeline probes it.
    [Fact]
    public void MeasurePipelineKeyLookupWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("SHARPEMU_PIPELINE_KEY_BENCH_OUTPUT");
        if (path is null) return;
        var map = new ConcurrentDictionary<GraphicsPipelineKey, int>();
        for (var index = 0; index < 300; index++) map[Key((ulong)index + 1, (uint)(index % 4) + 1, (uint)(index % 5) + 2)] = index;
        var results = new List<object>();
        foreach (var (bindings, attributes) in new[] { (2u, 3u), (4u, 6u) })
        {
            var lookup = Key(150, bindings, attributes);
            const int iterations = 2_000_000;
            var samples = new List<double>();
            long sum = 0;
            for (var round = 0; round < 12; round++)
            {
                var started = OperatingSystem.IsMacOS() ? DarwinThreadCpuClock.Nanoseconds() : 0;
                var wall = Stopwatch.GetTimestamp();
                for (var iteration = 0; iteration < iterations; iteration++)
                {
                    lookup.VertexInput.Reset();
                    lookup.VertexProgramId = (ulong)(iteration % 300) + 1;
                    lookup.PixelProgramId = lookup.VertexProgramId * 3;
                    lookup.VertexInput.BindingCount = (byte)((lookup.VertexProgramId - 1) % 4 + 1);
                    lookup.VertexInput.AttributeCount = (byte)((lookup.VertexProgramId - 1) % 5 + 2);
                    for (var index = 0; index < lookup.VertexInput.BindingCount; index++)
                        lookup.VertexInput.Bindings[index] = new PipelineVertexBinding(16u * (uint)(index + 1), index == 2);
                    for (var index = 0; index < lookup.VertexInput.AttributeCount; index++)
                        lookup.VertexInput.Attributes[index] = new PipelineVertexAttribute((uint)index * 4, (byte)(index % lookup.VertexInput.BindingCount));
                    sum += map.TryGetValue(lookup, out var found) ? found : -1;
                }

                var elapsed = OperatingSystem.IsMacOS()
                    ? (DarwinThreadCpuClock.Nanoseconds() - started) / (double)iterations
                    : Stopwatch.GetElapsedTime(wall).TotalNanoseconds / iterations;
                if (round >= 2) samples.Add(elapsed);
            }

            samples.Sort();
            results.Add(new { bindings, attributes, medianNanosecondsPerLookup = samples[samples.Count / 2], minNanosecondsPerLookup = samples[0], sum });
        }

        File.WriteAllText(path, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
