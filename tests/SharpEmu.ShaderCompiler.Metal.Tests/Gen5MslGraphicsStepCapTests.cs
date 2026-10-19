// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// The step cap keeps a mistranslated loop-exit condition from wedging the single Metal
/// queue. A compute (or mesh) program keeps the counter on every back edge; a structured
/// vertex or pixel program carries none, since the GPU watchdog ends its runaway loop
/// (Gen5MslCodegenOptions.GraphicsLoopStepCap, SHARPEMU_SHADER_MAX_STEPS_GRAPHICS=1).
/// </summary>
public sealed class Gen5MslGraphicsStepCapTests
{
    // s_mov_b32 s0, 5; loop: s_sub_i32 s0, s0, 1; s_cmp_lg_u32 s0, 0; s_cbranch_scc1 loop; s_endpgm
    private static readonly Gen5ComputeFixture CountedLoop = new(
        "counted-loop",
        [0xBE800385u, 0x81808100u, 0xBF078000u, 0xBF85FFFDu, 0xBF810000u],
        0,
        0);

    private static Gen5MslShader Compile(ShaderStage stage, Gen5MslCodegenOptions? options)
    {
        var program = Gen5ComputeFixtures.DecodeOrThrow(CountedLoop);
        var request = Gen5ComputeFixtures.RequestOrThrow(
            program,
            stage,
            pixelOutputs: stage == ShaderStage.Pixel ? [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)] : null);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error, options), error);
        return shader;
    }

    [Theory]
    [InlineData(ShaderStage.Vertex)]
    [InlineData(ShaderStage.Pixel)]
    public void AStructuredGraphicsLoopCarriesNoStepCounterByDefault(ShaderStage stage)
    {
        var shader = Compile(stage, new Gen5MslCodegenOptions());

        Assert.Contains("while (true)", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("++steps", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("uint steps", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("sharpemu_note_step_cap(sharpemu_resources", shader.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ShaderStage.Vertex)]
    [InlineData(ShaderStage.Pixel)]
    public void TheGraphicsSwitchRestoresTheStepCounter(ShaderStage stage)
    {
        var shader = Compile(stage, new Gen5MslCodegenOptions(GraphicsLoopStepCap: true));

        Assert.Contains("if (++steps >=", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_note_step_cap(sharpemu_resources", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void AComputeLoopKeepsTheStepCounterWhateverTheGraphicsSwitch()
    {
        foreach (var options in new[] { new Gen5MslCodegenOptions(), new Gen5MslCodegenOptions(GraphicsLoopStepCap: true) })
        {
            var shader = Compile(ShaderStage.Compute, options);

            Assert.Contains("if (++steps >=", shader.Source, StringComparison.Ordinal);
            Assert.Contains("sharpemu_note_step_cap(", shader.Source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheGraphicsStepCounterIsOnlyAnOptionOfTheSourceText()
    {
        // The text differs, so the library and archive keys (hashes of it) differ too.
        Assert.NotEqual(
            Compile(ShaderStage.Pixel, new Gen5MslCodegenOptions()).Source,
            Compile(ShaderStage.Pixel, new Gen5MslCodegenOptions(GraphicsLoopStepCap: true)).Source);
    }
}
