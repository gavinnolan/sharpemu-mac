// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Pixel shaders that export depth through MRTZ (DB_SHADER_CONTROL.Z_EXPORT_ENABLE):
/// the first 32-bit component replaces the fragment depth, and fragments that never
/// export it under EXEC keep the rasterized depth.
/// </summary>
public sealed class Gen5MslDepthExportTests(ITestOutputHelper output)
{
    private const uint TargetSize = 4;
    private const float ExportedDepth = 0.375f;
    private const float RasterDepth = 0.5f;

    // exp mrtz v0 (enable mask and COMPR patched per case).
    private const uint MrtzExport = 0xF8000080;
    private const uint Compressed = 0x400;

    private const string VertexSource = """

        struct SharpEmuDepthTestVertex
        {
            float4 position [[position]];
        };

        vertex SharpEmuDepthTestVertex sharpemu_depth_test_vs(
            uint id [[vertex_id]],
            constant float2* positions [[buffer(0)]])
        {
            SharpEmuDepthTestVertex vertex_out;
            vertex_out.position = float4(positions[id], 0.5f, 1.0f);
            return vertex_out;
        }
        """;

    private static readonly float[] FullscreenTriangle = [-1f, -1f, 3f, -1f, -1f, 3f];

    private static uint[] Program(uint mrtzEnableMask = 1, uint mrtzFlags = 0, bool clearExecAroundMrtz = false) =>
    [
        0x7E0002FF, 0x3EC00000, // v_mov_b32 v0, 0.375
        0x7E0202F2,             // v_mov_b32 v1, 1.0
        .. clearExecAroundMrtz
            ? new uint[]
            {
                0xBE80037E,     // s_mov_b32 s0, exec_lo
                0xBEFE0380,     // s_mov_b32 exec_lo, 0
            }
            : [],
        MrtzExport | mrtzFlags | mrtzEnableMask, 0x00000000, // exp mrtz v0, v0, v0, v0
        .. clearExecAroundMrtz
            ? new uint[] { 0xBEFE0300 } // s_mov_b32 exec_lo, s0
            : [],
        0xF800180F, 0x01010101, // exp mrt0 v1, v1, v1, v1 done vm
        0xBF810000,             // s_endpgm
    ];

    [Theory]
    [InlineData(true, 1u, 0u, true)]
    [InlineData(true, 3u, 0u, true)]
    [InlineData(true, 0xFu, 0u, true)]
    [InlineData(false, 1u, 0u, false)]
    [InlineData(true, 0u, 0u, false)]
    [InlineData(true, 2u, 0u, false)]
    [InlineData(true, 3u, Compressed, false)]
    public void DepthOutputRequiresEnabledUncompressedDepthComponent(
        bool depthExportEnable, uint enableMask, uint flags, bool expected)
    {
        var source = Compile(Program(enableMask, flags), depthExportEnable).Source;

        Assert.Equal(expected, source.Contains("float sharpemu_depth [[depth(any)]];", StringComparison.Ordinal));
        Assert.Equal(expected, source.Contains(
            "sharpemu_out.sharpemu_depth = sharpemu_in.sharpemu_frag_coord.z;", StringComparison.Ordinal));
        Assert.Equal(expected, source.Contains("sharpemu_out.sharpemu_depth = ", StringComparison.Ordinal));
        Assert.True(MetalNative.TryCompileLibrary(source, out _, out var error) || !MetalNative.IsAvailable, error);
    }

    [Theory]
    [InlineData(true, false, ExportedDepth)]
    [InlineData(true, true, RasterDepth)]
    [InlineData(false, false, RasterDepth)]
    public void ExportedDepthReachesTheDepthAttachment(bool depthExportEnable, bool clearExecAroundMrtz, float expected)
    {
        if (SkipWithoutMetalDevice("the depth export render"))
        {
            return;
        }

        var shader = Compile(Program(clearExecAroundMrtz: clearExecAroundMrtz), depthExportEnable);
        Assert.True(
            MetalNative.TryCompileLibrary(shader.Source + VertexSource, out var library, out var error),
            $"Metal rejected the depth export MSL: {error}\n{shader.Source}");

        var positions = new byte[FullscreenTriangle.Length * sizeof(float)];
        Buffer.BlockCopy(FullscreenTriangle, 0, positions, 0, positions.Length);
        var resources = new byte[Math.Max(shader.ArgumentLayout?.ByteSize ?? 0, 256)];
        Assert.True(
            MetalNative.TryRenderTrianglesDepth(
                library,
                "sharpemu_depth_test_vs",
                shader.EntryPoint,
                [(positions, 0)],
                [(resources, (uint)Gen5MslArgumentLayout.ResourcesBufferIndex)],
                3,
                TargetSize,
                TargetSize,
                MetalNative.Rgba32FloatPixelFormat,
                clearDepth: 1.0,
                out var depths,
                out error),
            error);

        Assert.All(depths, depth => Assert.Equal(expected, depth));
    }

    private static Gen5MslShader Compile(uint[] words, bool depthExportEnable)
    {
        var program = Gen5ComputeFixtures.DecodeOrThrow(new Gen5ComputeFixture("pixel-depth-export", words, 0, 0));
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Pixel,
            pixelDepthExportEnable: depthExportEnable);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader;
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
