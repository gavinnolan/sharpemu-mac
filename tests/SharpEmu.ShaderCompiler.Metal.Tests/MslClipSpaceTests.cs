// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// POS0 in Metal's clip space: the clip-disabled viewport transform, the y flip for a y-down
/// host viewport and the [-w, w] depth remap. The device tests draw with the viewport the
/// Metal V2 host builds from the same draw state and check the pixels and depth against
/// what the Vulkan path produces.
/// </summary>
public sealed class MslClipSpaceTests(ITestOutputHelper output)
{
    private const uint TargetSize = 32;

    // What the shared pipeline cache passes for the clip-disabled viewport: half of the
    // largest host viewport, which the draw state then sets at the origin.
    private const float HalfExtent = 8192f;

    private static readonly ShaderClipSpaceTransform ScreenSpace =
        new(true, 0.5f, 0.5f, 16f, 8f, HalfExtent, HalfExtent) { PositionYDown = true };

    // ---- structure ----

    [Fact]
    public void DefaultConventionsLeaveThePositionAlone()
    {
        var shader = CompileVertex(Triangle(1, 0, 1, 0, 0, 1), default);
        Assert.Matches(@"sharpemu_out\.sharpemu_position = (exec \? )?float4\(", shader.Source);
        Assert.DoesNotContain(".z + ", shader.Source);
        Assert.DoesNotMatch(@"\.y = -", shader.Source);
    }

    [Fact]
    public void ClipDisabledPositionGoesThroughTheViewportTransform()
    {
        var shader = CompileVertex(Triangle(1, 0, 1, 0, 0, 1), ScreenSpace);
        Assert.Matches(
            @"t\d+\.x = \(t\d+\.x \* as_type<float>\(0x3F000000u\) \+ as_type<float>\(0x41800000u\)\) / as_type<float>\(0x46000000u\) - 1\.0f;",
            shader.Source);
        Assert.Matches(@"t\d+\.y = -t\d+\.y;", shader.Source);
        AssertCompiles(shader);
    }

    [Fact]
    public void MeshConvertsTheFinalPositionBeforeSetVertex()
    {
        var shader = Gen5NggFixtures.CompileOrThrow(
            Gen5NggFixtures.CullingTriangle(),
            clipSpace: new ShaderClipSpaceTransform { PositionYDown = true, DepthNegativeOneToOne = true });
        var flip = shader.Source.IndexOf("sharpemu_out.sharpemu_position.y = -sharpemu_out.sharpemu_position.y;", StringComparison.Ordinal);
        var depth = shader.Source.IndexOf(
            "sharpemu_out.sharpemu_position.z = (sharpemu_out.sharpemu_position.z + sharpemu_out.sharpemu_position.w) * 0.5f;",
            StringComparison.Ordinal);
        var setVertex = shader.Source.IndexOf(".set_vertex(", StringComparison.Ordinal);
        Assert.True(flip > 0 && depth > flip && setVertex > depth, shader.Source);
        AssertCompiles(shader);
    }

    // ---- device ----

    [Fact]
    public void ClipDisabledScreenSpaceTriangleLandsOnTheGuestPixels()
    {
        if (SkipWithoutMetalDevice("the clip-disabled draw")) return;

        // Guest positions (0, 0), (128, 0), (0, 32) scaled by 0.5 and offset by (16, 8) are
        // the window positions (16, 8), (80, 8), (16, 24): Vulkan's pixels, y down.
        var pixels = Render(
            Triangle(128, 0, 32, 0, 0, 1),
            ScreenSpace,
            new MetalNative.MtlViewport { Width = HalfExtent * 2, Height = HalfExtent * 2, ZFar = 1 });
        Assert.Equal(1.0f, Green(pixels, 18, 9), 3);
        Assert.Equal(1.0f, Green(pixels, 30, 12), 3);
        Assert.Equal(0.0f, Green(pixels, 18, 6), 3);
        Assert.Equal(0.0f, Green(pixels, 18, 26), 3);
        Assert.Equal(0.0f, Green(pixels, 14, 12), 3);
        Assert.Equal(0.0f, Green(pixels, 30, 22), 3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GuestYScaleSignPicksTheVulkanRows(bool positiveYScale)
    {
        if (SkipWithoutMetalDevice("the viewport draw")) return;

        // The triangle (-1, -1), (3, -1), (-1, 0) spans the NDC half with y < 0. A positive
        // guest y scale puts that half at the top of the target, as Vulkan's y-down viewport
        // does; a negative one at the bottom, through the host's flipped viewport rect.
        var pixels = Render(
            Triangle(4, -1, 1, -1, 0, 1),
            new ShaderClipSpaceTransform { PositionYDown = positiveYScale },
            new MetalNative.MtlViewport { Width = TargetSize, Height = TargetSize, ZFar = 1 });
        var (covered, clear) = positiveYScale ? (2u, TargetSize - 3) : (TargetSize - 3, 2u);
        Assert.Equal(1.0f, Green(pixels, 2, covered), 3);
        Assert.Equal(0.0f, Green(pixels, 2, clear), 3);
    }

    [Fact]
    public void NegativeOneToOneDepthMapsLikeVulkan()
    {
        if (SkipWithoutMetalDevice("the depth draw")) return;

        // z = -1 with w = 2 is NDC depth -0.5. Vulkan's negative-one-to-one viewport over
        // ZOffset 0.5 and ZScale 0.25 maps it to 0.5 - 0.25 * 0.5; the host sets near and
        // far to ZOffset -/+ ZScale.
        var viewport = new MetalNative.MtlViewport
        {
            Width = TargetSize,
            Height = TargetSize,
            ZNear = 0.25,
            ZFar = 0.75,
        };
        var pixels = Render(
            Triangle(8, -2, 8, -2, -1, 2),
            new ShaderClipSpaceTransform { DepthNegativeOneToOne = true },
            viewport);
        Assert.Equal(1.0f, Green(pixels, TargetSize / 2, TargetSize / 2), 3);
        Assert.Equal(0.375f, Depth(pixels, TargetSize / 2, TargetSize / 2), 5);

        // Without the remap Metal clips the guest's negative depth half away.
        var unmapped = Render(Triangle(8, -2, 8, -2, -1, 2), default, viewport);
        Assert.Equal(0.0f, Green(unmapped, TargetSize / 2, TargetSize / 2), 3);
    }

    [Fact]
    public void MeshPositionFollowsTheYDownViewport()
    {
        if (SkipWithoutMetalDevice("the mesh draw")) return;

        // The culling triangle's apex is at NDC y = 0.8 and its base at -0.8. Flipped for a
        // y-down viewport the base lies along the top of the target.
        var flipped = RenderMesh(new ShaderClipSpaceTransform { PositionYDown = true });
        Assert.Equal(1.0f, Green(flipped, 8, 5), 3);
        Assert.Equal(0.0f, Green(flipped, 8, 26), 3);

        var upright = RenderMesh(default);
        Assert.Equal(0.0f, Green(upright, 8, 5), 3);
        Assert.Equal(1.0f, Green(upright, 8, 26), 3);
    }

    // ---- helpers ----

    /// <summary>One triangle whose vertex <c>id</c> exports x = ax * (id &amp; 1) + bx,
    /// y = ay * (id &gt;&gt; 1) + by, and the constant z and w.</summary>
    private static Gen5ShaderProgram Triangle(float ax, float bx, float ay, float by, float z, float w)
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
        Add("VMulF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(ax)), Vector(11)], [Vector(11)]);
        Add("VAddF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(bx)), Vector(11)], [Vector(11)]);
        Add("VLshrrevB32", Gen5ShaderEncoding.Vop2, [Inline(1), Vector(5)], [Vector(12)]);
        Add("VCvtF32U32", Gen5ShaderEncoding.Vop1, [Vector(12)], [Vector(13)]);
        Add("VMulF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(ay)), Vector(13)], [Vector(13)]);
        Add("VAddF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(by)), Vector(13)], [Vector(13)]);
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Literal(Bits(z))], [Vector(20)]);
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Literal(Bits(w))], [Vector(21)]);
        Add("Exp", Gen5ShaderEncoding.Exp, [Vector(11), Vector(13), Vector(20), Vector(21)], [],
            new Gen5ExportControl(12, 0xF, false, true, false));
        Add("SEndpgm", Gen5ShaderEncoding.Sopp, [], []);
        return new Gen5ShaderProgram(0, instructions);
    }

    private static Gen5MslShader CompileVertex(Gen5ShaderProgram program, ShaderClipSpaceTransform clipSpace)
    {
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Vertex, clipSpace: clipSpace);
        if (!Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error))
        {
            throw new InvalidOperationException($"vertex emit failed: {error}");
        }

        return shader;
    }

    private float[] Render(Gen5ShaderProgram program, ShaderClipSpaceTransform clipSpace, MetalNative.MtlViewport viewport)
    {
        var shader = CompileVertex(program, clipSpace);
        var source = shader.Source + FragmentSource;
        Assert.True(MetalNative.TryCompileLibrary(source, out var library, out var compileError), compileError);
        Assert.True(
            MetalNative.TryRenderVertices(
                library,
                shader.EntryPoint,
                "gen5_clip_test_fs",
                shader.ArgumentLayout?.ByteSize ?? 0,
                vertexCount: 3,
                TargetSize,
                TargetSize,
                layers: 1,
                [viewport],
                out var pixels,
                out var renderError),
            renderError);
        output.WriteLine($"rendered through {shader.EntryPoint}");
        return pixels;
    }

    private static float[] RenderMesh(ShaderClipSpaceTransform clipSpace)
    {
        var shader = Gen5NggFixtures.CompileOrThrow(Gen5NggFixtures.CullingTriangle(), clipSpace: clipSpace);
        var mesh = shader.MeshInfo!;
        Assert.True(
            MetalNative.TryCompileLibrary(shader.Source + FragmentSource, out var library, out var compileError),
            compileError);
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
                "gen5_clip_test_fs",
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
                out var renderError),
            renderError);
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

    // Stands in for the translated pixel program: green coverage and the window depth in blue.
    private const string FragmentSource = """

        struct Gen5ClipTestIn
        {
            float4 position [[position]];
        };

        fragment float4 gen5_clip_test_fs(Gen5ClipTestIn in [[stage_in]])
        {
            return float4(0.0f, 1.0f, in.position.z, 1.0f);
        }
        """;

    private static float Green(float[] pixels, uint x, uint y) => pixels[((y * TargetSize) + x) * 4 + 1];

    private static float Depth(float[] pixels, uint x, uint y) => pixels[((y * TargetSize) + x) * 4 + 2];

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
