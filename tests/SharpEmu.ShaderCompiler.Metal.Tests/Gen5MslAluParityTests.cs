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
        // A NaN operand makes V_MED3 answer MIN3, whose min skips the NaN: the clamp
        // med3(65504, x, 0) flushes a NaN x to 0 rather than to 65504.
        new object[] { "VMed3F32", 0x477FE000u, 0x7FC00000u, 0u, 0u, 0u, 0u, 0u, 0u, false, 0u },
        new object[] { "VMed3F32", 0x7FC00000u, 0x40E00000u, 0x40A00000u, 0u, 0u, 0u, 0u, 0u, false, 0x40A00000u }, // min3(NaN, 7, 5) = 5
        new object[] { "VMed3F32", 0x477FE000u, 0x40000000u, 0u, 0u, 0u, 0u, 0u, 0u, false, 0x40000000u }, // med3(65504, 2, 0) = 2
        new object[] { "VMed3F16", 0x7E00u, 0x4700u, 0x4500u, 0xABCD5678u, 0u, 0u, 0u, 0u, false, 0xABCD4500u }, // min3(NaN, 7, 5) = 5
    };

    public static IEnumerable<object[]> TranslationCases => Cases.Select(static row => row[..^1]);

    [Theory]
    [MemberData(nameof(TranslationCases))]
    public void EveryFixtureTranslates(string opcode, uint a, uint b, uint c, uint old,
        uint select, uint high, uint neg, uint abs, bool clamp)
    {
        var shader = Compile(opcode, a, b, c, old, select, high, neg, abs, clamp);
        Assert.NotEmpty(shader.Source);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryFixtureExecutesOnMetal(string opcode, uint a, uint b, uint c, uint old,
        uint select, uint high, uint neg, uint abs, bool clamp, uint expected)
    {
        if (!RequireDevice()) return;
        Assert.Equal(expected, Execute(Compile(opcode, a, b, c, old, select, high, neg, abs, clamp)));
    }

    // op_sel_hi marks a V_FMA_MIX operand as f16, literals included: 0x34CD is 0.3, not an
    // f32 denormal. Silent Hill weights a luminance with such literals and divides by it.
    [Theory]
    [InlineData(0x34CDu, 1u, 0x40000000u)]  // half-selected literal: f16 0.3 * 2
    [InlineData(0x3E800000u, 0u, 0x40000000u)] // f32 literal: 0.25 * 2
    public void FusedMixReadsHalfSelectedLiteralsAsHalfFloats(uint literal, uint high, uint factor)
    {
        var shader = Compile("VFmaMixF32", 0u, factor, 0u, 0u, 0u, high, 0u, 0u, false,
            new Gen5Operand(Gen5OperandKind.LiteralConstant, literal));
        if (!RequireDevice()) return;
        var weight = high != 0 ? (float)BitConverter.UInt16BitsToHalf((ushort)literal) : BitConverter.UInt32BitsToSingle(literal);
        Assert.Equal(
            BitConverter.SingleToUInt32Bits(MathF.FusedMultiplyAdd(weight, BitConverter.UInt32BitsToSingle(factor), 0f)),
            Execute(shader));
    }

    // V_CVT_PKRTZ_F16_F32 rounds toward zero: finite values past the f16 range saturate at
    // +-65504 instead of becoming infinity, and subnormal results keep whole 2^-24 steps.
    [Theory]
    [InlineData(5003.7f, -80000.3f)]
    [InlineData(70000f, -float.MaxValue)]
    [InlineData(65519.99f, -65504f)]
    [InlineData(3.3e-6f, -5e-5f)]
    [InlineData(5.9e-5f, -6.104e-5f)] // 989.86 steps of 2^-24: nearest would give 990
    [InlineData(1e-9f, -0f)]
    [InlineData(1.00097656f, -2.99f)]
    public void PackToHalfRoundsTowardZeroWithoutOverflowingToInfinity(float low, float high)
    {
        var shader = Compile("VCvtPkrtzF16F32", Bits(low), Bits(high), 0u, 0u, 0u, 0u, 0u, 0u, false);
        if (!RequireDevice()) return;
        Assert.Equal(HalfTowardZero(low) | HalfTowardZero(high) << 16, Execute(shader));
    }

    [Fact]
    public void PackToHalfKeepsNaNAndInfinity()
    {
        var nan = Compile("VCvtPkrtzF16F32", Bits(float.NaN), Bits(float.PositiveInfinity), 0u, 0u, 0u, 0u, 0u, 0u, false);
        var negative = Compile("VCvtPkrtzF16F32", Bits(float.NegativeInfinity), 0u, 0u, 0u, 0u, 0u, 0u, 0u, false);
        if (!RequireDevice()) return;
        var special = Execute(nan);
        Assert.True((special & 0x7C00) == 0x7C00 && (special & 0x3FF) != 0, $"0x{special:X8}");
        Assert.Equal(0x7C00u, special >> 16);
        Assert.Equal(0xFC00u, Execute(negative));
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Numerical execution requires the exclusive Metal device gate.");
            return false;
        }

        Assert.True(MetalNative.IsAvailable, "The required execution gate needs a Metal device.");
        return true;
    }

    private static uint Execute(Gen5MslShader shader)
    {
        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var error), error + shader.Source);
        var buffer = new byte[16];
        buffer.AsSpan().Fill(0xCD);
        Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, buffer,
            new byte[PushData.ByteSize], shader.ArgumentLayout!, 1, out var result, out error), error);
        Assert.Equal(buffer.AsSpan(4).ToArray(), result.AsSpan(4).ToArray());
        return BinaryPrimitives.ReadUInt32LittleEndian(result);
    }

    private static uint HalfTowardZero(float value)
    {
        var sign = value < 0 || (value == 0 && float.IsNegative(value)) ? 0x8000u : 0u;
        var magnitude = MathF.Abs(value);
        if (magnitude >= 65504f) return sign | 0x7BFF;
        if (magnitude < 6.10351562e-05f) return sign | (uint)Math.Floor(magnitude * 16777216.0);
        var bits = BitConverter.SingleToUInt32Bits(magnitude);
        return sign | ((((bits >> 23) - 127 + 15) << 10) | ((bits >> 13) & 0x3FF));
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

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
        uint select, uint high, uint neg, uint abs, bool clamp, Gen5Operand? firstSource = null)
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
            [], [firstSource ?? Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2)],
            [Gen5Operand.Vector(3)], control));
        program = program with { Instructions = instructions };
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Compute);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader;
    }
}
