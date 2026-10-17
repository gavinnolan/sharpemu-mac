// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ManualInterpolationTests
{
    // PERSP_CENTER only: I in v0, J in v1.
    private const uint PerspectiveCenter = 1u << 1;

    // Slot 0 reads param 3 and is custom-interpolated.
    private static readonly uint[] Controls = [3];
    private const uint Custom = 1;

    [Theory]
    [InlineData(false, Gen5InterpolantKind.HalfLow)]
    [InlineData(true, Gen5InterpolantKind.HalfHigh)]
    public void HalfPairInterpolatedByHand_BecomesOneHostInterpolant(bool high, Gen5InterpolantKind kind)
    {
        var plan = Analyze([.. HalfInterpolation(high, result: 20), Export(20)], out _);

        Assert.NotNull(plan);
        Assert.Equal(1u, plan!.Attributes);
        var rewrite = Assert.Single(plan.Rewrites);
        Assert.Equal(new Gen5InterpolationRewrite(0x20, 20, 3, 1, kind, 1f, 0f, false), rewrite);
        Assert.Equal([new Gen5Interpolant(3, kind, 1)], plan.Interpolants);
    }

    [Fact]
    public void ScaledByteInterpolation_KeepsTheScale()
    {
        // b_k = ubyte1(P_k); r = (P0 + I (P1 - P0) + J (P2 - P0)) / 255 with the scale
        // applied to the deltas and the origin first.
        const uint inverse255 = 0x3B808081;
        var plan = Analyze(
            Mov(0x00, 10, 2), Mov(0x04, 11, 0), Mov(0x08, 12, 1),
            Vop1(0x0C, "VCvtF32Ubyte1", 13, V(10)),
            Vop1(0x10, "VCvtF32Ubyte1", 14, V(11)),
            Vop1(0x14, "VCvtF32Ubyte1", 15, V(12)),
            Vop2(0x18, "VSubF32", 16, V(14), V(13)),
            Vop2(0x1C, "VSubF32", 17, V(15), V(13)),
            Vop2(0x20, "VMulF32", 13, V(13), Literal(inverse255)),
            Vop2(0x24, "VMulF32", 16, V(16), Literal(inverse255)),
            Vop2(0x28, "VMulF32", 17, V(17), Literal(inverse255)),
            Vop3(0x2C, "VFmaF32", 18, V(0), V(16), V(13)),
            Vop3(0x34, "VMadF32", 19, V(1), V(17), V(18)) with { Control = new Gen5Vop3Control(0, 0, 0, true, 0, null) },
            Export(19));

        Assert.NotNull(plan);
        var rewrite = Assert.Single(plan!.Rewrites);
        Assert.Equal(0x34u, rewrite.Pc);
        Assert.Equal(19u, rewrite.Register);
        Assert.Equal(Gen5InterpolantKind.Byte1, rewrite.Kind);
        Assert.Equal(BitConverter.UInt32BitsToSingle(inverse255), rewrite.Scale, 6);
        Assert.True(rewrite.Clamp);
    }

    [Fact]
    public void RawVertexValueReadElsewhere_KeepsPerVertexInputs()
    {
        var plan = Analyze(
            [.. HalfInterpolation(false, result: 20), Export(11)],
            out var reason);

        Assert.Null(plan);
        Assert.Contains("reads a vertex value", reason);
    }

    [Fact]
    public void UnfinishedInterpolation_KeepsPerVertexInputs()
    {
        // Only P0 + I (P1 - P0), then exported.
        var instructions = HalfInterpolation(false, result: 20).Take(5).Append(Export(14)).ToArray();

        Assert.Null(Analyze(instructions, out _));
    }

    [Fact]
    public void ProductOfVertexValues_KeepsPerVertexInputs()
    {
        var plan = Analyze(
            [Mov(0x00, 10, 2), Mov(0x04, 11, 0), Mov(0x08, 12, 1),
             Vop2(0x0C, "VMulF32", 13, V(11), V(12)), Export(13)],
            out var reason);

        Assert.Null(plan);
        Assert.Equal("product of vertex values", reason);
    }

    [Fact]
    public void InterpolationInsideNarrowedExec_IsKept()
    {
        var plan = Analyze(
            [SaveExec(0x00), .. HalfInterpolation(false, result: 20, pc: 0x10), RestoreExec(0x80), Export(20)],
            out _);

        Assert.NotNull(plan);
        Assert.Single(plan!.Rewrites);
    }

    [Fact]
    public void DeltaFromNarrowedExecReadAfterRestore_KeepsPerVertexInputs()
    {
        // d1 is computed for the lanes of a narrowed EXEC and combined after EXEC is full again.
        var instructions = HalfInterpolation(false, result: 20, pc: 0x10);
        var plan = Analyze(
            [SaveExec(0x00), .. instructions.Take(5), RestoreExec(0x80), .. instructions.Skip(5), Export(20)],
            out var reason);

        Assert.Null(plan);
        Assert.Equal("lane mixture of a vertex value", reason);
    }

    [Fact]
    public void ProgramWithoutPerVertexReads_HasNoPlan()
    {
        var plan = Analyze([Mov(0x00, 10, 2), Export(10)], out var reason);

        Assert.Null(plan);
        Assert.Equal("no per-vertex input", reason);
    }

    [Fact]
    public void EncodedInterpolant_RoundTrips()
    {
        var interpolant = new Gen5Interpolant(17, Gen5InterpolantKind.Byte2, 5);

        Assert.Equal((17u, Gen5InterpolantKind.Byte2), Gen5Interpolant.Decode(interpolant.Encoded));
    }

    // The DS idiom: raw vertex MOVs of channel 1, then
    //   d1 = P1.h - P0.h, d2 = P2.h - P0.h (V_FMA_MIX_F32 1.0, Pk.h, -P0.h)
    //   a = I * d1 + P0.h (V_FMA_MIX_F32), r = J * d2 + a (V_MAC_F32) into v[result].
    private static Gen5ShaderInstruction[] HalfInterpolation(bool high, uint result, uint pc = 0)
    {
        var select = high ? 6u : 0u;
        return
        [
            Mov(pc + 0x00, 10, 2, channel: 1),
            Mov(pc + 0x04, 11, 0, channel: 1),
            Mov(pc + 0x08, 12, 1, channel: 1),
            Mix(pc + 0x0C, 13, Inline(242), V(11), V(10), select, 7, negate: 4),
            Mix(pc + 0x14, 14, V(0), V(13), V(10), high ? 4u : 0u, 4, negate: 0),
            Mix(pc + 0x18, 15, Inline(242), V(12), V(10), select, 7, negate: 4),
            Vop3(pc + 0x1C, "VMovB32", result, V(14)) with { Control = null, Sources = [V(14)] },
            new(pc + 0x20, Gen5ShaderEncoding.Vop2, "VMacF32", [0], [V(15), V(1)], [V(result)], null),
        ];
    }

    private static Gen5ManualInterpolationPlan? Analyze(params Gen5ShaderInstruction[] instructions) =>
        Analyze(instructions, out _);

    private static Gen5ManualInterpolationPlan? Analyze(Gen5ShaderInstruction[] instructions, out string reason)
    {
        var program = new Gen5ShaderProgram(0, [.. instructions, new(0xFFF0, Gen5ShaderEncoding.Sopp, "SEndpgm", [0], [], [], null)]);
        return Gen5ManualInterpolation.Analyze(program, Controls, Custom, PerspectiveCenter, out reason);
    }

    private static Gen5Operand V(uint register) => Gen5Operand.Vector(register);

    private static Gen5Operand Inline(uint encoded) => new(Gen5OperandKind.EncodedConstant, encoded);

    private static Gen5Operand Literal(uint bits) => new(Gen5OperandKind.LiteralConstant, bits);

    private static Gen5ShaderInstruction Mov(uint pc, uint destination, uint parameter, uint channel = 1) =>
        new(pc, Gen5ShaderEncoding.Vintrp, "VInterpMovF32", [0], [V(parameter)], [V(destination)],
            new Gen5InterpolationControl(0, channel) { Parameter = parameter });

    private static Gen5ShaderInstruction Mix(uint pc, uint destination, Gen5Operand a, Gen5Operand b, Gen5Operand c,
        uint opSel, uint opSelHi, uint negate) =>
        new(pc, Gen5ShaderEncoding.Vop3p, "VFmaMixF32", [0], [a, b, c], [V(destination)],
            new Gen5Vop3pControl(opSel, opSelHi, negate, 0, false));

    private static Gen5ShaderInstruction Vop1(uint pc, string opcode, uint destination, Gen5Operand source) =>
        new(pc, Gen5ShaderEncoding.Vop1, opcode, [0], [source], [V(destination)], null);

    private static Gen5ShaderInstruction Vop2(uint pc, string opcode, uint destination, Gen5Operand a, Gen5Operand b) =>
        new(pc, Gen5ShaderEncoding.Vop2, opcode, [0], [a, b], [V(destination)], null);

    private static Gen5ShaderInstruction Vop3(uint pc, string opcode, uint destination, params Gen5Operand[] sources) =>
        new(pc, Gen5ShaderEncoding.Vop3, opcode, [0], sources, [V(destination)], new Gen5Vop3Control(0, 0, 0, false, 0, null));

    private static Gen5ShaderInstruction Export(uint register) =>
        new(0xFF00, Gen5ShaderEncoding.Exp, "Exp", [0], [V(register)], [], new Gen5ExportControl(0, 1, false, true, true));

    // s_and_saveexec_b64 s[0:1], vcc and s_or_b64 exec, exec, s[0:1].
    private static Gen5ShaderInstruction SaveExec(uint pc) =>
        new(pc, Gen5ShaderEncoding.Sop1, "SAndSaveexecB64", [0], [Gen5Operand.Scalar(106)], [Gen5Operand.Scalar(0)], null);

    private static Gen5ShaderInstruction RestoreExec(uint pc) =>
        new(pc, Gen5ShaderEncoding.Sop2, "SOrB64", [0], [Gen5Operand.Scalar(126), Gen5Operand.Scalar(0)], [Gen5Operand.Scalar(126)], null);
}
