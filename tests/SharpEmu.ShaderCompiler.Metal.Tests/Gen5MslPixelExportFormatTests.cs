// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Color exports read per SPI_SHADER_COL_FORMAT: each format carries its channels in
/// fixed export channels (32_AR's alpha in channel 1 on GFX10), and the 16-bit formats
/// pack two channels a VGPR that the CB reads as half, normalized or integer values.
/// The device tests render one export into a 32-bit-per-channel target and read the
/// channels back.
/// </summary>
public sealed class Gen5MslPixelExportFormatTests(ITestOutputHelper output)
{
    private const uint TargetSize = 4;
    private const uint Compressed = 0x400;

    private const string VertexSource = """
        #include <metal_stdlib>
        using namespace metal;
        struct VsOut { float4 position [[position]]; };
        vertex VsOut export_format_vs(uint id [[vertex_id]])
        {
            const float2 corners[3] = { float2(-1.0, -1.0), float2(3.0, -1.0), float2(-1.0, 3.0) };
            VsOut out;
            out.position = float4(corners[id], 0.5, 1.0);
            return out;
        }
        """;

    // v_mov_b32 v0..v3 with the given bits, exp mrt<target> v0, v1, v2, v3 done vm, s_endpgm.
    private static uint[] Program(uint[] registers, uint enableMask, uint flags, uint target = 0) =>
    [
        0x7E0002FF, registers[0],
        0x7E0202FF, registers[1],
        0x7E0402FF, registers[2],
        0x7E0602FF, registers[3],
        0xF8001800 | (target << 4) | flags | enableMask, 0x03020100,
        0xBF810000,
    ];

    private static uint F(float value) => BitConverter.SingleToUInt32Bits(value);

    private static uint Halves(float low, float high) =>
        BitConverter.HalfToUInt16Bits((Half)low) | ((uint)BitConverter.HalfToUInt16Bits((Half)high) << 16);

    public static TheoryData<Gen5ColorExportFormat, uint, uint, uint[], float[]> FloatCases => new()
    {
        { Gen5ColorExportFormat.Abgr32, 0xF, 0, [F(1.5f), F(2.5f), F(-3f), F(0.25f)], [1.5f, 2.5f, -3f, 0.25f] },
        // Channels a format does not carry read green and blue 0, alpha 1.
        { Gen5ColorExportFormat.R32, 0x1, 0, [F(1.5f), F(2.5f), F(-3f), F(0.25f)], [1.5f, 0f, 0f, 1f] },
        { Gen5ColorExportFormat.GR32, 0x3, 0, [F(1.5f), F(2.5f), F(-3f), F(0.25f)], [1.5f, 2.5f, 0f, 1f] },
        // GFX10 32_AR: red in channel 0, alpha in channel 1.
        { Gen5ColorExportFormat.AR32, 0x3, 0, [F(1.5f), F(2.5f), F(-3f), F(0.25f)], [1.5f, 0f, 0f, 2.5f] },
        { Gen5ColorExportFormat.Fp16Abgr, 0xF, Compressed, [Halves(1.5f, -2f), Halves(0.5f, 4f), 0, 0], [1.5f, -2f, 0.5f, 4f] },
        {
            Gen5ColorExportFormat.Unorm16Abgr, 0xF, Compressed, [0xFFFF0000, 0x80007FFF, 0, 0],
            [0f, 1f, 32767f / 65535f, 32768f / 65535f]
        },
        // SNORM16 -32768 clamps to -1.
        {
            Gen5ColorExportFormat.Snorm16Abgr, 0xF, Compressed, [0x80007FFF, 0xC0000000, 0, 0],
            [1f, -1f, 0f, -16384f / 32767f]
        },
        // A format that disagrees with COMPR leaves the export's own layout in charge.
        { Gen5ColorExportFormat.Fp16Abgr, 0xF, 0, [F(1.5f), F(2.5f), F(-3f), F(0.25f)], [1.5f, 2.5f, -3f, 0.25f] },
        // SPI_SHADER_ZERO: nothing reaches the target, which keeps its zero clear.
        { Gen5ColorExportFormat.Zero, 0xF, 0, [F(1.5f), F(2.5f), F(-3f), F(0.25f)], [0f, 0f, 0f, 0f] },
    };

    [Theory]
    [MemberData(nameof(FloatCases))]
    public void FloatTargetReadsTheExportFormat(
        Gen5ColorExportFormat format, uint enableMask, uint flags, uint[] registers, float[] expected)
    {
        var texel = Render(Program(registers, enableMask, flags),
            [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float) { ExportFormat = format }],
            MetalNative.Rgba32FloatPixelFormat);
        if (texel is null) return;

        for (var component = 0; component < 4; component++)
        {
            Assert.Equal(expected[component], BitConverter.UInt32BitsToSingle(texel[component]), 6);
        }
    }

    [Theory]
    [InlineData(Gen5ColorExportFormat.Uint16Abgr, Gen5PixelOutputKind.Uint, 0x00020001u, 0xFFFF0003u, new uint[] { 1, 2, 3, 0xFFFF })]
    [InlineData(Gen5ColorExportFormat.Sint16Abgr, Gen5PixelOutputKind.Sint, 0xFFFE0005u, 0x80007FFFu,
        new uint[] { 5, 0xFFFFFFFE, 0x7FFF, 0xFFFF8000 })]
    public void IntegerTargetReadsSixteenBitChannels(
        Gen5ColorExportFormat format, Gen5PixelOutputKind kind, uint low, uint high, uint[] expected)
    {
        var texel = Render(Program([low, high, 0, 0], 0xF, Compressed),
            [new Gen5PixelOutputBinding(0, 0, kind) { ExportFormat = format }],
            kind == Gen5PixelOutputKind.Uint ? MetalNative.Rgba32UintPixelFormat : MetalNative.Rgba32SintPixelFormat);
        if (texel is null) return;

        Assert.Equal(expected, texel);
    }

    [Fact]
    public void AlphaOfThirtyTwoBitArFollowsTheComponentMapping()
    {
        // The target stores its channels reversed (A in physical channel 0): 32_AR's alpha,
        // export channel 1, lands there.
        var texel = Render(Program([F(1.5f), F(2.5f), 0, 0], 0x3, 0),
            [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float, new Gen5ColorComponentMapping(0x1B))
            {
                ExportFormat = Gen5ColorExportFormat.AR32,
            }],
            MetalNative.Rgba32FloatPixelFormat);
        if (texel is null) return;

        Assert.Equal([2.5f, 0f, 0f, 1.5f], texel.Select(BitConverter.UInt32BitsToSingle).ToArray());
    }

    [Fact]
    public void DualSourceBlendReadsTheSecondExportAsSourceOne()
    {
        // MRT0 exports (2, 3, 4, 5) and MRT1 (0.5, 0.25, 2, 1). The blend is
        // src * SRC1_COLOR + dst * 0 (MTLBlendFactorSource1Color, MTLBlendFactorZero).
        uint[] words =
        [
            0x7E0002FF, F(2f), 0x7E0202FF, F(3f), 0x7E0402FF, F(4f), 0x7E0602FF, F(5f),
            0x7E0802FF, F(0.5f), 0x7E0A02FF, F(0.25f), 0x7E0C02FF, F(2f), 0x7E0E02FF, F(1f),
            0xF800000F, 0x03020100,             // exp mrt0 v0, v1, v2, v3
            0xF800181F, 0x07060504,             // exp mrt1 v4, v5, v6, v7 done vm
            0xBF810000,
        ];
        var shader = Compile(words,
        [
            new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float) { ExportFormat = Gen5ColorExportFormat.Abgr32 },
            new Gen5PixelOutputBinding(1, 0, Gen5PixelOutputKind.Float)
            {
                ExportTarget = 1,
                BlendSourceIndex = 1,
                ExportFormat = Gen5ColorExportFormat.Abgr32,
            },
        ]);
        Assert.Contains("float4 mrt1 [[color(0), index(1)]];", shader.Source, StringComparison.Ordinal);
        var texel = Render(shader, MetalNative.Rgba32FloatPixelFormat, (15, 0));
        if (texel is null) return;

        Assert.Equal([1f, 0.75f, 8f, 5f], texel.Select(BitConverter.UInt32BitsToSingle).ToArray());
    }

    private static Gen5MslShader Compile(uint[] words, Gen5PixelOutputBinding[] outputs)
    {
        var program = Gen5ComputeFixtures.DecodeOrThrow(new Gen5ComputeFixture("pixel-export-format", words, 0, 0));
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Pixel, pixelOutputs: outputs);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader;
    }

    private uint[]? Render(uint[] words, Gen5PixelOutputBinding[] outputs, nuint pixelFormat) =>
        Render(Compile(words, outputs), pixelFormat, null);

    // The texel at the center of the target, or null when there is no Metal device.
    private uint[]? Render(Gen5MslShader shader, nuint pixelFormat, (nuint, nuint)? blendFactors)
    {
        if (!MetalNative.IsAvailable)
        {
            Assert.False(
                OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1",
                "The required gate cannot run the export format render without a Metal device.");
            output.WriteLine("No Metal device on this host; the export format render skipped.");
            return null;
        }

        Assert.True(MetalNative.TryCompileLibrary(VertexSource, out var vertexLibrary, out var error), error);
        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var fragmentLibrary, out error), $"{error}\n{shader.Source}");
        Assert.True(
            MetalNative.TryRenderTriangles(vertexLibrary, "export_format_vs", fragmentLibrary, shader.EntryPoint, 3,
                TargetSize, TargetSize, pixelFormat, out var texels, out error, blendFactors),
            $"{error}\n{shader.Source}");
        const uint center = ((TargetSize / 2) * TargetSize) + (TargetSize / 2);
        return texels[(int)(center * 4)..(int)((center * 4) + 4)];
    }
}
