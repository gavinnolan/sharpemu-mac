// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.PerfAudit;

/// <summary>
/// 5. Pipeline-state lookup with 50 VS/PS pairs x 4 states = 200 cached pipelines. The program-cache probe
/// (ShaderProgramCache.ProgramKeyProbe) is internal to SharpEmu.Libs and is measured through the e2e phase
/// profile (ProgramCacheLookup / ProgramPermutationLookup) instead.
/// </summary>
internal static class LookupBenches
{
    private const int Pairs = 50;
    private const int States = 4;

    private static void Fill(GraphicsPipelineKey key, int pair, int state)
    {
        key.Rendering.Reset();
        key.VertexInput.Reset();
        key.StaticParameters.Reset();
        key.Rendering.ColorCount = 1;
        key.Rendering.ColorFormats[0] = GpuFormat.R8G8B8A8Unorm;
        key.StaticParameters.ColorCount = 1;
        key.StaticParameters.Topology = GpuPrimitiveTopology.TriangleList;
        key.StaticParameters.Samples = 1;
        key.StaticParameters.SetColorMask(0, 0xF);
        key.StaticParameters.CullBack = (state & 1) != 0;
        key.StaticParameters.SetBlendEnable(0, (state & 2) != 0);
        if ((state & 2) != 0)
        {
            key.StaticParameters.SetColorSourceBlend(0, 4);
            key.StaticParameters.SetColorDestinationBlend(0, 5);
        }

        key.VertexInput.Bindings[0] = new PipelineVertexBinding(32, false);
        key.VertexInput.Attributes[0] = new PipelineVertexAttribute(0, 0);
        key.VertexInput.Attributes[1] = new PipelineVertexAttribute(0, 16);
        key.VertexInput.BindingCount = 1;
        key.VertexInput.AttributeCount = 2;
        key.VertexProgramId = 0x1000UL + (ulong)pair * 2;
        key.PixelProgramId = 0x1001UL + (ulong)pair * 2;
    }

    private static GraphicsPipelineKey Make(int pair, int state)
    {
        var key = new GraphicsPipelineKey
        {
            Rendering = new PipelineRenderingState(),
            VertexInput = new PipelineVertexInputState(),
            StaticParameters = new PipelineStaticParameters(),
        };
        Fill(key, pair, state);
        return key;
    }

    public static void Run()
    {
        Console.WriteLine("--- 5. Shader / pipeline-state lookup (50 pairs x 4 states = 200) ---");
        var cache = new Dictionary<GraphicsPipelineKey, ulong>();
        for (var pair = 0; pair < Pairs; pair++)
            for (var state = 0; state < States; state++)
                cache[Make(pair, state)] = (ulong)(pair * States + state);
        var lookup = Make(0, 0);
        long sink = 0;
        // Pre-hashed variant: lookup key fully filled once, only ids change (best case).
        Harness.Measure("5a", "GraphicsPipelineKey dictionary lookup, 200 entries, ids swapped (hash + equals only)", 1, 6331, i =>
        {
            lookup.VertexProgramId = 0x1000UL + (ulong)(i % Pairs) * 2;
            lookup.PixelProgramId = lookup.VertexProgramId + 1;
            if (cache.TryGetValue(lookup, out var h)) sink += (long)h;
        }, note: "Libs/Gpu/Pipelines/PipelineKeys.cs:234; ShaderPipelineCache.cs:1127");
        // The per-draw path: Reset x3 + refill the lookup key from the draw's state, then probe.
        Harness.Measure("5b", "GraphicsPipelineKey refill (Reset x3 + ~20 setters) + lookup, 200 entries", 1, 6331, i =>
        {
            Fill(lookup, i % Pairs, (i >> 3) % States);
            if (cache.TryGetValue(lookup, out var h)) sink += (long)h;
        }, note: "ShaderPipelineCache.cs:1109-1127 (FillGraphicsState itself is internal; this is a faithful subset)");

        // Metal side: the descriptor key built when a pipeline state is created, and the shared-handle table probe.
        var metalInput = new PipelineVertexInputState();
        metalInput.Bindings[0] = new PipelineVertexBinding(8, false);
        metalInput.Attributes[0] = new PipelineVertexAttribute(0, 0);
        metalInput.BindingCount = 1;
        metalInput.AttributeCount = 1;
        var stage = new ShaderProgramInfo { Stage = ShaderStageKind.Vertex, Hash = 1 };
        stage.VertexFetchComponents[0] = 2;
        var description = new GraphicsPipelineDescription
        {
            Rendering = lookup.Rendering,
            VertexInput = metalInput,
            VertexInfo = new VertexInputInfo
            {
                Attributes = [new VertexAttributeResource(new BufferDescriptorWords(0, 8u << 16, 6, 64u << 12), 0, 2, 0, 0, 0, 0)],
            },
            VertexProgram = default,
            VertexStage = stage,
            StaticParameters = lookup.StaticParameters,
        };
        Fill(lookup, 0, 3);
        Harness.Measure("5c", "MetalRenderHost.PipelineStateKeyOf (descriptor key build + XxHash3), 1 color target + blend", 1, 6331, i =>
            sink += MetalRenderHost.PipelineStateKeyOf(description, new UInt128(0, (ulong)(i % 100)), new UInt128(0, (ulong)(i % 50)), false, false, 0).Length,
            note: "V2/MetalRenderHost.Pipelines.cs:1160; only paid on a pipeline-cache miss (not per draw)");
        var metalKeys = new Dictionary<MetalPipelineStateKey, int>();
        var probes = new MetalPipelineStateKey[200];
        for (var n = 0; n < 200; n++)
        {
            probes[n] = MetalRenderHost.PipelineStateKeyOf(description, new UInt128(0, (ulong)(n % 100)), new UInt128(0, (ulong)(n / 4)), false, false, 0);
            metalKeys[probes[n]] = n;
        }

        Harness.Measure("5d", "Dictionary<MetalPipelineStateKey> probe, 200 entries (hash cached, byte compare)", 1, 6331, i =>
            sink += metalKeys.TryGetValue(probes[i % 200], out var v) ? v : 0, note: "V2/MetalPipelineStateKey.cs:27");
        GC.KeepAlive(sink);
    }
}
