// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// The pixel input VGPRs and V_INTERP slots, mirroring the SPIR-V translator's
/// Gen5InterpolationParameterTests: position and system inputs, per-vertex inputs
/// (metal::vertex_value, MSL 4.0) and slots that share one VS parameter. The device
/// tests draw through a real render pipeline and read the exported registers back.
/// </summary>
public sealed class Gen5MslPixelInputTests(ITestOutputHelper output)
{
    private const uint PerspCenter = 1u << 1;
    private const uint PullModel = 1u << 3;
    private const uint PosW = 1u << 11;
    private const uint FrontFace = 1u << 12;
    private const uint Ancillary = 1u << 13;
    private const uint SampleCoverage = 1u << 14;
    private const uint PosFixedPoint = 1u << 15;

    // VINTRP op: 0 = V_INTERP_P1_F32, 1 = V_INTERP_P2_F32, 2 = V_INTERP_MOV_F32.
    private static uint Interp(uint vdst, uint op, uint attribute, uint channel, uint vsrc) =>
        0xC8000000u | (vdst << 18) | (op << 16) | (attribute << 10) | (channel << 8) | vsrc;

    // exp mrt0 v(a), v(b), v(c), v(d) done vm, then s_endpgm.
    private static uint[] ExportAndEnd(uint a, uint b, uint c, uint d) =>
        [0xF800180F, a | (b << 8) | (c << 16) | (d << 24), 0xBF810000];

    [Theory]
    [InlineData(PosW, true)]
    [InlineData(1u << 10, false)]
    public void PositionW_IsTheReciprocalOfTheFragmentCoordinate(uint inputs, bool reciprocal)
    {
        var shader = Compile(ExportAndEnd(0, 0, 0, 0), inputs);
        Assert.Equal(reciprocal, shader.Source.Contains(
            "v0 = as_type<uint>(1.0f / sharpemu_in.sharpemu_frag_coord.w);", StringComparison.Ordinal));
        Assert.Equal(!reciprocal, shader.Source.Contains(
            "v0 = as_type<uint>(sharpemu_in.sharpemu_frag_coord.z);", StringComparison.Ordinal));
    }

    [Fact]
    public void PixelSystemInputs_ReadTheirAttributes()
    {
        var shader = Compile(
            ExportAndEnd(2, 3, 4, 5), PerspCenter | FrontFace | Ancillary | SampleCoverage | PosFixedPoint);
        Assert.Contains("bool sharpemu_front_facing [[front_facing]];", shader.Source, StringComparison.Ordinal);
        Assert.Contains("uint sharpemu_layer [[render_target_array_index]];", shader.Source, StringComparison.Ordinal);
        Assert.Contains("uint sharpemu_sample_mask [[sample_mask]];", shader.Source, StringComparison.Ordinal);

        // PERSP_CENTER holds v0/v1, so the system inputs follow from v2 in bit order.
        Assert.Contains("v2 = (sharpemu_in.sharpemu_front_facing ? 0x3F800000u : 0u);", shader.Source, StringComparison.Ordinal);
        Assert.Contains("v3 = (sharpemu_in.sharpemu_layer << 16);", shader.Source, StringComparison.Ordinal);
        Assert.Contains("v4 = sharpemu_in.sharpemu_sample_mask;", shader.Source, StringComparison.Ordinal);
        Assert.Contains("v5 = ((uint(sharpemu_in.sharpemu_frag_coord.x) & 0xFFFFu) | " +
            "(uint(sharpemu_in.sharpemu_frag_coord.y) << 16));", shader.Source, StringComparison.Ordinal);
        AssertCompiles(shader);
    }

    [Fact]
    public void AddressedButDisabledSystemInputs_ReserveTheirRegisters()
    {
        var shader = Compile(ExportAndEnd(0, 1, 2, 3), FrontFace | Ancillary | PosFixedPoint, enable: PosFixedPoint);
        Assert.DoesNotContain("[[front_facing]]", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("[[render_target_array_index]]", shader.Source, StringComparison.Ordinal);
        Assert.Contains("v2 = ((uint(sharpemu_in.sharpemu_frag_coord.x)", shader.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0u, false, "second", true)]
    [InlineData(1u, false, "third", true)]
    [InlineData(0u, true, "second", false)]
    [InlineData(1u, true, "third", false)]
    public void ParameterMove_SelectsVertexAndPreservesCustomData(
        uint selector, bool custom, string vertex, bool subtractOrigin)
    {
        var shader = Compile([Interp(4, 2, 1, 2, selector), .. ExportAndEnd(4, 4, 4, 4)], PerspCenter,
            cntl: [0, 0x401], customMask: custom ? 2u : 0u);
        Assert.True(shader.UsesPerVertexInputs);
        Assert.Contains("vertex_value<float4> attr1 [[user(locn1)]];", shader.Source, StringComparison.Ordinal);
        Assert.Contains($"sharpemu_in.attr1.get(vertex_index::{vertex})[2]", shader.Source, StringComparison.Ordinal);
        Assert.Equal(subtractOrigin, shader.Source.Contains(
            "- sharpemu_in.attr1.get(vertex_index::first)[2]", StringComparison.Ordinal));
        AssertCompiles(shader);
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(255u)]
    public void ReservedSelector_Fails(uint selector)
    {
        var request = Request([Interp(4, 2, 1, 0, selector), Interp(5, 2, 1, 0, 0), .. ExportAndEnd(4, 5, 4, 4)],
            PerspCenter, PerspCenter, [0, 1], 0, true);
        Assert.False(Gen5MslTranslator.TryCompileProgram(request, out _, out var error));
        Assert.Contains("reserved interpolation parameter selector", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProvokingVertexMove_IsAFlatInput(bool supportsPerVertex)
    {
        var shader = Compile([Interp(4, 2, 1, 0, 2), .. ExportAndEnd(4, 4, 4, 4)], PerspCenter,
            cntl: [0, 1], supportsPerVertex: supportsPerVertex);
        Assert.False(shader.UsesPerVertexInputs);
        Assert.Contains("float4 attr1 [[user(locn1), flat]];", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("vertex_value", shader.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void VertexDifferenceMove_FallsBackToInterpolatedInputWithoutPerVertexSupport(uint selector)
    {
        var shader = Compile([Interp(4, 2, 1, 0, selector), .. ExportAndEnd(4, 4, 4, 4)], PerspCenter,
            cntl: [0, 1], supportsPerVertex: false);
        Assert.False(shader.UsesPerVertexInputs);
        Assert.Contains("float4 attr1 [[user(locn1)]];", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("barycentric_coord", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryInterpolation_DoesNotNeedPerVertexInputs()
    {
        var shader = Compile([Interp(4, 0, 1, 0, 0), Interp(4, 1, 1, 0, 1), .. ExportAndEnd(4, 4, 4, 4)], PerspCenter,
            cntl: [0, 1]);
        Assert.False(shader.UsesPerVertexInputs);
        Assert.DoesNotContain("barycentric_coord", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedBarycentricLocations_SeedEveryInterpolationSlot()
    {
        // PERSP_SAMPLE/CENTER/CENTROID and LINEAR_SAMPLE/CENTER/CENTROID: Metal has one
        // perspective and one linear barycentric, both at the pixel center here.
        // v0..v5 are the perspective slots' I/J and v6..v11 the linear ones'.
        var perspective = Compile([Interp(20, 2, 1, 0, 0), .. ExportAndEnd(0, 2, 4, 20)], 0x77, cntl: [0, 1]);
        var linear = Compile([Interp(20, 2, 1, 0, 0), .. ExportAndEnd(7, 9, 11, 20)], 0x77, cntl: [0, 1]);
        Assert.Contains("float3 sharpemu_bary_persp [[barycentric_coord, center_perspective]];", perspective.Source, StringComparison.Ordinal);
        Assert.Contains("float3 sharpemu_bary_linear [[barycentric_coord, center_no_perspective]];", perspective.Source, StringComparison.Ordinal);
        for (uint slot = 0; slot < 3; slot++)
        {
            Assert.Contains($"v{slot * 2} = as_type<uint>(sharpemu_in.sharpemu_bary_persp.y);", perspective.Source, StringComparison.Ordinal);
            Assert.Contains($"v{6 + (slot * 2) + 1} = as_type<uint>(sharpemu_in.sharpemu_bary_linear.z);", linear.Source, StringComparison.Ordinal);
        }

        AssertCompiles(perspective);
    }

    [Fact]
    public void CentroidOnlyBarycentrics_UseTheCentroidLocation()
    {
        var shader = Compile([Interp(20, 2, 1, 0, 0), .. ExportAndEnd(20, 20, 20, 20)], 0x44, cntl: [0, 1]);
        Assert.Contains("[[barycentric_coord, centroid_perspective]]", shader.Source, StringComparison.Ordinal);
        Assert.Contains("[[barycentric_coord, centroid_no_perspective]]", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void PullModel_SeedsTheScreenLinearCoordinates()
    {
        var shader = Compile([Interp(20, 2, 1, 0, 0), .. ExportAndEnd(2, 3, 4, 20)], PerspCenter | PullModel,
            cntl: [0, 1]);
        Assert.Contains("v2 = as_type<uint>(sharpemu_in.sharpemu_bary_persp.y * sharpemu_in.sharpemu_frag_coord.w);",
            shader.Source, StringComparison.Ordinal);
        Assert.Contains("v3 = as_type<uint>(sharpemu_in.sharpemu_bary_persp.z * sharpemu_in.sharpemu_frag_coord.w);",
            shader.Source, StringComparison.Ordinal);
        Assert.Contains("v4 = as_type<uint>(sharpemu_in.sharpemu_frag_coord.w);", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void SlotsReadingOneParameter_ShareOneInput()
    {
        // PS slots 1 and 2 both read VS parameter 1; the second slot must not move to a
        // location the vertex program never writes.
        var shader = Compile([Interp(4, 2, 1, 0, 1), Interp(5, 2, 2, 0, 1), .. ExportAndEnd(4, 5, 4, 4)], PerspCenter,
            cntl: [0, 1, 1], customMask: 6);
        Assert.Contains("vertex_value<float4> attr1 [[user(locn1)]];", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("attr2", shader.Source, StringComparison.Ordinal);
        Assert.Contains("v5 = as_type<uint>(sharpemu_in.attr1.get(vertex_index::third)[0]);", shader.Source, StringComparison.Ordinal);
        AssertCompiles(shader);
    }

    [Fact]
    public void FlatAndSmoothSlotsOfOneParameter_KeepOneInputOfEachKind()
    {
        var shader = Compile([Interp(4, 2, 1, 0, 2), Interp(5, 1, 2, 0, 1), Interp(6, 1, 3, 0, 1), .. ExportAndEnd(4, 5, 6, 4)],
            PerspCenter, cntl: [0, 1, 1, 1]);
        Assert.Contains("float4 attr1 [[user(locn1), flat]];", shader.Source, StringComparison.Ordinal);
        Assert.Contains("float4 attr2 [[user(", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("attr3", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void SmoothSlotSharingAPerVertexParameter_InterpolatesThePerVertexInput()
    {
        // Slot 1 is read per vertex, slot 2 interpolates the same VS parameter: one per-vertex
        // input serves both, and slot 2 is rebuilt from the vertices with the barycentrics.
        var shader = Compile([Interp(4, 2, 1, 0, 1), Interp(5, 1, 2, 0, 1), .. ExportAndEnd(4, 5, 4, 4)], PerspCenter,
            cntl: [0, 1, 1], customMask: 2);
        Assert.Contains("vertex_value<float4> attr1 [[user(locn1)]];", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("attr2", shader.Source, StringComparison.Ordinal);
        Assert.Contains("[[barycentric_coord, center_perspective]]", shader.Source, StringComparison.Ordinal);
        Assert.Contains(
            "(sharpemu_in.attr1.get(vertex_index::first)[0] * sharpemu_in.sharpemu_bary_persp.x + " +
            "sharpemu_in.attr1.get(vertex_index::second)[0] * sharpemu_in.sharpemu_bary_persp.y + " +
            "sharpemu_in.attr1.get(vertex_index::third)[0] * sharpemu_in.sharpemu_bary_persp.z)",
            shader.Source, StringComparison.Ordinal);
        AssertCompiles(shader);
    }

    // ---- device ----

    private const uint TargetWidth = 16;
    private const uint TargetHeight = 8;

    // Two triangles with w = 2 (clip xyz scaled with it): the left one wound one way
    // and the right one the other, so exactly one of them is front facing.
    private const string SystemInputVertexSource = """
        #include <metal_stdlib>
        using namespace metal;
        struct VsOut { float4 position [[position]]; };
        vertex VsOut system_vs(uint id [[vertex_id]])
        {
            const float2 corners[6] = {
                float2(-1.0, -1.0), float2(-1.0, 3.0), float2(0.0, -1.0),
                float2(0.0, -1.0), float2(1.0, -1.0), float2(1.0, 3.0) };
            VsOut out;
            out.position = float4(corners[id] * 2.0, 0.5 * 2.0, 2.0);
            return out;
        }
        """;

    [Fact]
    public void SystemInputsRenderTheirHardwareEncodings()
    {
        if (SkipWithoutMetalDevice("the pixel system input render test")) return;

        var inputs = PerspCenter | PosW | FrontFace | Ancillary | SampleCoverage | PosFixedPoint;
        // v0/v1 PERSP_CENTER, v2 POS_W, v3 FRONT_FACE, v4 ANCILLARY, v5 SAMPLE_COVERAGE, v6 POS_FIXED_PT.
        var first = Render(SystemInputVertexSource, "system_vs", 6, ExportAndEnd(2, 3, 5, 6), inputs,
            Gen5PixelOutputKind.Uint, languageVersion: null);
        var second = Render(SystemInputVertexSource, "system_vs", 6, ExportAndEnd(4, 4, 4, 4), inputs,
            Gen5PixelOutputKind.Uint, languageVersion: null);

        uint Texel(uint[] texels, uint x, uint y, int component) => texels[(((y * TargetWidth) + x) * 4) + component];
        const uint leftX = 2, rightX = TargetWidth - 3, y = 5;

        // POS_W is W itself (2), not the 1/W Metal's [[position]].w carries.
        Assert.Equal(BitConverter.SingleToUInt32Bits(2.0f), Texel(first, leftX, y, 0));
        Assert.Equal(BitConverter.SingleToUInt32Bits(2.0f), Texel(first, rightX, y, 0));

        // FRONT_FACE is 1.0f on the front-facing triangle and 0 on the other.
        var faces = new[] { Texel(first, leftX, y, 1), Texel(first, rightX, y, 1) };
        Assert.Contains(0x3F800000u, faces);
        Assert.Contains(0u, faces);

        // A single-sample target covers sample 0.
        Assert.Equal(1u, Texel(first, leftX, y, 2));

        // POS_FIXED_PT packs the integer pixel position.
        Assert.Equal(leftX | (y << 16), Texel(first, leftX, y, 3));
        Assert.Equal(rightX | (y << 16), Texel(first, rightX, y, 3));

        // ANCILLARY's layer field reads 0 when the vertex function writes no layer.
        Assert.Equal(0u, Texel(second, leftX, y, 0));
    }

    // One triangle whose param0.x and param1.x carry 10, 20 and 40 at its three vertices.
    private const string PerVertexVertexSource = """
        #include <metal_stdlib>
        using namespace metal;
        struct VsOut
        {
            float4 position [[position]];
            float4 param0 [[user(locn0)]];
            float4 param1 [[user(locn1)]];
        };
        vertex VsOut per_vertex_vs(uint id [[vertex_id]])
        {
            const float2 corners[3] = { float2(-1.0, -1.0), float2(-1.0, 3.0), float2(3.0, -1.0) };
            const float values[3] = { 10.0, 20.0, 40.0 };
            VsOut out;
            out.position = float4(corners[id], 0.5, 1.0);
            out.param0 = float4(values[id], 0.0, 0.0, 1.0);
            out.param1 = out.param0;
            return out;
        }
        """;

    [Fact]
    public void PerVertexInputsRebuildTheTriangleParameters()
    {
        if (SkipWithoutMetalDevice("the per-vertex pixel input render test")) return;
        if (!MetalNative.SupportsPerVertexPixelInputs)
        {
            // Apple9 and older compile vertex_value but read garbage from it, which is why
            // the V2 host reports per-vertex inputs only on Apple10.
            output.WriteLine("Per-vertex pixel inputs need MSL 4.0 on an Apple10 GPU; the render test skipped.");
            return;
        }

        // v0/v1 hold PERSP_CENTER's I/J. attr0 is read per vertex: P10 into v4, P20 into
        // v5, and P1/P2 over I/J into v6; attr1 is read only through P0, a flat input.
        uint[] words =
        [
            Interp(4, 2, 0, 0, 0),
            Interp(5, 2, 0, 0, 1),
            Interp(6, 0, 0, 0, 0),
            Interp(6, 1, 0, 0, 1),
            Interp(7, 2, 1, 0, 2),
            .. ExportAndEnd(4, 5, 6, 7),
        ];
        var texels = Render(PerVertexVertexSource, "per_vertex_vs", 3, words, PerspCenter,
            Gen5PixelOutputKind.Float, Gen5MslShader.PerVertexInputsLanguageVersion);

        // Window-space vertices of the triangle over the target (y down).
        var vertices = new (double X, double Y)[] { (0, TargetHeight * 1.0), (0, -TargetHeight * 1.0), (TargetWidth * 2.0, TargetHeight * 1.0) };
        double[] values = [10, 20, 40];
        foreach (var (x, y) in new (uint, uint)[] { (1, 1), (7, 3), (12, 6) })
        {
            float Component(int component) =>
                BitConverter.UInt32BitsToSingle(texels[(((y * TargetWidth) + x) * 4) + component]);

            Assert.Equal(10f, Component(0));
            Assert.Equal(30f, Component(1));
            Assert.Equal(10f, Component(3));
            var expected = Interpolate(vertices, values, x + 0.5, y + 0.5);
            Assert.True(Math.Abs(Component(2) - expected) < 1e-3, $"({x},{y}): {Component(2)} != {expected}");
        }
    }

    [Fact]
    public void SlotsSharingOneParameterRenderItsValue()
    {
        if (SkipWithoutMetalDevice("the shared pixel input render test")) return;

        // Slots 0 and 3 interpolate VS parameter 0; slots 1 and 2 read parameter 1 through
        // P0. Each pair shares one input, so no slot lands on a location the vertex
        // function never writes (which Metal would refuse to link).
        uint[] words =
        [
            Interp(4, 0, 0, 0, 0),
            Interp(4, 1, 0, 0, 1),
            Interp(5, 2, 1, 0, 2),
            Interp(6, 2, 2, 0, 2),
            Interp(7, 0, 3, 0, 0),
            Interp(7, 1, 3, 0, 1),
            .. ExportAndEnd(4, 5, 6, 7),
        ];
        var shader = Compile(words, PerspCenter, cntl: [0, 1, 1, 0]);
        Assert.Contains("float4 attr0 [[user(locn0)]];", shader.Source, StringComparison.Ordinal);
        Assert.Contains("float4 attr1 [[user(locn1), flat]];", shader.Source, StringComparison.Ordinal);
        var texels = Render(PerVertexVertexSource, "per_vertex_vs", 3, words, PerspCenter,
            Gen5PixelOutputKind.Float, languageVersion: null, cntl: [0, 1, 1, 0]);

        var vertices = new (double X, double Y)[] { (0, TargetHeight * 1.0), (0, -TargetHeight * 1.0), (TargetWidth * 2.0, TargetHeight * 1.0) };
        double[] values = [10, 20, 40];
        foreach (var (x, y) in new (uint, uint)[] { (1, 1), (12, 6) })
        {
            float Component(int component) =>
                BitConverter.UInt32BitsToSingle(texels[(((y * TargetWidth) + x) * 4) + component]);

            var expected = Interpolate(vertices, values, x + 0.5, y + 0.5);
            Assert.True(Math.Abs(Component(0) - expected) < 1e-3, $"({x},{y}): {Component(0)} != {expected}");
            Assert.Equal(10f, Component(1));
            Assert.Equal(10f, Component(2));
            Assert.Equal(Component(0), Component(3));
        }
    }

    private static double Interpolate((double X, double Y)[] v, double[] values, double x, double y)
    {
        double Edge((double X, double Y) a, (double X, double Y) b) => ((b.X - a.X) * (y - a.Y)) - ((b.Y - a.Y) * (x - a.X));
        var area = ((v[1].X - v[0].X) * (v[2].Y - v[0].Y)) - ((v[1].Y - v[0].Y) * (v[2].X - v[0].X));
        var b0 = Edge(v[1], v[2]) / area;
        var b1 = Edge(v[2], v[0]) / area;
        var b2 = Edge(v[0], v[1]) / area;
        return (b0 * values[0]) + (b1 * values[1]) + (b2 * values[2]);
    }

    private uint[] Render(
        string vertexSource,
        string vertexEntryPoint,
        uint vertexCount,
        uint[] pixelWords,
        uint inputs,
        Gen5PixelOutputKind outputKind,
        nuint? languageVersion,
        uint[]? cntl = null)
    {
        var shader = Compile(pixelWords, inputs, cntl: cntl ?? [0, 1], outputKind: outputKind);
        Assert.True(MetalNative.TryCompileLibrary(vertexSource, out var vertexLibrary, out var error), error);
        nint fragmentLibrary;
        Assert.True(
            languageVersion is { } version
                ? MetalNative.TryCompileLibrary(shader.Source, version, out fragmentLibrary, out error)
                : MetalNative.TryCompileLibrary(shader.Source, out fragmentLibrary, out error),
            $"{error}\n{shader.Source}");
        Assert.True(
            MetalNative.TryRenderTriangles(
                vertexLibrary,
                vertexEntryPoint,
                fragmentLibrary,
                shader.EntryPoint,
                vertexCount,
                TargetWidth,
                TargetHeight,
                outputKind == Gen5PixelOutputKind.Uint
                    ? MetalNative.Rgba32UintPixelFormat
                    : MetalNative.Rgba32FloatPixelFormat,
                out var texels,
                out error),
            $"{error}\n{shader.Source}");
        return texels;
    }

    // ---- helpers ----

    private void AssertCompiles(Gen5MslShader shader)
    {
        if (!MetalNative.IsAvailable || (shader.UsesPerVertexInputs && !OperatingSystem.IsMacOSVersionAtLeast(26)))
        {
            return;
        }

        var compiled = shader.UsesPerVertexInputs
            ? MetalNative.TryCompileLibrary(shader.Source, Gen5MslShader.PerVertexInputsLanguageVersion, out _, out var error)
            : MetalNative.TryCompileLibrary(shader.Source, out _, out error);
        Assert.True(compiled, $"Metal rejected the emitted pixel MSL: {error}\n{shader.Source}");
    }

    private static Gen5MslShader Compile(
        uint[] words,
        uint inputs,
        uint? enable = null,
        uint[]? cntl = null,
        uint customMask = 0,
        bool supportsPerVertex = true,
        Gen5PixelOutputKind outputKind = Gen5PixelOutputKind.Float)
    {
        var request = Request(words, inputs, enable ?? inputs, cntl ?? [], customMask, supportsPerVertex, outputKind);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader;
    }

    private static ShaderCompileRequest Request(
        uint[] words,
        uint address,
        uint enable,
        uint[] cntl,
        uint customMask,
        bool supportsPerVertex,
        Gen5PixelOutputKind outputKind = Gen5PixelOutputKind.Float)
    {
        var program = Gen5ComputeFixtures.DecodeOrThrow(new Gen5ComputeFixture("pixel-inputs", words, 0, 0));
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Pixel, 0x5049_5850_494E_5055, 0, 16);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, 16),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program),
            0);
        return new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = [new Gen5PixelOutputBinding(0, 0, outputKind)],
            PixelInputAddress = address,
            PixelInputEnable = enable,
            PixelInputCntl = cntl,
            PixelCustomInterpolationMask = customMask,
            SupportsPerVertexPixelInputs = supportsPerVertex,
        };
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
}
