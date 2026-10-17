// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5PixelInputMappingTests
{
    [Fact]
    public void PerVertexParameterMask_NamesTheParamsOfSlotsReadPerVertex()
    {
        // Slot 0 interpolates normally, slot 1 reads P10 and P0 by hand, slot 2 reads only
        // P0 (a flat input) and slot 3 reads P20; the masks are by SPI_PS_INPUT_CNTL.OFFSET.
        var program = Program(
            Interpolation("VInterpP1F32", 0, 0),
            Interpolation("VInterpMovF32", 1, 2),
            Interpolation("VInterpMovF32", 1, 0),
            Interpolation("VInterpMovF32", 2, 2),
            Interpolation("VInterpMovF32", 3, 1));
        uint[] controls = [0, 0x405, 0x406];

        // Slot 3 is past the programmed controls, so it reads param 3.
        Assert.Equal((1u << 5) | (1u << 3), Gen5PixelInputMapping.PerVertexParameterMask(program, controls));
    }

    [Theory]
    [InlineData("VInterpP1llF16", 0u, 0u)]
    [InlineData("VInterpP2F16", 0u, 0u)]
    [InlineData("VInterpP1F32", 0u, 2u)]
    [InlineData("VInterpP1F32", 2u, 0u)]
    public void PerVertexParameterMask_IncludesHalfAndModifiedBarycentricReads(string opcode, uint negate, uint absolute)
    {
        var instruction = Interpolation(opcode, 1, 0) with
        {
            Control = new Gen5InterpolationControl(1, 0)
            {
                Modifiers = negate != 0 || absolute != 0 ? new Gen5Vop3Control(absolute, negate, 0, false, 0, null) : null,
            },
        };
        var program = Program(instruction);
        Assert.Equal(1u << 4, Gen5PixelInputMapping.PerVertexParameterMask(program, [0, 4]));
    }

    [Fact]
    public void PerVertexParameterMask_IsEmptyForOrdinaryInterpolation()
    {
        var program = Program(Interpolation("VInterpP1F32", 1, 0), Interpolation("VInterpP2F32", 1, 0));
        Assert.Equal(0u, Gen5PixelInputMapping.PerVertexParameterMask(program, [0, 1]));
    }

    private static Gen5ShaderInstruction Interpolation(string opcode, uint attribute, uint parameter) =>
        new(0, Gen5ShaderEncoding.Vintrp, opcode, [0], [Gen5Operand.Vector(0)], [Gen5Operand.Vector(4)],
            new Gen5InterpolationControl(attribute, 0) { Parameter = parameter });

    private static Gen5ShaderProgram Program(params Gen5ShaderInstruction[] instructions) =>
        new(0, instructions);

    [Fact]
    public void ResolveLocations_PreservesUniqueMappings()
    {
        uint[] controls = [5, 7, 9];
        uint[] activeInputs = [0, 1, 2];

        Assert.Equal(
            [5u, 7u, 9u],
            Gen5PixelInputMapping.ResolveLocations(controls, activeInputs));
    }

    [Fact]
    public void ResolveLocations_RelocatesDuplicateMappings()
    {
        uint[] controls = [0, 0, 0];
        uint[] activeInputs = [0, 1, 2];

        Assert.Equal(
            [0u, 1u, 2u],
            Gen5PixelInputMapping.ResolveLocations(controls, activeInputs));
    }

    [Fact]
    public void ResolveLocations_SkipsOccupiedFallbackLocations()
    {
        uint[] controls = [1, 1, 2];
        uint[] activeInputs = [0, 1, 2];

        Assert.Equal(
            [1u, 2u, 3u],
            Gen5PixelInputMapping.ResolveLocations(controls, activeInputs));
    }

    [Fact]
    public void ResolveLocations_UsesIdentityBeyondProgrammedControls()
    {
        uint[] controls = [0x407];
        uint[] activeInputs = [0, 2, 5];

        Assert.Equal(
            [7u, 2u, 5u],
            Gen5PixelInputMapping.ResolveLocations(controls, activeInputs));
    }

    [Fact]
    public void ResolveLocations_RejectsFallbackPastLastLocation()
    {
        var controls = new uint[32];
        controls[0] = 31;
        controls[31] = 31;
        uint[] activeInputs = [0, 31];

        Assert.Throws<InvalidOperationException>(() =>
            Gen5PixelInputMapping.ResolveLocations(controls, activeInputs));
    }
}
