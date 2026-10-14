// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Structural checks over the mesh emission of NGG primitive shaders. Pure text generation,
/// so these run everywhere; the Metal-compiler and device checks live in
/// <see cref="NggMeshRuntimeTests"/>.
/// </summary>
public sealed class NggMeshTranslationTests
{
    [Fact]
    public void PassthroughPrimitiveShaderKeepsThePerVertexPath()
    {
        var program = Gen5NggFixtures.Passthrough();
        Assert.False(ShaderCompileRequest.RequiresPrimitiveShaderWorkgroup(program));

        var shader = Gen5NggFixtures.CompileOrThrow(program);
        Assert.Equal(Gen5MslStage.Vertex, shader.Stage);
        Assert.Equal("gen5_vs", shader.EntryPoint);
        Assert.Null(shader.MeshInfo);
    }

    [Fact]
    public void PrimitiveShaderWithoutTheStageMaskKeepsThePerVertexPath()
    {
        // The same program without the draw's primitive-shader state must not change
        // backends: only a request that says the draw selected the NGG stage may.
        var request = Gen5ComputeFixtures.RequestOrThrow(
            Gen5NggFixtures.CullingTriangle(),
            ShaderStage.Vertex,
            userDataBase: Gen5NggFixtures.VertexUserDataBase);
        Assert.False(Gen5MslTranslator.RequiresMeshEmission(request));
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Equal(Gen5MslStage.Vertex, shader.Stage);
    }

    [Fact]
    public void CullingPrimitiveShaderEmitsAMeshFunction()
    {
        var shader = Gen5NggFixtures.CompileOrThrow(Gen5NggFixtures.CullingTriangle());
        Assert.Equal(Gen5MslStage.Mesh, shader.Stage);
        Assert.Equal("gen5_ms", shader.EntryPoint);
        Assert.Contains("[[mesh, max_total_threads_per_threadgroup(64)]]", shader.Source, StringComparison.Ordinal);
        Assert.Contains("void gen5_ms(", shader.Source, StringComparison.Ordinal);
        Assert.Contains(
            "using Gen5MeshOutput = mesh<Gen5MeshVertex, void, 64, 1, topology::triangle>;",
            shader.Source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MeshInterfaceDescribesTheSubgroupLaunch()
    {
        var shader = Gen5NggFixtures.CompileOrThrow(Gen5NggFixtures.CullingTriangle());
        var mesh = Assert.IsType<Gen5MslMeshInfo>(shader.MeshInfo);
        Assert.Equal(64u, mesh.ThreadsPerThreadgroup);
        Assert.Equal(2u, mesh.WavesPerThreadgroup);
        Assert.Equal(Gen5NggFixtures.VertexCount, mesh.MaxVertices);
        Assert.Equal(Gen5NggFixtures.PrimitiveCount, mesh.MaxPrimitives);
        Assert.Equal(3u, mesh.VerticesPerPrimitive);
        Assert.Equal(Gen5MslMeshTopology.Triangle, mesh.Topology);
        Assert.Equal(2u, mesh.DrawInfoBufferIndex);
        Assert.Equal(3u, mesh.IndexBufferIndex);

        // The threadgroup size the host launches with has to match the interface.
        Assert.Equal(mesh.ThreadsPerThreadgroup, shader.ThreadgroupSizeX);
    }

    [Fact]
    public void MeshVertexPayloadMatchesTheVertexStageInterface()
    {
        // The pixel program is translated against user(locn*) params, so a mesh pipeline
        // can only reuse it when the mesh vertex struct declares the same locations.
        var mesh = Gen5NggFixtures.CompileOrThrow(Gen5NggFixtures.CullingTriangle(), requiredVertexOutputCount: 3);
        var vertex = Gen5NggFixtures.CompileOrThrow(Gen5NggFixtures.Passthrough(), requiredVertexOutputCount: 3);
        for (var location = 0; location < 3; location++)
        {
            var field = $"float4 param{location} [[user(locn{location})]];";
            Assert.Contains(field, mesh.Source, StringComparison.Ordinal);
            Assert.Contains(field, vertex.Source, StringComparison.Ordinal);
        }

        Assert.Contains("float4 sharpemu_position [[position]];", mesh.Source, StringComparison.Ordinal);
        Assert.Equal(3u, mesh.AttributeCount);
    }

    [Fact]
    public void LocalDataShareBecomesThreadgroupMemory()
    {
        var shader = Gen5NggFixtures.CompileOrThrow(Gen5NggFixtures.CullingTriangle());

        // Not the per-invocation scratch array the vertex path uses.
        Assert.Contains("threadgroup uint sharpemu_lds[", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("thread uint sharpemu_lds[", shader.Source, StringComparison.Ordinal);
        var mesh = Assert.IsType<Gen5MslMeshInfo>(shader.MeshInfo);
        Assert.Contains($"threadgroup uint sharpemu_lds[{mesh.ThreadgroupMemoryBytes / 4}]", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void BarrierParksTheWaveInsteadOfBarrieringInPlace()
    {
        var shader = Gen5NggFixtures.CompileOrThrow(Gen5NggFixtures.CullingTriangle());

        // S_BARRIER must end its block and park the wave; the dispatcher's loop body is
        // the only place a threadgroup_barrier may appear, because two waves of the same
        // subgroup can sit at different pc values.
        Assert.Contains("sharpemu_mesh_wait = true;", shader.Source, StringComparison.Ordinal);
        Assert.Contains("while (active && !sharpemu_mesh_wait)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("case 1u:", shader.Source, StringComparison.Ordinal);
        Assert.Contains("atomic_fetch_add_explicit(&sharpemu_mesh_parked", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void AllocationRequestDrivesThePrimitiveCount()
    {
        var shader = Gen5NggFixtures.CompileOrThrow(Gen5NggFixtures.CullingTriangle());

        // M0[9:0] is the vertex count and M0[22:12] the primitive count.
        Assert.Contains("sharpemu_mesh_alloc[0] = min(((s124 >> 0u) & 0x3FFu)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_mesh_alloc[1] = min(((s124 >> 12u) & 0x7FFu)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("set_primitive_count(min(sharpemu_mesh_alloc[1]", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimitiveExportDecodesTenBitSlots()
    {
        var shader = Gen5NggFixtures.CompileOrThrow(Gen5NggFixtures.CullingTriangle());
        Assert.Contains("sharpemu_mesh_prim[0] = (", shader.Source, StringComparison.Ordinal);
        Assert.Contains(">> 0u) & 0x1FFu;", shader.Source, StringComparison.Ordinal);
        Assert.Contains(">> 10u) & 0x1FFu;", shader.Source, StringComparison.Ordinal);
        Assert.Contains(">> 20u) & 0x1FFu;", shader.Source, StringComparison.Ordinal);
        Assert.Contains(">> 31u) & 1u) != 0u;", shader.Source, StringComparison.Ordinal);

        // A culled primitive becomes degenerate; Metal cannot drop one in place.
        Assert.Contains("sharpemu_mesh_culled ? uint3(0u) : sharpemu_mesh_prim", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void MergedWaveInfoSeedsTheGuestExecRebuild()
    {
        var shader = Gen5NggFixtures.CompileOrThrow(Gen5NggFixtures.CullingTriangle());
        Assert.Contains("s3 = sharpemu_mesh_wave_verts |", shader.Source, StringComparison.Ordinal);
        Assert.Contains("(sharpemu_mesh_wave_prims << 8u)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("(2u << 28u)", shader.Source, StringComparison.Ordinal);
        Assert.Contains("s2 =", shader.Source, StringComparison.Ordinal);

        // The ES/VS vector inputs stay where the per-vertex path puts them.
        Assert.Contains("v5 = sharpemu_mesh_vertex_index;", shader.Source, StringComparison.Ordinal);
        Assert.Contains("v8 = sharpemu_mesh_instance_index;", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemScalarsThatAreAlsoUserDataAreLeftAlone()
    {
        // A program whose user data starts at s0 has no system scalars to seed; writing
        // them would clobber a resource pointer.
        var request = Gen5ComputeFixtures.RequestOrThrow(
            Gen5NggFixtures.CullingTriangle(),
            ShaderStage.Vertex,
            primitiveShader: Gen5NggFixtures.StageInfo,
            userDataBase: 0);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Equal(Gen5MslStage.Mesh, shader.Stage);
        Assert.DoesNotContain("s3 = sharpemu_mesh_wave_verts", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludedSystemScalarsAreSeededWhenTheUserDataStartsAtS0()
    {
        // A fused program's user data starts at s0 so the geometry user-data pointer can
        // occupy s[0:1]; s2..s7 are excluded from it and are still the launch state.
        var request = Gen5ComputeFixtures.RequestOrThrow(
            Gen5NggFixtures.CullingTriangle(),
            ShaderStage.Vertex,
            primitiveShader: Gen5NggFixtures.StageInfo,
            userDataBase: 0,
            excludedUserDataRegisters: 0xFC);
        Assert.Equal(0xFCUL, request.ExcludedUserDataRegisters);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.Equal(Gen5MslStage.Mesh, shader.Stage);
        Assert.Contains("s3 = sharpemu_mesh_wave_verts", shader.Source, StringComparison.Ordinal);
        Assert.Contains("s2 =", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void SubgroupBudgetIsClampedToWhatAMeshThreadgroupCanCarry()
    {
        // A title that programs more than the hardware's 256 output vertices, or leaves the
        // on-chip registers at zero, still has to produce a compilable pipeline.
        var huge = Gen5NggFixtures.StageInfo with
        {
            VerticesPerSubgroup = 4096,
            PrimitivesPerSubgroup = 4096,
        };
        var clamped = Gen5NggFixtures.CompileOrThrow(Gen5NggFixtures.CullingTriangle(), huge);
        var mesh = Assert.IsType<Gen5MslMeshInfo>(clamped.MeshInfo);
        Assert.Equal(256u, mesh.MaxVertices);
        Assert.Equal(256u, mesh.MaxPrimitives);
        Assert.Equal(256u, mesh.ThreadsPerThreadgroup);

        var empty = Gen5NggFixtures.StageInfo with
        {
            VerticesPerSubgroup = 0,
            PrimitivesPerSubgroup = 0,
            MaxOutputPerSubgroup = 0,
        };
        var fallback = Gen5NggFixtures.CompileOrThrow(Gen5NggFixtures.CullingTriangle(), empty);
        var fallbackMesh = Assert.IsType<Gen5MslMeshInfo>(fallback.MeshInfo);
        Assert.Equal(32u, fallbackMesh.ThreadsPerThreadgroup);
        Assert.Equal(1u, fallbackMesh.WavesPerThreadgroup);
    }

    [Fact]
    public void MeshStageKeepsItsOwnVertexFetchBinding()
    {
        // The ES part of a real primitive shader fetches vertices itself, through a V# its
        // user data carries. A mesh function has no [[stage_in]] to substitute that with, so
        // the fetch has to reach a real buffer binding.
        var runtimeInputs = new ResourceRuntimeInputs { UserData = Gen5NggFixtures.PositionUserData() };
        var request = Gen5NggFixtures.Request(
            Gen5NggFixtures.BufferFetchTriangle(), runtimeInputs: runtimeInputs);
        Assert.Empty(request.VertexInputs);
        var buffer = Assert.Single(request.Resources.Info.Buffers);
        Assert.Equal(77u, buffer.DescriptorFormat);

        var shader = Gen5NggFixtures.CompileOrThrow(
            Gen5NggFixtures.BufferFetchTriangle(), runtimeInputs: runtimeInputs);
        Assert.Equal(Gen5MslStage.Mesh, shader.Stage);

        // The fetch reads the bound buffer, not a vertex attribute.
        Assert.Contains("b0", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("[[stage_in]]", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("[[attribute(", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedVertexFetchIsRefusedForAWorkgroupPrimitiveShader()
    {
        // The substitution rewrites the program's own fetch into a host vertex attribute and
        // drops the scalar loads behind its V#. For the merged ES/GS workgroup stage that
        // leaves the fetch with nothing to read, so it must not be applied.
        var culling = Gen5NggFixtures.CullingTriangle();
        Assert.False(ShaderCompileRequest.AllowsEmbeddedVertexFetch(culling, Gen5NggFixtures.StageInfo));

        // Without the draw's primitive-shader state the program is an ordinary vertex
        // shader again, and the substitution is allowed.
        Assert.True(ShaderCompileRequest.AllowsEmbeddedVertexFetch(culling, null));

        // A passthrough primitive shader stays on the per-vertex path, so it keeps it too.
        Assert.True(ShaderCompileRequest.AllowsEmbeddedVertexFetch(
            Gen5NggFixtures.Passthrough(), Gen5NggFixtures.StageInfo));
    }

    [Fact]
    public void LineAndPointOutputPrimitivesChangeTheTopology()
    {
        var line = Gen5NggFixtures.CompileOrThrow(
            Gen5NggFixtures.CullingTriangle(),
            Gen5NggFixtures.StageInfo with { OutputPrimitiveType = Gen5NggExports.OutputPrimitiveLineStrip });
        Assert.Equal(Gen5MslMeshTopology.Line, Assert.IsType<Gen5MslMeshInfo>(line.MeshInfo).Topology);
        Assert.Contains("topology::line", line.Source, StringComparison.Ordinal);

        var point = Gen5NggFixtures.CompileOrThrow(
            Gen5NggFixtures.CullingTriangle(),
            Gen5NggFixtures.StageInfo with { OutputPrimitiveType = Gen5NggExports.OutputPrimitivePointList });
        Assert.Equal(Gen5MslMeshTopology.Point, Assert.IsType<Gen5MslMeshInfo>(point.MeshInfo).Topology);
        Assert.Contains("topology::point", point.Source, StringComparison.Ordinal);
    }
    [Fact]
    public void AGsHalfAssemblesItsInputPrimitivesFromTheDrawTopology()
    {
        // A fused program's GS half reads its input vertices back from LDS through the GS
        // vertex offsets: the guest takes bits 2-15 of each half as the ES vertex, so the
        // offsets carry the vertex index shifted by two, and the subgroup takes its share
        // of the draw's input primitives rather than a full budget.
        var shader = Gen5NggFixtures.CompileOrThrow(
            Gen5NggFixtures.CullingTriangle(),
            Gen5NggFixtures.StageInfo with { GeometryVerticesOut = 3 });
        var mesh = Assert.IsType<Gen5MslMeshInfo>(shader.MeshInfo);
        Assert.True(mesh.HasGeometryHalf);
        Assert.Contains("v0 = ((sharpemu_gs_v0 << 2u) & 0xFFFFu) |", shader.Source, StringComparison.Ordinal);
        Assert.Contains("(((sharpemu_gs_v1 << 2u) & 0xFFFFu) << 16u);", shader.Source, StringComparison.Ordinal);
        Assert.Contains("v1 = (sharpemu_gs_v2 << 2u) & 0xFFFFu;", shader.Source, StringComparison.Ordinal);
        Assert.Contains(
            "const uint sharpemu_mesh_vertex_slot = sharpemu_gs_vertex_start + sharpemu_tg_index;",
            shader.Source,
            StringComparison.Ordinal);
        Assert.Contains("sharpemu_draw.input_vertex_count", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("sharpemu_mesh_prim_base", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void APrimitiveShaderWithoutAGsHalfKeepsTheListOffsets()
    {
        var shader = Gen5NggFixtures.CompileOrThrow(Gen5NggFixtures.CullingTriangle());
        Assert.False(Assert.IsType<Gen5MslMeshInfo>(shader.MeshInfo).HasGeometryHalf);
        Assert.Contains("const uint sharpemu_mesh_prim_base = sharpemu_tg_index * 3u;", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("sharpemu_gs_v0", shader.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Gen5MslMeshInputTopology.TriangleList, 3u, 10u)]
    [InlineData(Gen5MslMeshInputTopology.TriangleStrip, 3u, 10u)]
    [InlineData(Gen5MslMeshInputTopology.PointList, 3u, 10u)]
    [InlineData(Gen5MslMeshInputTopology.TriangleStrip, 4u, 8u)]
    [InlineData(Gen5MslMeshInputTopology.TriangleStrip, 1u, 30u)]
    public void AGsSubgroupTakesWhatItsInputsAndOutputsFit(
        Gen5MslMeshInputTopology input, uint verticesOut, uint expected)
    {
        var mesh = new Gen5MslMeshInfo(
            ThreadsPerThreadgroup: 64, WavesPerThreadgroup: 2, MaxVertices: 64, MaxPrimitives: 64,
            VerticesPerPrimitive: 3, Topology: Gen5MslMeshTopology.Triangle, ThreadgroupMemoryBytes: 4096,
            DrawInfoBufferIndex: 2, IndexBufferIndex: 3, ReadsIndexBuffer: true, GeometryVerticesOut: verticesOut);
        Assert.Equal(expected, mesh.InputPrimitivesPerSubgroup(input));
    }

    [Theory]
    [InlineData(Gen5MslMeshInputTopology.TriangleStrip, 4u, 2u)]
    [InlineData(Gen5MslMeshInputTopology.TriangleStrip, 2u, 0u)]
    [InlineData(Gen5MslMeshInputTopology.TriangleList, 6u, 2u)]
    [InlineData(Gen5MslMeshInputTopology.PointList, 5u, 5u)]
    public void AGsDrawCountsItsInputPrimitivesByTopology(Gen5MslMeshInputTopology input, uint count, uint expected) =>
        Assert.Equal(expected, Gen5MslMeshInfo.InputPrimitiveCount(input, count));
}
