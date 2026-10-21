// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using static SharpEmu.ShaderCompiler.Metal.Tests.Conformance.OpType;
using static SharpEmu.ShaderCompiler.Metal.Tests.Conformance.Semantics;

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

/// <summary>f32 and f64 VALU arithmetic and the conversions between f32/f64 and integers/f16
/// (RDNA2 ISA 12.7 VOP2, 12.8 VOP1, 12.12 VOP3; MODE per 6.4, output modifiers per 6.2.2).</summary>
internal static partial class Rdna2Ops
{
    [OpCategory("float")]
    private static IEnumerable<object> Float()
    {
        var f32 = FloatFormat.F32;
        var f64 = FloatFormat.F64;
        const VectorEncoding Vop1 = VectorEncoding.Vop1;
        const VectorEncoding Vop2 = VectorEncoding.Vop2;
        const VectorEncoding Vop3 = VectorEncoding.Vop3;

        static VectorOp Op(string mnemonic, VectorEncoding encoding, OpType result, OpType[] sources, LaneFunction function,
            string citation, VectorFlags flags = VectorFlags.None) =>
            new(mnemonic, encoding, result, sources, function, flags) { Citation = citation };

        // --- Basic arithmetic (D = S0 op S1, rounded once under MODE).
        yield return Op("v_add_f32", Vop2, F32, [F32, F32], Binary(f32, static (a, b, f, k) => SoftFloat.Add(a, b, f, k)), "ISA 12.7 V_ADD_F32");
        yield return Op("v_sub_f32", Vop2, F32, [F32, F32], Binary(f32, static (a, b, f, k) => SoftFloat.Sub(a, b, f, k)), "ISA 12.7 V_SUB_F32");
        yield return Op("v_subrev_f32", Vop2, F32, [F32, F32], Binary(f32, static (a, b, f, k) => SoftFloat.Sub(b, a, f, k)), "ISA 12.7 V_SUBREV_F32");
        yield return Op("v_mul_f32", Vop2, F32, [F32, F32], Binary(f32, static (a, b, f, k) => SoftFloat.Mul(a, b, f, k)), "ISA 12.7 V_MUL_F32");
        yield return Op("v_add_f64", Vop3, F64, [F64, F64], Binary(f64, static (a, b, f, k) => SoftFloat.Add(a, b, f, k)), "ISA 12.12 V_ADD_F64");
        yield return Op("v_mul_f64", Vop3, F64, [F64, F64], Binary(f64, static (a, b, f, k) => SoftFloat.Mul(a, b, f, k)), "ISA 12.12 V_MUL_F64");

        // --- Fused multiply-add: D = S0 * S1 + S2 rounded once.
        LaneFunction fma32 = Ternary(f32, static (a, b, c, f, k) => SoftFloat.Fma(a, b, c, f, k));
        yield return Op("v_fma_f32", Vop3, F32, [F32, F32, F32], fma32, "ISA 12.12 V_FMA_F32");
        yield return Op("v_fma_f64", Vop3, F64, [F64, F64, F64], Ternary(f64, static (a, b, c, f, k) => SoftFloat.Fma(a, b, c, f, k)), "ISA 12.12 V_FMA_F64");
        yield return Op("v_fmac_f32", Vop2, F32, [F32, F32, F32], fma32, "ISA 12.7 V_FMAC_F32", VectorFlags.Accumulates);
        // V_FMAMK_F32: D = S0 * K + S1; V_FMAAK_F32: D = S0 * S1 + K (K a literal; no VOP3 form).
        foreach (var literal in (uint[])[0x3DCCCCCD, 0xC7C35000])
        {
            yield return Op("v_fmamk_f32", Vop2, F32, [F32, F32, F32], fma32, "ISA 12.7 V_FMAMK_F32") with { Literal = literal };
            yield return Op("v_fmaak_f32", Vop2, F32, [F32, F32, F32], fma32, "ISA 12.7 V_FMAAK_F32") with { Literal = literal };
        }

        // --- Unfused multiply-add, gfx10.1 opcodes the PS5 (gfx1013) keeps; assembled for gfx1013
        // (LlvmMcEncodings.Gfx1013Mnemonics): D = round(round(S0 * S1) + S2), denormals flushed.
        yield return Op("v_mad_f32", Vop3, F32, [F32, F32, F32], FloatModel.Mad(legacy: false), "RDNA1 ISA 12.12 V_MAD_F32");
        yield return Op("v_mac_f32", Vop2, F32, [F32, F32, F32], FloatModel.Mad(legacy: false), "RDNA1 ISA 12.7 V_MAC_F32", VectorFlags.Accumulates);
        foreach (var literal in (uint[])[0x3DCCCCCD, 0xC7C35000])
        {
            yield return Op("v_madmk_f32", Vop2, F32, [F32, F32, F32], FloatModel.Mad(legacy: false), "RDNA1 ISA 12.7 V_MADMK_F32") with { Literal = literal };
            yield return Op("v_madak_f32", Vop2, F32, [F32, F32, F32], FloatModel.Mad(legacy: false), "RDNA1 ISA 12.7 V_MADAK_F32") with { Literal = literal };
        }

        // --- DX9 "legacy" multiplies: 0.0 * anything (inf, NaN) = +0.0 (ISA 12.7, 12.12). VOP3 0x140
        // and VOP2 0x06 are V_MAD_LEGACY_F32 / V_MAC_LEGACY_F32 on gfx1013 (V_FMA/FMAC_LEGACY_F32 in the
        // RDNA2 ISA, same encodings); both are unfused on hardware (AnyPS5 hardware-oracle rows,
        // tests/execution/FmaLegacy.cpp: (1 + 2^-12)^2 - 1 = 2^-11 exactly).
        yield return Op("v_mul_legacy_f32", Vop2, F32, [F32, F32], FloatModel.MulLegacy, "ISA 12.7 V_MUL_LEGACY_F32");
        yield return Op("v_mad_legacy_f32", Vop3, F32, [F32, F32, F32], FloatModel.Mad(legacy: true), "RDNA1 ISA 12.12 V_MAD_LEGACY_F32 (RDNA2 V_FMA_LEGACY_F32)");
        yield return Op("v_mac_legacy_f32", Vop2, F32, [F32, F32, F32], FloatModel.Mad(legacy: true), "RDNA1 ISA 12.7 V_MAC_LEGACY_F32 (RDNA2 V_FMAC_LEGACY_F32)", VectorFlags.Accumulates);

        // --- Min/max (ISA 12.7 V_MIN_F32/V_MAX_F32 pseudo-code; 12.12 MIN3/MAX3/MED3, F64).
        yield return Op("v_min_f32", Vop2, F32, [F32, F32], FloatModel.MinMax2(f32, max: false), "ISA 12.7 V_MIN_F32");
        yield return Op("v_max_f32", Vop2, F32, [F32, F32], FloatModel.MinMax2(f32, max: true), "ISA 12.7 V_MAX_F32");
        yield return Op("v_min_f64", Vop3, F64, [F64, F64], FloatModel.MinMax2(f64, max: false), "ISA 12.12 V_MIN_F64");
        yield return Op("v_max_f64", Vop3, F64, [F64, F64], FloatModel.MinMax2(f64, max: true), "ISA 12.12 V_MAX_F64");
        yield return Op("v_min3_f32", Vop3, F32, [F32, F32, F32], FloatModel.MinMax3(max: false), "ISA 12.12 V_MIN3_F32");
        yield return Op("v_max3_f32", Vop3, F32, [F32, F32, F32], FloatModel.MinMax3(max: true), "ISA 12.12 V_MAX3_F32");
        yield return Op("v_med3_f32", Vop3, F32, [F32, F32, F32], FloatModel.Med3, "ISA 12.12 V_MED3_F32");

        // --- Exponent manipulation.
        yield return Op("v_ldexp_f32", Vop3, F32, [F32, I32], FloatModel.Ldexp(f32), "ISA 12.12 V_LDEXP_F32") with { Pool = FloatModel.LdexpPool };
        yield return Op("v_ldexp_f64", Vop3, F64, [F64, I32], FloatModel.Ldexp(f64), "ISA 12.12 V_LDEXP_F64") with { Pool = FloatModel.LdexpPool };
        yield return Op("v_frexp_mant_f32", Vop1, F32, [F32], FloatModel.FrexpMant(f32), "ISA 12.8 V_FREXP_MANT_F32");
        yield return Op("v_frexp_mant_f64", Vop1, F64, [F64], FloatModel.FrexpMant(f64), "ISA 12.8 V_FREXP_MANT_F64");
        yield return Op("v_frexp_exp_i32_f32", Vop1, I32, [F32], FloatModel.FrexpExp(f32), "ISA 12.8 V_FREXP_EXP_I32_F32");
        yield return Op("v_frexp_exp_i32_f64", Vop1, I32, [F64], FloatModel.FrexpExp(f64), "ISA 12.8 V_FREXP_EXP_I32_F64");

        // --- Rounding to integral values and the fractional part.
        yield return Op("v_fract_f32", Vop1, F32, [F32], FloatModel.Fract(f32), "ISA 12.8 V_FRACT_F32");
        yield return Op("v_fract_f64", Vop1, F64, [F64], FloatModel.Fract(f64), "ISA 12.8 V_FRACT_F64");
        foreach (var (name, mode) in ((string, int)[])[("rndne", 0), ("ceil", 1), ("floor", 2), ("trunc", 3)])
        {
            var upper = name.ToUpperInvariant();
            yield return Op($"v_{name}_f32", Vop1, F32, [F32], FloatModel.RoundIntegral(f32, mode), $"ISA 12.8 V_{upper}_F32");
            yield return Op($"v_{name}_f64", Vop1, F64, [F64], FloatModel.RoundIntegral(f64, mode), $"ISA 12.8 V_{upper}_F64");
        }

        // --- Hardware-approximate f32 functions (1 ULP per ISA 12.8; special values exact).
        yield return Op("v_exp_f32", Vop1, F32, [F32], FloatModel.Exp2, "ISA 12.8 V_EXP_F32", VectorFlags.Approximate) with { UlpTolerance = 1, Pool = FloatModel.ExpPool };
        // Metal's fast::log2 is within 2 ULP just above 1.0 where the ISA says 1 ULP (an intentional deviation).
        yield return Op("v_log_f32", Vop1, F32, [F32], FloatModel.Log2, "ISA 12.8 V_LOG_F32", VectorFlags.Approximate) with { UlpTolerance = Deviations.LogUlps };
        yield return Op("v_rcp_f32", Vop1, F32, [F32], FloatModel.Rcp32, "ISA 12.8 V_RCP_F32", VectorFlags.Approximate) with { UlpTolerance = 1 };
        yield return Op("v_rcp_iflag_f32", Vop1, F32, [F32], FloatModel.Rcp32, "ISA 12.8 V_RCP_IFLAG_F32", VectorFlags.Approximate) with { UlpTolerance = 1 };
        yield return Op("v_rsq_f32", Vop1, F32, [F32], FloatModel.Rsq32, "ISA 12.8 V_RSQ_F32", VectorFlags.Approximate) with { UlpTolerance = 1 };
        yield return Op("v_sqrt_f32", Vop1, F32, [F32], FloatModel.Sqrt32, "ISA 12.8 V_SQRT_F32", VectorFlags.Approximate) with { UlpTolerance = 1 };
        // V_SIN/V_COS: sin(S0 * 2pi) over the full input range; the ISA states no accuracy, so the
        // error is bounded absolutely (the value near the zeros has no meaningful ULP bound).
        yield return Op("v_sin_f32", Vop1, F32, [F32], FloatModel.Sin(cosine: false), "ISA 12.8 V_SIN_F32", VectorFlags.Approximate) with { AbsoluteTolerance = FloatModel.TrigTolerance, Pool = FloatModel.TrigPool };
        yield return Op("v_cos_f32", Vop1, F32, [F32], FloatModel.Sin(cosine: true), "ISA 12.8 V_COS_F32", VectorFlags.Approximate) with { AbsoluteTolerance = FloatModel.TrigTolerance, Pool = FloatModel.TrigPool };
        // f64: (2**29) ULP accuracy, denormals supported (ISA 12.8).
        yield return Op("v_rcp_f64", Vop1, F64, [F64], FloatModel.Approx64(FloatModel.Function64.Rcp), "ISA 12.8 V_RCP_F64", VectorFlags.Approximate) with { UlpTolerance = 1 << 29 };
        yield return Op("v_rsq_f64", Vop1, F64, [F64], FloatModel.Approx64(FloatModel.Function64.Rsq), "ISA 12.8 V_RSQ_F64", VectorFlags.Approximate) with { UlpTolerance = 1 << 29 };
        yield return Op("v_sqrt_f64", Vop1, F64, [F64], FloatModel.Approx64(FloatModel.Function64.Sqrt), "ISA 12.8 V_SQRT_F64", VectorFlags.Approximate) with { UlpTolerance = 1 << 29 };

        // --- Cube map helpers (ISA 12.12 V_CUBEID/SC/TC/MA_F32 pseudo-code).
        yield return Op("v_cubeid_f32", Vop3, F32, [F32, F32, F32], FloatModel.Cube(FloatModel.CubeResult.Id), "ISA 12.12 V_CUBEID_F32");
        yield return Op("v_cubesc_f32", Vop3, F32, [F32, F32, F32], FloatModel.Cube(FloatModel.CubeResult.Sc), "ISA 12.12 V_CUBESC_F32");
        yield return Op("v_cubetc_f32", Vop3, F32, [F32, F32, F32], FloatModel.Cube(FloatModel.CubeResult.Tc), "ISA 12.12 V_CUBETC_F32");
        yield return Op("v_cubema_f32", Vop3, F32, [F32, F32, F32], FloatModel.Cube(FloatModel.CubeResult.Ma), "ISA 12.12 V_CUBEMA_F32");

        yield return Op("v_mullit_f32", Vop3, F32, [F32, F32, F32], FloatModel.MulLit, "ISA 12.12 V_MULLIT_F32 (RDNA3 ISA pseudo-code)");
        yield return Op("v_trig_preop_f64", Vop3, F64, [F64, OpType.U32], FloatModel.TrigPreop, "ISA 12.12 V_TRIG_PREOP_F64") with { Pool = FloatModel.TrigPreopPool };

        // --- Division helpers: V_DIV_SCALE (VOP3B: no ABS, a mask destination), V_DIV_FMAS (reads
        // VCC), V_DIV_FIXUP (ISA 12.12).
        foreach (var (format, type, suffix) in ((FloatFormat, OpType, string)[])[(f32, F32, "f32"), (f64, F64, "f64")])
        {
            var scale = Op($"v_div_scale_{suffix}", Vop3, type, [type, type, type], FloatModel.DivScale(format),
                $"ISA 12.12 V_DIV_SCALE_{suffix.ToUpperInvariant()}", VectorFlags.CarryOut | VectorFlags.NoOutputModifiers) with { Pool = FloatModel.DivScalePool(format) };
            yield return new VectorCase(new VectorForm(scale, FormKind.E64), FormGenerator.BothWaves, FormGenerator.FloatModes);
            yield return new VectorCase(new VectorForm(scale, FormKind.E64) { Neg = 0b101 }, FormGenerator.Wave32, FormGenerator.DefaultMode);
            yield return new VectorCase(new VectorForm(scale, FormKind.E64) { Neg = 0b010 }, FormGenerator.Wave32, FormGenerator.DefaultMode);

            var fmas = Op($"v_div_fmas_{suffix}", Vop3, type, [type, type, type], FloatModel.DivFmas(format),
                $"ISA 12.12 V_DIV_FMAS_{suffix.ToUpperInvariant()}");
            foreach (var vectorCase in FormGenerator.Cases(fmas))
            {
                // The lane's VCC bit selects the scale: run every form at both wave sizes.
                yield return vectorCase with { Waves = FormGenerator.BothWaves };
            }

            yield return Op($"v_div_fixup_{suffix}", Vop3, type, [type, type, type], FloatModel.DivFixup(format),
                $"ISA 12.12 V_DIV_FIXUP_{suffix.ToUpperInvariant()}") with { Pool = FloatModel.DivFixupPool(format) };
        }

        // --- Integer <-> float conversions (ISA 12.8).
        yield return Op("v_cvt_f32_i32", Vop1, F32, [I32], FloatModel.FromInteger(f32, static s => (int)s), "ISA 12.8 V_CVT_F32_I32");
        yield return Op("v_cvt_f32_u32", Vop1, F32, [OpType.U32], FloatModel.FromInteger(f32, static s => (uint)s), "ISA 12.8 V_CVT_F32_U32");
        yield return Op("v_cvt_f64_i32", Vop1, F64, [I32], FloatModel.FromInteger(f64, static s => (int)s), "ISA 12.8 V_CVT_F64_I32");
        yield return Op("v_cvt_f64_u32", Vop1, F64, [OpType.U32], FloatModel.FromInteger(f64, static s => (uint)s), "ISA 12.8 V_CVT_F64_U32");
        for (var index = 0; index < 4; index++)
        {
            var shift = index * 8;
            yield return Op($"v_cvt_f32_ubyte{index}", Vop1, F32, [OpType.U32], FloatModel.FromInteger(f32, s => (s >> shift) & 0xFF), $"ISA 12.8 V_CVT_F32_UBYTE{index}");
        }

        yield return Op("v_cvt_off_f32_i4", Vop1, F32, [OpType.U32], FloatModel.OffsetI4, "ISA 12.8 V_CVT_OFF_F32_I4") with { Pool = static _ => FloatModel.NibblePool };
        // Float -> integer: truncate, saturate, NaN -> 0; CLAMP only enables the INEXACT exception.
        const VectorFlags clampIgnored = VectorFlags.IntegerClamp;
        yield return Op("v_cvt_i32_f32", Vop1, I32, [F32], FloatModel.ToInteger(f32, signed: true, FloatModel.ToIntegerRounding.Truncate), "ISA 12.8 V_CVT_I32_F32", clampIgnored) with { Pool = FloatModel.ToIntegerPool };
        yield return Op("v_cvt_u32_f32", Vop1, OpType.U32, [F32], FloatModel.ToInteger(f32, signed: false, FloatModel.ToIntegerRounding.Truncate), "ISA 12.8 V_CVT_U32_F32", clampIgnored) with { Pool = FloatModel.ToIntegerPool };
        yield return Op("v_cvt_i32_f64", Vop1, I32, [F64], FloatModel.ToInteger(f64, signed: true, FloatModel.ToIntegerRounding.Truncate), "ISA 12.8 V_CVT_I32_F64", clampIgnored);
        yield return Op("v_cvt_u32_f64", Vop1, OpType.U32, [F64], FloatModel.ToInteger(f64, signed: false, FloatModel.ToIntegerRounding.Truncate), "ISA 12.8 V_CVT_U32_F64", clampIgnored);
        yield return Op("v_cvt_flr_i32_f32", Vop1, I32, [F32], FloatModel.ToInteger(f32, signed: true, FloatModel.ToIntegerRounding.Floor), "ISA 12.8 V_CVT_FLR_I32_F32", clampIgnored) with { Pool = FloatModel.ToIntegerPool };
        yield return Op("v_cvt_rpi_i32_f32", Vop1, I32, [F32], FloatModel.ToInteger(f32, signed: true, FloatModel.ToIntegerRounding.PlusHalfFloor), "ISA 12.8 V_CVT_RPI_I32_F32", clampIgnored) with { Pool = FloatModel.ToIntegerPool };

        // --- Float <-> float conversions.
        yield return Op("v_cvt_f32_f64", Vop1, F32, [F64], FloatModel.Convert(f64, f32), "ISA 12.8 V_CVT_F32_F64");
        yield return Op("v_cvt_f64_f32", Vop1, F64, [F32], FloatModel.Convert(f32, f64), "ISA 12.8 V_CVT_F64_F32");
        yield return Op("v_cvt_f16_f32", Vop1, F16, [F32], FloatModel.Convert(f32, FloatFormat.F16), "ISA 12.8 V_CVT_F16_F32");
        yield return Op("v_cvt_f32_f16", Vop1, F32, [F16], FloatModel.Convert(FloatFormat.F16, f32), "ISA 12.8 V_CVT_F32_F16");
        yield return Op("v_cvt_pkrtz_f16_f32", Vop2, B32, [F32, F32], FloatModel.PackRtz, "ISA 12.7 V_CVT_PKRTZ_F16_F32");
        yield return Op("v_cvt_pknorm_i16_f32", Vop3, B32, [F32, F32], FloatModel.PackNorm(signed: true), "ISA 12.12 V_CVT_PKNORM_I16_F32") with { Pool = FloatModel.NormPool };
        yield return Op("v_cvt_pknorm_u16_f32", Vop3, B32, [F32, F32], FloatModel.PackNorm(signed: false), "ISA 12.12 V_CVT_PKNORM_U16_F32") with { Pool = FloatModel.NormPool };
        yield return Op("v_cvt_pk_u8_f32", Vop3, OpType.U32, [F32, OpType.U32, OpType.U32], FloatModel.PackU8, "ISA 12.12 V_CVT_PK_U8_F32") with { Pool = FloatModel.PackU8Pool };
    }
}

/// <summary>The float semantics of <see cref="Rdna2Ops"/>' "float" category, written from the ISA
/// pseudo-code on exact arithmetic (<see cref="SoftFloat"/>).</summary>
internal static class FloatModel
{
    private static readonly FloatFormat F16 = FloatFormat.F16;
    private static readonly FloatFormat F32 = FloatFormat.F32;
    private static readonly FloatFormat F64 = FloatFormat.F64;

    /// <summary>The absolute error allowed for V_SIN_F32/V_COS_F32 results.</summary>
    public const double TrigTolerance = 1.0 / (1 << 20);

    private static ulong In(in LaneInputs lane, ulong bits, FloatFormat f) => lane.Mode.FlushIn(bits & f.ValueMask, f);

    private static bool KeepOut(in LaneInputs lane, FloatFormat f) => lane.Mode.KeepsOutputDenormals(f);

    private static ulong Out(in LaneInputs lane, ulong bits, FloatFormat f) =>
        KeepOut(lane, f) ? bits : SoftFloat.FlushDenormal(bits, f);

    private static LaneResult Nan(FloatFormat f) => FloatResult(f.DefaultNaN, f);

    // ---------------------------------------------------------------- legacy multiplies

    /// <summary>V_MUL_LEGACY_F32: D = S0 * S1 with 0.0 * x = +0.0 for any x (ISA 12.7). The zero is
    /// +0.0 (LLVM AMDGPUInstCombineIntrinsic amdgcn_fmul_legacy; RDNA3 ISA V_MUL_DX9_ZERO_F32);
    /// an input denormal flushed by MODE is a zero.</summary>
    public static LaneResult MulLegacy(in LaneInputs lane)
    {
        var a = In(lane, lane.S0, F32);
        var b = In(lane, lane.S1, F32);
        if (SoftFloat.IsZero(a, F32) || SoftFloat.IsZero(b, F32)) return new LaneResult(0);
        return FloatResult(SoftFloat.Mul(a, b, F32, KeepOut(lane, F32)), F32);
    }

    /// <summary>V_MAD_F32 family (RDNA1 ISA 12.12: "1ULP accuracy, IEEE compliant, denormals are
    /// flushed"): the product rounded to f32, then the sum rounded, denormals flushed throughout.
    /// Legacy (V_MAD/MAC_LEGACY_F32): the product is +0.0 when either factor is zero (DX9), so
    /// S2 = -0 gives +0 (LLVM folds fma_legacy(0, x, y) to +0.0 + y the same way).</summary>
    public static LaneFunction Mad(bool legacy) => (in LaneInputs lane) =>
    {
        var a = SoftFloat.FlushDenormal(lane.S0 & 0xFFFFFFFF, F32);
        var b = SoftFloat.FlushDenormal(lane.S1 & 0xFFFFFFFF, F32);
        var c = SoftFloat.FlushDenormal(lane.S2 & 0xFFFFFFFF, F32);
        var product = legacy && (SoftFloat.IsZero(a, F32) || SoftFloat.IsZero(b, F32))
            ? 0
            : SoftFloat.Mul(a, b, F32, keepDenormal: false);
        return FloatResult(SoftFloat.Add(product, c, F32, keepDenormal: false), F32);
    };

    /// <summary>V_MULLIT_F32: the RDNA2 ISA states "0.0 * x = 0.0" and "specific INF, NaN, overflow
    /// rules" without giving them, so only the stated rule is asserted: the product with the DX9 zero
    /// rule, as V_MUL_LEGACY_F32 (an intentional deviation).</summary>
    public static LaneResult MulLit(in LaneInputs lane) => MulLegacy(lane);

    // ---------------------------------------------------------------- min / max

    /// <summary>The ISA 12.7 V_MIN_F32/V_MAX_F32 pseudo-code (also F64, ISA 12.12): sNaN quieted
    /// in IEEE mode, otherwise a NaN yields the other operand; -0 &lt; +0; IEEE max picks S0 on ties.</summary>
    public static ulong MinMax(ulong a, ulong b, bool max, bool ieee, FloatFormat f)
    {
        if (ieee && SoftFloat.IsSignalingNaN(a, f)) return SoftFloat.Quiet(a, f);
        if (ieee && SoftFloat.IsSignalingNaN(b, f)) return SoftFloat.Quiet(b, f);
        if (SoftFloat.IsNaN(a, f)) return b;
        if (SoftFloat.IsNaN(b, f)) return a;
        if (a == 0 && b == f.SignMask) return max ? a : b;
        if (a == f.SignMask && b == 0) return max ? b : a;
        var order = SoftFloat.Compare(a, b, f);
        if (max) return (ieee ? order >= 0 : order > 0) ? a : b;
        return order < 0 ? a : b;
    }

    // The bit-level ops (min/max, med3, frexp) are not subject to the f32 flush deviation: the
    // translator evaluates them on the bits and flushes by FP_DENORM alone.
    private static ulong BitsIn(in LaneInputs lane, ulong bits, FloatFormat f) =>
        f == F32 ? lane.Mode.FlushInAsRequested(bits & f.ValueMask, f) : In(lane, bits, f);

    private static bool BitsOutFlushes(in LaneInputs lane, FloatFormat f) =>
        f == F32 ? !lane.Mode.RequestsOutputDenormals(f) : !KeepOut(lane, f);

    private static ulong BitsOut(in LaneInputs lane, ulong bits, FloatFormat f) =>
        f == F32 ? lane.Mode.FlushOutAsRequested(bits, f) : Out(lane, bits, f);

    public static LaneFunction MinMax2(FloatFormat f, bool max) => (in LaneInputs lane) =>
        new LaneResult(BitsOut(lane, MinMax(BitsIn(lane, lane.S0, f), BitsIn(lane, lane.S1, f), max, lane.Mode.Ieee, f), f));

    public static LaneFunction MinMax3(bool max) => (in LaneInputs lane) =>
    {
        var ieee = lane.Mode.Ieee;
        var first = MinMax(BitsIn(lane, lane.S0, F32), BitsIn(lane, lane.S1, F32), max, ieee, F32);
        return new LaneResult(BitsOut(lane, MinMax(first, BitsIn(lane, lane.S2, F32), max, ieee, F32), F32));
    };

    /// <summary>V_MED3_F32 (ISA 12.12): any NaN gives V_MIN3_F32; otherwise the max of the two
    /// operands that are not (equal to) the maximum.</summary>
    public static LaneResult Med3(in LaneInputs lane)
    {
        var ieee = lane.Mode.Ieee;
        var a = BitsIn(lane, lane.S0, F32);
        var b = BitsIn(lane, lane.S1, F32);
        var c = BitsIn(lane, lane.S2, F32);
        ulong result;
        if (SoftFloat.IsNaN(a, F32) || SoftFloat.IsNaN(b, F32) || SoftFloat.IsNaN(c, F32))
        {
            result = MinMax(MinMax(a, b, false, ieee, F32), c, false, ieee, F32);
        }
        else
        {
            var top = MinMax(MinMax(a, b, true, ieee, F32), c, true, ieee, F32);
            result = SoftFloat.Compare(top, a, F32) == 0 ? MinMax(b, c, true, ieee, F32)
                : SoftFloat.Compare(top, b, F32) == 0 ? MinMax(a, c, true, ieee, F32)
                : MinMax(a, b, true, ieee, F32);
        }

        return new LaneResult(BitsOut(lane, result, F32));
    }

    // ---------------------------------------------------------------- exponent manipulation

    /// <summary>Exponents around every boundary of f32 and f64, then the generic integer edges.</summary>
    public static IReadOnlyList<ulong>? LdexpPool(int index) => index == 1
        ?
        [
            0, 1, 2, 0xFFFFFFFF, 0xFFFFFFFE, 23, 24, 52, 53, 64, 0xFFFFFFC0, 126, 127, 128, 149, 150, 0xFFFFFF82, 0xFFFFFF81,
            0xFFFFFF6B, 0xFFFFFF6A, 254, 255, 256, 277, 278, 1022, 1023, 1024, 1074, 1075, 2046, 2047, 2098, 0xFFFFFC02,
            0xFFFFFBCE, 0xFFFFFBCD, 0xFFFFF800, 0x7FFFFFFF, 0x80000000, 0x80000001, 0x00010000, 0xFFFF0000,
        ]
        : null;

    /// <summary>V_LDEXP_F32/F64: D = S0 * 2^S1.i, rounded once (ISA 12.12).</summary>
    public static LaneFunction Ldexp(FloatFormat f) => (in LaneInputs lane) =>
    {
        var a = In(lane, lane.S0, f);
        if (SoftFloat.IsNaN(a, f)) return FloatResult(a, f);
        if (SoftFloat.IsInfinity(a, f) || SoftFloat.IsZero(a, f)) return new LaneResult(a);
        var (negative, mantissa, exponent) = SoftFloat.Decompose(a, f);
        var scale = (int)Math.Clamp((long)(int)lane.S1, -10000, 10000);
        return new LaneResult(SoftFloat.Round(negative, mantissa, exponent + scale, f, KeepOut(lane, f)));
    };

    /// <summary>V_FREXP_MANT: inf/NaN pass through (D = S0); otherwise the significand in
    /// [0.5, 1.0) with S0's sign (ISA 12.8; a zero stays a zero, C frexp()).</summary>
    public static LaneFunction FrexpMant(FloatFormat f) => (in LaneInputs lane) =>
    {
        var a = BitsIn(lane, lane.S0, f);
        if (SoftFloat.IsNaN(a, f) || SoftFloat.IsInfinity(a, f) || SoftFloat.IsZero(a, f)) return new LaneResult(a);
        var (negative, mantissa, _) = SoftFloat.Decompose(a, f);
        return new LaneResult(SoftFloat.Round(negative, mantissa, -(int)mantissa.GetBitLength(), f));
    };

    /// <summary>V_FREXP_EXP_I32: inf/NaN give 0; otherwise the exponent e with S0 = mant * 2^e,
    /// mant in [0.5, 1.0) (ISA 12.8; zero gives 0 like C frexp(), LLVM folds it the same way).</summary>
    public static LaneFunction FrexpExp(FloatFormat f) => (in LaneInputs lane) =>
    {
        var a = BitsIn(lane, lane.S0, f);
        if (SoftFloat.IsNaN(a, f) || SoftFloat.IsInfinity(a, f) || SoftFloat.IsZero(a, f)) return new LaneResult(0);
        var (_, mantissa, exponent) = SoftFloat.Decompose(a, f);
        return new LaneResult((uint)(exponent + (int)mantissa.GetBitLength()));
    };

    // ---------------------------------------------------------------- rounding

    /// <summary>V_FRACT: D = S0 + -floor(S0) rounded once; the RDNA3 ISA adds "result clamped to
    /// 0x3f7fffff" (the largest value below 1.0, which a tiny negative input would round to).</summary>
    public static LaneFunction Fract(FloatFormat f) => (in LaneInputs lane) =>
    {
        var a = In(lane, lane.S0, f);
        if (SoftFloat.IsNaN(a, f)) return FloatResult(a, f);
        // inf + -inf: the default NaN 0xffc00000 (AnyPS5 hardware rows, Vop1FloatUnary.cpp).
        if (SoftFloat.IsInfinity(a, f)) return Nan(f);
        var floor = SoftFloat.RoundToIntegral(a, f, 2);
        var difference = SoftFloat.Sub(a, floor, f, KeepOut(lane, f));
        return new LaneResult(SoftFloat.Compare(difference, f.One, f) >= 0 ? f.One - 1 : difference);
    };

    /// <summary>V_RNDNE/CEIL/FLOOR/TRUNC: round to an integral value keeping the sign of zero
    /// (ISA 12.8; RNDNE is IEEE roundToIntegralTiesToEven).</summary>
    public static LaneFunction RoundIntegral(FloatFormat f, int mode) => (in LaneInputs lane) =>
        FloatResult(SoftFloat.RoundToIntegral(In(lane, lane.S0, f), f, mode), f);

    // ---------------------------------------------------------------- approximate f32 functions

    private static double Value32(ulong bits) => BitConverter.UInt32BitsToSingle((uint)bits);

    /// <summary>A host double rounded to f32 with denormal results flushed ("denormals are flushed").</summary>
    private static LaneResult Approx32(double value) =>
        FloatResult(SoftFloat.Convert(SoftFloat.F64(value), F64, F32, keepDenormal: false), F32);

    private static ulong FlushedInput(in LaneInputs lane) => SoftFloat.FlushDenormal(lane.S0 & 0xFFFFFFFF, F32);

    public static IReadOnlyList<ulong>? ExpPool(int index) =>
    [
        .. OperandPools.F32Edges,
        0x42FE0000, 0x43000000, 0x42FFFFFF, 0xC2FC0000, 0xC2FC0001, 0xC3150000, 0xC2FE0000, 0x3F000000, 0xBF000000,
        0x41200000, 0xC1200000, 0x3E800000,
    ];

    /// <summary>V_EXP_F32 = 2^S0 (ISA 12.8): exp(-inf) = 0, exp(+-0) = 1, exp(+inf) = +inf.</summary>
    public static LaneResult Exp2(in LaneInputs lane)
    {
        var a = FlushedInput(lane);
        if (SoftFloat.IsNaN(a, F32)) return Nan(F32);
        if (a == 0xFF800000) return new LaneResult(0);
        if (a == 0x7F800000) return new LaneResult(a);
        return Approx32(Math.Pow(2, Value32(a)));
    }

    /// <summary>V_LOG_F32 = log2(S0) (ISA 12.8): log(-inf) = log(-1) = 0xffc00000 (any negative
    /// input), log(+-0) = -inf, log(1) = 0, log(+inf) = +inf.</summary>
    public static LaneResult Log2(in LaneInputs lane)
    {
        var a = FlushedInput(lane);
        if (SoftFloat.IsNaN(a, F32)) return Nan(F32);
        if (SoftFloat.IsZero(a, F32)) return new LaneResult(0xFF800000);
        if (SoftFloat.IsNegative(a, F32)) return new LaneResult(0xFFC00000);
        if (a == 0x7F800000) return new LaneResult(a);
        return Approx32(Math.Log2(Value32(a)));
    }

    /// <summary>V_RCP_F32 = 1 / S0 (ISA 12.8): rcp(+-inf) = +-0, rcp(+-0) = +-inf.</summary>
    public static LaneResult Rcp32(in LaneInputs lane)
    {
        var a = FlushedInput(lane);
        if (SoftFloat.IsNaN(a, F32)) return Nan(F32);
        if (SoftFloat.IsZero(a, F32)) return new LaneResult(SoftFloat.Inf(SoftFloat.IsNegative(a, F32), F32));
        if (SoftFloat.IsInfinity(a, F32)) return new LaneResult(SoftFloat.Zero(SoftFloat.IsNegative(a, F32), F32));
        return Approx32(1.0 / Value32(a));
    }

    /// <summary>V_RSQ_F32 = 1 / sqrt(S0) (ISA 12.8): rsq(-inf) = 0xffc00000, rsq(-0) = -inf,
    /// rsq(+0) = +inf, rsq(+inf) = +0; other negative inputs give a NaN.</summary>
    public static LaneResult Rsq32(in LaneInputs lane)
    {
        var a = FlushedInput(lane);
        if (SoftFloat.IsNaN(a, F32)) return Nan(F32);
        if (a == 0xFF800000) return new LaneResult(0xFFC00000);
        if (SoftFloat.IsZero(a, F32)) return new LaneResult(SoftFloat.Inf(SoftFloat.IsNegative(a, F32), F32));
        if (SoftFloat.IsNegative(a, F32)) return Nan(F32);
        if (a == 0x7F800000) return new LaneResult(0);
        return Approx32(1.0 / Math.Sqrt(Value32(a)));
    }

    /// <summary>V_SQRT_F32 (ISA 12.8): sqrt(-inf) = 0xffc00000, sqrt(+-0) = +-0, sqrt(+inf) = +inf;
    /// other negative inputs give a NaN.</summary>
    public static LaneResult Sqrt32(in LaneInputs lane)
    {
        var a = FlushedInput(lane);
        if (SoftFloat.IsNaN(a, F32)) return Nan(F32);
        if (a == 0xFF800000) return new LaneResult(0xFFC00000);
        if (SoftFloat.IsZero(a, F32) || a == 0x7F800000) return new LaneResult(a);
        if (SoftFloat.IsNegative(a, F32)) return Nan(F32);
        return Approx32(Math.Sqrt(Value32(a)));
    }

    public static IReadOnlyList<ulong>? TrigPool(int index) =>
    [
        .. OperandPools.F32Edges,
        0x3E800000, 0xBE800000, 0x3F400000, 0xBF400000, 0x3E000000, 0x3D800000, 0x40490FDB, 0x447A0000, 0x49742400,
        0x4CBEBC20, 0xCE6E6B28, 0x3F7FFFFF, 0x3EFFFFFF, 0x3E7FFFFF,
    ];

    /// <summary>V_SIN_F32 = sin(S0 * 2pi), V_COS_F32 = cos(S0 * 2pi), over the full input range
    /// (ISA 12.8; RDNA3 ISA: "full range input is supported"): the integral part of S0 does not
    /// matter, so sin(-MaxFloat) = 0 and cos(-MaxFloat) = 1. sin(+-inf) = 0xffc00000,
    /// sin(-0) = -0, sin(0.25) = 1, cos(0.25) = 0.</summary>
    public static LaneFunction Sin(bool cosine) => (in LaneInputs lane) =>
    {
        var a = FlushedInput(lane);
        if (SoftFloat.IsNaN(a, F32)) return Nan(F32);
        if (SoftFloat.IsInfinity(a, F32)) return new LaneResult(0xFFC00000);
        var x = Value32(a);
        var turn = x - Math.Round(x); // exact: the fractional turn in [-0.5, 0.5]
        if (turn == 0) return new LaneResult(cosine ? F32.One : x == 0 ? a : 0);
        if (Math.Abs(turn) == 0.25)
        {
            return new LaneResult(cosine ? 0 : turn > 0 ? F32.One : F32.One | F32.SignMask);
        }

        if (Math.Abs(turn) == 0.5) return new LaneResult(cosine ? F32.One | F32.SignMask : 0);
        return Approx32(cosine ? Math.Cos(2 * Math.PI * turn) : Math.Sin(2 * Math.PI * turn));
    };

    public enum Function64 { Rcp, Rsq, Sqrt }

    /// <summary>V_RCP/RSQ/SQRT_F64 ((2**29) ULP, denormals per MODE, ISA 12.8), with the f32
    /// variants' special values.</summary>
    public static LaneFunction Approx64(Function64 function) => (in LaneInputs lane) =>
    {
        var a = In(lane, lane.S0, F64);
        var negative = SoftFloat.IsNegative(a, F64);
        if (SoftFloat.IsNaN(a, F64)) return Nan(F64);
        var x = BitConverter.UInt64BitsToDouble(a);
        switch (function)
        {
            case Function64.Rcp:
                if (SoftFloat.IsZero(a, F64)) return new LaneResult(SoftFloat.Inf(negative, F64));
                if (SoftFloat.IsInfinity(a, F64)) return new LaneResult(SoftFloat.Zero(negative, F64));
                return new LaneResult(Out(lane, SoftFloat.F64(1.0 / x), F64));
            case Function64.Rsq:
                if (SoftFloat.IsZero(a, F64)) return new LaneResult(SoftFloat.Inf(negative, F64));
                if (negative) return Nan(F64);
                if (SoftFloat.IsInfinity(a, F64)) return new LaneResult(0);
                return new LaneResult(Out(lane, SoftFloat.F64(1.0 / Math.Sqrt(x)), F64));
            default:
                if (SoftFloat.IsZero(a, F64) || SoftFloat.IsInfinity(a, F64) && !negative) return new LaneResult(a);
                if (negative) return Nan(F64);
                return new LaneResult(Out(lane, SoftFloat.F64(Math.Sqrt(x)), F64));
        }
    };

    // ---------------------------------------------------------------- cube map

    public enum CubeResult { Id, Sc, Tc, Ma }

    /// <summary>|a| >= |b| (false when either is NaN).</summary>
    private static bool AbsAtLeast(ulong a, ulong b) =>
        !SoftFloat.IsNaN(a, F32) && !SoftFloat.IsNaN(b, F32) &&
        SoftFloat.Compare(SoftFloat.Abs(a, F32), SoftFloat.Abs(b, F32), F32) >= 0;

    private static bool BelowZero(ulong a) =>
        !SoftFloat.IsNaN(a, F32) && !SoftFloat.IsZero(a, F32) && SoftFloat.IsNegative(a, F32);

    /// <summary>V_CUBEID/SC/TC/MA_F32 pseudo-code (ISA 12.12), x = S0, y = S1, z = S2.</summary>
    public static LaneFunction Cube(CubeResult which) => (in LaneInputs lane) =>
    {
        var x = In(lane, lane.S0, F32);
        var y = In(lane, lane.S1, F32);
        var z = In(lane, lane.S2, F32);
        var zMajor = AbsAtLeast(z, x) && AbsAtLeast(z, y);
        var yMajor = !zMajor && AbsAtLeast(y, x);
        ulong result;
        switch (which)
        {
            case CubeResult.Id:
                var face = zMajor ? (BelowZero(z) ? 5 : 4) : yMajor ? (BelowZero(y) ? 3 : 2) : (BelowZero(x) ? 1 : 0);
                return new LaneResult(SoftFloat.FromInteger(face, F32));
            case CubeResult.Sc:
                result = zMajor ? (BelowZero(z) ? SoftFloat.Negate(x, F32) : x) : yMajor ? x : BelowZero(x) ? z : SoftFloat.Negate(z, F32);
                break;
            case CubeResult.Tc:
                result = zMajor ? SoftFloat.Negate(y, F32) : yMajor ? (BelowZero(y) ? SoftFloat.Negate(z, F32) : z) : SoftFloat.Negate(y, F32);
                break;
            default:
                return FloatResult(SoftFloat.Mul(SoftFloat.F32(2f), zMajor ? z : yMajor ? y : x, F32, KeepOut(lane, F32)), F32);
        }

        return FloatResult(result, F32);
    };

    // ---------------------------------------------------------------- V_TRIG_PREOP_F64

    /// <summary>2/pi as the 1201-bit fraction 0.b1200 b1199 ... b0 of the ISA's table, computed
    /// from pi (Machin's formula) on exact integers.</summary>
    private static readonly Lazy<BigInteger> TwoOverPi = new(static () =>
    {
        const int bits = 1201;
        const int guard = 64;
        var one = BigInteger.One << (bits + guard + 8);
        BigInteger Atan(int inverse)
        {
            var square = (BigInteger)inverse * inverse;
            var term = one / inverse;
            var sum = BigInteger.Zero;
            for (var k = 0; !term.IsZero; k++)
            {
                sum += (k % 2 == 0 ? term : -term) / (2 * k + 1);
                term /= square;
            }

            return sum;
        }

        var pi = 16 * Atan(5) - 4 * Atan(239); // pi * 2^(bits + guard + 8)
        // floor(2/pi * 2^bits) = floor(2^(bits + 1) * one / pi) / one-scale
        return (BigInteger.One << (2 * (bits + guard + 8) - (guard + 8) + 1)) / pi >> 0;
    });

    public static IReadOnlyList<ulong>? TrigPreopPool(int index) => index == 0
        ?
        [
            .. OperandPools.F64Edges,
            0x4340000000000000, 0x4350000000000000, 0x4360000000000000, 0x4370000000000000, 0x4800000000000000,
            0x6000000000000000, 0x7AF0000000000000, 0x7B00000000000000, 0x7B10000000000000, 0x7FE0000000000000,
            0xC370000000000000, 0x4440000000000000,
        ]
        : [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 15, 16, 19, 20, 21, 22, 23, 31, 32, 33, 0xFFFFFFFF, 0x12345678];

    /// <summary>V_TRIG_PREOP_F64 (ISA 12.12): the 53-bit segment of 2/pi starting S1.u[4:0] * 53
    /// bits in (further for large exponents), scaled back, rounded toward zero.</summary>
    public static LaneResult TrigPreop(in LaneInputs lane)
    {
        var exponent = (int)((lane.S0 >> 52) & 0x7FF);
        var shift = (int)(lane.S1 & 31) * 53;
        if (exponent > 1077) shift += exponent - 1077;
        var table = TwoOverPi.Value;
        // floor(2/pi * 2^(53 + shift)) mod 2^53, table bits past b0 being zero.
        var position = 1201 - 53 - shift;
        var window = position >= 0 ? table >> position : table << -position;
        var segment = window & ((BigInteger.One << 53) - 1);
        var scale = -53 - shift;
        if (exponent >= 1968) scale += 128;
        return new LaneResult(segment.IsZero ? 0 : SoftFloat.Round(false, segment, scale, F64, KeepOut(lane, F64), mode: 3));
    }

    // ---------------------------------------------------------------- division helpers

    private static int BiasedExponent(ulong bits, FloatFormat f) => (int)((bits & f.ExponentMask) >> f.MantissaBits);


    private static ulong Scale(ulong bits, int scale, FloatFormat f, bool keepDenormal)
    {
        if (SoftFloat.IsNaN(bits, f) || SoftFloat.IsInfinity(bits, f) || SoftFloat.IsZero(bits, f)) return bits;
        var (negative, mantissa, exponent) = SoftFloat.Decompose(bits, f);
        return SoftFloat.Round(negative, mantissa, exponent + scale, f, keepDenormal);
    }

    public static Func<int, IReadOnlyList<ulong>> DivScalePool(FloatFormat f) => f.Width == 32
        ? static _ =>
        [
            0x00000000, 0x80000000, 0x7F800000, 0xFF800000, 0x7FC00000, 0x7F800001, 0x00000001, 0x807FFFFF,
            0x00800000, 0x3F800000, 0xBF800000, 0x40400000, 0x3EAAAAAB, 0x0B800000, 0x0C000000, 0x0C800000,
            0x1F800000, 0x20000000, 0x5F000000, 0x5F800000, 0x7E800000, 0x7F000000, 0xFF7FFFFF, 0x7F7FFFFF,
            0x0A800000, 0x01000000, 0x34000000, 0x42F60000,
        ]
        : static _ =>
        [
            0x0000000000000000, 0x8000000000000000, 0x7FF0000000000000, 0xFFF0000000000000, 0x7FF8000000000000,
            0x7FF0000000000001, 0x0000000000000001, 0x800FFFFFFFFFFFFF, 0x0010000000000000, 0x3FF0000000000000,
            0xBFF0000000000000, 0x4008000000000000, 0x3FD5555555555555, 0x0350000000000000, 0x0360000000000000,
            0x0370000000000000, 0x1FF0000000000000, 0x2000000000000000, 0x5FE0000000000000, 0x5FF0000000000000,
            0x7FD0000000000000, 0x7FE0000000000000, 0xFFEFFFFFFFFFFFFF, 0x7FEFFFFFFFFFFFFF, 0x0340000000000000,
            0x0020000000000000, 0x3CB0000000000000, 0x405EC00000000000,
        ];

    /// <summary>V_DIV_SCALE_F32/F64 (ISA 12.12): S0 (the denominator or the numerator) is scaled by
    /// 2^+-64 (f64: 2^+-128) so the Newton-Raphson division avoids denormal terms, and VCC flags a
    /// quotient V_DIV_FMAS must rescale. The ISA's pseudo-code states the cases as properties of the
    /// values ("S2 / S1 == DENORM", "1 / S1 == DENORM", VCC = 0 for a zero operand); hardware
    /// decides them on the biased exponent fields alone, d = exp(S2) - exp(S1) (AnyPS5
    /// hardware-oracle rows, tests/execution/Division.cpp HelperVectors and ScaleVectors):
    /// <list type="bullet">
    /// <item>VCC = d &gt;= 96 (near MAX_FLOAT), or d &lt;= -96 (a denormal quotient) with S1 not
    /// denormal; f64 uses 768.</item>
    /// <item>A zero S1 or S2 gives 0xffc00000 (0xfff8000000000000); a NaN S0 is never scaled.</item>
    /// <item>d &gt;= 96: scale up if S0 is S1 (bit-equal). Else a denormal (or zero) S1: scale up.
    /// Else exp(S1) &gt;= 253 (f64 2045, 1 / S1 denormal): scale down unless d &lt;= -96 and S0 is
    /// not S1. Else d &lt;= -96: scale up unless S0 is S1. Else exp(S2) &lt;= 24 (f64 53, a tiny
    /// numerator): scale up.</item>
    /// </list>
    /// Inputs are flushed per MODE; an unscaled D is S0.</summary>
    public static LaneFunction DivScale(FloatFormat f) => (in LaneInputs lane) =>
    {
        var s0 = BitsIn(lane, lane.S0, f);
        var denominator = BitsIn(lane, lane.S1, f);
        var numerator = BitsIn(lane, lane.S2, f);
        var wide = f.Width == 64;
        var factor = wide ? 128 : 64;
        var keep = !BitsOutFlushes(lane, f);
        var denominatorExponent = BiasedExponent(denominator, f);
        var numeratorExponent = BiasedExponent(numerator, f);
        var difference = numeratorExponent - denominatorExponent;
        var nearMax = difference >= (wide ? 768 : 96);
        var nearMin = difference <= -(wide ? 768 : 96);
        var denominatorDenormal = denominatorExponent == 0;
        var denominatorHuge = denominatorExponent >= (wide ? 2045 : 253);
        var numeratorTiny = numeratorExponent <= (wide ? 53 : 24);
        var isDenominator = s0 == denominator;
        var vcc = nearMax || (!denominatorDenormal && nearMin);
        if (SoftFloat.IsZero(numerator, f) || SoftFloat.IsZero(denominator, f))
        {
            return new LaneResult(f.DefaultNaN, vcc, f.ValueMask);
        }

        var scale = 0;
        if (nearMax) scale = isDenominator ? factor : 0;
        else if (denominatorDenormal) scale = factor;
        else if (denominatorHuge) scale = !nearMin || isDenominator ? -factor : 0;
        else if (nearMin) scale = isDenominator ? 0 : factor;
        else if (numeratorTiny) scale = factor;
        var result = scale == 0 || SoftFloat.IsNaN(s0, f) ? s0 : Scale(s0, scale, f, keep);
        return new LaneResult(BitsOut(lane, result, f), vcc);
    };

    /// <summary>V_DIV_FMAS_F32/F64 (ISA 12.12): D = fma(S0, S1, S2), rescaled when the lane's VCC
    /// bit is set, rounded once. The ISA writes the scale as 2^32 (f64: 2^64), which cannot undo
    /// V_DIV_SCALE's 2^+-64 (2^+-128); hardware scales by 2^64 when S2's biased exponent is at
    /// least 128 and by 2^-64 otherwise (f64: 2^+-128 around 1024) (AnyPS5 hardware-oracle rows,
    /// tests/execution/Division.cpp HelperVectors). Input denormals follow FP_DENORM (the
    /// translator flushes the f32 bits itself); output flushing follows MODE.</summary>
    public static LaneFunction DivFmas(FloatFormat f) => (in LaneInputs lane) =>
    {
        var wide = f.Width == 64;
        var a = BitsIn(lane, lane.S0, f);
        var b = BitsIn(lane, lane.S1, f);
        var c = BitsIn(lane, lane.S2, f);
        var keep = !BitsOutFlushes(lane, f);
        var up = BiasedExponent(c, f) >= (wide ? 1024 : 128);
        var scale = lane.CarryIn ? (up ? 1 : -1) * (wide ? 128 : 64) : 0;
        if (SoftFloat.IsNaN(a, f) || SoftFloat.IsNaN(b, f) || SoftFloat.IsNaN(c, f) ||
            SoftFloat.IsInfinity(a, f) || SoftFloat.IsInfinity(b, f) || SoftFloat.IsInfinity(c, f))
        {
            return FloatResult(SoftFloat.Fma(a, b, c, f, keep), f);
        }

        return new LaneResult(ExactFma(a, b, c, f, scale, keep));
    };

    /// <summary>a * b + c on exact integers, scaled by 2^scale, rounded once (finite operands).</summary>
    private static ulong ExactFma(ulong a, ulong b, ulong c, FloatFormat f, int scale, bool keep)
    {
        var (sa, ma, ea) = SoftFloat.Decompose(a, f);
        var (sb, mb, eb) = SoftFloat.Decompose(b, f);
        var (sc, mc, ec) = SoftFloat.Decompose(c, f);
        var product = ma * mb;
        var productNegative = sa ^ sb;
        if (product.IsZero && mc.IsZero) return SoftFloat.Zero(productNegative && sc, f);
        var exponent = Math.Min(ea + eb, ec);
        var sum = (productNegative ? -product : product) << (ea + eb - exponent);
        sum += (sc ? -mc : mc) << (ec - exponent);
        if (sum.IsZero) return 0;
        // The translator rounds on integers, not through a Metal f32 operation.
        return SoftFloat.Round(sum.Sign < 0, BigInteger.Abs(sum), exponent + scale, f, keep, metalTininess: false);
    }

    public static Func<int, IReadOnlyList<ulong>?> DivFixupPool(FloatFormat f) => f.Width == 32
        ? static index => index == 0 ? null :
        [
            .. OperandPools.F32Edges, 0x0B800000, 0x0A000000, 0x09800000, 0x5F800000, 0x74000000, 0x7E800000,
        ]
        : static index => index == 0 ? null :
        [
            .. OperandPools.F64Edges, 0x0350000000000000, 0x0030000000000000, 0x0020000000000000, 0x7FD0000000000000,
            0x4350000000000000,
        ];

    /// <summary>V_DIV_FIXUP_F32/F64 (ISA 12.12 pseudo-code): S0 = quotient, S1 = denominator,
    /// S2 = numerator; special cases of the division, else the quotient's magnitude with the
    /// sign of S1 ^ S2.</summary>
    public static LaneFunction DivFixup(FloatFormat f) => (in LaneInputs lane) =>
    {
        var quotient = BitsIn(lane, lane.S0, f);
        var denominator = BitsIn(lane, lane.S1, f);
        var numerator = BitsIn(lane, lane.S2, f);
        var negative = SoftFloat.IsNegative(denominator, f) ^ SoftFloat.IsNegative(numerator, f);
        var wide = f.Width == 64;
        var invalid = f.DefaultNaN | f.SignMask; // 0xffc00000 / 0xfff8000000000000
        if (SoftFloat.IsNaN(numerator, f)) return new LaneResult(SoftFloat.Quiet(numerator, f));
        if (SoftFloat.IsNaN(denominator, f)) return new LaneResult(SoftFloat.Quiet(denominator, f));
        if (SoftFloat.IsZero(denominator, f) && SoftFloat.IsZero(numerator, f)) return new LaneResult(invalid);
        if (SoftFloat.IsInfinity(denominator, f) && SoftFloat.IsInfinity(numerator, f)) return new LaneResult(invalid);
        if (SoftFloat.IsZero(denominator, f) || SoftFloat.IsInfinity(numerator, f)) return new LaneResult(SoftFloat.Inf(negative, f));
        if (SoftFloat.IsInfinity(denominator, f) || SoftFloat.IsZero(numerator, f)) return new LaneResult(SoftFloat.Zero(negative, f));
        if (BiasedExponent(numerator, f) - BiasedExponent(denominator, f) < (wide ? -1075 : -150))
        {
            // UNDERFLOW: the quotient underflows to zero.
            return new LaneResult(SoftFloat.Zero(negative, f));
        }

        var magnitude = SoftFloat.Abs(quotient, f);
        return new LaneResult(BitsOut(lane, negative ? SoftFloat.Negate(magnitude, f) : magnitude, f));
    };

    // ---------------------------------------------------------------- conversions

    public static LaneFunction FromInteger(FloatFormat f, Func<ulong, BigInteger> value) => (in LaneInputs lane) =>
        new LaneResult(SoftFloat.FromInteger(value(lane.S0), f));

    public static readonly ulong[] NibblePool =
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 0x10, 0x1F, 0xF8, 0xFFFFFFF7, 0x12345678, 0x80000007];

    /// <summary>V_CVT_OFF_F32_I4: S0[3:0] as a signed 4-bit integer, divided by 16 (ISA 12.8 table).</summary>
    public static LaneResult OffsetI4(in LaneInputs lane)
    {
        var nibble = (int)(lane.S0 & 0xF);
        var value = nibble >= 8 ? nibble - 16 : nibble;
        return new LaneResult(value == 0 ? 0 : SoftFloat.Round(value < 0, Math.Abs(value), -4, F32));
    }

    public enum ToIntegerRounding { Truncate, Floor, PlusHalfFloor }

    public static IReadOnlyList<ulong>? ToIntegerPool(int index) =>
    [
        .. OperandPools.F32Edges,
        0x3EFFFFFF, 0xBEFFFFFF, 0x3F3FFFFF, 0xBF3FFFFF, 0x4EFFFFFF, 0xCF000001, 0xCEFFFFFF, 0x4F7FFFFF, 0x3F7FFFFF,
        0xBF7FFFFF, 0x4B7FFFFF, 0xCB7FFFFF, 0xBF400000, 0x3F400000,
    ];

    /// <summary>Float to 32-bit integer: out-of-range values (and infinities) saturate, NaN gives 0
    /// (ISA 12.8 V_CVT_I32_F32 etc.; FLR rounds down, RPI is floor(S0 + 0.5) with the f32 sum rounded).</summary>
    public static LaneFunction ToInteger(FloatFormat f, bool signed, ToIntegerRounding rounding) => (in LaneInputs lane) =>
    {
        var a = In(lane, lane.S0, f);
        if (SoftFloat.IsNaN(a, f)) return new LaneResult(0);
        BigInteger value;
        if (SoftFloat.IsInfinity(a, f))
        {
            value = SoftFloat.IsNegative(a, f) ? -(BigInteger.One << 40) : BigInteger.One << 40;
        }
        else if (SoftFloat.IsZero(a, f))
        {
            value = 0;
        }
        else
        {
            var (negative, mantissa, exponent) = SoftFloat.Decompose(a, f);
            var signedMantissa = negative ? -mantissa : mantissa;
            value = rounding switch
            {
                ToIntegerRounding.Truncate => SoftFloat.TruncateToInteger(a, f),
                // BigInteger >> rounds toward negative infinity.
                ToIntegerRounding.Floor => exponent >= 0 ? signedMantissa << exponent : signedMantissa >> -exponent,
                // D.i = (int)floor(S0.f + 0.5): the sum is an f32 addition, rounded to nearest even.
                _ => SoftFloat.TruncateToInteger(
                    SoftFloat.RoundToIntegral(SoftFloat.Add(a, SoftFloat.F32(0.5f), f, keepDenormal: false), f, 2), f),
            };
        }

        var low = signed ? int.MinValue : 0;
        var high = signed ? int.MaxValue : (BigInteger)uint.MaxValue;
        var clamped = BigInteger.Max(low, BigInteger.Min(high, value));
        return new LaneResult((ulong)(uint)(long)clamped);
    };

    /// <summary>V_CVT_F32_F64, V_CVT_F64_F32, V_CVT_F16_F32, V_CVT_F32_F16: input denormals per the
    /// source format's FP_DENORM field, the result rounded to nearest even with output denormals per
    /// the destination's (ISA 12.8; RDNA3 ISA: f16 follows the double/half field).</summary>
    public static LaneFunction Convert(FloatFormat from, FloatFormat to) => (in LaneInputs lane) =>
    {
        // f32 to f64 is the translator's own integer code, which flushes the f32 input by FP_DENORM alone.
        var a = from == F32 && to == F64 ? BitsIn(lane, lane.S0, from) : In(lane, lane.S0, from);
        return FloatResult(SoftFloat.Convert(a, from, to, KeepOut(lane, to)), to);
    };

    /// <summary>V_CVT_PKRTZ_F16_F32: both sources to f16 rounding toward zero (ISA 12.7).</summary>
    public static LaneResult PackRtz(in LaneInputs lane)
    {
        var keep = KeepOut(lane, F16);
        var low = SoftFloat.Convert(In(lane, lane.S0, F32), F32, F16, keep, mode: 3);
        var high = SoftFloat.Convert(In(lane, lane.S1, F32), F32, F16, keep, mode: 3);
        var loose = (SoftFloat.IsNaN(low, F16) ? 0xFFFFul : 0) | (SoftFloat.IsNaN(high, F16) ? 0xFFFF0000ul : 0);
        return new LaneResult(low | (high << 16), false, loose);
    }

    public static IReadOnlyList<ulong>? NormPool(int index) =>
    [
        .. OperandPools.F32Edges.Where((_, i) => i % 2 == index),
        .. Enumerable.Range(-24, 49).Select(static i => SoftFloat.F32(i / 20f + 1f / 131070)),
        0x37000080, 0x38000000, 0xB8000000, 0x3F7FFF00, 0xBF7FFF00,
    ];

    /// <summary>Exact a * scale rounded to the nearest integer, ties to even.</summary>
    private static BigInteger RoundToInteger(ulong a, int scale, FloatFormat f)
    {
        var (negative, mantissa, exponent) = SoftFloat.Decompose(a, f);
        var product = mantissa * scale;
        BigInteger magnitude;
        if (exponent >= 0)
        {
            magnitude = product << exponent;
        }
        else
        {
            var quotient = product >> -exponent;
            var remainder = product - (quotient << -exponent);
            var half = BigInteger.One << (-exponent - 1);
            magnitude = remainder > half || (remainder == half && !quotient.IsEven) ? quotient + 1 : quotient;
        }

        return negative ? -magnitude : magnitude;
    }

    /// <summary>f32 to a 16-bit normalized integer: clamped to [-1, 1] (snorm) or [0, 1] (unorm),
    /// scaled by 32767 / 65535 and rounded to nearest even; NaN gives 0.</summary>
    private static ulong Normalize(ulong a, bool signed)
    {
        if (SoftFloat.IsNaN(a, F32) || SoftFloat.IsZero(a, F32)) return 0;
        var scale = signed ? 32767 : 65535;
        if (SoftFloat.Compare(SoftFloat.Abs(a, F32), F32.One, F32) >= 0 || SoftFloat.IsInfinity(a, F32))
        {
            return SoftFloat.IsNegative(a, F32) ? (signed ? 0x8001ul : 0) : (ulong)scale;
        }

        if (!signed && SoftFloat.IsNegative(a, F32)) return 0;
        return (ulong)(ushort)(short)(int)RoundToInteger(a, scale, F32);
    }

    /// <summary>V_CVT_PKNORM_I16_F32 / V_CVT_PKNORM_U16_F32: D.lo = norm(S0), D.hi = norm(S1)
    /// (ISA 12.12; snorm/unorm per the D3D conversion rules ACO relies on for pack_[su]norm_2x16).</summary>
    public static LaneFunction PackNorm(bool signed) => (in LaneInputs lane) =>
        new LaneResult(Normalize(In(lane, lane.S0, F32), signed) | (Normalize(In(lane, lane.S1, F32), signed) << 16));

    public static IReadOnlyList<ulong>? PackU8Pool(int index) => index switch
    {
        0 =>
        [
            .. OperandPools.F32Edges, 0x437F0000, 0x437F8000, 0x437E8000, 0x43800000, 0x3F000001, 0x40400000,
            0x40600000, 0x437F7FFF, 0xBF000000, 0x42FF0000,
        ],
        1 => [0, 1, 2, 3, 4, 5, 6, 7, 0xFFFFFFFF, 0x100],
        _ => null,
    };

    /// <summary>V_CVT_PK_U8_F32 (ISA 12.12): S0 converted to an unsigned byte and inserted into byte
    /// S1[1:0] of S2. The conversion rounds to nearest even and saturates (Mesa ACO README-ISA
    /// errata); NaN gives 0.</summary>
    public static LaneResult PackU8(in LaneInputs lane)
    {
        var a = In(lane, lane.S0, F32);
        ulong value;
        if (SoftFloat.IsNaN(a, F32) || SoftFloat.IsZero(a, F32) || SoftFloat.IsNegative(a, F32))
        {
            value = 0;
        }
        else if (SoftFloat.IsInfinity(a, F32))
        {
            value = 255;
        }
        else
        {
            value = (ulong)BigInteger.Min(255, RoundToInteger(a, 1, F32));
        }

        var shift = (int)(lane.S1 & 3) * 8;
        return new LaneResult(((uint)lane.S2 & ~(0xFFu << shift)) | (uint)(value << shift));
    }
}
