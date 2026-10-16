// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>Bit-exact ALU semantics from the RDNA2 ISA (sections 6.2, 12.7-12.10) that the
/// Metal host does not give for free. Each case runs one inserted instruction on a single
/// lane with v0..v3 preset and stores v3; execution needs the exclusive device gate.</summary>
public sealed class Gen5MslFloatSemanticsTests(ITestOutputHelper output)
{
    // V_ADD/SUB/SUBREV_NC_U32 (VOP2 0x25-0x27) saturate as unsigned under an SDWA clamp; only
    // the VOP3-only V_ADD/SUB_NC_I32 saturate as signed.
    [Theory]
    [InlineData("VAddI32", 0xFFFFFFF0u, 0x20u, 0xFFFFFFFFu)]
    [InlineData("VSubI32", 3u, 5u, 0u)]
    [InlineData("VSubrevI32", 5u, 3u, 0u)]
    public void Vop2NoCarryAddSubClampSaturatesUnsigned(string opcode, uint a, uint b, uint expected)
    {
        var shader = Compile([
            new Gen5ShaderInstruction(28, Gen5ShaderEncoding.Vop2, opcode, [], [Gen5Operand.Vector(0), Gen5Operand.Vector(1)],
                [Gen5Operand.Vector(3)], new Gen5SdwaControl(6, 0, 6, 6, false, false, 0, 0, 0, true, null)),
        ], a, b);
        if (!RequireDevice()) return;
        Assert.Equal(expected, Execute(shader));
    }

    // A 16-bit float operand reads an integer inline constant as its bit pattern (129 is the
    // denormal 0x0001, 193 is 0xFFFF) and 248 as the f16 1/(2*pi), 0x3118.
    [Theory]
    [InlineData("VAddF16", 129u, 0x0001u, 0xABCD0002u)]
    [InlineData("VMulF16", 248u, 0x3C00u, 0xABCD3118u)]
    [InlineData("VMinF16", 130u, 0x0003u, 0xABCD0002u)]
    [InlineData("VMaxF16", 192u, 0x0003u, 0xABCD0040u)]
    public void HalfOperandsReadIntegerInlineConstantsAsBits(string opcode, uint constant, uint b, uint expected)
    {
        var shader = Compile([Vop3(opcode, [Gen5Operand.Source(constant), Gen5Operand.Vector(1)])], 0u, b, 0u, 0xABCD5678u);
        if (!RequireDevice()) return;
        Assert.Equal(expected, Execute(shader));
    }

    [Fact]
    public void FmaMixReadsAHalfSelectedInlineConstantAsF16()
    {
        // fma_mix(src0 = 1 as f16 (0x0001), src1 = v1 as f32 2^24, src2 = v2 as f32 0) = 1.0
        var shader = Compile([Vop3("VFmaMixF32", [Gen5Operand.Source(129), Gen5Operand.Vector(1), Gen5Operand.Vector(2)],
            new Gen5Vop3pControl(0, 1, 0, 0, false))], 0u, 0x4B800000u, 0u);
        if (!RequireDevice()) return;
        Assert.Equal(0x3F800000u, Execute(shader));
    }

    // V_CMP_CLASS_F32 tells a signalling NaN (bit 0) from a quiet one (bit 1) and reports an
    // f32 denormal as a subnormal (bits 4/7) although the host flushes it in float compares.
    [Theory]
    [InlineData(0x7F800001u, 0x001u, 1u)]
    [InlineData(0x7F800001u, 0x002u, 0u)]
    [InlineData(0x7FC00000u, 0x002u, 1u)]
    [InlineData(0x7FC00000u, 0x001u, 0u)]
    [InlineData(0x00400000u, 0x080u, 1u)]
    [InlineData(0x00400000u, 0x040u, 0u)]
    [InlineData(0x80000001u, 0x010u, 1u)]
    [InlineData(0x80000001u, 0x020u, 0u)]
    [InlineData(0x80000000u, 0x020u, 1u)]
    public void CompareClassF32ReadsTheBits(uint value, uint mask, uint expected)
    {
        var shader = Compile([
            new Gen5ShaderInstruction(28, Gen5ShaderEncoding.Vopc, "VCmpClassF32", [], [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], [], null),
            CndmaskVcc(),
        ], value, mask);
        if (!RequireDevice()) return;
        Assert.Equal(expected, Execute(shader));
    }

    // v3 = VCC ? 1 : 0
    private static Gen5ShaderInstruction CndmaskVcc() =>
        new(28, Gen5ShaderEncoding.Vop2, "VCndmaskB32", [], [Gen5Operand.Source(128), Gen5Operand.Source(129)], [Gen5Operand.Vector(3)], null);

    private static Gen5ShaderInstruction Vop3(string opcode, Gen5Operand[] sources, Gen5InstructionControl? control = null) =>
        new(28, Gen5ShaderEncoding.Vop3, opcode, [], sources, [Gen5Operand.Vector(3)], control ?? new Gen5Vop3Control(0, 0, 0, false, 0, null));

    private static readonly Gen5Operand[] V012 = [Gen5Operand.Vector(0), Gen5Operand.Vector(1), Gen5Operand.Vector(2)];

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
        return BinaryPrimitives.ReadUInt32LittleEndian(result);
    }

    private static Gen5MslShader Compile(Gen5ShaderInstruction[] inserted, uint a, uint b, uint c = 0, uint old = 0)
    {
        Assert.True(TryCompile(inserted, a, b, c, old, out var shader, out var error), error);
        return shader!;
    }

    // v0 = a, v1 = b, v2 = c, v3 = old; the inserted instructions run before v3 is stored.
    private static bool TryCompile(Gen5ShaderInstruction[] inserted, uint a, uint b, uint c, uint old,
        out Gen5MslShader? shader, out string error)
    {
        var fixture = new Gen5ComputeFixture("float-semantics", [
            0x7E0002FF, a, 0x7E0202FF, b, 0x7E0402FF, c, 0x7E0602FF, old,
            0xE0700000, 0x80020300, 0xBF810000], 8, 16);
        var program = Gen5ComputeFixtures.DecodeOrThrow(fixture);
        var instructions = program.Instructions.ToList();
        instructions.InsertRange(4, inserted);
        var request = Gen5ComputeFixtures.RequestOrThrow(program with { Instructions = instructions }, ShaderStage.Compute);
        var compiled = Gen5MslTranslator.TryCompileProgram(request, out var result, out error);
        shader = compiled ? result : null;
        return compiled;
    }
}
