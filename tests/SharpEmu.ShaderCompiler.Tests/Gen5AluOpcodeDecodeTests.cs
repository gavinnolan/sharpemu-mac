// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

/// <summary>
/// Decoder coverage for the gfx10 ALU opcodes. Every row is (word0, word1, size in dwords,
/// expected opcode name); the encodings come from llvm-mc (-mcpu=gfx1011, wave64) and the
/// trailing comment is the assembly they were produced from.
/// </summary>
public sealed class Gen5AluOpcodeDecodeTests
{
    private const uint SEndpgm = 0xBF810000;

    private static Gen5ShaderInstruction DecodeFirst(uint word0, uint word1, int sizeDwords)
    {
        var words = new List<uint> { word0 };
        if (sizeDwords > 1)
        {
            words.Add(word1);
        }

        words.Add(SEndpgm);
        var bytes = new byte[words.Count * sizeof(uint)];
        for (var i = 0; i < words.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint)), words[i]);
        }

        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error), error);
        return program.Instructions[0];
    }

    private static void AssertDecodes(uint word0, uint word1, int sizeDwords, string expected)
    {
        var instruction = DecodeFirst(word0, word1, sizeDwords);
        Assert.Equal(expected, instruction.Opcode);
        Assert.Equal(sizeDwords, instruction.Words.Count);
    }

    // ---- VOP1 ----

    public static TheoryData<uint, uint, int, string> Vop1Cases = new()
    {
        { 0x7E000702, 0x00000000, 1, "VCvtI32F64" }, // v_cvt_i32_f64 v0, v[2:3]
        { 0x7E002102, 0x00000000, 1, "VCvtF64F32" }, // v_cvt_f64_f32 v[0:1], v2
        { 0x7E002B02, 0x00000000, 1, "VCvtU32F64" }, // v_cvt_u32_f64 v0, v[2:3]
        { 0x7E002F02, 0x00000000, 1, "VTruncF64" }, // v_trunc_f64 v[0:1], v[2:3]
        { 0x7E003102, 0x00000000, 1, "VCeilF64" }, // v_ceil_f64 v[0:1], v[2:3]
        { 0x7E003302, 0x00000000, 1, "VRndneF64" }, // v_rndne_f64 v[0:1], v[2:3]
        { 0x7E003502, 0x00000000, 1, "VFloorF64" }, // v_floor_f64 v[0:1], v[2:3]
        { 0x7E006302, 0x00000000, 1, "VRsqF64" }, // v_rsq_f64 v[0:1], v[2:3]
        { 0x7E007902, 0x00000000, 1, "VFrexpExpI32F64" }, // v_frexp_exp_i32_f64 v0, v[2:3]
        { 0x7E007B02, 0x00000000, 1, "VFrexpMantF64" }, // v_frexp_mant_f64 v[0:1], v[2:3]
        { 0x7E007D02, 0x00000000, 1, "VFractF64" }, // v_fract_f64 v[0:1], v[2:3]
        { 0x7E007F01, 0x00000000, 1, "VFrexpExpI32F32" }, // v_frexp_exp_i32_f32 v0, v1
        { 0x7E008101, 0x00000000, 1, "VFrexpMantF32" }, // v_frexp_mant_f32 v0, v1
        { 0x7E00A101, 0x00000000, 1, "VCvtF16U16" }, // v_cvt_f16_u16 v0, v1
        { 0x7E00A301, 0x00000000, 1, "VCvtF16I16" }, // v_cvt_f16_i16 v0, v1
        { 0x7E00A701, 0x00000000, 1, "VCvtI16F16" }, // v_cvt_i16_f16 v0, v1
        { 0x7E00B301, 0x00000000, 1, "VFrexpMantF16" }, // v_frexp_mant_f16 v0, v1
        { 0x7E00B501, 0x00000000, 1, "VFrexpExpI16F16" }, // v_frexp_exp_i16_f16 v0, v1
        { 0x7E00C501, 0x00000000, 1, "VSatPkU8I16" }, // v_sat_pk_u8_i16 v0, v1
        { 0x7E00C701, 0x00000000, 1, "VCvtNormI16F16" }, // v_cvt_norm_i16_f16 v0, v1
        { 0x7E00C901, 0x00000000, 1, "VCvtNormU16F16" }, // v_cvt_norm_u16_f16 v0, v1
        { 0x7E00CB01, 0x00000000, 1, "VSwapB32" }, // v_swap_b32 v0, v1
        { 0x7E00D101, 0x00000000, 1, "VSwaprelB32" }, // v_swaprel_b32 v0, v1
        { 0x7E003600, 0x00000000, 1, "VPipeflush" }, // v_pipeflush
        { 0x7E008200, 0x00000000, 1, "VClrexcp" }, // v_clrexcp
    };

    [Theory]
    [MemberData(nameof(Vop1Cases))]
    public void Vop1DecodesToExpectedOpcode(uint word0, uint word1, int sizeDwords, string expected) =>
        AssertDecodes(word0, word1, sizeDwords, expected);

    // ---- VOP2 ----

    public static TheoryData<uint, uint, int, string> Vop2Cases = new()
    {
        { 0x0C000501, 0x00000000, 1, "VMacLegacyF32" }, // v_mac_legacy_f32 v0, v1, v2
        { 0x0E000501, 0x00000000, 1, "VMulLegacyF32" }, // v_mul_legacy_f32 v0, v1, v2
        { 0x14000501, 0x00000000, 1, "VMulHiI32I24" }, // v_mul_hi_i32_i24 v0, v1, v2
        { 0x1A000501, 0x00000000, 1, "VDot4cI32I8" }, // v_dot4c_i32_i8 v0, v1, v2
        { 0x6C000501, 0x00000000, 1, "VFmacF16" }, // v_fmac_f16 v0, v1, v2
        { 0x76000501, 0x00000000, 1, "VLdexpF16" }, // v_ldexp_f16 v0, v1, v2
    };

    [Theory]
    [MemberData(nameof(Vop2Cases))]
    public void Vop2DecodesToExpectedOpcode(uint word0, uint word1, int sizeDwords, string expected) =>
        AssertDecodes(word0, word1, sizeDwords, expected);

    // ---- VOPC ----

    public static TheoryData<uint, uint, int, string> VopcCases = new()
    {
        { 0x7C400500, 0x00000000, 1, "VCmpFF64" }, // v_cmp_f_f64 vcc, v[0:1], v[2:3]
        { 0x7C420500, 0x00000000, 1, "VCmpLtF64" }, // v_cmp_lt_f64 vcc, v[0:1], v[2:3]
        { 0x7C440500, 0x00000000, 1, "VCmpEqF64" }, // v_cmp_eq_f64 vcc, v[0:1], v[2:3]
        { 0x7C460500, 0x00000000, 1, "VCmpLeF64" }, // v_cmp_le_f64 vcc, v[0:1], v[2:3]
        { 0x7C480500, 0x00000000, 1, "VCmpGtF64" }, // v_cmp_gt_f64 vcc, v[0:1], v[2:3]
        { 0x7C4A0500, 0x00000000, 1, "VCmpLgF64" }, // v_cmp_lg_f64 vcc, v[0:1], v[2:3]
        { 0x7C4C0500, 0x00000000, 1, "VCmpGeF64" }, // v_cmp_ge_f64 vcc, v[0:1], v[2:3]
        { 0x7C4E0500, 0x00000000, 1, "VCmpOF64" }, // v_cmp_o_f64 vcc, v[0:1], v[2:3]
        { 0x7C500500, 0x00000000, 1, "VCmpUF64" }, // v_cmp_u_f64 vcc, v[0:1], v[2:3]
        { 0x7C520500, 0x00000000, 1, "VCmpNgeF64" }, // v_cmp_nge_f64 vcc, v[0:1], v[2:3]
        { 0x7C540500, 0x00000000, 1, "VCmpNlgF64" }, // v_cmp_nlg_f64 vcc, v[0:1], v[2:3]
        { 0x7C560500, 0x00000000, 1, "VCmpNgtF64" }, // v_cmp_ngt_f64 vcc, v[0:1], v[2:3]
        { 0x7C580500, 0x00000000, 1, "VCmpNleF64" }, // v_cmp_nle_f64 vcc, v[0:1], v[2:3]
        { 0x7C5A0500, 0x00000000, 1, "VCmpNeqF64" }, // v_cmp_neq_f64 vcc, v[0:1], v[2:3]
        { 0x7C5C0500, 0x00000000, 1, "VCmpNltF64" }, // v_cmp_nlt_f64 vcc, v[0:1], v[2:3]
        { 0x7C5E0500, 0x00000000, 1, "VCmpTruF64" }, // v_cmp_tru_f64 vcc, v[0:1], v[2:3]
        { 0x7C600500, 0x00000000, 1, "VCmpxFF64" }, // v_cmpx_f_f64 v[0:1], v[2:3]
        { 0x7C620500, 0x00000000, 1, "VCmpxLtF64" }, // v_cmpx_lt_f64 v[0:1], v[2:3]
        { 0x7C640500, 0x00000000, 1, "VCmpxEqF64" }, // v_cmpx_eq_f64 v[0:1], v[2:3]
        { 0x7C660500, 0x00000000, 1, "VCmpxLeF64" }, // v_cmpx_le_f64 v[0:1], v[2:3]
        { 0x7C680500, 0x00000000, 1, "VCmpxGtF64" }, // v_cmpx_gt_f64 v[0:1], v[2:3]
        { 0x7C6A0500, 0x00000000, 1, "VCmpxLgF64" }, // v_cmpx_lg_f64 v[0:1], v[2:3]
        { 0x7C6C0500, 0x00000000, 1, "VCmpxGeF64" }, // v_cmpx_ge_f64 v[0:1], v[2:3]
        { 0x7C6E0500, 0x00000000, 1, "VCmpxOF64" }, // v_cmpx_o_f64 v[0:1], v[2:3]
        { 0x7C700500, 0x00000000, 1, "VCmpxUF64" }, // v_cmpx_u_f64 v[0:1], v[2:3]
        { 0x7C720500, 0x00000000, 1, "VCmpxNgeF64" }, // v_cmpx_nge_f64 v[0:1], v[2:3]
        { 0x7C740500, 0x00000000, 1, "VCmpxNlgF64" }, // v_cmpx_nlg_f64 v[0:1], v[2:3]
        { 0x7C760500, 0x00000000, 1, "VCmpxNgtF64" }, // v_cmpx_ngt_f64 v[0:1], v[2:3]
        { 0x7C780500, 0x00000000, 1, "VCmpxNleF64" }, // v_cmpx_nle_f64 v[0:1], v[2:3]
        { 0x7C7A0500, 0x00000000, 1, "VCmpxNeqF64" }, // v_cmpx_neq_f64 v[0:1], v[2:3]
        { 0x7C7C0500, 0x00000000, 1, "VCmpxNltF64" }, // v_cmpx_nlt_f64 v[0:1], v[2:3]
        { 0x7C7E0500, 0x00000000, 1, "VCmpxTruF64" }, // v_cmpx_tru_f64 v[0:1], v[2:3]
        { 0x7D1E0501, 0x00000000, 1, "VCmpClassF16" }, // v_cmp_class_f16 vcc, v1, v2
        { 0x7D3E0501, 0x00000000, 1, "VCmpxClassF16" }, // v_cmpx_class_f16 v1, v2
        { 0x7D500500, 0x00000000, 1, "VCmpClassF64" }, // v_cmp_class_f64 vcc, v[0:1], v2
        { 0x7D700500, 0x00000000, 1, "VCmpxClassF64" }, // v_cmpx_class_f64 v[0:1], v2
    };

    [Theory]
    [MemberData(nameof(VopcCases))]
    public void VopcDecodesToExpectedOpcode(uint word0, uint word1, int sizeDwords, string expected) =>
        AssertDecodes(word0, word1, sizeDwords, expected);

    // ---- VOPC_E64 ----

    public static TheoryData<uint, uint, int, string> VopcE64Cases = new()
    {
        { 0xD4200000, 0x00020500, 2, "VCmpFF64" }, // v_cmp_f_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD4210000, 0x00020500, 2, "VCmpLtF64" }, // v_cmp_lt_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD4220000, 0x00020500, 2, "VCmpEqF64" }, // v_cmp_eq_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD4230000, 0x00020500, 2, "VCmpLeF64" }, // v_cmp_le_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD4240000, 0x00020500, 2, "VCmpGtF64" }, // v_cmp_gt_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD4250000, 0x00020500, 2, "VCmpLgF64" }, // v_cmp_lg_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD4260000, 0x00020500, 2, "VCmpGeF64" }, // v_cmp_ge_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD4270000, 0x00020500, 2, "VCmpOF64" }, // v_cmp_o_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD4280000, 0x00020500, 2, "VCmpUF64" }, // v_cmp_u_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD4290000, 0x00020500, 2, "VCmpNgeF64" }, // v_cmp_nge_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD42A0000, 0x00020500, 2, "VCmpNlgF64" }, // v_cmp_nlg_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD42B0000, 0x00020500, 2, "VCmpNgtF64" }, // v_cmp_ngt_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD42C0000, 0x00020500, 2, "VCmpNleF64" }, // v_cmp_nle_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD42D0000, 0x00020500, 2, "VCmpNeqF64" }, // v_cmp_neq_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD42E0000, 0x00020500, 2, "VCmpNltF64" }, // v_cmp_nlt_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD42F0000, 0x00020500, 2, "VCmpTruF64" }, // v_cmp_tru_f64_e64 s[0:1], v[0:1], v[2:3]
        { 0xD430007E, 0x00020500, 2, "VCmpxFF64" }, // v_cmpx_f_f64_e64 v[0:1], v[2:3]
        { 0xD431007E, 0x00020500, 2, "VCmpxLtF64" }, // v_cmpx_lt_f64_e64 v[0:1], v[2:3]
        { 0xD432007E, 0x00020500, 2, "VCmpxEqF64" }, // v_cmpx_eq_f64_e64 v[0:1], v[2:3]
        { 0xD433007E, 0x00020500, 2, "VCmpxLeF64" }, // v_cmpx_le_f64_e64 v[0:1], v[2:3]
        { 0xD434007E, 0x00020500, 2, "VCmpxGtF64" }, // v_cmpx_gt_f64_e64 v[0:1], v[2:3]
        { 0xD435007E, 0x00020500, 2, "VCmpxLgF64" }, // v_cmpx_lg_f64_e64 v[0:1], v[2:3]
        { 0xD436007E, 0x00020500, 2, "VCmpxGeF64" }, // v_cmpx_ge_f64_e64 v[0:1], v[2:3]
        { 0xD437007E, 0x00020500, 2, "VCmpxOF64" }, // v_cmpx_o_f64_e64 v[0:1], v[2:3]
        { 0xD438007E, 0x00020500, 2, "VCmpxUF64" }, // v_cmpx_u_f64_e64 v[0:1], v[2:3]
        { 0xD439007E, 0x00020500, 2, "VCmpxNgeF64" }, // v_cmpx_nge_f64_e64 v[0:1], v[2:3]
        { 0xD43A007E, 0x00020500, 2, "VCmpxNlgF64" }, // v_cmpx_nlg_f64_e64 v[0:1], v[2:3]
        { 0xD43B007E, 0x00020500, 2, "VCmpxNgtF64" }, // v_cmpx_ngt_f64_e64 v[0:1], v[2:3]
        { 0xD43C007E, 0x00020500, 2, "VCmpxNleF64" }, // v_cmpx_nle_f64_e64 v[0:1], v[2:3]
        { 0xD43D007E, 0x00020500, 2, "VCmpxNeqF64" }, // v_cmpx_neq_f64_e64 v[0:1], v[2:3]
        { 0xD43E007E, 0x00020500, 2, "VCmpxNltF64" }, // v_cmpx_nlt_f64_e64 v[0:1], v[2:3]
        { 0xD43F007E, 0x00020500, 2, "VCmpxTruF64" }, // v_cmpx_tru_f64_e64 v[0:1], v[2:3]
        { 0xD48F0000, 0x00020501, 2, "VCmpClassF16" }, // v_cmp_class_f16_e64 s[0:1], v1, v2
        { 0xD49F007E, 0x00020501, 2, "VCmpxClassF16" }, // v_cmpx_class_f16_e64 v1, v2
        { 0xD4A80000, 0x00020500, 2, "VCmpClassF64" }, // v_cmp_class_f64_e64 s[0:1], v[0:1], v2
        { 0xD4B8007E, 0x00020500, 2, "VCmpxClassF64" }, // v_cmpx_class_f64_e64 v[0:1], v2
    };

    [Theory]
    [MemberData(nameof(VopcE64Cases))]
    public void VopcE64DecodesToExpectedOpcode(uint word0, uint word1, int sizeDwords, string expected) =>
        AssertDecodes(word0, word1, sizeDwords, expected);

    // ---- VOP3B ----

    public static TheoryData<uint, uint, int, string> Vop3bCases = new()
    {
        { 0xD56D0400, 0x040E0501, 2, "VDivScaleF32" }, // v_div_scale_f32 v0, s[4:5], v1, v2, v3
        { 0xD56E0400, 0x041A0902, 2, "VDivScaleF64" }, // v_div_scale_f64 v[0:1], s[4:5], v[2:3], v[4:5], v[6:7]
        { 0xD5770400, 0x04120702, 2, "VMadI64I32" }, // v_mad_i64_i32 v[0:1], s[4:5], v2, v3, v[4:5]
    };

    [Theory]
    [MemberData(nameof(Vop3bCases))]
    public void Vop3bDecodesToExpectedOpcode(uint word0, uint word1, int sizeDwords, string expected) =>
        AssertDecodes(word0, word1, sizeDwords, expected);

    // ---- VOP3 ----

    public static TheoryData<uint, uint, int, string> Vop3Cases = new()
    {
        { 0xD5400000, 0x040E0501, 2, "VMadLegacyF32" }, // v_mad_legacy_f32 v0, v1, v2, v3
        { 0xD54D0000, 0x040E0501, 2, "VLerpU8" }, // v_lerp_u8 v0, v1, v2, v3
        { 0xD5500000, 0x040E0501, 2, "VMullitF32" }, // v_mullit_f32 v0, v1, v2, v3
        { 0xD55F0000, 0x040E0501, 2, "VDivFixupF32" }, // v_div_fixup_f32 v0, v1, v2, v3
        { 0xD5600000, 0x041A0902, 2, "VDivFixupF64" }, // v_div_fixup_f64 v[0:1], v[2:3], v[4:5], v[6:7]
        { 0xD75F0000, 0x040E0501, 2, "VDivFixupF16" }, // v_div_fixup_f16 v0, v1, v2, v3
        { 0xD5640000, 0x00020902, 2, "VAddF64" }, // v_add_f64 v[0:1], v[2:3], v[4:5]
        { 0xD5660000, 0x00020902, 2, "VMinF64" }, // v_min_f64 v[0:1], v[2:3], v[4:5]
        { 0xD5670000, 0x00020902, 2, "VMaxF64" }, // v_max_f64 v[0:1], v[2:3], v[4:5]
        { 0xD5680000, 0x00020902, 2, "VLdexpF64" }, // v_ldexp_f64 v[0:1], v[2:3], v4
        { 0xD56F0000, 0x040E0501, 2, "VDivFmasF32" }, // v_div_fmas_f32 v0, v1, v2, v3
        { 0xD5700000, 0x041A0902, 2, "VDivFmasF64" }, // v_div_fmas_f64 v[0:1], v[2:3], v[4:5], v[6:7]
        { 0xD5710000, 0x040E0501, 2, "VMsadU8" }, // v_msad_u8 v0, v1, v2, v3
        { 0xD5720000, 0x041A0902, 2, "VQsadPkU16U8" }, // v_qsad_pk_u16_u8 v[0:1], v[2:3], v4, v[6:7]
        { 0xD5730000, 0x041A0902, 2, "VMqsadPkU16U8" }, // v_mqsad_pk_u16_u8 v[0:1], v[2:3], v4, v[6:7]
        { 0xD5750000, 0x041A0902, 2, "VMqsadU32U8" }, // v_mqsad_u32_u8 v[0:3], v[2:3], v4, v[6:9]
        { 0xD5740000, 0x00020902, 2, "VTrigPreopF64" }, // v_trig_preop_f64 v[0:1], v[2:3], v4
        { 0xD7010000, 0x00020902, 2, "VAshrrevI64" }, // v_ashrrev_i64 v[0:1], v2, v[4:5]
        { 0xD7050000, 0x00020501, 2, "VMulLoU16" }, // v_mul_lo_u16 v0, v1, v2
        { 0xD7110000, 0x00020501, 2, "VPackB32F16" }, // v_pack_b32_f16 v0, v1, v2
        { 0xD7120000, 0x00020501, 2, "VCvtPknormI16F16" }, // v_cvt_pknorm_i16_f16 v0, v1, v2
        { 0xD7130000, 0x00020501, 2, "VCvtPknormU16F16" }, // v_cvt_pknorm_u16_f16 v0, v1, v2
        { 0xD7400000, 0x040E0501, 2, "VMadU16" }, // v_mad_u16 v0, v1, v2, v3
        { 0xD75E0000, 0x040E0501, 2, "VMadI16" }, // v_mad_i16 v0, v1, v2, v3
        { 0xD7440000, 0x040E0501, 2, "VPermB32" }, // v_perm_b32 v0, v1, v2, v3
        { 0xD7520000, 0x040E0501, 2, "VMin3I16" }, // v_min3_i16 v0, v1, v2, v3
        { 0xD7530000, 0x040E0501, 2, "VMin3U16" }, // v_min3_u16 v0, v1, v2, v3
        { 0xD7550000, 0x040E0501, 2, "VMax3I16" }, // v_max3_i16 v0, v1, v2, v3
        { 0xD7560000, 0x040E0501, 2, "VMax3U16" }, // v_max3_u16 v0, v1, v2, v3
        { 0xD7580000, 0x040E0501, 2, "VMed3I16" }, // v_med3_i16 v0, v1, v2, v3
        { 0xD7590000, 0x040E0501, 2, "VMed3U16" }, // v_med3_u16 v0, v1, v2, v3
        { 0xD7750000, 0x040E0501, 2, "VMadI32I16" }, // v_mad_i32_i16 v0, v1, v2, v3
        { 0xD77F0000, 0x00020501, 2, "VAddI32" }, // v_add_nc_i32 v0, v1, v2
        { 0xD7760000, 0x00020501, 2, "VSubI32" }, // v_sub_nc_i32 v0, v1, v2
        { 0xD5250000, 0x00020501, 2, "VAddU32" }, // v_add_nc_u32_e64 v0, v1, v2
        { 0xD5260000, 0x00020501, 2, "VSubU32" }, // v_sub_nc_u32_e64 v0, v1, v2
        { 0xD5270000, 0x00020501, 2, "VSubrevU32" }, // v_subrev_nc_u32_e64 v0, v1, v2
    };

    [Theory]
    [MemberData(nameof(Vop3Cases))]
    public void Vop3DecodesToExpectedOpcode(uint word0, uint word1, int sizeDwords, string expected) =>
        AssertDecodes(word0, word1, sizeDwords, expected);

    // ---- VOP2_E64 ----

    public static TheoryData<uint, uint, int, string> Vop2E64Cases = new()
    {
        { 0xD5050000, 0x00020501, 2, "VSubrevF32" }, // v_subrev_f32_e64 v0, v1, v2
        { 0xD5060000, 0x00020501, 2, "VMacLegacyF32" }, // v_mac_legacy_f32_e64 v0, v1, v2
        { 0xD5070000, 0x00020501, 2, "VMulLegacyF32" }, // v_mul_legacy_f32_e64 v0, v1, v2
        { 0xD50A0000, 0x00020501, 2, "VMulHiI32I24" }, // v_mul_hi_i32_i24_e64 v0, v1, v2
        { 0xD50B0000, 0x00020501, 2, "VMulU32U24" }, // v_mul_u32_u24_e64 v0, v1, v2
        { 0xD50C0000, 0x00020501, 2, "VMulHiU32U24" }, // v_mul_hi_u32_u24_e64 v0, v1, v2
        { 0xD5110000, 0x00020501, 2, "VMinI32" }, // v_min_i32_e64 v0, v1, v2
        { 0xD5120000, 0x00020501, 2, "VMaxI32" }, // v_max_i32_e64 v0, v1, v2
        { 0xD5130000, 0x00020501, 2, "VMinU32" }, // v_min_u32_e64 v0, v1, v2
        { 0xD5140000, 0x00020501, 2, "VMaxU32" }, // v_max_u32_e64 v0, v1, v2
        { 0xD5160000, 0x00020501, 2, "VLshrrevB32" }, // v_lshrrev_b32_e64 v0, v1, v2
        { 0xD5180000, 0x00020501, 2, "VAshrrevI32" }, // v_ashrrev_i32_e64 v0, v1, v2
        { 0xD51A0000, 0x00020501, 2, "VLshlrevB32" }, // v_lshlrev_b32_e64 v0, v1, v2
        { 0xD51B0000, 0x00020501, 2, "VAndB32" }, // v_and_b32_e64 v0, v1, v2
        { 0xD51C0000, 0x00020501, 2, "VOrB32" }, // v_or_b32_e64 v0, v1, v2
        { 0xD51D0000, 0x00020501, 2, "VXorB32" }, // v_xor_b32_e64 v0, v1, v2
        { 0xD51E0000, 0x00020501, 2, "VXnorB32" }, // v_xnor_b32_e64 v0, v1, v2
        { 0xD5320000, 0x00020501, 2, "VAddF16" }, // v_add_f16_e64 v0, v1, v2
        { 0xD5330000, 0x00020501, 2, "VSubF16" }, // v_sub_f16_e64 v0, v1, v2
        { 0xD5340000, 0x00020501, 2, "VSubrevF16" }, // v_subrev_f16_e64 v0, v1, v2
        { 0xD5350000, 0x00020501, 2, "VMulF16" }, // v_mul_f16_e64 v0, v1, v2
        { 0xD5360000, 0x00020501, 2, "VFmacF16" }, // v_fmac_f16_e64 v0, v1, v2
        { 0xD5390000, 0x00020501, 2, "VMaxF16" }, // v_max_f16_e64 v0, v1, v2
        { 0xD53A0000, 0x00020501, 2, "VMinF16" }, // v_min_f16_e64 v0, v1, v2
        { 0xD53B0000, 0x00020501, 2, "VLdexpF16" }, // v_ldexp_f16_e64 v0, v1, v2
    };

    [Theory]
    [MemberData(nameof(Vop2E64Cases))]
    public void Vop2E64DecodesToExpectedOpcode(uint word0, uint word1, int sizeDwords, string expected) =>
        AssertDecodes(word0, word1, sizeDwords, expected);

    // ---- VOP1_E64 ----

    public static TheoryData<uint, uint, int, string> Vop1E64Cases = new()
    {
        { 0xD5810000, 0x00000101, 2, "VMovB32" }, // v_mov_b32_e64 v0, v1
        { 0xD5830000, 0x00000102, 2, "VCvtI32F64" }, // v_cvt_i32_f64_e64 v0, v[2:3]
        { 0xD5850000, 0x00000101, 2, "VCvtF32I32" }, // v_cvt_f32_i32_e64 v0, v1
        { 0xD5860000, 0x00000101, 2, "VCvtF32U32" }, // v_cvt_f32_u32_e64 v0, v1
        { 0xD5870000, 0x00000101, 2, "VCvtU32F32" }, // v_cvt_u32_f32_e64 v0, v1
        { 0xD5880000, 0x00000101, 2, "VCvtI32F32" }, // v_cvt_i32_f32_e64 v0, v1
        { 0xD58A0000, 0x00000101, 2, "VCvtF16F32" }, // v_cvt_f16_f32_e64 v0, v1
        { 0xD58B0000, 0x00000101, 2, "VCvtF32F16" }, // v_cvt_f32_f16_e64 v0, v1
        { 0xD58C0000, 0x00000101, 2, "VCvtRpiI32F32" }, // v_cvt_rpi_i32_f32_e64 v0, v1
        { 0xD58D0000, 0x00000101, 2, "VCvtFlrI32F32" }, // v_cvt_flr_i32_f32_e64 v0, v1
        { 0xD58E0000, 0x00000101, 2, "VCvtOffF32I4" }, // v_cvt_off_f32_i4_e64 v0, v1
        { 0xD5900000, 0x00000102, 2, "VCvtF64F32" }, // v_cvt_f64_f32_e64 v[0:1], v2
        { 0xD5910000, 0x00000101, 2, "VCvtF32Ubyte0" }, // v_cvt_f32_ubyte0_e64 v0, v1
        { 0xD5920000, 0x00000101, 2, "VCvtF32Ubyte1" }, // v_cvt_f32_ubyte1_e64 v0, v1
        { 0xD5930000, 0x00000101, 2, "VCvtF32Ubyte2" }, // v_cvt_f32_ubyte2_e64 v0, v1
        { 0xD5940000, 0x00000101, 2, "VCvtF32Ubyte3" }, // v_cvt_f32_ubyte3_e64 v0, v1
        { 0xD5950000, 0x00000102, 2, "VCvtU32F64" }, // v_cvt_u32_f64_e64 v0, v[2:3]
        { 0xD5970000, 0x00000102, 2, "VTruncF64" }, // v_trunc_f64_e64 v[0:1], v[2:3]
        { 0xD5980000, 0x00000102, 2, "VCeilF64" }, // v_ceil_f64_e64 v[0:1], v[2:3]
        { 0xD5990000, 0x00000102, 2, "VRndneF64" }, // v_rndne_f64_e64 v[0:1], v[2:3]
        { 0xD59A0000, 0x00000102, 2, "VFloorF64" }, // v_floor_f64_e64 v[0:1], v[2:3]
        { 0xD5A00000, 0x00000101, 2, "VFractF32" }, // v_fract_f32_e64 v0, v1
        { 0xD5A10000, 0x00000101, 2, "VTruncF32" }, // v_trunc_f32_e64 v0, v1
        { 0xD5A20000, 0x00000101, 2, "VCeilF32" }, // v_ceil_f32_e64 v0, v1
        { 0xD5A30000, 0x00000101, 2, "VRndneF32" }, // v_rndne_f32_e64 v0, v1
        { 0xD5A40000, 0x00000101, 2, "VFloorF32" }, // v_floor_f32_e64 v0, v1
        { 0xD5A50000, 0x00000101, 2, "VExpF32" }, // v_exp_f32_e64 v0, v1
        { 0xD5A70000, 0x00000101, 2, "VLogF32" }, // v_log_f32_e64 v0, v1
        { 0xD5AA0000, 0x00000101, 2, "VRcpF32" }, // v_rcp_f32_e64 v0, v1
        { 0xD5AB0000, 0x00000101, 2, "VRcpIflagF32" }, // v_rcp_iflag_f32_e64 v0, v1
        { 0xD5AE0000, 0x00000101, 2, "VRsqF32" }, // v_rsq_f32_e64 v0, v1
        { 0xD5B10000, 0x00000102, 2, "VRsqF64" }, // v_rsq_f64_e64 v[0:1], v[2:3]
        { 0xD5B30000, 0x00000101, 2, "VSqrtF32" }, // v_sqrt_f32_e64 v0, v1
        { 0xD5B50000, 0x00000101, 2, "VSinF32" }, // v_sin_f32_e64 v0, v1
        { 0xD5B60000, 0x00000101, 2, "VCosF32" }, // v_cos_f32_e64 v0, v1
        { 0xD5B70000, 0x00000101, 2, "VNotB32" }, // v_not_b32_e64 v0, v1
        { 0xD5B80000, 0x00000101, 2, "VBfrevB32" }, // v_bfrev_b32_e64 v0, v1
        { 0xD5B90000, 0x00000101, 2, "VFfbhU32" }, // v_ffbh_u32_e64 v0, v1
        { 0xD5BA0000, 0x00000101, 2, "VFfblB32" }, // v_ffbl_b32_e64 v0, v1
        { 0xD5BB0000, 0x00000101, 2, "VFfbhI32" }, // v_ffbh_i32_e64 v0, v1
        { 0xD5BC0000, 0x00000102, 2, "VFrexpExpI32F64" }, // v_frexp_exp_i32_f64_e64 v0, v[2:3]
        { 0xD5BD0000, 0x00000102, 2, "VFrexpMantF64" }, // v_frexp_mant_f64_e64 v[0:1], v[2:3]
        { 0xD5BE0000, 0x00000102, 2, "VFractF64" }, // v_fract_f64_e64 v[0:1], v[2:3]
        { 0xD5BF0000, 0x00000101, 2, "VFrexpExpI32F32" }, // v_frexp_exp_i32_f32_e64 v0, v1
        { 0xD5C00000, 0x00000101, 2, "VFrexpMantF32" }, // v_frexp_mant_f32_e64 v0, v1
        { 0xD5C20000, 0x00000101, 2, "VMovreldB32" }, // v_movreld_b32_e64 v0, v1
        { 0xD5C30000, 0x00000101, 2, "VMovrelsB32" }, // v_movrels_b32_e64 v0, v1
        { 0xD5C40000, 0x00000101, 2, "VMovrelsdB32" }, // v_movrelsd_b32_e64 v0, v1
        { 0xD5C80000, 0x00000101, 2, "VMovrelsd2B32" }, // v_movrelsd_2_b32_e64 v0, v1
        { 0xD5D00000, 0x00000101, 2, "VCvtF16U16" }, // v_cvt_f16_u16_e64 v0, v1
        { 0xD5D10000, 0x00000101, 2, "VCvtF16I16" }, // v_cvt_f16_i16_e64 v0, v1
        { 0xD5D30000, 0x00000101, 2, "VCvtI16F16" }, // v_cvt_i16_f16_e64 v0, v1
        { 0xD5D90000, 0x00000101, 2, "VFrexpMantF16" }, // v_frexp_mant_f16_e64 v0, v1
        { 0xD5DA0000, 0x00000101, 2, "VFrexpExpI16F16" }, // v_frexp_exp_i16_f16_e64 v0, v1
        { 0xD5E20000, 0x00000101, 2, "VSatPkU8I16" }, // v_sat_pk_u8_i16_e64 v0, v1
        { 0xD5E30000, 0x00000101, 2, "VCvtNormI16F16" }, // v_cvt_norm_i16_f16_e64 v0, v1
        { 0xD5E40000, 0x00000101, 2, "VCvtNormU16F16" }, // v_cvt_norm_u16_f16_e64 v0, v1
    };

    [Theory]
    [MemberData(nameof(Vop1E64Cases))]
    public void Vop1E64DecodesToExpectedOpcode(uint word0, uint word1, int sizeDwords, string expected) =>
        AssertDecodes(word0, word1, sizeDwords, expected);

    // ---- VOP3P ----

    public static TheoryData<uint, uint, int, string> Vop3pCases = new()
    {
        { 0xCC004000, 0x1C0E0501, 2, "VPkMadI16" }, // v_pk_mad_i16 v0, v1, v2, v3
        { 0xCC014000, 0x18020501, 2, "VPkMulLoU16" }, // v_pk_mul_lo_u16 v0, v1, v2
        { 0xCC044000, 0x18020501, 2, "VPkLshlrevB16" }, // v_pk_lshlrev_b16 v0, v1, v2
        { 0xCC054000, 0x18020501, 2, "VPkLshrrevB16" }, // v_pk_lshrrev_b16 v0, v1, v2
        { 0xCC064000, 0x18020501, 2, "VPkAshrrevI16" }, // v_pk_ashrrev_i16 v0, v1, v2
        { 0xCC074000, 0x18020501, 2, "VPkMaxI16" }, // v_pk_max_i16 v0, v1, v2
        { 0xCC084000, 0x18020501, 2, "VPkMinI16" }, // v_pk_min_i16 v0, v1, v2
        { 0xCC094000, 0x1C0E0501, 2, "VPkMadU16" }, // v_pk_mad_u16 v0, v1, v2, v3
        { 0xCC0A4000, 0x18020501, 2, "VPkAddU16" }, // v_pk_add_u16 v0, v1, v2
        { 0xCC0B4000, 0x18020501, 2, "VPkSubU16" }, // v_pk_sub_u16 v0, v1, v2
        { 0xCC0C4000, 0x18020501, 2, "VPkMaxU16" }, // v_pk_max_u16 v0, v1, v2
        { 0xCC0D4000, 0x18020501, 2, "VPkMinU16" }, // v_pk_min_u16 v0, v1, v2
        { 0xCC134000, 0x1C0E0501, 2, "VDot2F32F16" }, // v_dot2_f32_f16 v0, v1, v2, v3
        { 0xCC144000, 0x1C0E0501, 2, "VDot2I32I16" }, // v_dot2_i32_i16 v0, v1, v2, v3
        { 0xCC154000, 0x1C0E0501, 2, "VDot2U32U16" }, // v_dot2_u32_u16 v0, v1, v2, v3
        { 0xCC164000, 0x1C0E0501, 2, "VDot4I32I8" }, // v_dot4_i32_i8 v0, v1, v2, v3
        { 0xCC174000, 0x1C0E0501, 2, "VDot4U32U8" }, // v_dot4_u32_u8 v0, v1, v2, v3
        { 0xCC184000, 0x1C0E0501, 2, "VDot8I32I4" }, // v_dot8_i32_i4 v0, v1, v2, v3
        { 0xCC194000, 0x1C0E0501, 2, "VDot8U32U4" }, // v_dot8_u32_u4 v0, v1, v2, v3
    };

    [Theory]
    [MemberData(nameof(Vop3pCases))]
    public void Vop3pDecodesToExpectedOpcode(uint word0, uint word1, int sizeDwords, string expected) =>
        AssertDecodes(word0, word1, sizeDwords, expected);

    // ---- SOP1 ----

    public static TheoryData<uint, uint, int, string> Sop1Cases = new()
    {
        { 0xBE800501, 0x00000000, 1, "SCselectB32" }, // s_cmov_b32 s0, s1
        { 0xBE800C02, 0x00000000, 1, "SBrevB64" }, // s_brev_b64 s[0:1], s[2:3]
        { 0xBE800D01, 0x00000000, 1, "SBcnt0I32B32" }, // s_bcnt0_i32_b32 s0, s1
        { 0xBE800E02, 0x00000000, 1, "SBcnt0I32B64" }, // s_bcnt0_i32_b64 s0, s[2:3]
        { 0xBE801101, 0x00000000, 1, "SFF0I32B32" }, // s_ff0_i32_b32 s0, s1
        { 0xBE801202, 0x00000000, 1, "SFF0I32B64" }, // s_ff0_i32_b64 s0, s[2:3]
        { 0xBE801701, 0x00000000, 1, "SFlbitI32" }, // s_flbit_i32 s0, s1
        { 0xBE801802, 0x00000000, 1, "SFlbitI32I64" }, // s_flbit_i32_i64 s0, s[2:3]
        { 0xBE801901, 0x00000000, 1, "SSextI32I8" }, // s_sext_i32_i8 s0, s1
        { 0xBE801A01, 0x00000000, 1, "SSextI32I16" }, // s_sext_i32_i16 s0, s1
        { 0xBE802202, 0x00000000, 1, "SRfeB64" }, // s_rfe_b64 s[2:3]
        { 0xBE802E01, 0x00000000, 1, "SMovrelsB32" }, // s_movrels_b32 s0, s1
        { 0xBE802F02, 0x00000000, 1, "SMovrelsB64" }, // s_movrels_b64 s[0:1], s[2:3]
        { 0xBE803001, 0x00000000, 1, "SMovreldB32" }, // s_movreld_b32 s0, s1
        { 0xBE803102, 0x00000000, 1, "SMovreldB64" }, // s_movreld_b64 s[0:1], s[2:3]
        { 0xBE804601, 0x00000000, 1, "SAndn1WrexecB32" }, // s_andn1_wrexec_b32 s0, s1
        { 0xBE804701, 0x00000000, 1, "SAndn2WrexecB32" }, // s_andn2_wrexec_b32 s0, s1
        { 0xBE803902, 0x00000000, 1, "SAndn1WrexecB64" }, // s_andn1_wrexec_b64 s[0:1], s[2:3]
        { 0xBE803A02, 0x00000000, 1, "SAndn2WrexecB64" }, // s_andn2_wrexec_b64 s[0:1], s[2:3]
        { 0xBE804901, 0x00000000, 1, "SMovrelsd2B32" }, // s_movrelsd_2_b32 s0, s1
    };

    [Theory]
    [MemberData(nameof(Sop1Cases))]
    public void Sop1DecodesToExpectedOpcode(uint word0, uint word1, int sizeDwords, string expected) =>
        AssertDecodes(word0, word1, sizeDwords, expected);

    // ---- SOP2 ----

    public static TheoryData<uint, uint, int, string> Sop2Cases = new()
    {
        { 0x96000201, 0x00000000, 1, "SAbsdiffI32" }, // s_absdiff_i32 s0, s1, s2
    };

    [Theory]
    [MemberData(nameof(Sop2Cases))]
    public void Sop2DecodesToExpectedOpcode(uint word0, uint word1, int sizeDwords, string expected) =>
        AssertDecodes(word0, word1, sizeDwords, expected);

    // ---- SOPK ----

    public static TheoryData<uint, uint, int, string> SopkCases = new()
    {
        { 0xB0801234, 0x00000000, 1, "SVersion" }, // s_version 0x1234
        { 0xB1001234, 0x00000000, 1, "SCmovkI32" }, // s_cmovk_i32 s0, 0x1234
        { 0xB900F801, 0x00000000, 1, "SGetregB32" }, // s_getreg_b32 s0, hwreg(HW_REG_MODE)
        { 0xBA80F801, 0x12345678, 2, "SSetregImm32B32" }, // s_setreg_imm32_b32 hwreg(HW_REG_MODE), 0x12345678
        { 0xBB000004, 0x00000000, 1, "SCallB64" }, // s_call_b64 s[0:1], 4
        { 0xBD800004, 0x00000000, 1, "SSubvectorLoopBegin" }, // s_subvector_loop_begin s0, 4
        { 0xBE000004, 0x00000000, 1, "SSubvectorLoopEnd" }, // s_subvector_loop_end s0, 4
    };

    [Theory]
    [MemberData(nameof(SopkCases))]
    public void SopkDecodesToExpectedOpcode(uint word0, uint word1, int sizeDwords, string expected) =>
        AssertDecodes(word0, word1, sizeDwords, expected);

    // ---- SOPP ----

    public static TheoryData<uint, uint, int, string> SoppCases = new()
    {
        { 0xBF830000, 0x00000000, 1, "SWakeup" }, // s_wakeup
        { 0xBF8B0001, 0x00000000, 1, "SSetkill" }, // s_setkill 0x1
        { 0xBF8D0001, 0x00000000, 1, "SSethalt" }, // s_sethalt 0x1
        { 0xBF8E0003, 0x00000000, 1, "SSleep" }, // s_sleep 0x3
        { 0xBF910001, 0x00000000, 1, "SSendmsghalt" }, // s_sendmsghalt sendmsg(MSG_INTERRUPT)
        { 0xBF930000, 0x00000000, 1, "SIcacheInv" }, // s_icache_inv
        { 0xBF940001, 0x00000000, 1, "SIncperflevel" }, // s_incperflevel 0x1
        { 0xBF950001, 0x00000000, 1, "SDecperflevel" }, // s_decperflevel 0x1
        { 0xBF9B0000, 0x00000000, 1, "SEndpgmSaved" }, // s_endpgm_saved
        { 0xBF9E0000, 0x00000000, 1, "SEndpgm" }, // s_endpgm_ordered_ps_done
        { 0xBFA20000, 0x00000000, 1, "SWaitIdle" }, // s_wait_idle
        { 0xBFA40005, 0x00000000, 1, "SRoundMode" }, // s_round_mode 0x5
        { 0xBFA50005, 0x00000000, 1, "SDenormMode" }, // s_denorm_mode 0x5
        { 0xBFA80001, 0x00000000, 1, "STtracedataImm" }, // s_ttracedata_imm 0x1
    };

    [Theory]
    [MemberData(nameof(SoppCases))]
    public void SoppDecodesToExpectedOpcode(uint word0, uint word1, int sizeDwords, string expected) =>
        AssertDecodes(word0, word1, sizeDwords, expected);

    // ---- Operand layouts and relocated opcodes ----

    [Fact]
    public void VRsqF64LivesAtVop1Opcode0x31NotTheOldSlot()
    {
        // VOP1 word: opcode in [16:9], vdst v0, src0 v2.
        const uint Src0V2 = 0x102;
        Assert.Equal("VRsqF64", DecodeFirst(0x7E000000 | (0x31u << 9) | Src0V2, 0, 1).Opcode);

        // 0x30 used to decode as VRsqF64; it must not any more (it may be a raw/unknown opcode).
        var words = new uint[] { 0x7E000000 | (0x30u << 9) | Src0V2, SEndpgm };
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint)), words[i]);
        }

        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        if (Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out _))
        {
            Assert.NotEqual("VRsqF64", program.Instructions[0].Opcode);
        }
    }

    [Fact]
    public void VSwapB32WritesBothOperands()
    {
        // v_swap_b32 v3, v7
        var word = 0x7E000000u | (3u << 17) | (0x65u << 9) | (0x100u + 7);
        var instruction = DecodeFirst(word, 0, 1);
        Assert.Equal("VSwapB32", instruction.Opcode);
        Assert.Equal([Gen5Operand.Vector(3), Gen5Operand.Vector(7)], instruction.Destinations);
    }

    [Fact]
    public void VRsqF64Vop3FormMovedToOpcode0x1B1()
    {
        // VOP3 encoding of VOP1 opcode 0x31 is 0x181 + 0x30 = 0x1B1.
        var instruction = DecodeFirst(0xD4000000u | (0x1B1u << 16), 0x00000102, 2);
        Assert.Equal("VRsqF64", instruction.Opcode);
    }

    [Theory]
    [InlineData(0xD56D0400u, 0x040E0501u, 4u)] // v_div_scale_f32 v0, s[4:5], v1, v2, v3
    [InlineData(0xD56E0400u, 0x041A0902u, 4u)] // v_div_scale_f64 v[0:1], s[4:5], v[2:3], v[4:5], v[6:7]
    [InlineData(0xD5770400u, 0x04120702u, 4u)] // v_mad_i64_i32 v[0:1], s[4:5], v2, v3, v[4:5]
    public void Vop3bDecodesScalarDestination(uint word0, uint word1, uint expectedSdst)
    {
        var instruction = DecodeFirst(word0, word1, 2);
        var control = Assert.IsType<Gen5Vop3Control>(instruction.Control);
        Assert.Equal(expectedSdst, control.ScalarDestination);
        Assert.Equal([Gen5Operand.Vector(0)], instruction.Destinations);
    }

    [Fact]
    public void SCmovB32DecodesAsSelectWithOldDestinationAsSecondSource()
    {
        // s_cmov_b32 s0, s1
        var instruction = DecodeFirst(0xBE800501, 0, 1);
        Assert.Equal("SCselectB32", instruction.Opcode);
        Assert.Equal(Gen5Operand.Scalar(0), Assert.Single(instruction.Destinations));
        Assert.Equal([Gen5Operand.Source(1), Gen5Operand.Scalar(0)], instruction.Sources);
    }

    [Fact]
    public void SSetregImm32B32CarriesLiteralAndRegisterField()
    {
        // s_setreg_imm32_b32 hwreg(HW_REG_MODE), 0x12345678
        var instruction = DecodeFirst(0xBA80F801, 0x12345678, 2);
        Assert.Equal("SSetregImm32B32", instruction.Opcode);
        Assert.Equal(2, instruction.Words.Count);
        Assert.Equal(
            [
                new Gen5Operand(Gen5OperandKind.LiteralConstant, 0x12345678),
                new Gen5Operand(Gen5OperandKind.EncodedConstant, 0xF801),
            ],
            instruction.Sources);
    }

    [Fact]
    public void SAbsdiffI32MovedToOpcode0x2CAndOldSlotIsNoLongerIt()
    {
        Assert.Equal("SAbsdiffI32", DecodeFirst(0x96000201, 0, 1).Opcode);

        // SOP2 opcode 0x2D: previously SAbsdiffI32.
        var words = new uint[] { 0x96800201, SEndpgm };
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * sizeof(uint)), words[i]);
        }

        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        if (Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out _))
        {
            Assert.NotEqual("SAbsdiffI32", program.Instructions[0].Opcode);
        }
    }

    [Fact]
    public void SVersionDecodesWithoutDestination()
    {
        // s_version 0x1234
        var instruction = DecodeFirst(0xB0801234, 0, 1);
        Assert.Equal("SVersion", instruction.Opcode);
        Assert.Empty(instruction.Destinations);
    }


    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length))
            {
                return false;
            }

            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
