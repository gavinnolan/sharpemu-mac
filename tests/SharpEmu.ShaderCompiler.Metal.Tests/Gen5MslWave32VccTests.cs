// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// In wave32 VCC is VCC_LO alone and VCC_HI (s107) is an ordinary SGPR. GTA V's BVH
// rotation pass (compute 0x5515EAC3C1B5EDC8 and 0xCA99120EAF300BFD) loads a v_cmp_class
// mask into s107 and then writes VCC with a V_CMP before reading it; the translation
// cleared s107 on every VCC write, and the rotations wrote wrong parent links and
// child pointers.
public sealed class Gen5MslWave32VccTests(ITestOutputHelper output)
{
    private const uint Threads = 32;
    private const uint ResultBytes = Threads * 4;

    private static readonly uint[] Words =
    [
        0x7E040280,             // v_mov_b32 v2, 0
        0xB06B0204,             // s_movk_i32 s107, 0x204
        0x7D880080,             // v_cmp_gt_u32 vcc, 0, v0 (no lane: VCC_LO = 0)
        0x7E02026B,             // v_mov_b32 v1, s107
        0xBF860001,             // s_cbranch_vccz +1 (VCC_LO is zero, s107 is not)
        0x7E0402C1,             // v_mov_b32 v2, -1
        0xE0702000, 0x80010100, // buffer_store_dword v1, v0, s[4:7] idxen
        0xE0702000 | ResultBytes, 0x80010200, // buffer_store_dword v2, v0, s[4:7] idxen offset:ResultBytes
        0xBF810000,             // s_endpgm
    ];

    [Fact]
    public void AVccWriteKeepsVccHiAndVcczReadsVccLoOnly()
    {
        var program = Gen5ComputeFixtures.DecodeOrThrow(new Gen5ComputeFixture("wave32-vcc-hi", Words, 0, 0));
        Assert.Equal("SMovkI32", program.Instructions[1].Opcode);
        Assert.Equal("VCmpGtU32", program.Instructions[2].Opcode);
        Assert.Equal("SCbranchVccz", program.Instructions[4].Opcode);

        if (SkipWithoutMetalDevice()) return;

        var shader = Compile(program);
        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var compileError), compileError);
        Assert.True(
            MetalNative.TryExecuteWithArgumentBuffer(
                library,
                shader.EntryPoint,
                new byte[ResultBytes * 2],
                new byte[Resources.PushData.ByteSize],
                shader.ArgumentLayout!,
                Threads,
                out var result,
                out var runError),
            runError);

        for (var lane = 0; lane < Threads; lane++)
        {
            Assert.Equal(0x204u, BitConverter.ToUInt32(result, lane * 4));
            Assert.Equal(0u, BitConverter.ToUInt32(result, (int)ResultBytes + (lane * 4)));
        }
    }

    // A guest wave32 NGG primitive shader (GS_W32_EN) or pixel shader (PS_W32_EN) keeps VCC_HI
    // across a VCC write too; wave64 clears it, its upper lanes having no hardware lanes here.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGraphicsWave32VccWriteKeepsVccHi(bool wave64)
    {
        var mesh = Gen5NggFixtures.CullingTriangle();
        var compare = new Gen5ShaderInstruction(mesh.Instructions[0].Pc, Gen5ShaderEncoding.Vopc, "VCmpGtU32", [],
            [Gen5Operand.Source(128), Gen5Operand.Vector(0)], [], null);
        var meshShader = CompileGraphics(
            mesh with { Instructions = [compare, .. mesh.Instructions] },
            ShaderStage.Vertex,
            primitiveShader: wave64 ? Gen5NggFixtures.Wave64StageInfo : Gen5NggFixtures.StageInfo);
        Assert.Equal(Gen5MslStage.Mesh, meshShader.Stage);
        Assert.Equal(wave64, ClearsVccHi(meshShader.Source));

        var pixelShader = CompileGraphics(
            Gen5ComputeFixtures.DecodeOrThrow(new Gen5ComputeFixture("pixel-vcc-hi", Words, 0, 0)),
            ShaderStage.Pixel,
            pixelGuestWave32: !wave64);
        Assert.Equal(wave64, ClearsVccHi(pixelShader.Source));
        // S_CBRANCH_VCCZ reads VCC_LO alone in wave32.
        Assert.Equal(wave64, pixelShader.Source.Contains("(s106 | s107) == 0u", StringComparison.Ordinal));
    }

    // The VCC ballot store and a VCC_HI clear right after it (the entry's register
    // initialization also zeroes s107, so a bare match would not tell).
    private static bool ClearsVccHi(string source) =>
        System.Text.RegularExpressions.Regex.IsMatch(source, @"s106 = sharpemu_ballot\(vcc\);\s*s107 = 0u;");

    private static Gen5MslShader CompileGraphics(Gen5ShaderProgram program, ShaderStage stage,
        Gen5NggStageInfo? primitiveShader = null, bool pixelGuestWave32 = false)
    {
        var request = Gen5ComputeFixtures.RequestOrThrow(program, stage, primitiveShader: primitiveShader,
            pixelGuestWave32: pixelGuestWave32);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader!;
    }

    private static Gen5MslShader Compile(Gen5ShaderProgram program)
    {
        var userData = new uint[16];
        userData[4] = 0x200000;
        userData[5] = 4u << 16;
        userData[6] = Threads * 2;
        userData[7] = DescriptorConstants.IdentityDestinationSelect;
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Compute, waveLaneCount: 32, localSizeX: Threads,
            runtimeInputs: new ResourceRuntimeInputs { UserData = userData });
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader!;
    }

    private bool SkipWithoutMetalDevice()
    {
        if (MetalNative.IsAvailable) return false;
        Assert.False(
            OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1",
            "The required gate cannot run the wave32 VCC test without a Metal device.");
        output.WriteLine("No Metal device on this host; the wave32 VCC test skipped.");
        return true;
    }
}
