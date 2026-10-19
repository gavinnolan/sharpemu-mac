// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

/// <summary>
/// VINTRP and the VOP3-encoded interpolation opcodes (0x200-0x202, and the f16 forms 0x342,
/// 0x343, 0x35A that only VOP3 has). In VOP3 form src0 holds the attribute [5:0], channel
/// [7:6] and HIGH [8], src1 the I/J VGPR (V_INTERP_MOV's parameter) and src2 the f16 forms'
/// S2. The encodings come from llvm-mc -mcpu=gfx1030; the comment is the assembly.
/// </summary>
public sealed class Gen5InterpolationDecodeTests
{
    private const uint SEndpgm = 0xBF810000;

    public static TheoryData<uint[], string, uint, uint, uint, bool, uint[]> Cases => new()
    {
        // v_interp_p1_f32_e32 v0, v1, attr2.y
        { [0xC8000901], "VInterpP1F32", 2, 1, 0, false, [257] },
        // v_interp_p1_f32_e64 v0, v1, attr2.y
        { [0xD6000000, 0x00020242], "VInterpP1F32", 2, 1, 0, false, [257] },
        // v_interp_p2_f32_e64 v3, v4, attr5.w
        { [0xD6010003, 0x000208C5], "VInterpP2F32", 5, 3, 3, false, [260] },
        // v_interp_mov_f32_e64 v3, p20, attr5.z
        { [0xD6020003, 0x00000285], "VInterpMovF32", 5, 2, 3, false, [] },
        // v_interp_mov_f32_e64 v3, p0, attr31.x
        { [0xD6020003, 0x0000041F], "VInterpMovF32", 31, 0, 3, false, [] },
        // v_interp_mov_f32_e64 v7, p10, attr32.w
        { [0xD6020007, 0x000000E0], "VInterpMovF32", 32, 3, 7, false, [] },
        // v_interp_p1ll_f16 v0, v1, attr2.y
        { [0xD7420000, 0x00020242], "VInterpP1llF16", 2, 1, 0, false, [257] },
        // v_interp_p1ll_f16 v0, v1, attr2.y high
        { [0xD7420000, 0x00020342], "VInterpP1llF16", 2, 1, 0, true, [257] },
        // v_interp_p1lv_f16 v0, v1, attr2.y, v3
        { [0xD7430000, 0x040E0242], "VInterpP1lvF16", 2, 1, 0, false, [257, 259] },
        // v_interp_p1lv_f16 v0, v1, attr2.y, v3 high
        { [0xD7430000, 0x040E0342], "VInterpP1lvF16", 2, 1, 0, true, [257, 259] },
        // v_interp_p2_f16 v0, v1, attr2.y, v3
        { [0xD75A0000, 0x040E0242], "VInterpP2F16", 2, 1, 0, false, [257, 259] },
        // v_interp_p2_f16 v5, v9, attr32.w, v6 high clamp
        { [0xD75A8005, 0x041A13E0], "VInterpP2F16", 32, 3, 5, true, [265, 262] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void InterpolationDecodesAttributeChannelAndOperands(
        uint[] words, string opcode, uint attribute, uint channel, uint destination, bool high, uint[] sources)
    {
        var instruction = DecodeFirst(words);

        Assert.Equal(opcode, instruction.Opcode);
        Assert.Equal(words.Length, instruction.Words.Count);
        var control = Assert.IsType<Gen5InterpolationControl>(instruction.Control);
        Assert.Equal(attribute, control.Attribute);
        Assert.Equal(channel, control.Channel);
        Assert.Equal(high, control.High);
        Assert.Equal(Gen5Operand.Vector(destination), Assert.Single(instruction.Destinations));
        Assert.Equal(sources.Select(source => Gen5Operand.Source(source)).ToArray(), instruction.Sources.ToArray());
    }

    [Theory]
    // v_interp_mov_f32 v4, p20, attr1.x (VINTRP vsrc) and the VOP3 forms' src1.
    [InlineData(new uint[] { 0xC8120401 }, 1u)]
    [InlineData(new uint[] { 0xD6020003, 0x00000285 }, 1u)]
    [InlineData(new uint[] { 0xD6020003, 0x0000041F }, 2u)]
    [InlineData(new uint[] { 0xD6020007, 0x000000E0 }, 0u)]
    public void ParameterMoveSelectsItsParameter(uint[] words, uint parameter)
    {
        var control = Assert.IsType<Gen5InterpolationControl>(DecodeFirst(words).Control);

        Assert.Equal(parameter, control.Parameter);
    }

    [Fact]
    public void VinterpCarriesNoVop3Modifiers()
    {
        Assert.Null(Assert.IsType<Gen5InterpolationControl>(DecodeFirst([0xC8000901]).Control).Modifiers);
    }

    [Theory]
    // v_interp_p1_f32_e64 v0, -v1, attr2.y clamp: neg[1].
    [InlineData(new uint[] { 0xD6008000, 0x40020242 }, 0u, 2u, 0u, true)]
    // v_interp_p2_f32_e64 v0, |v1|, attr2.y div:2: abs[1], omod 3.
    [InlineData(new uint[] { 0xD6010200, 0x18020242 }, 2u, 0u, 3u, false)]
    // v_interp_p1ll_f16 v0, -v1, attr2.y clamp
    [InlineData(new uint[] { 0xD7428000, 0x40020242 }, 0u, 2u, 0u, true)]
    // v_interp_p1ll_f16 v0, v1, attr2.y mul:2
    [InlineData(new uint[] { 0xD7420000, 0x08020242 }, 0u, 0u, 1u, false)]
    // v_interp_p1lv_f16 v5, -v9, attr3.z, |v6|: neg[1], abs[2].
    [InlineData(new uint[] { 0xD7430405, 0x441A1283 }, 4u, 2u, 0u, false)]
    public void Vop3InterpolationKeepsItsModifiers(uint[] words, uint abs, uint neg, uint omod, bool clamp)
    {
        var control = Assert.IsType<Gen5InterpolationControl>(DecodeFirst(words).Control);
        var modifiers = Assert.IsType<Gen5Vop3Control>(control.Modifiers);

        Assert.Equal(abs, modifiers.AbsoluteMask);
        Assert.Equal(neg, modifiers.NegateMask);
        Assert.Equal(omod, modifiers.OutputModifier);
        Assert.Equal(clamp, modifiers.Clamp);
    }

    private static Gen5ShaderInstruction DecodeFirst(uint[] words)
    {
        var bytes = new byte[(words.Length + 1) * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(words.Length * sizeof(uint)), SEndpgm);
        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error), error);
        Assert.Equal(2, program.Instructions.Count);
        return program.Instructions[0];
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            var offset = virtualAddress - 0x1000;
            if (virtualAddress < 0x1000 || offset + (ulong)destination.Length > (ulong)bytes.Length)
            {
                return false;
            }

            bytes.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }
}
