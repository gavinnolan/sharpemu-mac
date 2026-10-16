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
