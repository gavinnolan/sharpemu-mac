// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests;

// The static precompile keeps nothing it compiles: storing a few hundred libraries from two
// threads, as its background workers do, raises the process footprint by far less than the
// 300 MB it may take, and what it reaches is reported.
public sealed class MetalStaticPrecompileMemoryTests(ITestOutputHelper output) : IDisposable
{
    private const int Programs = 160;
    private const ulong AllowedGrowthBytes = 300UL << 20;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sharpemu-static-precompile-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void StoringLibrariesKeepsTheFootprintWithinTheBudget()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("The static precompile compiles on the device only behind the exclusive device gate.");
            return;
        }

        Assert.True(OperatingSystem.IsMacOS());
        var device = MetalNative.MTLCreateSystemDefaultDevice();
        Assert.NotEqual(0, device);

        using var archive = MetalBinaryArchive.Open(_root, device, "translator-memory", MetalBinaryArchive.DiskBudgetBytes);
        var (start, _) = MetalHostMemory.Footprint();
        var peak = start;
        var stored = 0;
        var next = -1;
        var started = Stopwatch.GetTimestamp();
        var workers = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
        {
            int index;
            while ((index = Interlocked.Increment(ref next)) < Programs)
            {
                foreach (var shader in Shaders(index))
                {
                    var key = MetalBinaryArchive.LibraryKey(shader.Source, shader.UsesPerVertexInputs);
                    if (MetalStaticPrecompile.Store(archive, device, shader, key, out var error) > 0)
                    {
                        Interlocked.Increment(ref stored);
                    }
                    else
                    {
                        Assert.Fail(error ?? "not stored");
                    }

                    var (current, _) = MetalHostMemory.Footprint();
                    lock (archive)
                    {
                        peak = Math.Max(peak, current);
                    }
                }
            }
        })).ToList();
        workers.ForEach(static thread => thread.Start());
        workers.ForEach(static thread => thread.Join());
        var (end, _) = MetalHostMemory.Footprint();
        output.WriteLine(
            $"stored={stored} s={Stopwatch.GetElapsedTime(started).TotalSeconds:F1} archive_mb={archive.DiskBytes() / 1048576.0:F1} " +
            $"footprint_mb start={start / 1048576.0:F0} peak={peak / 1048576.0:F0} end={end / 1048576.0:F0} " +
            $"growth_mb={(peak - start) / 1048576.0:F0}");
        Assert.Equal(Programs * 3, stored);
        Assert.True(peak - start < AllowedGrowthBytes, $"The footprint grew by {(peak - start) >> 20} MB.");
    }

    // A vertex, a pixel and a compute program of their own source each.
    private static IEnumerable<Gen5MslShader> Shaders(int index)
    {
        var constant = (index * 0.013f + 0.5f).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        yield return new Gen5MslShader($$"""
            #include <metal_stdlib>
            using namespace metal;
            struct Gen5VsOut { float4 position [[position]]; float4 attr0 [[user(locn0)]]; };
            vertex Gen5VsOut gen5_vs(uint id [[vertex_id]], constant float4* data [[buffer(0)]])
            {
                Gen5VsOut out;
                out.position = data[id] * {{constant}}f;
                out.attr0 = sin(data[id + 1] * {{constant}}f);
                return out;
            }
            """, "gen5_vs", Gen5MslStage.Vertex, 1);
        yield return new Gen5MslShader($$"""
            #include <metal_stdlib>
            using namespace metal;
            struct Gen5PsIn { float4 position [[position]]; float4 attr0 [[user(locn0), flat]]; };
            struct Gen5PsOut { float4 mrt0 [[color(0)]]; float depth [[depth(any)]]; };
            fragment Gen5PsOut gen5_ps(Gen5PsIn in [[stage_in]], texture2d<float> t [[texture(0)]])
            {
                constexpr sampler s(filter::linear);
                Gen5PsOut out;
                out.mrt0 = t.sample(s, in.attr0.xy) * {{constant}}f + cos(in.attr0);
                out.depth = in.position.z * {{constant}}f;
                return out;
            }
            """, "gen5_ps", Gen5MslStage.Pixel, 1);
        yield return new Gen5MslShader($$"""
            #include <metal_stdlib>
            using namespace metal;
            kernel void gen5_cs(device uint* out [[buffer(0)]], uint id [[thread_position_in_grid]])
            {
                out[id] = as_type<uint>(sin(float(id) * {{constant}}f));
            }
            """, "gen5_cs", Gen5MslStage.Compute, 1);
    }
}
