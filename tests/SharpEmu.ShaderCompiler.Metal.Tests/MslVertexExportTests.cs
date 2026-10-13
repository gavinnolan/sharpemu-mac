// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// The auxiliary position exports (POS1..POS3): point size, render target array index,
/// viewport index, clip and cull distances, and the zero-position cull plane on POS0. The
/// structural tests read the emitted MSL; the device tests draw through a real vertex or
/// mesh pipeline and read the target back.
/// </summary>
public sealed class MslVertexExportTests(ITestOutputHelper output)
{
    private const uint TargetSize = 32;

    // PA_CL_VS_OUT_CNTL fields the translator decodes.
    private const uint ClipDistance0 = 1u << 0;
    private const uint CullDistance0 = 1u << 8;
    private const uint CullDistance1 = 1u << 9;
    private const uint UseVertexPointSize = 1u << 16;
    private const uint UseVertexLayer = 1u << 18;
    private const uint UseVertexViewport = 1u << 19;
    private const uint MiscVector = 1u << 21;
    private const uint ClipCullVector0 = 1u << 22;

    private const uint Pos0 = 12;
    private const uint Pos1 = 13;
    private const uint Pos2 = 14;

    // ---- structure ----

    [Fact]
    public void MiscVectorDeclaresPointSizeLayerAndViewportIndex()
    {
        var shader = CompileVertex(
            MiscVector | UseVertexPointSize | UseVertexLayer | UseVertexViewport,
            (Pos1, 0x5, [Vector(30), Vector(30), Vector(31), Vector(30)]));

        Assert.Contains("float sharpemu_point_size [[point_size]];", shader.Source);
        Assert.Contains("uint sharpemu_layer [[render_target_array_index]];", shader.Source);
        Assert.Contains("uint sharpemu_viewport [[viewport_array_index]];", shader.Source);
        Assert.Contains("sharpemu_out.sharpemu_point_size = as_type<float>(", shader.Source);
        Assert.Contains(" & 0x7FFu;", shader.Source);
        Assert.Contains(" >> 16u) & 0xFu;", shader.Source);
        Assert.True(shader.WritesLayer);
        Assert.True(shader.WritesViewportIndex);
        AssertCompiles(shader);
    }

    [Fact]
    public void ClipAndCullDistancesShareTheClipDistanceArray()
    {
        // POS1 x is clip plane 0 and y is cull plane 0; the zero-position plane sits between.
        var shader = CompileVertex(
            ClipCullVector0 | ClipDistance0 | CullDistance1,
            (Pos1, 0x3, [Vector(30), Vector(31), Vector(30), Vector(30)]));

        Assert.Contains("float sharpemu_clip_distance [[clip_distance]] [3];", shader.Source);
        Assert.Contains("sharpemu_out.sharpemu_clip_distance[0] = as_type<float>(", shader.Source);
        Assert.Contains(
            "sharpemu_out.sharpemu_clip_distance[1] = all(sharpemu_out.sharpemu_position == float4(0.0f)) ? -1.0f : 0.0f;",
            shader.Source);
        Assert.Matches(
            @"sharpemu_out\.sharpemu_clip_distance\[2\] = as_type<float>\(t\d+\) < 0\.0f \? -1\.0f : 1\.0e18f;",
            shader.Source);
        Assert.False(shader.WritesLayer);
        Assert.False(shader.WritesViewportIndex);
        AssertCompiles(shader);
    }

    [Fact]
    public void ClipCullVectorFollowsTheMiscVectorInThePositionSlots()
    {
        // With the misc vector enabled it owns POS1, so the distances move to POS2.
        var shader = CompileVertex(
            MiscVector | UseVertexPointSize | ClipCullVector0 | ClipDistance0,
            (Pos2, 0x1, [Vector(30), Vector(30), Vector(30), Vector(30)]));

        Assert.Contains("sharpemu_out.sharpemu_clip_distance[0] = as_type<float>(", shader.Source);

        // The point size is enabled but POS1 is never exported.
        Assert.DoesNotContain("sharpemu_out.sharpemu_point_size =", shader.Source);
        AssertCompiles(shader);
    }

    [Fact]
    public void ZeroPositionPlaneNeedsHostClipDistanceSupport()
    {
        var supported = CompileVertex(0);
        Assert.Contains("float sharpemu_clip_distance [[clip_distance]] [1];", supported.Source);

        var unsupported = CompileVertex(0, supportsClipDistance: false);
        Assert.DoesNotContain("[[clip_distance]]", unsupported.Source);
    }

    [Fact]
    public void MeshLayerAndViewportIndexArePerPrimitive()
    {
        var shader = Gen5NggFixtures.CompileOrThrow(MeshWithPos1(layer: 1u | (1u << 16)), positionExportControl:
            MiscVector | UseVertexLayer | UseVertexViewport);

        Assert.Contains("struct Gen5MeshPrimitive", shader.Source);
        Assert.Contains("uint sharpemu_layer [[render_target_array_index]];", shader.Source);
        Assert.Contains("uint sharpemu_viewport [[viewport_array_index]];", shader.Source);
        Assert.Contains("mesh<Gen5MeshVertex, Gen5MeshPrimitive,", shader.Source);
        Assert.Contains("sharpemu_mesh.set_primitive(sharpemu_tg_index, primitive);", shader.Source);
        Assert.Contains("float sharpemu_clip_distance [[clip_distance]] [1];", shader.Source);
        AssertCompiles(shader);
    }

    // ---- device ----

    [Fact]
    public void ClipDistanceClipsTheNegativeHalf()
    {
        if (SkipWithoutMetalDevice("the clip distance draw")) return;

        // Clip plane 0 is the position's x, so everything left of the center is clipped.
        var pixels = RenderVertices(
            ClipCullVector0 | ClipDistance0,
            [(Pos1, 0x1, [Vector(11), Vector(11), Vector(11), Vector(11)])]);
        Assert.Equal(0.0f, Green(pixels, 0, 4, TargetSize / 2), 3);
        Assert.Equal(1.0f, Green(pixels, 0, TargetSize - 4, TargetSize / 2), 3);
    }

    [Fact]
    public void CullDistanceKeepsAPrimitiveWithAnyVertexInside()
    {
        if (SkipWithoutMetalDevice("the cull distance draw")) return;

        // The same plane as a cull distance: two vertices are negative and one is not, so
        // the triangle survives whole instead of being clipped at x = 0.
        var pixels = RenderVertices(
            ClipCullVector0 | CullDistance0,
            [(Pos1, 0x1, [Vector(11), Vector(11), Vector(11), Vector(11)])]);
        for (var x = 0u; x < TargetSize; x++)
        {
            Assert.Equal(1.0f, Green(pixels, 0, x, TargetSize / 2), 3);
        }
    }

    [Fact]
    public void CullDistanceCullsAPrimitiveWithEveryVertexOutside()
    {
        if (SkipWithoutMetalDevice("the cull distance draw")) return;

        var pixels = RenderVertices(
            ClipCullVector0 | CullDistance0,
            [(Pos1, 0x1, [Vector(22), Vector(22), Vector(22), Vector(22)])]);
        AssertLayerClear(pixels, 0);
    }

    [Fact]
    public void ZeroPositionCullsThePrimitive()
    {
        if (SkipWithoutMetalDevice("the zero-position draw")) return;

        // The third vertex exports (0, 0, 0, 0): its primitive is dropped.
        var pixels = RenderVertices(0, [], zeroThirdVertex: true);
        AssertLayerClear(pixels, 0);

        // The same draw with every vertex valid covers the target, so the cull is the
        // plane's doing and not the harness's.
        var control = RenderVertices(0, []);
        Assert.Equal(1.0f, Green(control, 0, TargetSize / 2, TargetSize / 2), 3);
    }

    [Fact]
    public void LayerIndexRendersIntoTheArraySlice()
    {
        if (SkipWithoutMetalDevice("the layered draw")) return;

        var pixels = RenderVertices(
            MiscVector | UseVertexLayer,
            [(Pos1, 0x4, [Vector(23), Vector(23), Vector(23), Vector(23)])],
            layers: 2);
        AssertLayerClear(pixels, 0);
        Assert.Equal(1.0f, Green(pixels, 1, TargetSize / 2, TargetSize / 2), 3);
        Assert.Equal(1.0f, Green(pixels, 1, 1, 1), 3);
    }

    [Fact]
    public void ViewportIndexSelectsTheViewport()
    {
        if (SkipWithoutMetalDevice("the multi-viewport draw")) return;

        // Viewport 0 is the left half and viewport 1 the right half; the program picks 1.
        var half = TargetSize / 2.0;
        var pixels = RenderVertices(
            MiscVector | UseVertexViewport,
            [(Pos1, 0x4, [Vector(24), Vector(24), Vector(24), Vector(24)])],
            viewports:
            [
                new MetalNative.MtlViewport { OriginX = 0, Width = half, Height = TargetSize, ZFar = 1 },
                new MetalNative.MtlViewport { OriginX = half, Width = half, Height = TargetSize, ZFar = 1 },
            ]);
        Assert.Equal(0.0f, Green(pixels, 0, 4, TargetSize / 2), 3);
        Assert.Equal(1.0f, Green(pixels, 0, TargetSize - 4, TargetSize / 2), 3);
    }

    [Fact]
    public void MeshLayerIndexRendersIntoTheArraySlice()
    {
        if (SkipWithoutMetalDevice("the layered mesh draw")) return;

        var shader = Gen5NggFixtures.CompileOrThrow(MeshWithPos1(layer: 1u), positionExportControl:
            MiscVector | UseVertexLayer);
        var mesh = shader.MeshInfo!;
        var source = shader.Source + FragmentSource;
        Assert.True(MetalNative.TryCompileLibrary(source, out var library, out var compileError), compileError);
        var drawInfo = new uint[Gen5MslMeshInfo.DrawInfoDwordCount];
        drawInfo[0] = mesh.MaxVertices;
        drawInfo[1] = mesh.MaxPrimitives;
        drawInfo[6] = 1;
        drawInfo[8] = mesh.MaxVertices;
        drawInfo[9] = mesh.MaxPrimitives;
        var drawInfoBytes = new byte[drawInfo.Length * sizeof(uint)];
        Buffer.BlockCopy(drawInfo, 0, drawInfoBytes, 0, drawInfoBytes.Length);
        Assert.True(
            MetalNative.TryRenderMesh(
                library,
                shader.EntryPoint,
                "gen5_export_test_fs",
                [
                    (new byte[Math.Max(shader.ArgumentLayout?.ByteSize ?? 0, 8)], 0u),
                    (new byte[64], 1u),
                    (drawInfoBytes, mesh.DrawInfoBufferIndex),
                    (new byte[16], mesh.IndexBufferIndex),
                ],
                threadgroups: 1,
                mesh.ThreadsPerThreadgroup,
                TargetSize,
                TargetSize,
                out var pixels,
                out var renderError,
                layers: 2),
            renderError);

        AssertLayerClear(pixels, 0);
        Assert.Equal(1.0f, Green(pixels, 1, TargetSize / 2, TargetSize / 2), 3);
    }

    [Fact]
    public void MeshLayerIndexRendersIntoTheVolumeDepthPlane()
    {
        // A volume LUT rendered slice by slice: the primitive's layer selects the depth
        // plane of a 3D colour attachment, which Metal renders only with the pass's
        // renderTargetArrayLength set to the texture's depth.
        if (SkipWithoutMetalDevice("the volume mesh draw")) return;

        const uint depth = 4;
        const uint layer = 2;
        var shader = Gen5NggFixtures.CompileOrThrow(MeshWithPos1(layer), positionExportControl:
            MiscVector | UseVertexLayer);
        var mesh = shader.MeshInfo!;
        var source = shader.Source + FragmentSource;
        Assert.True(MetalNative.TryCompileLibrary(source, out var library, out var compileError), compileError);
        var drawInfo = new uint[Gen5MslMeshInfo.DrawInfoDwordCount];
        drawInfo[0] = mesh.MaxVertices;
        drawInfo[1] = mesh.MaxPrimitives;
        drawInfo[6] = 1;
        drawInfo[8] = mesh.MaxVertices;
        drawInfo[9] = mesh.MaxPrimitives;
        var drawInfoBytes = new byte[drawInfo.Length * sizeof(uint)];
        Buffer.BlockCopy(drawInfo, 0, drawInfoBytes, 0, drawInfoBytes.Length);
        Assert.True(
            MetalNative.TryRenderMesh(
                library,
                shader.EntryPoint,
                "gen5_export_test_fs",
                [
                    (new byte[Math.Max(shader.ArgumentLayout?.ByteSize ?? 0, 8)], 0u),
                    (new byte[64], 1u),
                    (drawInfoBytes, mesh.DrawInfoBufferIndex),
                    (new byte[16], mesh.IndexBufferIndex),
                ],
                threadgroups: 1,
                mesh.ThreadsPerThreadgroup,
                TargetSize,
                TargetSize,
                out var pixels,
                out var renderError,
                layers: depth,
                volume: true),
            renderError);

        for (var plane = 0u; plane < depth; plane++)
        {
            if (plane != layer)
            {
                AssertLayerClear(pixels, plane);
            }
        }

        Assert.Equal(1.0f, Green(pixels, layer, TargetSize / 2, TargetSize / 2), 3);
    }

    // ---- helpers ----

    private static Gen5MslShader CompileVertex(
        uint positionExportControl,
        (uint Target, uint Mask, Gen5Operand[] Sources)? auxExport = null,
        bool supportsClipDistance = true) =>
        CompileVertex(
            positionExportControl,
            auxExport is { } export ? [export] : [],
            supportsClipDistance,
            zeroThirdVertex: false);

    private static Gen5MslShader CompileVertex(
        uint positionExportControl,
        IReadOnlyList<(uint Target, uint Mask, Gen5Operand[] Sources)> auxExports,
        bool supportsClipDistance,
        bool zeroThirdVertex)
    {
        var request = Gen5ComputeFixtures.RequestOrThrow(
            CoveringTriangle(auxExports, zeroThirdVertex),
            ShaderStage.Vertex,
            positionExportControl: positionExportControl,
            supportsClipDistance: supportsClipDistance);
        if (!Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error))
        {
            throw new InvalidOperationException($"vertex emit failed: {error}");
        }

        Assert.Equal(Gen5MslStage.Vertex, shader.Stage);
        return shader;
    }

    /// <summary>One triangle over vertices (-1,-1), (3,-1), (-1,3), which covers the whole
    /// target: x = 4 * (id &amp; 1) - 1 lands in v11, y = 4 * (id &gt;&gt; 1) - 1 in v13. v22 holds
    /// -1.0, v23 the integer 1 and v24 the viewport index 1 in bits 16-19.</summary>
    private static Gen5ShaderProgram CoveringTriangle(
        IReadOnlyList<(uint Target, uint Mask, Gen5Operand[] Sources)> auxExports,
        bool zeroThirdVertex)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;

        void Add(string opcode, Gen5ShaderEncoding encoding, Gen5Operand[] sources,
            Gen5Operand[] destinations, Gen5InstructionControl? control = null)
        {
            instructions.Add(new Gen5ShaderInstruction(pc, encoding, opcode, [0u], sources, destinations, control));
            pc += sizeof(uint);
        }

        Add("VAndB32", Gen5ShaderEncoding.Vop2, [Inline(1), Vector(5)], [Vector(10)]);
        Add("VCvtF32U32", Gen5ShaderEncoding.Vop1, [Vector(10)], [Vector(11)]);
        Add("VMulF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(4.0f)), Vector(11)], [Vector(11)]);
        Add("VAddF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(-1.0f)), Vector(11)], [Vector(11)]);
        Add("VLshrrevB32", Gen5ShaderEncoding.Vop2, [Inline(1), Vector(5)], [Vector(12)]);
        Add("VCvtF32U32", Gen5ShaderEncoding.Vop1, [Vector(12)], [Vector(13)]);
        Add("VMulF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(4.0f)), Vector(13)], [Vector(13)]);
        Add("VAddF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(-1.0f)), Vector(13)], [Vector(13)]);
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Inline(0)], [Vector(20)]);
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Literal(Bits(1.0f))], [Vector(21)]);
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Literal(Bits(-1.0f))], [Vector(22)]);
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Inline(1)], [Vector(23)]);
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Literal(1u << 16)], [Vector(24)]);
        if (zeroThirdVertex)
        {
            // s = 1 - (id >> 1) is 0 only for the third vertex; scale the whole position by it.
            Add("VCvtF32U32", Gen5ShaderEncoding.Vop1, [Vector(12)], [Vector(26)]);
            Add("VSubF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(1.0f)), Vector(26)], [Vector(21)]);
            Add("VMulF32", Gen5ShaderEncoding.Vop2, [Vector(21), Vector(11)], [Vector(11)]);
            Add("VMulF32", Gen5ShaderEncoding.Vop2, [Vector(21), Vector(13)], [Vector(13)]);
        }

        Add("Exp", Gen5ShaderEncoding.Exp, [Vector(11), Vector(13), Vector(20), Vector(21)], [],
            new Gen5ExportControl(Pos0, 0xF, false, auxExports.Count == 0, false));
        for (var index = 0; index < auxExports.Count; index++)
        {
            var (target, mask, sources) = auxExports[index];
            Add("Exp", Gen5ShaderEncoding.Exp, sources, [],
                new Gen5ExportControl(target, mask, false, index == auxExports.Count - 1, false));
        }

        Add("SEndpgm", Gen5ShaderEncoding.Sopp, [], []);
        return new Gen5ShaderProgram(0, instructions);
    }

    /// <summary>The NGG culling triangle with a POS1 export of <paramref name="layer"/> in z
    /// (the misc vector's layer/viewport component) ahead of its end.</summary>
    private static Gen5ShaderProgram MeshWithPos1(uint layer)
    {
        var program = Gen5NggFixtures.CullingTriangle();
        var instructions = program.Instructions.Take(program.Instructions.Count - 1).ToList();
        var end = program.Instructions[^1];
        var pc = end.Pc;
        instructions.Add(new Gen5ShaderInstruction(
            pc, Gen5ShaderEncoding.Vop1, "VMovB32", [0u], [Literal(layer)], [Vector(40)], null));
        instructions.Add(new Gen5ShaderInstruction(
            pc + 4, Gen5ShaderEncoding.Exp, "Exp", [0u],
            [Vector(40), Vector(40), Vector(40), Vector(40)], [],
            new Gen5ExportControl(Gen5NggExports.FirstPositionTarget + 1, 0x4, false, false, false)));
        instructions.Add(end with { Pc = pc + 8 });
        return new Gen5ShaderProgram(program.Address, instructions);
    }

    private float[] RenderVertices(
        uint positionExportControl,
        IReadOnlyList<(uint Target, uint Mask, Gen5Operand[] Sources)> auxExports,
        uint layers = 1,
        IReadOnlyList<MetalNative.MtlViewport>? viewports = null,
        bool zeroThirdVertex = false)
    {
        var shader = CompileVertex(positionExportControl, auxExports, supportsClipDistance: true, zeroThirdVertex);
        var source = shader.Source + FragmentSource;
        if (!MetalNative.TryCompileLibrary(source, out var library, out var compileError))
        {
            throw new InvalidOperationException($"Metal rejected the vertex MSL: {compileError}\n{source}");
        }

        if (!MetalNative.TryRenderVertices(
                library,
                shader.EntryPoint,
                "gen5_export_test_fs",
                shader.ArgumentLayout?.ByteSize ?? 0,
                vertexCount: 3,
                TargetSize,
                TargetSize,
                layers,
                viewports,
                out var pixels,
                out var renderError))
        {
            throw new InvalidOperationException($"the vertex draw failed: {renderError}");
        }

        output.WriteLine($"rendered {layers} layer(s) through {shader.EntryPoint}");
        return pixels;
    }

    private void AssertCompiles(Gen5MslShader shader)
    {
        if (!MetalNative.IsAvailable)
        {
            output.WriteLine("No Metal device on this host; the runtime compile is skipped.");
            return;
        }

        Assert.True(
            MetalNative.TryCompileLibrary(shader.Source, out _, out var error),
            $"Metal rejected the emitted MSL: {error}\n{shader.Source}");
    }

    // Stands in for the translated pixel program; it reads nothing but the position.
    private const string FragmentSource = """

        struct Gen5ExportTestIn
        {
            float4 position [[position]];
        };

        fragment float4 gen5_export_test_fs(Gen5ExportTestIn in [[stage_in]])
        {
            return float4(0.0f, 1.0f, 0.0f, 1.0f);
        }
        """;

    private static float Green(float[] pixels, uint layer, uint x, uint y) =>
        pixels[((((layer * TargetSize) + y) * TargetSize) + x) * 4 + 1];

    private static void AssertLayerClear(float[] pixels, uint layer)
    {
        for (var y = 0u; y < TargetSize; y++)
        {
            for (var x = 0u; x < TargetSize; x++)
            {
                Assert.Equal(0.0f, Green(pixels, layer, x, y), 3);
            }
        }
    }

    private bool SkipWithoutMetalDevice(string activity)
    {
        if (MetalNative.IsAvailable) return false;
        Assert.False(
            OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1",
            $"The required gate cannot run {activity} without a Metal device.");
        output.WriteLine($"No Metal device on this host; {activity} skipped.");
        return true;
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static Gen5Operand Literal(uint value) => new(Gen5OperandKind.LiteralConstant, value);

    private static Gen5Operand Inline(uint value) => new(Gen5OperandKind.EncodedConstant, 128 + value);

    private static Gen5Operand Vector(uint index) => Gen5Operand.Vector(index);
}
