// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>DPP, SDWA and float-modifier fixtures: a decoded prologue sets registers, one IR
/// instruction under test is inserted before the store, and every lane's result is read
/// back. Execution needs the exclusive device gate; translation always runs.</summary>
public sealed class Gen5MslModifierTests(ITestOutputHelper output)
{
    private const uint QuadPermIdentity = 0xE4;
    private const uint Sentinel = 0xAAAA;

    // ---- DPP ----

    // DPP16 FI=1 only lets an EXEC-inactive source lane be read. A lane whose row_shr source is
    // out of the row reads zero (BC=1 writes it), not its own value (RDNA2 ISA table 91).
    [Fact]
    public void DppFetchInactiveStillReadsZeroOutOfRange()
    {
        var result = RunLanes(
            [.. LaneValue(0, 100), .. MovLiteral(1, 1000)],
            new Gen5ShaderInstruction(0x1000, Gen5ShaderEncoding.Vop2, "VAddI32", [],
                [Gen5Operand.Vector(0), Gen5Operand.Vector(1)], [Gen5Operand.Vector(2)],
                new Gen5DppControl(0x111, true, true, 0, 0, 0xF, 0xF)),
            resultRegister: 2);
        if (result is null) return;
        Assert.Equal(1000u, result[0]);
        Assert.Equal(1000u, result[16]);
        Assert.Equal(1100u, result[1]);
        Assert.Equal(1116u, result[17]);
    }

    // Each BANK_MASK bit gates four consecutive lanes (bit 24: lanes 0-3, 16-19, ...).
    [Theory]
    [InlineData(0x1u)]
    [InlineData(0x6u)]
    [InlineData(0xCu)]
    public void DppBankMaskGatesFourLaneGroups(uint bankMask)
    {
        var result = RunLanes(
            MovLiteral(1, Sentinel),
            new Gen5ShaderInstruction(0x1000, Gen5ShaderEncoding.Vop1, "VMovB32", [],
                [Gen5Operand.Vector(0)], [Gen5Operand.Vector(1)],
                new Gen5DppControl(QuadPermIdentity, false, false, 0, 0, bankMask, 0xF)),
            resultRegister: 1);
        if (result is null) return;
        for (var lane = 0; lane < 32; lane++)
        {
            var written = ((bankMask >> ((lane >> 2) & 3)) & 1) != 0;
            Assert.Equal(written ? (uint)lane : Sentinel, result[lane]);
        }
    }

    // A wave64 in a 128-thread workgroup runs as 32-wide simdgroups; the upper simdgroup of
    // each guest wave is lanes 32-63, which row_mask bits 2 and 3 govern.
    [Theory]
    [InlineData(0xCu)]
    [InlineData(0x3u)]
    public void DppRowMaskReachesTheUpperHalfOfAWave64InABigWorkgroup(uint rowMask)
    {
        var result = RunLanes(
            MovLiteral(1, Sentinel),
            new Gen5ShaderInstruction(0x1000, Gen5ShaderEncoding.Vop1, "VMovB32", [],
                [Gen5Operand.Vector(0)], [Gen5Operand.Vector(1)],
                new Gen5DppControl(QuadPermIdentity, false, false, 0, 0, 0xF, rowMask)),
            resultRegister: 1, lanes: 128, waveLaneCount: 64);
        if (result is null) return;
        for (var thread = 0; thread < 128; thread++)
        {
            var row = (thread & 63) >> 4;
            var written = ((rowMask >> row) & 1) != 0;
            Assert.Equal(written ? (uint)thread : Sentinel, result[thread]);
        }
    }

    // A V_CMPX lane whose DPP write is disabled keeps its EXEC bit; VCC is no part of it.
    [Fact]
    public void CmpxWithDppKeepsExecForLanesWhoseWriteIsDisabled()
    {
        // EXEC is all ones and VCC is clear. Row-start lanes have no row_shr source and
        // bound_ctrl is off, so they are the only lanes that keep EXEC after 1.0 < 0.0.
        var result = RunLanes(
            [.. MovLiteral(0x6, 7), .. MovLiteral(7, 0x3F800000), .. MovLiteral(8, 0)],
            new Gen5ShaderInstruction(0x1000, Gen5ShaderEncoding.Vopc, "VCmpxLtF32", [],
                [Gen5Operand.Vector(7), Gen5Operand.Vector(8)], [],
                new Gen5DppControl(0x111, false, false, 0, 0, 0xF, 0xF)),
            resultRegister: 6);
        if (result is null) return;
        for (var lane = 0; lane < 32; lane++)
        {
            Assert.Equal(lane % 16 == 0 ? 7u : 0u, result[lane]);
        }
    }

    // DPP abs/neg act on the sign bit of a V_CNDMASK_B32 source.
    [Fact]
    public void CndmaskDppAppliesSourceNegate()
    {
        var result = RunLanes(
            [.. MovLiteral(1, 0x3F800000), .. MovLiteral(2, 0x40000000)],
            new Gen5ShaderInstruction(0x1000, Gen5ShaderEncoding.Vop2, "VCndmaskB32", [],
                [Gen5Operand.Vector(1), Gen5Operand.Vector(2)], [Gen5Operand.Vector(6)],
                new Gen5DppControl(QuadPermIdentity, false, false, 0, 1, 0xF, 0xF)),
            resultRegister: 6);
        if (result is null) return;
        Assert.All(result, value => Assert.Equal(0xBF800000u, value));
    }

    // V_MOV_B32 takes floating-point source modifiers: neg flips the sign bit, abs clears it.
    [Theory]
    [InlineData(0u, 1u, 0x3F800000u, 0xBF800000u, false)]
    [InlineData(1u, 0u, 0xC0000000u, 0x40000000u, false)]
    [InlineData(0u, 1u, 0x3F800000u, 0xBF800000u, true)]
    [InlineData(1u, 0u, 0xC0000000u, 0x40000000u, true)]
    public void MovAppliesSourceAbsAndNegate(uint abs, uint neg, uint source, uint expected, bool dpp)
    {
        Gen5InstructionControl control = dpp
            ? new Gen5DppControl(QuadPermIdentity, false, false, abs, neg, 0xF, 0xF)
            : new Gen5Vop3Control(abs, neg, 0, false, 0, null);
        var result = RunLanes(
            MovLiteral(1, source),
            new Gen5ShaderInstruction(0x1000, dpp ? Gen5ShaderEncoding.Vop1 : Gen5ShaderEncoding.Vop3, "VMovB32", [],
                [Gen5Operand.Vector(1)], [Gen5Operand.Vector(6)], control),
            resultRegister: 6);
        if (result is null) return;
        Assert.All(result, value => Assert.Equal(expected, value));
    }

    // ---- float modifiers ----

    // An SDWA f16 result honours clamp and omod before the half is placed in dst_sel.
    [Theory]
    [InlineData("VMulF16", 0x4000u, 0x4200u, 0u, true, 0xABCD3C00u)]  // 2.0 * 3.0 clamped to 1.0
    [InlineData("VMulF16", 0x4000u, 0x4200u, 2u, false, 0xABCD4E00u)] // 2.0 * 3.0 * 4 = 24.0
    [InlineData("VAddF16", 0x3800u, 0x3800u, 1u, false, 0xABCD4000u)] // (0.5 + 0.5) * 2 = 2.0
    [InlineData("VAddF16", 0x3A00u, 0x3A00u, 0u, true, 0xABCD3C00u)]  // 0.75 + 0.75 clamped to 1.0
    public void SdwaHalfResultsApplyClampAndOutputModifier(
        string opcode, uint left, uint right, uint omod, bool clamp, uint expected)
    {
        var result = RunLanes(
            [.. MovLiteral(1, left), .. MovLiteral(2, right), .. MovLiteral(6, 0xABCD0000)],
            new Gen5ShaderInstruction(0x1000, Gen5ShaderEncoding.Vop2, opcode, [],
                [Gen5Operand.Vector(1), Gen5Operand.Vector(2)], [Gen5Operand.Vector(6)],
                new Gen5SdwaControl(4, 2, 4, 4, false, false, 0, 0, omod, clamp, null)),
            resultRegister: 6);
        if (result is null) return;
        Assert.All(result, value => Assert.Equal(expected, value));
    }

    // V_CVT_F16_F32 clamps and scales its f32 result before rounding to f16.
    [Theory]
    [InlineData(0x3FC00000u, 0u, true, 0x00003C00u)]  // 1.5 clamped to 1.0
    [InlineData(0xBE800000u, 0u, true, 0x00000000u)]  // -0.25 clamped to 0
    [InlineData(0x7FC00000u, 0u, true, 0x00000000u)]  // NaN clamped to 0
    [InlineData(0x3FC00000u, 2u, false, 0x00004600u)] // 1.5 * 4 = 6.0
    public void CvtF16F32AppliesClampAndOutputModifier(uint source, uint omod, bool clamp, uint expected)
    {
        var result = RunLanes(
            MovLiteral(1, source),
            new Gen5ShaderInstruction(0x1000, Gen5ShaderEncoding.Vop3, "VCvtF16F32", [],
                [Gen5Operand.Vector(1)], [Gen5Operand.Vector(6)],
                new Gen5Vop3Control(0, 0, omod, clamp, 0, null)),
            resultRegister: 6);
        if (result is null) return;
        Assert.All(result, value => Assert.Equal(expected, value));
    }

    // Output modifiers are ignored when MODE.IEEE is set (RDNA2 ISA 6.2.2).
    [Theory]
    [InlineData(true, 0x3FC00000u)]  // 1.5
    [InlineData(false, 0x40400000u)] // 1.5 * 2 = 3.0
    public void OutputModifierIsIgnoredInIeeeMode(bool ieee, uint expected)
    {
        var result = RunLanes(
            [.. MovLiteral(1, 0x3FC00000), .. MovLiteral(2, 0x3F800000)],
            new Gen5ShaderInstruction(0x1000, Gen5ShaderEncoding.Vop3, "VMulF32", [],
                [Gen5Operand.Vector(1), Gen5Operand.Vector(2)], [Gen5Operand.Vector(6)],
                new Gen5Vop3Control(0, 0, 1, false, 0, null)),
            resultRegister: 6, ieeeMode: ieee);
        if (result is null) return;
        Assert.All(result, value => Assert.Equal(expected, value));
    }

    // V_CMP_CLASS_F32 classifies the value after abs/neg, sign included.
    [Theory]
    [InlineData(0u, 1u, 0x7F800000u, 0x4u, 1u)]   // -(+inf) is -inf
    [InlineData(1u, 0u, 0xFF800000u, 0x200u, 1u)] // |-inf| is +inf
    [InlineData(1u, 0u, 0xFF800000u, 0x4u, 0u)]   // |-inf| is not -inf
    [InlineData(0u, 1u, 0x3F800000u, 0x8u, 1u)]   // -(1.0) is a negative normal
    public void CompareClassUsesTheSignAfterAbsAndNegate(uint abs, uint neg, uint source, uint classMask, uint expected)
    {
        var result = RunLanes(
            MovLiteral(1, source),
            [
                new Gen5ShaderInstruction(0x1000, Gen5ShaderEncoding.Vop3, "VCmpClassF32", [],
                    [Gen5Operand.Vector(1), Gen5Operand.Source(255, classMask)], [Gen5Operand.Scalar(0)],
                    new Gen5Vop3Control(abs, neg, 0, false, 0, null)),
                new Gen5ShaderInstruction(0x1001, Gen5ShaderEncoding.Vop3, "VCndmaskB32", [],
                    [Gen5Operand.Source(128), Gen5Operand.Source(129), Gen5Operand.Scalar(0)],
                    [Gen5Operand.Vector(6)], new Gen5Vop3Control(0, 0, 0, false, 0, null)),
            ],
            resultRegister: 6);
        if (result is null) return;
        Assert.All(result, value => Assert.Equal(expected, value));
    }

    // ---- VOP3P / packed f16 constants ----

    // A VOP3P literal is a pair of halves like a register: op_sel picks the half each lane reads.
    [Fact]
    public void PackedF16LiteralIsAPairOfHalves()
    {
        var result = RunLanes(
            MovLiteral(1, 0x3C003C00),
            new Gen5ShaderInstruction(0x1000, Gen5ShaderEncoding.Vop3p, "VPkMulF16", [],
                [Gen5Operand.Source(255, 0x40003C00), Gen5Operand.Vector(1)], [Gen5Operand.Vector(6)],
                new Gen5Vop3pControl(0, 3, 0, 0, false)),
            resultRegister: 6);
        if (result is null) return;
        Assert.All(result, value => Assert.Equal(0x40003C00u, value)); // lo 1*1, hi 2*1
    }

    // Before gfx11 a V_PK_FMAC_F16 float inline constant is an f16 in the low half and zero above.
    [Fact]
    public void PackedFmacReadsAFloatInlineConstantAsALowHalf()
    {
        var result = RunLanes(
            [.. MovLiteral(1, 0x40004000), .. MovLiteral(6, 0x3C003C00)],
            new Gen5ShaderInstruction(0x1000, Gen5ShaderEncoding.Vop2, "VPkFmacF16", [],
                [Gen5Operand.Source(242), Gen5Operand.Vector(1)], [Gen5Operand.Vector(6)], null),
            resultRegister: 6);
        if (result is null) return;
        // lo 1.0 * 2.0 + 1.0 = 3.0; hi 0 * 2.0 + 1.0 = 1.0
        Assert.All(result, value => Assert.Equal(0x3C004200u, value));
    }

    // ---- harness ----

    private static uint[] MovLiteral(uint register, uint value) =>
        [0x7E0002FFu | (register << 17), value];

    // v_add_nc_u32 vN, base, v0: the lane's own thread id plus a constant.
    private static uint[] LaneValue(uint register, uint baseValue) =>
        [0x4A0000FFu | (register << 17), baseValue];

    private uint[]? RunLanes(
        uint[] setup,
        Gen5ShaderInstruction instruction,
        uint resultRegister,
        uint lanes = 32,
        uint waveLaneCount = 32,
        bool ieeeMode = false) =>
        RunLanes(setup, [instruction], resultRegister, lanes, waveLaneCount, ieeeMode);

    private uint[]? RunLanes(
        uint[] setup,
        Gen5ShaderInstruction[] instructions,
        uint resultRegister,
        uint lanes = 32,
        uint waveLaneCount = 32,
        bool ieeeMode = false)
    {
        // v4 = lane * 4 is the store offset; the prologue runs before it so a setup that
        // overwrites v0 (the lane id) keeps its offset.
        uint[] words =
        [
            0x34080082,                                // v_lshlrev_b32 v4, 2, v0
            .. setup,
            0xE0701000, 0x80020004u | (resultRegister << 8), // buffer_store_dword vR, v4, s[8:11], 0 offen
            0xBF810000,                                // s_endpgm
        ];
        var backing = (int)lanes * 4;
        var fixture = new Gen5ComputeFixture("modifier", words, 8, backing);
        var program = Gen5ComputeFixtures.DecodeOrThrow(fixture);
        var list = program.Instructions.ToList();
        var store = list.FindIndex(static candidate => candidate.Control is Gen5BufferMemoryControl);
        list.InsertRange(store, instructions);
        var userData = new uint[16];
        userData[8] = 0x200000;
        userData[10] = (uint)backing;
        userData[11] = DescriptorConstants.IdentityDestinationSelect;
        var request = Gen5ComputeFixtures.RequestOrThrow(
            program with { Instructions = list }, ShaderStage.Compute, waveLaneCount, lanes,
            runtimeInputs: new ResourceRuntimeInputs { UserData = userData }, ieeeMode: ieeeMode);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        if (!RequireDevice()) return null;

        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out error), error + shader.Source);
        Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, new byte[backing],
            new byte[PushData.ByteSize], shader.ArgumentLayout!, lanes, out var result, out error), error);
        return Enumerable.Range(0, (int)lanes)
            .Select(lane => BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(lane * 4)))
            .ToArray();
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
}
