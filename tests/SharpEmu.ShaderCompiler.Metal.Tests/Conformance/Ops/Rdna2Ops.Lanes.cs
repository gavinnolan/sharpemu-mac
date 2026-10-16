// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using static SharpEmu.ShaderCompiler.Metal.Tests.Conformance.Semantics;

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

/// <summary>
/// Sub-dword and cross-lane forms (ISA 6.8, 6.9, 13.3.7-13.3.10) on a representative VOP1/VOP2
/// set of each operand kind, and the VOP3 lane ops V_PERMLANE16/V_PERMLANEX16, V_READLANE,
/// V_READFIRSTLANE and V_WRITELANE (ISA 12.8, 12.12). The opcodes carry minimal semantics of
/// their own here; the full catalogs live in the float/half/integer categories. SDWA operand
/// selection and destination insertion and DPP lane selection are the wave model's
/// (<see cref="Sdwa"/>, <see cref="Dpp"/>).
/// </summary>
internal static partial class Rdna2Ops
{
    /// <summary>DPP16 controls (ISA 13.3.9 Table 91; row_share/row_xmask are gfx10 controls the
    /// table omits, semantics from LLVM's AMDGPUModifierSyntax dpp16_ctrl).</summary>
    private static readonly int[] LaneDppControls =
    [
        0xB1, 0x4F, 0x00, 0x4E, // quad_perm [1,0,3,2], [3,3,0,1], [0,0,0,0], [2,3,0,1]
        0x101, 0x10F, 0x111, 0x113, 0x11F, // row_shl 1, 15; row_shr 1, 3, 15
        0x121, 0x128, 0x12F, // row_ror 1, 8, 15
        0x140, 0x141, // row_mirror, row_half_mirror
        0x150, 0x15D, // row_share 0, 13
        0x161, 0x166, 0x16F, // row_xmask 1, 6, 15
    ];

    private static readonly int[][] LaneDpp8Selects =
    [
        [7, 6, 5, 4, 3, 2, 1, 0],
        [0, 0, 0, 0, 7, 7, 7, 7],
        [3, 4, 5, 6, 7, 0, 1, 2],
        [1, 1, 2, 3, 5, 5, 6, 4],
    ];

    private enum LaneShape { Move, Int, IntUnsigned, Float, Half, Convert, Mask, Carry, Accumulate }

    [OpCategory("lanes")]
    private static IEnumerable<object> Lanes()
    {
        var f32 = FloatFormat.F32;
        var f16 = FloatFormat.F16;
        var ops = new (VectorOp Op, LaneShape Shape)[]
        {
            // V_MOV_B32: D.u = S0.u (ISA 12.8).
            (new VectorOp("v_mov_b32", VectorEncoding.Vop1, OpType.B32, [OpType.B32],
                static (in LaneInputs lane) => new LaneResult((uint)lane.S0)) { Citation = "ISA 12.8 V_MOV_B32" }, LaneShape.Move),
            // V_CVT_F32_F16: D.f = f16_to_f32(S0.f16) (ISA 12.8); an f16 denormal input flushes per FP_DENORM.
            (new VectorOp("v_cvt_f32_f16", VectorEncoding.Vop1, OpType.F32, [OpType.F16],
                static (in LaneInputs lane) =>
                {
                    var value = lane.Mode.FlushIn(lane.S0 & 0xFFFF, FloatFormat.F16);
                    return FloatResult(SoftFloat.Convert(value, FloatFormat.F16, FloatFormat.F32), FloatFormat.F32);
                }) { Citation = "ISA 12.8 V_CVT_F32_F16, 6.4" }, LaneShape.Convert),
            // V_ADD_NC_U32: D.u = S0.u + S1.u; clamp saturates (ISA 12.7, 6.5).
            (new VectorOp("v_add_nc_u32", VectorEncoding.Vop2, OpType.U32, [OpType.U32, OpType.U32],
                static (in LaneInputs lane) =>
                {
                    var sum = (Int128)(uint)lane.S0 + (uint)lane.S1;
                    return new LaneResult(lane.Clamp ? SaturateUnsigned(sum, 32) : (ulong)(uint)sum);
                }, VectorFlags.IntegerClamp) { Citation = "ISA 12.7 V_ADD_NC_U32, 6.5" }, LaneShape.IntUnsigned),
            // V_AND_B32: D.u = S0.u & S1.u (ISA 12.7).
            (new VectorOp("v_and_b32", VectorEncoding.Vop2, OpType.B32, [OpType.B32, OpType.B32],
                Int(static (a, b, _) => (a & b) & 0xFFFFFFFF)) { Citation = "ISA 12.7 V_AND_B32" }, LaneShape.Int),
            // V_LSHLREV_B32: D.u = S1.u << S0.u[4:0] (ISA 12.7).
            (new VectorOp("v_lshlrev_b32", VectorEncoding.Vop2, OpType.B32, [OpType.U32, OpType.B32],
                Int(static (a, b, _) => (b << (int)(a & 31)) & 0xFFFFFFFF)) { Citation = "ISA 12.7 V_LSHLREV_B32" }, LaneShape.Int),
            // V_MAX_I32: D.i = max(S0.i, S1.i) (ISA 12.7).
            (new VectorOp("v_max_i32", VectorEncoding.Vop2, OpType.I32, [OpType.I32, OpType.I32],
                Int(static (a, b, _) => (ulong)(uint)Math.Max((int)a, (int)b))) { Citation = "ISA 12.7 V_MAX_I32" }, LaneShape.Int),
            // V_MIN_U32: D.u = min(S0.u, S1.u) (ISA 12.7).
            (new VectorOp("v_min_u32", VectorEncoding.Vop2, OpType.U32, [OpType.U32, OpType.U32],
                Int(static (a, b, _) => Math.Min(a & 0xFFFFFFFF, b & 0xFFFFFFFF))) { Citation = "ISA 12.7 V_MIN_U32" }, LaneShape.Int),
            // V_ADD_F32 / V_SUB_F32 / V_MUL_F32: IEEE arithmetic under MODE (ISA 12.7, 6.4).
            (new VectorOp("v_add_f32", VectorEncoding.Vop2, OpType.F32, [OpType.F32, OpType.F32],
                Binary(f32, static (a, b, f, keep) => SoftFloat.Add(a, b, f, keep))) { Citation = "ISA 12.7 V_ADD_F32" }, LaneShape.Float),
            (new VectorOp("v_sub_f32", VectorEncoding.Vop2, OpType.F32, [OpType.F32, OpType.F32],
                Binary(f32, static (a, b, f, keep) => SoftFloat.Sub(a, b, f, keep))) { Citation = "ISA 12.7 V_SUB_F32" }, LaneShape.Float),
            (new VectorOp("v_mul_f32", VectorEncoding.Vop2, OpType.F32, [OpType.F32, OpType.F32],
                Binary(f32, static (a, b, f, keep) => SoftFloat.Mul(a, b, f, keep))) { Citation = "ISA 12.7 V_MUL_F32" }, LaneShape.Float),
            // V_ADD_F16 / V_MUL_F16: 16-bit IEEE arithmetic (ISA 12.7); the high half is preserved.
            (new VectorOp("v_add_f16", VectorEncoding.Vop2, OpType.F16, [OpType.F16, OpType.F16],
                Binary(f16, static (a, b, f, keep) => SoftFloat.Add(a, b, f, keep))) { Citation = "ISA 12.7 V_ADD_F16" }, LaneShape.Half),
            (new VectorOp("v_mul_f16", VectorEncoding.Vop2, OpType.F16, [OpType.F16, OpType.F16],
                Binary(f16, static (a, b, f, keep) => SoftFloat.Mul(a, b, f, keep))) { Citation = "ISA 12.7 V_MUL_F16" }, LaneShape.Half),
            // V_CNDMASK_B32: D.u = VCC[lane] ? S1.u : S0.u (ISA 12.7).
            (new VectorOp("v_cndmask_b32", VectorEncoding.Vop2, OpType.B32, [OpType.B32, OpType.B32],
                static (in LaneInputs lane) => new LaneResult((uint)(lane.CarryIn ? lane.S1 : lane.S0)),
                VectorFlags.ReadsMask) { Citation = "ISA 12.7 V_CNDMASK_B32" }, LaneShape.Mask),
            // V_ADD_CO_CI_U32: D.u = S0.u + S1.u + VCC[lane]; VCC[lane] = carry out (ISA 12.7).
            (new VectorOp("v_add_co_ci_u32", VectorEncoding.Vop2, OpType.U32, [OpType.U32, OpType.U32],
                static (in LaneInputs lane) =>
                {
                    var sum = (ulong)(uint)lane.S0 + (uint)lane.S1 + (lane.CarryIn ? 1ul : 0);
                    return new LaneResult(sum & 0xFFFFFFFF, sum >> 32 != 0);
                }, VectorFlags.CarryIn | VectorFlags.CarryOut) { Citation = "ISA 12.7 V_ADD_CO_CI_U32" }, LaneShape.Carry),
            // V_FMAC_F32: D.f = S0.f * S1.f + D.f, fused (ISA 12.7). No SDWA (ISA 12.19.2).
            (new VectorOp("v_fmac_f32", VectorEncoding.Vop2, OpType.F32, [OpType.F32, OpType.F32, OpType.F32],
                Ternary(f32, static (a, b, c, f, keep) => SoftFloat.Fma(a, b, c, f, keep)), VectorFlags.Accumulates)
                { Citation = "ISA 12.7 V_FMAC_F32" }, LaneShape.Accumulate),
        };

        foreach (var (op, shape) in ops)
        {
            foreach (var item in LaneSdwaCases(op, shape)) yield return item;
            foreach (var item in LaneDppCases(op, shape)) yield return item;
        }

        foreach (var item in LaneVop3Ops()) yield return item;
    }

    private static VectorCase LaneSdwa(VectorOp op, SdwaControl control, int wave, uint abs = 0, uint neg = 0,
        bool clamp = false, int omod = 0) =>
        new(new VectorForm(op, FormKind.Sdwa) { Sdwa = control, Abs = abs, Neg = neg, Clamp = clamp, Omod = omod },
            [wave], FormGenerator.DefaultMode);

    /// <summary>SDWA (ISA 6.8, 13.3.7): byte/word source selects with and without sign
    /// extension, destination selects with each DST_UNUSED, abs/neg on float operands, and
    /// clamp/omod where the opcode has them. Float results are only placed in sub-dword fields
    /// by the 16-bit ops (a NaN's payload bits are unspecified, so a slice of an f32 NaN has no
    /// defined value), and the float operands carry no sext (it is an integer modifier).</summary>
    private static IEnumerable<VectorCase> LaneSdwaCases(VectorOp op, LaneShape shape)
    {
        switch (shape)
        {
            case LaneShape.Move:
                yield return LaneSdwa(op, new SdwaControl(DstSel: 4, DstUnused: 0, Src0Sel: 1), 32);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 1, DstUnused: 1, Src0Sel: 2), 32);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 5, DstUnused: 2, Src0Sel: 5), 32);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 6, Src0Sel: 3, Src0Sext: true), 32);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 3, DstUnused: 2, Src0Sel: 4, Src0Sext: true), 64);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 2, DstUnused: 1, Src0Sel: 0), 64);
                break;
            case LaneShape.Int or LaneShape.IntUnsigned:
                yield return LaneSdwa(op, new SdwaControl(DstSel: 4, DstUnused: 0, Src0Sel: 1, Src1Sel: 5), 32);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 2, DstUnused: 1, Src0Sel: 3, Src1Sel: 4, Src0Sext: true, Src1Sext: true), 32);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 5, DstUnused: 2, Src0Sel: 5, Src1Sel: 0), 32);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 3, DstUnused: 2, Src0Sel: 2, Src1Sel: 6), 64,
                    clamp: shape == LaneShape.IntUnsigned);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 6, Src0Sel: 4, Src1Sel: 3, Src0Sext: true), 32,
                    clamp: shape == LaneShape.IntUnsigned);
                break;
            case LaneShape.Float:
                yield return LaneSdwa(op, new SdwaControl(), 32, abs: 2, neg: 1);
                yield return LaneSdwa(op, new SdwaControl(), 32, abs: 1, neg: 3, clamp: true);
                yield return LaneSdwa(op, new SdwaControl(), 32, omod: 1);
                yield return LaneSdwa(op, new SdwaControl(), 64, omod: 3, clamp: true, neg: 2);
                yield return LaneSdwa(op, new SdwaControl(Src0Sel: 5, Src1Sel: 6), 32);
                break;
            case LaneShape.Half:
                yield return LaneSdwa(op, new SdwaControl(DstSel: 4, DstUnused: 2, Src0Sel: 5, Src1Sel: 4), 32);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 5, DstUnused: 0, Src0Sel: 4, Src1Sel: 5), 32, abs: 2, neg: 1);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 5, DstUnused: 2, Src0Sel: 5, Src1Sel: 5), 32, clamp: true);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 4, DstUnused: 0, Src0Sel: 4, Src1Sel: 4), 64, abs: 1, neg: 2, omod: 1);
                break;
            case LaneShape.Convert:
                yield return LaneSdwa(op, new SdwaControl(Src0Sel: 5), 32, neg: 1);
                yield return LaneSdwa(op, new SdwaControl(Src0Sel: 4), 32, abs: 1, clamp: true);
                yield return LaneSdwa(op, new SdwaControl(Src0Sel: 5), 64, omod: 2);
                break;
            case LaneShape.Mask:
                yield return LaneSdwa(op, new SdwaControl(DstSel: 4, DstUnused: 0, Src0Sel: 1, Src1Sel: 5), 32);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 3, DstUnused: 2, Src0Sel: 1, Src1Sel: 5), 64);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 6, Src0Sel: 2, Src1Sel: 4), 32);
                break;
            case LaneShape.Carry:
                yield return LaneSdwa(op, new SdwaControl(DstSel: 5, DstUnused: 2, Src0Sel: 0, Src1Sel: 6), 32);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 6, Src0Sel: 5, Src1Sel: 5), 64);
                yield return LaneSdwa(op, new SdwaControl(DstSel: 4, DstUnused: 0, Src0Sel: 3, Src1Sel: 1, Src0Sext: true), 32);
                break;
        }
    }

    private static VectorCase LaneDpp(VectorOp op, DppControl dpp, int[] waves, uint abs = 0, uint neg = 0) =>
        new(new VectorForm(op, dpp.Lanes8 is null ? FormKind.Dpp16 : FormKind.Dpp8) { Dpp = dpp, Abs = abs, Neg = neg },
            waves, FormGenerator.DefaultMode);

    /// <summary>DPP16 and DPP8 (ISA 6.9, 13.3.9, 13.3.10). V_MOV_B32 runs every control with each
    /// BOUND_CTRL/FI combination at wave32 and wave64, every DPP8 pattern with and without FI,
    /// and row/bank masks; the other opcodes a spread of controls with their source modifiers.
    /// The carry op keeps every lane writing (permutations, BOUND_CTRL set, full masks): whether a
    /// lane DPP leaves unwritten still writes its carry is not something the ISA states.</summary>
    private static IEnumerable<VectorCase> LaneDppCases(VectorOp op, LaneShape shape)
    {
        var both = FormGenerator.BothWaves;
        var floatOperands = shape is LaneShape.Float or LaneShape.Half or LaneShape.Accumulate;
        switch (shape)
        {
            case LaneShape.Move:
                foreach (var ctrl in LaneDppControls)
                {
                    foreach (var (bc, fi) in ((bool, bool)[])[(false, false), (true, false), (false, true), (true, true)])
                    {
                        yield return LaneDpp(op, new DppControl(ctrl, BoundCtrl: bc, FetchInactive: fi), both);
                    }
                }

                // Row and bank masks gate the destination write only (ISA 13.3.9).
                yield return LaneDpp(op, new DppControl(0x111, RowMask: 0x2, BankMask: 0x9), both);
                yield return LaneDpp(op, new DppControl(0xB1, RowMask: 0xD, BankMask: 0x6, BoundCtrl: true), both);
                yield return LaneDpp(op, new DppControl(0x16F, RowMask: 0x5, BankMask: 0xE, FetchInactive: true), both);
                foreach (var lanes in LaneDpp8Selects)
                {
                    yield return LaneDpp(op, new DppControl(Lanes8: lanes), both);
                    yield return LaneDpp(op, new DppControl(Lanes8: lanes, FetchInactive: true), both);
                }

                break;
            case LaneShape.Carry:
                yield return LaneDpp(op, new DppControl(0xB1, BoundCtrl: true), both);
                yield return LaneDpp(op, new DppControl(0x140, BoundCtrl: true, FetchInactive: true), both);
                yield return LaneDpp(op, new DppControl(Lanes8: LaneDpp8Selects[0], FetchInactive: true), both);
                break;
            default:
                yield return LaneDpp(op, new DppControl(0x111), [32], abs: floatOperands ? 2u : 0, neg: floatOperands ? 1u : 0);
                yield return LaneDpp(op, new DppControl(0x4E, BoundCtrl: true, FetchInactive: true), [64]);
                yield return LaneDpp(op, new DppControl(0x16F, RowMask: 0xB, BankMask: 0x7), [64],
                    abs: floatOperands ? 1u : 0, neg: floatOperands ? 2u : 0);
                yield return LaneDpp(op, new DppControl(0x101, BoundCtrl: true), [32]);
                yield return LaneDpp(op, new DppControl(Lanes8: LaneDpp8Selects[2]), [32]);
                yield return LaneDpp(op, new DppControl(Lanes8: LaneDpp8Selects[3], FetchInactive: true), [64]);
                break;
        }
    }

    /// <summary>VOP3 lane ops whose lane selects come from SGPRs: the harness presets s24/s25
    /// (<see cref="VectorWave.ScalarDestinationLow"/>, <see cref="VectorWave.ScalarDestinationHigh"/>),
    /// so those registers and inline/literal constants serve as the selects.</summary>
    private static IEnumerable<VectorOp> LaneVop3Ops()
    {
        const ulong presetSelects = VectorWave.ScalarDestinationLow | ((ulong)VectorWave.ScalarDestinationHigh << 32);
        const uint literal = 0x9C3E07B5;
        foreach (var cross in (bool[])[false, true])
        {
            var mnemonic = cross ? "v_permlanex16_b32" : "v_permlane16_b32";
            foreach (var (fi, bc) in ((bool, bool)[])[(false, false), (true, false), (false, true), (true, true)])
            {
                var opSel = fi || bc ? $" op_sel:[{(fi ? 1 : 0)},{(bc ? 1 : 0)}]" : "";
                yield return LaneOp(mnemonic, _ => $"{mnemonic} v7, v1, s24, s25{opSel}",
                    Permlane(cross, presetSelects, fi, bc), $"ISA 12.12 {mnemonic.ToUpperInvariant()}, 13.3.9 FI/BC");
                if (fi == bc)
                {
                    yield return LaneOp(mnemonic, _ => $"{mnemonic} v7, v1, 0x{literal:x}, 0x{literal:x}{opSel}",
                        Permlane(cross, literal | ((ulong)literal << 32), fi, bc), $"ISA 12.12 {mnemonic.ToUpperInvariant()}, 13.3.9 FI/BC");
                }
            }
        }

        // V_READLANE_B32: SGPR = S0 of lane S1 % wave size; ignores EXEC (ISA 12.12 opcode 864).
        yield return LaneOp("v_readlane_b32", _ => "v_readlane_b32 s24, v1, s25",
            ReadLane(static (_, size) => (int)(VectorWave.ScalarDestinationHigh % (uint)size)), "ISA 12.12 V_READLANE_B32");
        yield return LaneOp("v_readlane_b32", _ => "v_readlane_b32 s24, v1, 45",
            ReadLane(static (_, size) => 45 % size), "ISA 12.12 V_READLANE_B32");
        // V_READFIRSTLANE_B32: SGPR = S0 of the lowest lane set in EXEC (ISA 12.8 opcode 2).
        yield return LaneOp("v_readfirstlane_b32", _ => "v_readfirstlane_b32 s24, v1",
            ReadLane(static (exec, _) => System.Numerics.BitOperations.TrailingZeroCount(exec)), "ISA 12.8 V_READFIRSTLANE_B32");
        // V_WRITELANE_B32: lane S1 % wave size of D = S0; ignores EXEC (ISA 12.12 opcode 865).
        yield return LaneOp("v_writelane_b32", _ => "v_writelane_b32 v7, s24, s25",
            WriteLane(VectorWave.ScalarDestinationLow, static size => (int)(VectorWave.ScalarDestinationHigh % (uint)size)),
            "ISA 12.12 V_WRITELANE_B32");
        yield return LaneOp("v_writelane_b32", _ => "v_writelane_b32 v7, s25, 45",
            WriteLane(VectorWave.ScalarDestinationHigh, static size => 45 % size), "ISA 12.12 V_WRITELANE_B32");
    }

    private static VectorOp LaneOp(string mnemonic, Func<int, string> assembly, Action<VectorWave, VectorOutcome> semantics,
        string citation) =>
        new(mnemonic, VectorEncoding.Vop3, OpType.B32, [OpType.B32], static (in LaneInputs _) => default)
        {
            Assembly = assembly,
            WaveSemantics = semantics,
            Citation = citation,
        };

    /// <summary>V_PERMLANE16_B32 / V_PERMLANEX16_B32 (ISA 12.12 opcodes 887, 888): lane i of each
    /// row reads lane {S2,S1}[4i+3:4i] of its row, or of the other row of its pair (altrow =
    /// {row[1], ~row[0]}). OP_SEL[0] is FI and OP_SEL[1] BOUND_CTRL as in DPP: an inactive source
    /// lane is read with FI; without it the lane reads zero with BOUND_CTRL and is left unwritten
    /// otherwise (the DPP16 rule, <see cref="Dpp.UsesZero"/>). Inactive lanes are not written.</summary>
    private static Action<VectorWave, VectorOutcome> Permlane(bool cross, ulong selects, bool fetchInactive, bool boundCtrl) =>
        (wave, outcome) =>
        {
            for (var lane = 0; lane < wave.Size; lane++)
            {
                if ((wave.Exec >> lane & 1) == 0) continue;
                var row = lane >> 4;
                var sourceRow = cross ? row ^ 1 : row;
                var source = sourceRow * 16 + (int)((selects >> ((lane & 15) * 4)) & 0xF);
                ulong value;
                if ((wave.Exec >> source & 1) != 0 || fetchInactive) value = wave.Sources[0][source] & 0xFFFFFFFF;
                else if (boundCtrl) value = 0;
                else continue;
                outcome.Destination[lane] = (wave.Old[lane] & 0xFFFFFFFF00000000ul) | value;
            }
        };

    private static Action<VectorWave, VectorOutcome> ReadLane(Func<ulong, int, int> selectLane) =>
        (wave, outcome) =>
        {
            var lane = selectLane(wave.Exec & wave.LaneMask, wave.Size);
            outcome.ScalarDestination = (wave.ScalarDestinationBefore & 0xFFFFFFFF00000000ul) | (wave.Sources[0][lane] & 0xFFFFFFFF);
        };

    private static Action<VectorWave, VectorOutcome> WriteLane(uint value, Func<int, int> selectLane) =>
        (wave, outcome) =>
        {
            var lane = selectLane(wave.Size);
            outcome.Destination[lane] = (wave.Old[lane] & 0xFFFFFFFF00000000ul) | value;
        };
}
