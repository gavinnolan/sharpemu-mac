// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// The rectangle-list form of a vertex program (Gen5MslRectangleList): each guest
/// rectangle is three vertices and the fourth corner is derived from their outputs. The
/// device tests draw it the way the v2 host does and check that every rectangle is filled
/// whole, with its parameters extended over the derived half.
/// </summary>
public sealed class MslRectangleListTests(ITestOutputHelper output)
{
    private const uint TargetSize = 32;
    private const uint HalfTarget = TargetSize / 2;
    private const uint Pos0 = 12;
    private const uint Param0 = 32;

    // ---- structure ----

    [Fact]
    public void TheVertexEntryBecomesAHelperBehindAPatchEntry()
    {
        var shader = Compile(VertexIdRectangles());
        var source = Gen5MslRectangleList.BuildSource(shader);

        Assert.DoesNotContain($"vertex Gen5VsOut {shader.EntryPoint}(", source);
        Assert.Contains("static inline Gen5VsOut gen5_vs_guest(", source);
        Assert.Contains("[[patch(quad, 3)]]", source);
        Assert.Contains($"vertex Gen5VsOut {Gen5MslRectangleList.EntryPoint}(", source);
        Assert.Contains($"[[buffer({Gen5MslRectangleList.DrawInfoBufferIndex})]]", source);
        Assert.Contains($"[[buffer({Gen5MslRectangleList.IndexBufferIndex})]]", source);
        Assert.Contains("o3.sharpemu_position = sharpemu_rect_corner(", source);
        Assert.Contains("o3.param0 = sharpemu_rect_corner(", source);
        Assert.Contains("o3.sharpemu_clip_distance[0] = sharpemu_rect_corner(", source);
        Assert.DoesNotContain("[[stage_in]]", source);
    }

    [Fact]
    public void AProgramWithVertexInputsReadsThemAsPatchControlPoints()
    {
        var source = Gen5MslRectangleList.BuildSource(CompileFetchedRectangles());

        Assert.Contains("patch_control_point<Gen5VsIn> sharpemu_rect_points [[stage_in]]", source);
        Assert.Contains("gen5_vs_guest(sharpemu_rect_points[corner]", source);
    }

    [Fact]
    public void OnlyAVertexProgramHasARectangleListForm()
    {
        var pixel = new Gen5MslShader("// pixel", "gen5_ps", Gen5MslStage.Pixel, AttributeCount: 0);
        Assert.Throws<ArgumentException>(() => Gen5MslRectangleList.BuildSource(pixel));
    }

    // ---- device ----

    // One rectangle is the left half; two are both halves. A triangle-list draw of the same
    // vertices would fill only the lower-left triangle of each.
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void EveryRectangleIsFilledWithItsParametersExtended(uint rectangles)
    {
        if (SkipWithoutMetalDevice("the rectangle-list draw")) return;

        var pixels = Render(Compile(VertexIdRectangles()), rectangles, vertexBase: 0, indices: null, positions: null);
        for (var rectangle = 0u; rectangle < 2; rectangle++)
        {
            AssertRectangle(pixels, rectangle, filled: rectangle < rectangles);
        }
    }

    [Fact]
    public void AnAutomaticDrawStartsAtItsFirstVertex()
    {
        if (SkipWithoutMetalDevice("the rectangle-list draw")) return;

        // First vertex 3 is the second rectangle alone.
        var pixels = Render(Compile(VertexIdRectangles()), 1, vertexBase: 3, indices: null, positions: null);
        AssertRectangle(pixels, 0, filled: false);
        AssertRectangle(pixels, 1, filled: true);
    }

    // The indices put the right-angle corner last; the fourth corner is still opposite it,
    // which v1 + v2 - v0 would not be.
    [Fact]
    public void AnIndexedDrawFindsTheRightAngleCorner()
    {
        if (SkipWithoutMetalDevice("the rectangle-list draw")) return;

        var pixels = Render(Compile(VertexIdRectangles()), 2, vertexBase: 0, indices: [4, 5, 3, 1, 2, 0], positions: null);
        AssertRectangle(pixels, 0, filled: true);
        AssertRectangle(pixels, 1, filled: true);
    }

    // Positions from a vertex buffer: the corners reach the program as control points.
    [Fact]
    public void FetchedVerticesReachEveryCorner()
    {
        if (SkipWithoutMetalDevice("the rectangle-list draw")) return;

        float[] positions =
        [
            -1, -1, 0, -1, -1, 1,
            0, -1, 1, -1, 0, 1,
        ];
        var pixels = Render(CompileFetchedRectangles(), 2, vertexBase: 0, indices: null, positions);
        AssertRectangle(pixels, 0, filled: true);
        AssertRectangle(pixels, 1, filled: true);
    }

    // ---- helpers ----

    private float[] Render(Gen5MslShader shader, uint rectangles, uint vertexBase, ushort[]? indices, float[]? positions)
    {
        var source = Gen5MslRectangleList.BuildSource(shader) + FragmentSource;
        if (!MetalNative.TryCompileLibrary(source, out var library, out var compileError))
        {
            throw new InvalidOperationException($"Metal rejected the rectangle-list MSL: {compileError}\n{source}");
        }

        Assert.True(
            MetalNative.TryRenderRectanglePatches(
                library,
                "rect_test_fs",
                shader.ArgumentLayout?.ByteSize ?? 0,
                rectangles,
                vertexBase,
                indices,
                positions,
                TargetSize,
                TargetSize,
                out var pixels,
                out var renderError),
            renderError);
        output.WriteLine($"drew {rectangles} rectangle(s)");
        return pixels;
    }

    // Rectangle r covers the r-th half of the target. Red runs 0..1 left to right across it
    // and green 0..1 bottom to top: both are 1 only at the derived corner.
    private static void AssertRectangle(float[] pixels, uint rectangle, bool filled)
    {
        for (var y = 0u; y < TargetSize; y++)
        {
            for (var x = rectangle * HalfTarget; x < (rectangle + 1) * HalfTarget; x++)
            {
                var texel = ((y * TargetSize) + x) * 4;
                if (!filled)
                {
                    Assert.Equal(0.0f, pixels[texel + 3]);
                    continue;
                }

                Assert.True(pixels[texel + 3] == 1.0f, $"rectangle {rectangle} left texel ({x}, {y}) uncovered");
                var u = (x - (rectangle * HalfTarget) + 0.5f) / HalfTarget;
                var v = 1.0f - ((y + 0.5f) / TargetSize);
                Assert.True(Math.Abs(pixels[texel] - u) < 0.01f, $"texel ({x}, {y}) red {pixels[texel]} != {u}");
                Assert.True(Math.Abs(pixels[texel + 1] - v) < 0.01f, $"texel ({x}, {y}) green {pixels[texel + 1]} != {v}");
            }
        }
    }

    private static Gen5MslShader Compile(Gen5ShaderProgram program)
    {
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Vertex, requiredVertexOutputCount: 1);
        if (!Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error))
        {
            throw new InvalidOperationException($"vertex emit failed: {error}");
        }

        return shader;
    }

    // The fetched program: BUFFER_LOAD_FORMAT_XY at pc 0 is vertex input 0 and the position.
    private static Gen5MslShader CompileFetchedRectangles()
    {
        var program = RectangleProgram(fetchPosition: true);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Vertex, 1, 0, 0, new HashSet<uint> { 0 });
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(resources.Info, [], false, false, false);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            VertexInputs = [new ShaderVertexInput(0, 0, 2, 7, false, [])],
            RequiredVertexOutputCount = 1,
        };
        if (!Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error))
        {
            throw new InvalidOperationException($"vertex emit failed: {error}");
        }

        return shader;
    }

    private static Gen5ShaderProgram VertexIdRectangles() => RectangleProgram(fetchPosition: false);

    /// <summary>Rectangle r = id / 3, corner c = id % 3: corner 0 is the bottom-left of the
    /// r-th half of the target, 1 its bottom-right and 2 its top-left. PARAM0 is
    /// (c == 1, c == 2, 0, 1), so it reaches (1, 1) only at the top-right corner the
    /// rectangle derives. The position is computed from the id, or fetched.</summary>
    private static Gen5ShaderProgram RectangleProgram(bool fetchPosition)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;

        void Add(string opcode, Gen5ShaderEncoding encoding, Gen5Operand[] sources,
            Gen5Operand[] destinations, Gen5InstructionControl? control = null)
        {
            instructions.Add(new Gen5ShaderInstruction(pc, encoding, opcode, [0u], sources, destinations, control));
            pc += sizeof(uint);
        }

        if (fetchPosition)
        {
            Add("BufferLoadFormatXy", Gen5ShaderEncoding.Mubuf, [], [],
                new Gen5BufferMemoryControl(2, 5, 15, 0, 0, IndexEnabled: true, OffsetEnabled: false, Glc: false, Slc: false));
        }

        Add("VCvtF32U32", Gen5ShaderEncoding.Vop1, [Vector(5)], [Vector(10)]);
        Add("VMulF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(1.0f / 3.0f)), Vector(10)], [Vector(11)]);
        Add("VFloorF32", Gen5ShaderEncoding.Vop1, [Vector(11)], [Vector(11)]);
        Add("VMulF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(3.0f)), Vector(11)], [Vector(12)]);
        Add("VSubF32", Gen5ShaderEncoding.Vop2, [Vector(10), Vector(12)], [Vector(12)]);
        // u = c * (2 - c), v = c * (c - 1) / 2.
        Add("VSubF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(2.0f)), Vector(12)], [Vector(13)]);
        Add("VMulF32", Gen5ShaderEncoding.Vop2, [Vector(12), Vector(13)], [Vector(13)]);
        Add("VAddF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(-1.0f)), Vector(12)], [Vector(14)]);
        Add("VMulF32", Gen5ShaderEncoding.Vop2, [Vector(12), Vector(14)], [Vector(14)]);
        Add("VMulF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(0.5f)), Vector(14)], [Vector(14)]);
        if (!fetchPosition)
        {
            // x = r - 1 + u, y = 2v - 1.
            Add("VAddF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(-1.0f)), Vector(11)], [Vector(15)]);
            Add("VAddF32", Gen5ShaderEncoding.Vop2, [Vector(15), Vector(13)], [Vector(15)]);
            Add("VMulF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(2.0f)), Vector(14)], [Vector(16)]);
            Add("VAddF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(-1.0f)), Vector(16)], [Vector(16)]);
        }

        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Inline(0)], [Vector(20)]);
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Literal(Bits(1.0f))], [Vector(21)]);
        Add("Exp", Gen5ShaderEncoding.Exp, [Vector(13), Vector(14), Vector(20), Vector(21)], [],
            new Gen5ExportControl(Param0, 0xF, false, false, false));
        // The fetch lands in v15/v16, where the computed position goes otherwise.
        Add("Exp", Gen5ShaderEncoding.Exp, [Vector(15), Vector(16), Vector(20), Vector(21)], [],
            new Gen5ExportControl(Pos0, 0xF, false, true, false));
        Add("SEndpgm", Gen5ShaderEncoding.Sopp, [], []);
        return new Gen5ShaderProgram(0, instructions);
    }

    private const string FragmentSource = """

        struct RectTestIn
        {
            float4 position [[position]];
            float4 param0 [[user(locn0)]];
        };

        fragment float4 rect_test_fs(RectTestIn in [[stage_in]])
        {
            return float4(in.param0.x, in.param0.y, 0.0f, 1.0f);
        }
        """;

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
