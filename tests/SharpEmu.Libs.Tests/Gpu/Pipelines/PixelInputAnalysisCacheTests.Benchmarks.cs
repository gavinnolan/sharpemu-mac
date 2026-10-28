// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.PerfAudit;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed partial class PixelInputAnalysisCacheTests
{
    [Fact]
    public void BenchmarkPixelInputAnalysisWhenRequested()
    {
        var output = Environment.GetEnvironmentVariable("SHARPEMU_PIXEL_INPUT_BENCH_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        var rows = new List<object>();
        // Exact previous attribute-cache factory, paired with the unchanged compiler mask.
        // This includes the old cold-cache work instead of comparing a cache fill to a mask alone.
        var referenceAttributes = new ConditionalWeakTable<Gen5ShaderProgram, uint[]>();
        uint Cold(Gen5ShaderProgram program, uint[] controls, bool cached)
        {
            if (cached) ShaderPipelineCache.InterpolatedAttributesOf(program);
            else referenceAttributes.GetValue(program, static p => p.Instructions
                .Select(static i => i.Control).OfType<Gen5InterpolationControl>()
                .Select(static c => c.Attribute).Distinct().Order().ToArray());
            return ShaderPipelineCache.PerVertexParametersOf(program, controls, cached);
        }
        foreach (var length in new[] { 16, 512, 4096 })
        foreach (var dense in new[] { false, true })
        foreach (var active in new[] { false, true })
        {
            var ordinary = dense ? Interpolation("VInterpP1F32", 1) :
                new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Vop2, "VAddF32", [0],
                    [Gen5Operand.Vector(0)], [Gen5Operand.Vector(1), Gen5Operand.Vector(2)], null);
            var instructions = Enumerable.Repeat(ordinary, length).ToArray();
            if (active)
                for (var i = 0; i < 8; i++) instructions[i * (length / 8)] = Interpolation("VInterpMovF32", (uint)i);
            var program = new Gen5ShaderProgram(0, instructions);
            uint[] controls = Enumerable.Range(0, 32).Select(i => (uint)i).ToArray();
            var expected = active ? 255u : 0u;
            ShaderPipelineCache.InterpolatedAttributesOf(program);
            uint Run(bool cached, int count)
            {
                var sum = 0u;
                for (var i = 0; i < count; i++)
                    sum += ShaderPipelineCache.PerVertexParametersOf(program, controls, cached);
                return sum;
            }
            var warm = Stopwatch.StartNew();
            while (warm.Elapsed < TimeSpan.FromSeconds(2)) { Run(false, 256); Run(true, 256); }
            var count = length == 4096 ? 10000 : 100000;
            for (var round = 0; round < 7; round++)
            foreach (var cached in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var cpu = DarwinThreadCpuClock.Nanoseconds();
                var started = Stopwatch.GetTimestamp();
                var sum = Run(cached, count);
                var wallNs = Stopwatch.GetElapsedTime(started).TotalNanoseconds / count;
                var cpuNs = (DarwinThreadCpuClock.Nanoseconds() - cpu) / (double)count;
                var allocated = (GC.GetAllocatedBytesForCurrentThread() - bytes) / (double)count;
                Assert.Equal(unchecked(expected * count), sum);
                rows.Add(new { phase="warm", length, dense, active, round, cached, count, wallNs, cpuNs, allocated });
            }
            // Precreate keys outside the measurement. Instructions are shared and immutable;
            // each record instance has a distinct weak-cache identity, including the warmup keys.
            foreach (var cached in new[] { false, true })
                for (var i = 0; i < 64; i++) Cold(program with { }, controls, cached);
            const int coldCount = 1024;
            for (var round = 0; round < 7; round++)
            foreach (var cached in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
            {
                var keys = Enumerable.Range(0, coldCount).Select(_ => program with { }).ToArray();
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var cpu = DarwinThreadCpuClock.Nanoseconds();
                var started = Stopwatch.GetTimestamp();
                var sum = 0u;
                foreach (var key in keys) sum += Cold(key, controls, cached);
                var wallNs = Stopwatch.GetElapsedTime(started).TotalNanoseconds / coldCount;
                var cpuNs = (DarwinThreadCpuClock.Nanoseconds() - cpu) / (double)coldCount;
                var allocated = (GC.GetAllocatedBytesForCurrentThread() - bytes) / (double)coldCount;
                Assert.Equal(unchecked(expected * coldCount), sum);
                GC.KeepAlive(keys);
                rows.Add(new { phase="cold", length, dense, active, round, cached, count=coldCount, wallNs, cpuNs, allocated });
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { architecture=RuntimeInformation.ProcessArchitecture.ToString(),
            framework=RuntimeInformation.FrameworkDescription, warmupSeconds=2, rounds=7,
            caveat="Synthetic sparse ALU and interpolation-dense stress programs, no GPU. Cold includes old attribute-cache factory plus compiler mask versus combined cache plus live mapping; key creation excluded. No game FPS inference.", rows }, new JsonSerializerOptions { WriteIndented=true }));
    }
}
