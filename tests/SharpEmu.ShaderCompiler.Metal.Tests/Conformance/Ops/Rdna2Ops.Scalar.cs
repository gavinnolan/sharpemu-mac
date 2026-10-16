// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

/// <summary>The SALU: every SOP2, SOPK, SOP1 and SOPC opcode the translator supports, with
/// semantics from the RDNA2 ISA 12.1 (SOP2), 12.2 (SOPK), 12.3 (SOP1) and 12.4 (SOPC).
/// Integer arithmetic in the ISA's pseudo-code is of unbounded precision (S_ADD_U32's carry is
/// "S0.u32 + S1.u32 >= 0x100000000"), and the model computes it that way. Plain ops run at
/// one wave size (they do not depend on it); the 64-bit EXEC ops run at wave64 and the
/// 32-bit (EXEC_LO) ones at wave32.</summary>
internal static partial class Rdna2Ops
{
    private const ulong Low32 = 0xFFFFFFFFul;
    private static readonly int[] ScalarWave32 = [32];
    private static readonly int[] ScalarWave64 = [64];
    /// <summary>A plain op runs at one wave size, the one fitting the most cases per dispatch.</summary>
    private static readonly int[] PlainWaves = ScalarWave64;
    private static readonly ushort[] SopkImmediates = [0x0000, 0x0001, 0x7FFF, 0x8000, 0xFFFF];
    private static readonly ushort[] SopkCompareImmediates = [0x0000, 0x7FFF, 0x8000, 0xFFFF];

    private const ScalarFlags D = ScalarFlags.WritesDestination;
    private const ScalarFlags DScc = ScalarFlags.WritesDestination | ScalarFlags.WritesScc;
    private const ScalarFlags Scc = ScalarFlags.WritesScc;
    private const ScalarFlags Exec = ScalarFlags.WritesDestination | ScalarFlags.WritesScc | ScalarFlags.Exec;

    /// <summary>S_BFE's S1: field offsets and widths at and past the operand width, plus junk in
    /// the bits between and above the fields.</summary>
    private static readonly ulong[] BitfieldControls = BuildBitfieldControls();

    private static ulong[] BuildBitfieldControls()
    {
        var values = new List<ulong>();
        foreach (var offset in (uint[])[0, 1, 7, 16, 31, 32, 33, 48, 63])
        {
            foreach (var width in (uint[])[0, 1, 7, 8, 16, 24, 31, 32, 33, 63, 64, 65, 127])
            {
                values.Add((width << 16) | offset);
            }
        }

        values.Add(0xFF800000u | (8u << 16) | 3); // junk above the width field
        values.Add((8u << 16) | 0xFFC0u | 5); // junk between the fields
        values.Add(0xFFFFFFFFu);
        return [.. values];
    }

    private static ulong SignExtend(ulong value, int bits) =>
        bits >= 64 ? value : (ulong)((long)(value << (64 - bits)) >> (64 - bits));

    private static ulong FieldMask(int width) => width >= 64 ? ulong.MaxValue : (1ul << width) - 1;

    private static ScalarResult WithScc(ulong value, ulong width) =>
        new(value, (value & (width == 64 ? ulong.MaxValue : Low32)) != 0);

    private static ScalarOp Sop2(string mnemonic, OpType result, OpType s0, OpType s1, ScalarFunction function, ScalarFlags flags) =>
        new(mnemonic, ScalarEncoding.Sop2, result, [s0, s1], function, flags)
        {
            Citation = "ISA 12.1 " + mnemonic.ToUpperInvariant(),
            Waves = PlainWaves,
        };

    private static ScalarOp Sop1(string mnemonic, OpType result, OpType s0, ScalarFunction function, ScalarFlags flags) =>
        new(mnemonic, ScalarEncoding.Sop1, result, [s0], function, flags)
        {
            Citation = "ISA 12.3 " + mnemonic.ToUpperInvariant(),
            Waves = flags.HasFlag(ScalarFlags.Exec) ? (result.Width() == 64 ? ScalarWave64 : ScalarWave32) : PlainWaves,
        };

    private static ScalarOp Sopc(string mnemonic, OpType s0, OpType s1, Func<ulong, ulong, bool> compare) =>
        new(mnemonic, ScalarEncoding.Sopc, s0, [s0, s1], (in ScalarInputs s) => new ScalarResult(0, compare(s.S0, s.S1)), Scc)
        {
            Citation = "ISA 12.4 " + mnemonic.ToUpperInvariant(),
            Waves = PlainWaves,
        };

    private static ScalarOp Sopk(string mnemonic, OpType[] sources, ScalarFunction function, ScalarFlags flags, ushort[] immediates) =>
        new(mnemonic, ScalarEncoding.Sopk, OpType.I32, sources, function, flags)
        {
            Citation = "ISA 12.2 " + mnemonic.ToUpperInvariant(),
            Waves = PlainWaves,
            Immediates = immediates,
        };

    /// <summary>A bitwise SOP2 op: D = op(S0, S1), SCC = (D != 0).</summary>
    private static ScalarOp Bitwise(string mnemonic, int width, Func<ulong, ulong, ulong> operation)
    {
        var type = width == 64 ? OpType.B64 : OpType.B32;
        var mask = width == 64 ? ulong.MaxValue : Low32;
        return Sop2(mnemonic, type, type, type, (in ScalarInputs s) =>
        {
            var value = operation(s.S0, s.S1) & mask;
            return new ScalarResult(value, value != 0);
        }, DScc);
    }

    /// <summary>A SAVEEXEC op: D = EXEC; EXEC = op(S0, EXEC); SCC = (EXEC != 0). The B32 forms
    /// work on EXEC_LO.</summary>
    private static ScalarOp SaveExec(string mnemonic, int width, Func<ulong, ulong, ulong> operation)
    {
        var type = width == 64 ? OpType.B64 : OpType.B32;
        var mask = width == 64 ? ulong.MaxValue : Low32;
        return Sop1(mnemonic, type, type, (in ScalarInputs s) =>
        {
            var exec = s.Exec & mask;
            var next = operation(s.S0, exec) & mask;
            return new ScalarResult(exec, next != 0, next);
        }, Exec);
    }

    /// <summary>A WREXEC op: EXEC = op(S0, EXEC); D = EXEC; SCC = (EXEC != 0).</summary>
    private static ScalarOp WrExec(string mnemonic, int width, Func<ulong, ulong, ulong> operation)
    {
        var type = width == 64 ? OpType.B64 : OpType.B32;
        var mask = width == 64 ? ulong.MaxValue : Low32;
        return Sop1(mnemonic, type, type, (in ScalarInputs s) =>
        {
            var next = operation(s.S0, s.Exec & mask) & mask;
            return new ScalarResult(next, next != 0, next);
        }, Exec);
    }

    [OpCategory("scalar")]
    private static IEnumerable<object> Scalar()
    {
        const OpType b32 = OpType.B32, b64 = OpType.B64, u32 = OpType.U32, i32 = OpType.I32, u64 = OpType.U64, i64 = OpType.I64;

        // ---- SOP2 (ISA 12.1) ----
        yield return Sop2("s_add_u32", u32, u32, u32, static (in ScalarInputs s) =>
        {
            var sum = s.S0 + s.S1;
            return new ScalarResult(sum & Low32, sum >= 0x100000000ul);
        }, DScc);
        yield return Sop2("s_sub_u32", u32, u32, u32,
            static (in ScalarInputs s) => new ScalarResult((s.S0 - s.S1) & Low32, s.S1 > s.S0), DScc);
        yield return Sop2("s_add_i32", i32, i32, i32, static (in ScalarInputs s) =>
        {
            var d = (s.S0 + s.S1) & Low32;
            var a = s.S0 >> 31;
            return new ScalarResult(d, a == s.S1 >> 31 && a != d >> 31);
        }, DScc);
        yield return Sop2("s_sub_i32", i32, i32, i32, static (in ScalarInputs s) =>
        {
            var d = (s.S0 - s.S1) & Low32;
            var a = s.S0 >> 31;
            return new ScalarResult(d, a != s.S1 >> 31 && a != d >> 31);
        }, DScc);
        yield return Sop2("s_addc_u32", u32, u32, u32, static (in ScalarInputs s) =>
        {
            var sum = s.S0 + s.S1 + (s.SccIn ? 1ul : 0);
            return new ScalarResult(sum & Low32, sum >= 0x100000000ul);
        }, DScc);
        yield return Sop2("s_subb_u32", u32, u32, u32, static (in ScalarInputs s) =>
        {
            var borrow = s.SccIn ? 1ul : 0;
            return new ScalarResult((s.S0 - s.S1 - borrow) & Low32, s.S1 + borrow > s.S0);
        }, DScc);
        yield return Sop2("s_min_i32", i32, i32, i32, static (in ScalarInputs s) =>
        {
            var less = (int)s.S0 < (int)s.S1;
            return new ScalarResult(less ? s.S0 : s.S1, less);
        }, DScc);
        yield return Sop2("s_min_u32", u32, u32, u32, static (in ScalarInputs s) =>
        {
            var less = s.S0 < s.S1;
            return new ScalarResult(less ? s.S0 : s.S1, less);
        }, DScc);
        yield return Sop2("s_max_i32", i32, i32, i32, static (in ScalarInputs s) =>
        {
            var greater = (int)s.S0 > (int)s.S1;
            return new ScalarResult(greater ? s.S0 : s.S1, greater);
        }, DScc);
        yield return Sop2("s_max_u32", u32, u32, u32, static (in ScalarInputs s) =>
        {
            var greater = s.S0 > s.S1;
            return new ScalarResult(greater ? s.S0 : s.S1, greater);
        }, DScc);
        yield return Sop2("s_cselect_b32", b32, b32, b32, static (in ScalarInputs s) => new ScalarResult(s.SccIn ? s.S0 : s.S1), D);
        yield return Sop2("s_cselect_b64", b64, b64, b64, static (in ScalarInputs s) => new ScalarResult(s.SccIn ? s.S0 : s.S1), D);
        foreach (var width in (int[])[32, 64])
        {
            var suffix = width == 32 ? "_b32" : "_b64";
            yield return Bitwise("s_and" + suffix, width, static (a, b) => a & b);
            yield return Bitwise("s_or" + suffix, width, static (a, b) => a | b);
            yield return Bitwise("s_xor" + suffix, width, static (a, b) => a ^ b);
            yield return Bitwise("s_andn2" + suffix, width, static (a, b) => a & ~b);
            yield return Bitwise("s_orn2" + suffix, width, static (a, b) => a | ~b);
            yield return Bitwise("s_nand" + suffix, width, static (a, b) => ~(a & b));
            yield return Bitwise("s_nor" + suffix, width, static (a, b) => ~(a | b));
            yield return Bitwise("s_xnor" + suffix, width, static (a, b) => ~(a ^ b));
        }

        yield return Sop2("s_lshl_b32", b32, b32, u32, static (in ScalarInputs s) => WithScc((s.S0 << (int)(s.S1 & 31)) & Low32, 32), DScc);
        yield return Sop2("s_lshl_b64", b64, b64, u32, static (in ScalarInputs s) => WithScc(s.S0 << (int)(s.S1 & 63), 64), DScc);
        yield return Sop2("s_lshr_b32", b32, b32, u32, static (in ScalarInputs s) => WithScc(s.S0 >> (int)(s.S1 & 31), 32), DScc);
        yield return Sop2("s_lshr_b64", b64, b64, u32, static (in ScalarInputs s) => WithScc(s.S0 >> (int)(s.S1 & 63), 64), DScc);
        yield return Sop2("s_ashr_i32", i32, i32, u32,
            static (in ScalarInputs s) => WithScc((ulong)((int)s.S0 >> (int)(s.S1 & 31)) & Low32, 32), DScc);
        yield return Sop2("s_ashr_i64", i64, i64, u32,
            static (in ScalarInputs s) => WithScc((ulong)((long)s.S0 >> (int)(s.S1 & 63)), 64), DScc);
        yield return Sop2("s_bfm_b32", b32, u32, u32,
            static (in ScalarInputs s) => new ScalarResult((((1ul << (int)(s.S0 & 31)) - 1) << (int)(s.S1 & 31)) & Low32), D);
        yield return Sop2("s_bfm_b64", b64, u32, u32,
            static (in ScalarInputs s) => new ScalarResult(((1ul << (int)(s.S0 & 63)) - 1) << (int)(s.S1 & 63)), D);
        yield return Sop2("s_mul_i32", i32, i32, i32, static (in ScalarInputs s) => new ScalarResult((s.S0 * s.S1) & Low32), D);
        // S_BFE: S1[4:0] (S1[5:0] for 64 bits) is the offset, S1[22:16] the width; the
        // "(1 << width) - 1" mask is taken at unbounded precision, so a width at or past the
        // operand width keeps every bit shifted down.
        yield return Sop2("s_bfe_u32", u32, u32, u32, static (in ScalarInputs s) =>
            WithScc((s.S0 >> (int)(s.S1 & 31)) & FieldMask((int)((s.S1 >> 16) & 0x7F)) & Low32, 32), DScc) with { Pool = BfePool };
        yield return Sop2("s_bfe_i32", i32, i32, u32, static (in ScalarInputs s) =>
        {
            var width = (int)((s.S1 >> 16) & 0x7F);
            var shifted = (ulong)((long)(int)s.S0 >> (int)(s.S1 & 31));
            var value = width == 0 ? 0 : SignExtend(shifted & FieldMask(width), width);
            return WithScc(value & Low32, 32);
        }, DScc) with { Pool = BfePool };
        yield return Sop2("s_bfe_u64", u64, u64, u32, static (in ScalarInputs s) =>
            WithScc((s.S0 >> (int)(s.S1 & 63)) & FieldMask((int)((s.S1 >> 16) & 0x7F)), 64), DScc) with { Pool = BfePool };
        yield return Sop2("s_bfe_i64", i64, i64, u32, static (in ScalarInputs s) =>
        {
            var width = (int)((s.S1 >> 16) & 0x7F);
            var shifted = (ulong)((long)s.S0 >> (int)(s.S1 & 63));
            return WithScc(width == 0 ? 0 : SignExtend(shifted & FieldMask(width), width), 64);
        }, DScc) with { Pool = BfePool };
        yield return Sop2("s_absdiff_i32", i32, i32, i32, static (in ScalarInputs s) =>
        {
            var difference = (int)(uint)((s.S0 - s.S1) & Low32);
            var value = (ulong)(uint)(difference < 0 ? unchecked(-difference) : difference);
            return WithScc(value, 32);
        }, DScc);
        for (var shift = 1; shift <= 4; shift++)
        {
            var n = shift;
            yield return Sop2($"s_lshl{n}_add_u32", u32, u32, u32, (in ScalarInputs s) =>
            {
                var sum = (s.S0 << n) + s.S1;
                return new ScalarResult(sum & Low32, sum >= 0x100000000ul);
            }, DScc);
        }

        yield return Sop2("s_pack_ll_b32_b16", b32, b32, b32,
            static (in ScalarInputs s) => new ScalarResult(((s.S1 & 0xFFFF) << 16) | (s.S0 & 0xFFFF)), D);
        yield return Sop2("s_pack_lh_b32_b16", b32, b32, b32,
            static (in ScalarInputs s) => new ScalarResult((s.S1 & 0xFFFF0000) | (s.S0 & 0xFFFF)), D);
        yield return Sop2("s_pack_hh_b32_b16", b32, b32, b32,
            static (in ScalarInputs s) => new ScalarResult((s.S1 & 0xFFFF0000) | (s.S0 >> 16)), D);
        yield return Sop2("s_mul_hi_u32", u32, u32, u32, static (in ScalarInputs s) => new ScalarResult((s.S0 * s.S1) >> 32), D);
        yield return Sop2("s_mul_hi_i32", i32, i32, i32,
            static (in ScalarInputs s) => new ScalarResult((ulong)(((long)(int)s.S0 * (int)s.S1) >> 32) & Low32), D);

        // ---- SOPK (ISA 12.2) ----
        yield return Sopk("s_movk_i32", [], static (in ScalarInputs s) => new ScalarResult(SignExtend(s.Imm, 16) & Low32), D, SopkImmediates);
        yield return Sopk("s_cmovk_i32", [i32],
            static (in ScalarInputs s) => new ScalarResult(s.SccIn ? SignExtend(s.Imm, 16) & Low32 : s.Old), D | ScalarFlags.DestinationIsSource,
            SopkImmediates);
        foreach (var (name, compare) in new (string, Func<long, long, bool>)[]
                 {
                     ("eq", static (a, b) => a == b), ("lg", static (a, b) => a != b),
                     ("gt", static (a, b) => a > b), ("ge", static (a, b) => a >= b),
                     ("lt", static (a, b) => a < b), ("le", static (a, b) => a <= b),
                 })
        {
            // The I32 forms compare against signext(SIMM16), the U32 forms against SIMM16.
            yield return Sopk($"s_cmpk_{name}_i32", [i32],
                (in ScalarInputs s) => new ScalarResult(0, compare((int)s.S0, (long)SignExtend(s.Imm, 16))), Scc, SopkCompareImmediates);
            yield return Sopk($"s_cmpk_{name}_u32", [u32],
                (in ScalarInputs s) => new ScalarResult(0, compare((long)(uint)s.S0, s.Imm)), Scc, SopkCompareImmediates);
        }

        yield return Sopk("s_addk_i32", [i32], static (in ScalarInputs s) =>
        {
            var old = s.Old & Low32;
            var d = (old + SignExtend(s.Imm, 16)) & Low32;
            var oldSign = old >> 31;
            return new ScalarResult(d, oldSign == (ulong)(s.Imm >> 15) && oldSign != d >> 31);
        }, DScc | ScalarFlags.DestinationIsSource, SopkImmediates);
        yield return Sopk("s_mulk_i32", [i32],
            static (in ScalarInputs s) => new ScalarResult((s.Old * SignExtend(s.Imm, 16)) & Low32), D | ScalarFlags.DestinationIsSource,
            SopkImmediates);

        // ---- SOP1 (ISA 12.3) ----
        yield return Sop1("s_mov_b32", b32, b32, static (in ScalarInputs s) => new ScalarResult(s.S0), D);
        yield return Sop1("s_mov_b64", b64, b64, static (in ScalarInputs s) => new ScalarResult(s.S0), D);
        yield return Sop1("s_cmov_b32", b32, b32, static (in ScalarInputs s) => new ScalarResult(s.SccIn ? s.S0 : s.Old), D);
        yield return Sop1("s_cmov_b64", b64, b64, static (in ScalarInputs s) => new ScalarResult(s.SccIn ? s.S0 : s.Old), D);
        yield return Sop1("s_not_b32", b32, b32, static (in ScalarInputs s) => WithScc(~s.S0 & Low32, 32), DScc);
        yield return Sop1("s_not_b64", b64, b64, static (in ScalarInputs s) => WithScc(~s.S0, 64), DScc);
        foreach (var width in (int[])[32, 64])
        {
            var bits = width;
            var type = width == 64 ? b64 : b32;
            var suffix = width == 64 ? "b64" : "b32";
            // S_WQM: D[i] = (S0[(i & ~3):(i | 3)] != 0).
            yield return Sop1($"s_wqm_{suffix}", type, type, (in ScalarInputs s) =>
            {
                var value = 0ul;
                for (var i = 0; i < bits; i++)
                {
                    if (((s.S0 >> (i & ~3)) & 0xF) != 0) value |= 1ul << i;
                }

                return WithScc(value, (ulong)bits);
            }, DScc);
            yield return Sop1($"s_brev_{suffix}", type, type, (in ScalarInputs s) =>
            {
                var value = 0ul;
                for (var i = 0; i < bits; i++)
                {
                    if (((s.S0 >> i) & 1) != 0) value |= 1ul << (bits - 1 - i);
                }

                return new ScalarResult(value);
            }, D);
            yield return Sop1($"s_bcnt0_i32_{suffix}", i32, type,
                (in ScalarInputs s) => WithScc((ulong)(bits - BitOperations.PopCount(s.S0)), 32), DScc);
            yield return Sop1($"s_bcnt1_i32_{suffix}", i32, type,
                (in ScalarInputs s) => WithScc((ulong)BitOperations.PopCount(s.S0), 32), DScc);
            // S_FF0/S_FF1: the first zero/one from the LSB, or -1.
            yield return Sop1($"s_ff0_i32_{suffix}", i32, type, (in ScalarInputs s) => new ScalarResult(FirstFrom(s.S0, bits, lsb: true, one: false)), D);
            yield return Sop1($"s_ff1_i32_{suffix}", i32, type, (in ScalarInputs s) => new ScalarResult(FirstFrom(s.S0, bits, lsb: true, one: true)), D);
            // S_FLBIT_I32_B32/B64: zeros before the first one from the MSB, or -1.
            yield return Sop1($"s_flbit_i32_{suffix}", i32, type, (in ScalarInputs s) => new ScalarResult(FirstFrom(s.S0, bits, lsb: false, one: true)), D);
            // S_BITSET0/1: D.u[S0.u[4:0]] (D.u64[S0.u[5:0]]) = 0/1, the other bits of D kept.
            yield return Sop1($"s_bitset0_{suffix}", type, u32,
                (in ScalarInputs s) => new ScalarResult(s.Old & ~(1ul << (int)(s.S0 & (ulong)(bits - 1)))), D);
            yield return Sop1($"s_bitset1_{suffix}", type, u32,
                (in ScalarInputs s) => new ScalarResult(s.Old | (1ul << (int)(s.S0 & (ulong)(bits - 1)))), D);
            // S_QUADMASK: D[i] = (S0[i*4+3:i*4] != 0) for i < bits / 4.
            yield return Sop1($"s_quadmask_{suffix}", type, type, (in ScalarInputs s) =>
            {
                var value = 0ul;
                for (var i = 0; i < bits / 4; i++)
                {
                    if (((s.S0 >> (i * 4)) & 0xF) != 0) value |= 1ul << i;
                }

                return WithScc(value, (ulong)bits);
            }, DScc);

            // EXEC ops: B64 on EXEC (wave64), B32 on EXEC_LO (wave32).
            yield return SaveExec($"s_and_saveexec_{suffix}", width, static (s0, exec) => s0 & exec);
            yield return SaveExec($"s_or_saveexec_{suffix}", width, static (s0, exec) => s0 | exec);
            yield return SaveExec($"s_xor_saveexec_{suffix}", width, static (s0, exec) => s0 ^ exec);
            yield return SaveExec($"s_andn2_saveexec_{suffix}", width, static (s0, exec) => s0 & ~exec);
            yield return SaveExec($"s_orn2_saveexec_{suffix}", width, static (s0, exec) => s0 | ~exec);
            yield return SaveExec($"s_nand_saveexec_{suffix}", width, static (s0, exec) => ~(s0 & exec));
            yield return SaveExec($"s_nor_saveexec_{suffix}", width, static (s0, exec) => ~(s0 | exec));
            yield return SaveExec($"s_xnor_saveexec_{suffix}", width, static (s0, exec) => ~(s0 ^ exec));
            yield return SaveExec($"s_andn1_saveexec_{suffix}", width, static (s0, exec) => ~s0 & exec);
            yield return SaveExec($"s_orn1_saveexec_{suffix}", width, static (s0, exec) => ~s0 | exec);
            yield return WrExec($"s_andn1_wrexec_{suffix}", width, static (s0, exec) => ~s0 & exec);
            yield return WrExec($"s_andn2_wrexec_{suffix}", width, static (s0, exec) => s0 & ~exec);
        }

        // S_FLBIT_I32 / S_FLBIT_I32_I64: bits after the sign bit equal to it, or -1 if all are.
        yield return Sop1("s_flbit_i32", i32, i32, static (in ScalarInputs s) => new ScalarResult(SignRun(s.S0, 32)), D);
        yield return Sop1("s_flbit_i32_i64", i32, i64, static (in ScalarInputs s) => new ScalarResult(SignRun(s.S0, 64)), D);
        yield return Sop1("s_sext_i32_i8", i32, i32, static (in ScalarInputs s) => new ScalarResult(SignExtend(s.S0 & 0xFF, 8) & Low32), D);
        yield return Sop1("s_sext_i32_i16", i32, i32, static (in ScalarInputs s) => new ScalarResult(SignExtend(s.S0 & 0xFFFF, 16) & Low32), D);
        yield return Sop1("s_abs_i32", i32, i32, static (in ScalarInputs s) =>
        {
            var value = (int)(uint)s.S0;
            return WithScc((ulong)(uint)(value < 0 ? unchecked(-value) : value), 32);
        }, DScc);
        yield return Sop1("s_bitreplicate_b64_b32", b64, b32, static (in ScalarInputs s) =>
        {
            var value = 0ul;
            for (var i = 0; i < 32; i++)
            {
                if (((s.S0 >> i) & 1) != 0) value |= 3ul << (i * 2);
            }

            return new ScalarResult(value);
        }, D);

        // ---- SOPC (ISA 12.4) ----
        foreach (var (name, compare) in new (string, Func<long, long, bool>)[]
                 {
                     ("eq", static (a, b) => a == b), ("lg", static (a, b) => a != b), ("gt", static (a, b) => a > b),
                     ("ge", static (a, b) => a >= b), ("lt", static (a, b) => a < b), ("le", static (a, b) => a <= b),
                 })
        {
            yield return Sopc($"s_cmp_{name}_i32", i32, i32, (a, b) => compare((int)a, (int)b));
            yield return Sopc($"s_cmp_{name}_u32", u32, u32, (a, b) => compare((long)a, (long)b));
        }

        yield return Sopc("s_bitcmp0_b32", b32, u32, static (a, b) => ((a >> (int)(b & 31)) & 1) == 0);
        yield return Sopc("s_bitcmp1_b32", b32, u32, static (a, b) => ((a >> (int)(b & 31)) & 1) == 1);
        yield return Sopc("s_bitcmp0_b64", b64, u32, static (a, b) => ((a >> (int)(b & 63)) & 1) == 0);
        yield return Sopc("s_bitcmp1_b64", b64, u32, static (a, b) => ((a >> (int)(b & 63)) & 1) == 1);
        yield return Sopc("s_cmp_eq_u64", u64, u64, static (a, b) => a == b);
        yield return Sopc("s_cmp_lg_u64", u64, u64, static (a, b) => a != b);
    }

    private static IReadOnlyList<ulong> BfePool(int source) => source == 1 ? BitfieldControls : null!;

    /// <summary>The index of the first one (or zero) searching from the LSB, or the count of
    /// bits before it searching from the MSB; -1 (0xFFFFFFFF) when there is none.</summary>
    private static ulong FirstFrom(ulong value, int bits, bool lsb, bool one)
    {
        for (var i = 0; i < bits; i++)
        {
            var bit = lsb ? i : bits - 1 - i;
            if ((((value >> bit) & 1) != 0) == one) return (ulong)i;
        }

        return Low32;
    }

    private static ulong SignRun(ulong value, int bits)
    {
        var sign = (value >> (bits - 1)) & 1;
        for (var i = 1; i < bits; i++)
        {
            if (((value >> (bits - 1 - i)) & 1) != sign) return (ulong)i;
        }

        return Low32;
    }
}
