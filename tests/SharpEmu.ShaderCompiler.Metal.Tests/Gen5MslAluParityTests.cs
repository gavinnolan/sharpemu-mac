// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>Numerical fixtures based on RDNA2 ISA sections 12.7, 12.8 and 12.10.
/// Translation alone is checked everywhere; execution must be requested with the
/// exclusive device gate, and compares returned bits rather than generated strings.</summary>
public sealed class Gen5MslAluParityTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases => new List<object[]>()
    {
        // opcode, source0/1/2, old destination, op_sel, op_sel_hi, neg, abs/high-neg, clamp, expected
        new object[] { "VFmaMixF32", 0xBE003E00u, 0x4080C000u, 0x41200000u, 0u, 0u, 3u, 0u, 0u, false, 0x40E00000u }, // 1.5 * -2 + 10 = 7
        new object[] { "VFmaMixF32", 0xBE003E00u, 0x40003C00u, 0x41200000u, 0u, 3u, 3u, 0u, 0u, false, 0x40E00000u }, // selected high halves: -1.5*2+10 = 7
        new object[] { "VFmaMixloF16", 0x00003E00u, 0x00004000u, 0x00003C00u, 0xABCD1234u, 0u, 7u, 0u, 0u, false, 0xABCD4400u }, // 1.5*2+1 = 4, preserve high
        new object[] { "VFmaMixhiF16", 0x00003E00u, 0x00004000u, 0x00003C00u, 0xABCD1234u, 0u, 7u, 0u, 0u, false, 0x44001234u },
        new object[] { "VFmaMixF32", 0x0000BE00u, 0x00004000u, 0x3F800000u, 0u, 0u, 3u, 1u, 1u, false, 0xC0000000u }, // abs then negate: -1.5*2+1 = -2
        new object[] { "VFmaMixloF16", 0x7FC00000u, 0x3F800000u, 0u, 0xABCDFFFFu, 0u, 0u, 0u, 0u, true, 0xABCD0000u }, // clamp NaN -> 0
        new object[] { "VPkFmaF16", 0x3E003E00u, 0x3C013C01u, 0x80018001u, 0u, 0u, 7u, 0u, 0u, false, 0x3E013E01u }, // exact result just below midpoint; float fma + half incorrectly gives 3E02
        new object[] { "VPkFmacF16", 0x3E003E00u, 0x3C013C01u, 0u, 0x80018001u, 0u, 0u, 0u, 0u, false, 0x3E013E01u },
        new object[] { "VPkAddF16", 0x40003C00u, 0x44004200u, 0u, 0u, 0u, 3u, 0u, 0u, false, 0x46004400u }, // (1+3, 2+4)
        new object[] { "VPkMulF16", 0x40003C00u, 0x44004200u, 0u, 0u, 0u, 3u, 0u, 0u, false, 0x48004200u }, // (1*3, 2*4)
        new object[] { "VPkAddI16", 0x7FFFFFFFu, 0x00010002u, 0u, 0u, 0u, 3u, 0u, 0u, false, 0x80000001u },
        new object[] { "VPkSubI16", 0x00000000u, 0x00010001u, 0u, 0u, 0u, 3u, 0u, 0u, false, 0xFFFFFFFFu },
        new object[] { "VDot2cF32F16", 0x40003C00u, 0x44004200u, 0u, 0x40A00000u, 0u, 0u, 0u, 0u, false, 0x41800000u }, // 1*3+2*4+5 = 16 (no clamp)
        new object[] { "VFfbhU32", 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, false, 0xFFFFFFFFu },
        new object[] { "VFfbhU32", 0x100000FFu, 0u, 0u, 0u, 0u, 0u, 0u, 0u, false, 3u },
        new object[] { "VFfbhI32", 0xFFFFFFFEu, 0u, 0u, 0u, 0u, 0u, 0u, 0u, false, 31u },
        new object[] { "VFfbhI32", 0xFFFFFFFFu, 0u, 0u, 0u, 0u, 0u, 0u, 0u, false, 0xFFFFFFFFu },
        new object[] { "VBfeI32", 0x000000F0u, 4u, 4u, 0u, 0u, 0u, 0u, 0u, false, 0xFFFFFFFFu },
        new object[] { "VMadI32I24", 0x00FFFFFFu, 2u, 3u, 0u, 0u, 0u, 0u, 0u, false, 1u },
        new object[] { "VAddNcU16", 0xFFFFu, 2u, 0u, 0xABCD5678u, 0u, 0u, 0u, 0u, false, 0xABCD0001u },
        new object[] { "VAshrrevI16", 4u, 0x8000u, 0u, 0xABCD5678u, 0u, 0u, 0u, 0u, false, 0xABCDF800u },
        new object[] { "VMaxI16", 0xFFFFu, 2u, 0u, 0xABCD5678u, 0u, 0u, 0u, 0u, false, 0xABCD0002u },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryFixtureTranslates(string opcode, uint a, uint b, uint c, uint old,
        uint select, uint high, uint neg, uint abs, bool clamp, uint expected)
    {
        var shader = Compile(opcode, a, b, c, old, select, high, neg, abs, clamp);
        Assert.NotEmpty(shader.Source);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryFixtureExecutesOnMetal(string opcode, uint a, uint b, uint c, uint old,
        uint select, uint high, uint neg, uint abs, bool clamp, uint expected)
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Numerical execution requires the exclusive Metal device gate.");
            return;
        }
        Assert.True(MetalNative.IsAvailable, "The required execution gate needs a Metal device.");
        var shader = Compile(opcode, a, b, c, old, select, high, neg, abs, clamp);
        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var error), error + shader.Source);
        var buffer = new byte[16];
        buffer.AsSpan().Fill(0xCD);
        Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, buffer,
            new byte[PushData.ByteSize], shader.ArgumentLayout!, 1, out var result, out error), error);
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(result));
        Assert.Equal(buffer.AsSpan(4).ToArray(), result.AsSpan(4).ToArray());
    }

    [Theory]
    [InlineData(0xCC201103u, 0x9C0A0300u, "VFmaMixF32")]
    [InlineData(0xCC214003u, 0x1C0A0300u, "VFmaMixloF16")]
    public void RealInstructionEncodingsReachTheLowering(uint first, uint second, string expected)
    {
        var fixture = new Gen5ComputeFixture(expected, [first, second, 0xBF810000], 0, 0);
        Assert.Equal(expected, Gen5ComputeFixtures.DecodeOrThrow(fixture).Instructions[0].Opcode);
        Assert.NotEmpty(Gen5ComputeFixtures.CompileRequestOrThrow(fixture).Source);
    }

    private static Gen5MslShader Compile(string opcode, uint a, uint b, uint c, uint old,
        uint select, uint high, uint neg, uint abs, bool clamp)
    {
        // Actual decoded register initialization and store, with a direct IR opcode
        // insertion so each semantic case can exercise the control masks independently.
        var fixture = new Gen5ComputeFixture(opcode, [
            0x7E0002FF, a, 0x7E0202FF, b, 0x7E0402FF, c, 0x7E0602FF, old,
            0xE0700000, 0x80020300, 0xBF810000], 8, 16);
        var program = Gen5ComputeFixtures.DecodeOrThrow(fixture);
        var instructions = program.Instructions.ToList();
        Gen5InstructionControl? control = opcode.StartsWith("VPk", StringComparison.Ordinal) && opcode != "VPkFmacF16" ||
            opcode.StartsWith("VFmaMix", StringComparison.Ordinal)
            ? new Gen5Vop3pControl(select, high, neg, abs, clamp)
            : new Gen5Vop3Control(abs, neg, 0, clamp, select, null);
        instructions.Insert(4, new Gen5ShaderInstruction(28, Gen5ShaderEncoding.Vop3, opcode,
            [], [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2)],
            [Gen5Operand.Vector(3)], control));
        program = program with { Instructions = instructions };
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Compute);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader;
    }
}
