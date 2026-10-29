// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed partial class PixelInputAnalysisCacheTests
{
    private static Gen5ShaderInstruction Interpolation(string opcode, uint attribute, uint parameter = 0,
        uint absolute = 0, uint negate = 0) =>
        new(0, Gen5ShaderEncoding.Vintrp, opcode, [0], [Gen5Operand.Vector(0)], [Gen5Operand.Vector(4)],
            new Gen5InterpolationControl(attribute, 0)
            {
                Parameter = parameter,
                Modifiers = absolute != 0 || negate != 0 ? new Gen5Vop3Control(absolute, negate, 0, false, 0, null) : null,
            });

    [Fact]
    public void ChangingControlsAreMappedAfterTheInstructionAnalysis()
    {
        var program = new Gen5ShaderProgram(0x1000,
            [Interpolation("VInterpMovF32", 1), Interpolation("VInterpMovF32", 31), Interpolation("VInterpMovF32", 2, 2)]);
        uint[] controls = [0, 0x405];
        Assert.Equal((1u << 5) | (1u << 31), ShaderPipelineCache.PerVertexParametersOf(program, controls, true));
        controls[1] = 0x41f;
        Assert.Equal(1u << 31, ShaderPipelineCache.PerVertexParametersOf(program, controls, true));
        Assert.Equal((1u << 1) | (1u << 31), ShaderPipelineCache.PerVertexParametersOf(program, [], true));
        Assert.Equal(Gen5PixelInputMapping.PerVertexParameterMask(program, controls),
            ShaderPipelineCache.PerVertexParametersOf(program, controls, false));
    }

    [Fact]
    public void RecordCopiesWithDifferentInstructionsDoNotInheritAnalysis()
    {
        var original = new Gen5ShaderProgram(0x1000, [Interpolation("VInterpMovF32", 1)]);
        Assert.Equal(2u, ShaderPipelineCache.PerVertexParametersOf(original, [], true));
        var changed = original with { Instructions = [Interpolation("VInterpMovF32", 4)] };
        var empty = original with { Instructions = [] };
        Assert.Equal(16u, ShaderPipelineCache.PerVertexParametersOf(changed, [], true));
        Assert.Equal(0u, ShaderPipelineCache.PerVertexParametersOf(empty, [], true));
        Assert.Equal(2u, ShaderPipelineCache.PerVertexParametersOf(original, [], true));
    }

    [Fact]
    public void CachedAnalysisMatchesTheCompilerAcrossInterpolationFormsAndControlLengths()
    {
        string[] opcodes = ["VInterpMovF32", "VInterpP1F32", "VInterpP2F32", "VInterpP1llF16", "VInterpP1lvF16", "VInterpP2F16"];
        var random = new System.Random(70413);
        for (var trial = 0; trial < 64; trial++)
        {
            var instructions = Enumerable.Range(0, 128).Select(_ => Interpolation(opcodes[random.Next(opcodes.Length)],
                (uint)random.Next(40), (uint)random.Next(3), (uint)random.Next(8), (uint)random.Next(8))).ToArray();
            var program = new Gen5ShaderProgram((ulong)trial, instructions);
            foreach (var length in new[] { 0, 1, 7, 31, 32, 40 })
            {
                var controls = Enumerable.Range(0, length).Select(_ => (uint)random.Next()).ToArray();
                Assert.Equal(Gen5PixelInputMapping.PerVertexParameterMask(program, controls),
                    ShaderPipelineCache.PerVertexParametersOf(program, controls, true));
            }
        }
    }

    [Fact]
    public async Task SharedProgramAnalysisIsSafeForConcurrentReaders()
    {
        var program = new Gen5ShaderProgram(0, [Interpolation("VInterpMovF32", 3), Interpolation("VInterpP1F32", 3)]);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            uint[] controls = [0, 1, 2, (uint)index];
            for (var iteration = 0; iteration < 1000; iteration++)
                Assert.Equal(1u << index, ShaderPipelineCache.PerVertexParametersOf(program, controls, true));
        })));
    }

    [Fact]
    public void PublicCompilerAnalysisStillObservesEditableInstructionArrays()
    {
        Gen5ShaderInstruction[] instructions = [Interpolation("VInterpMovF32", 1)];
        var program = new Gen5ShaderProgram(0, instructions);
        Assert.Equal(2u, Gen5PixelInputMapping.PerVertexParameterMask(program, []));
        instructions[0] = Interpolation("VInterpMovF32", 4);
        Assert.Equal(16u, Gen5PixelInputMapping.PerVertexParameterMask(program, []));
    }
}
