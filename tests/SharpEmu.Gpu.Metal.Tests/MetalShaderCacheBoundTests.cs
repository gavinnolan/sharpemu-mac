// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Gpu.Metal.Tests.Scenes;
using SharpEmu.Gpu.Metal.V2;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// The bound on the render host's shader modules and pipeline states: a released one is made
/// again on its next use and draws what it drew before, and nothing a command buffer that is
/// not done encoded is released.
/// </summary>
public sealed class MetalShaderCacheBoundTests(ITestOutputHelper output) : SceneTestBase(output)
{
    private const uint Size = 32;

    // Everything idle for a second goes, with no grace for recent use: an hour from now,
    // everything the scene made is that.
    private static readonly MetalShaderCacheLimits ReleaseEverything = new(0, 1, 0, 1, 0);

    private static long AnHourFromNow => Stopwatch.GetTimestamp() + 3600 * Stopwatch.Frequency;

    [Fact]
    public void TheDefaultsBoundTheCacheAndTheEnvironmentOverridesThem()
    {
        Assert.Equal(MetalShaderCacheLimits.Default, MetalShaderCacheLimits.FromEnvironment(_ => null));
        Assert.True(MetalShaderCacheLimits.Default.Enabled);
        // Only the counts bound it by default: nothing a title compiled is dropped for being idle.
        Assert.Equal(0, MetalShaderCacheLimits.Default.PipelineIdleSeconds);
        Assert.Equal(0, MetalShaderCacheLimits.Default.ModuleIdleSeconds);
        // ~224 MB at the measured costs per object.
        var bound = MetalShaderCacheLimits.DefaultModules * MetalShaderCacheLimits.EstimatedModuleBytes +
            MetalShaderCacheLimits.DefaultPipelines * MetalShaderCacheLimits.EstimatedPipelineBytes;
        Assert.InRange(bound, 128L << 20, 256L << 20);

        var configured = MetalShaderCacheLimits.FromEnvironment(name => name switch
        {
            "SHARPEMU_METAL_V2_PIPELINE_LIMIT" => "100",
            "SHARPEMU_METAL_V2_PIPELINE_IDLE_S" => "0",
            "SHARPEMU_METAL_V2_MODULE_LIMIT" => "not a number",
            "SHARPEMU_METAL_V2_MODULE_IDLE_S" => "2.5",
            _ => null,
        });
        Assert.Equal(new MetalShaderCacheLimits(100, 0, MetalShaderCacheLimits.DefaultModules, 2.5, MetalShaderCacheLimits.DefaultMinimumIdleSeconds), configured);

        var off = MetalShaderCacheLimits.FromEnvironment(name => name == "SHARPEMU_METAL_V2_SHADER_EVICTION" ? "0" : "1");
        Assert.Equal(MetalShaderCacheLimits.Unbounded, off);
        Assert.False(off.Enabled);
    }

    [Fact]
    public void AReleasedRenderPipelineAndItsModulesAreBuiltAgainAndDrawTheSame()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        rig.Host.ShaderCacheLimits = MetalShaderCacheLimits.Unbounded;
        var texels = new byte[Size * Size * 4];
        for (var index = 0; index < Size * Size; index++)
        {
            BitConverter.TryWriteBytes(texels.AsSpan(index * 4), 0xFF000000u | (uint)(index * 2654435761u >> 8 & 0xFFFFFF));
        }

        var image = rig.LinearImage(Size, Size, 4, texels);
        uint[] userData = [.. Descriptors.Texture(image, 56, Size, Size), .. Descriptors.Sampler()];
        byte[] Draw()
        {
            var target = rig.ColorTarget(Size, Size);
            var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size);
            rig.Submit(rig.DrawMesh(new ScenePm4(), state, SceneDraws.TexturedQuad(), ScenePrograms.SampledTexture, userData));
            return rig.ReadImage(target, Size, Size);
        }

        var first = Draw();
        var (pipelines, modules) = rig.Host.TrimShaderCache(rig.Host.CompletedSerial, AnHourFromNow, ReleaseEverything);
        var released = rig.Host.ShaderCacheCounters;
        var second = Draw();
        var rebuilt = rig.Host.ShaderCacheCounters;

        Assert.Equal(texels, first);
        Assert.Equal(first, second);
        Assert.Empty(rig.Fatals);
        Assert.True(pipelines >= 1, $"released pipelines={pipelines}");
        Assert.True(modules >= 2, $"released modules={modules}");
        Assert.Equal(0, released.ResidentPipelines);
        Assert.Equal(0, released.ResidentModules);
        Assert.True(rebuilt.PipelineRebuilds >= 1);
        Assert.True(rebuilt.ModuleRebuilds >= 2);
    }

    [Fact]
    public void AReleasedComputePipelineIsBuiltAgainAndStoresTheSame()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        rig.Host.ShaderCacheLimits = MetalShaderCacheLimits.Unbounded;
        const uint groups = 4;
        uint[] Dispatch()
        {
            var buffer = rig.Memory.Allocate(groups * 64 * 4, 256);
            rig.Submit(rig.Dispatch(new ScenePm4(), ScenePrograms.StoreGlobalId, Descriptors.Buffer(buffer, 4, groups * 64, format: 20), groups));
            return rig.Read<uint>(buffer, (int)(groups * 64));
        }

        var first = Dispatch();
        var (pipelines, modules) = rig.Host.TrimShaderCache(rig.Host.CompletedSerial, AnHourFromNow, ReleaseEverything);
        var second = Dispatch();

        for (var index = 0u; index < first.Length; index++)
        {
            Assert.Equal(index ^ 0x5A5Au, first[index]);
        }

        Assert.Equal(first, second);
        Assert.Empty(rig.Fatals);
        Assert.Equal((1, 1), (pipelines, modules));
        Assert.Equal(1, rig.Host.ShaderCacheCounters.PipelineRebuilds);
        Assert.Equal(1, rig.Host.ShaderCacheCounters.ModuleRebuilds);
    }

    [Fact]
    public void APipelineStateIsKeptUntilTheLastCommandBufferThatEncodedItIsDone()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        rig.Host.ShaderCacheLimits = MetalShaderCacheLimits.Unbounded;
        var target = rig.ColorTarget(Size, Size);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size);
        rig.Submit(rig.DrawMesh(new ScenePm4(), state, SceneDraws.Quad(0.5f, 1, 0, 0)));
        var used = rig.Host.NewestPipelineUseSerial;
        Assert.NotEqual(0UL, used);

        // As if the command buffer that drew were still on the GPU.
        var (early, _) = rig.Host.TrimShaderCache(used - 1, AnHourFromNow, ReleaseEverything);
        Assert.Equal(0, early);
        Assert.Equal(1, rig.Host.ShaderCacheCounters.ResidentPipelines);

        var (done, _) = rig.Host.TrimShaderCache(used, AnHourFromNow, ReleaseEverything);
        Assert.Equal(1, done);
        Assert.Equal(0, rig.Host.ShaderCacheCounters.ResidentPipelines);
    }

    [Fact]
    public void RecentUseAndUnboundedLimitsKeepEverything()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        rig.Host.ShaderCacheLimits = MetalShaderCacheLimits.Unbounded;
        var target = rig.ColorTarget(Size, Size);
        rig.Submit(rig.DrawMesh(new ScenePm4(), new SceneState(Size, Size).ColorTarget(0, target, Size, Size), SceneDraws.Quad(0.5f, 1, 0, 0)));
        var completed = rig.Host.CompletedSerial;

        // Unbounded: nothing goes however old it is.
        Assert.Equal((0, 0), rig.Host.TrimShaderCache(completed, AnHourFromNow, MetalShaderCacheLimits.Unbounded));
        // The defaults keep what was used within the minimum idle time, even over a limit of one.
        var tight = MetalShaderCacheLimits.Default with { Pipelines = 1, Modules = 1 };
        Assert.Equal((0, 0), rig.Host.TrimShaderCache(completed, Stopwatch.GetTimestamp(), tight));
        // Over the limit and idle past the grace, the oldest go down to it.
        var (pipelines, modules) = rig.Host.TrimShaderCache(
            completed, Stopwatch.GetTimestamp() + 60 * Stopwatch.Frequency, tight with { Modules = 1 });
        Assert.Equal(0, pipelines);
        Assert.Equal(1, modules);
        Assert.Equal(1, rig.Host.ShaderCacheCounters.ResidentModules);
    }
}
