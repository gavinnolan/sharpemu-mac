// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using static SharpEmu.ShaderCompiler.Metal.Tests.Conformance.Semantics;

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

/// <summary>Integer and bitwise VALU opcodes (VOP1, VOP2, VOP3A/B), written from the RDNA2 ISA
/// chapter 12 pseudo-code. Integer CLAMP saturates the exact result to the destination type's
/// range (ISA 6.5).</summary>
internal static partial class Rdna2Ops
{
    // Operand pools for sources whose interesting values are not the type's edges.
    private static readonly ulong[] Int24Edges =
    [
        0, 1, 2, 0x7FFFFF, 0x800000, 0x800001, 0xFFFFFF, 0x1000000, 0x1000001, 0xFFFFFFFF, 0xFF800000,
        0xFFFFFFFE, 0x7FFFFFFF, 0x80000000, 0x00ABCDEF, 0xAB123456, 0x12FEDCBA, 0x5A800000, 0x00400000, 0xC0FFFFFF,
    ];

    /// <summary>Field offsets and widths, shift and align amounts: 0..33 and values whose ignored
    /// high bits are set.</summary>
    private static readonly ulong[] SmallAmounts =
    [
        0, 1, 2, 3, 4, 7, 8, 15, 16, 17, 23, 24, 30, 31, 32, 33, 63, 64, 0xFFFFFFE5, 0xFFFFFFFF, 0x80000010, 0x123,
    ];

    /// <summary>V_PERM_B32 selectors: every byte selector 0..15 plus out-of-range ones.</summary>
    private static readonly ulong[] PermSelectors = PermSelectorPool();

    private static ulong[] PermSelectorPool()
    {
        var values = new List<ulong>
        {
            0x03020100, 0x07060504, 0x0B0A0908, 0x0F0E0D0C, 0x0C0C0C0C, 0x00010203, 0x04050607, 0x08090A0B,
            0x10203040, 0xFFFFFFFF, 0x0D0E0F80,
        };
        var random = new Random(0x9E2);
        for (var index = 0; index < 21; index++)
        {
            ulong value = 0;
            for (var b = 0; b < 4; b++) value |= (ulong)random.Next(16) << (8 * b);
            values.Add(value);
        }

        return [.. values];
    }

    private static Func<int, IReadOnlyList<ulong>> PoolAt(int index, IReadOnlyList<ulong> pool) =>
        source => source == index ? pool : null!;

    private static Func<int, IReadOnlyList<ulong>> PoolAt(int first, IReadOnlyList<ulong> firstPool, int second, IReadOnlyList<ulong> secondPool) =>
        source => source == first ? firstPool : source == second ? secondPool : null!;

    private static LaneFunction U32(Func<uint, uint, uint, uint> operation) =>
        (in LaneInputs lane) => new LaneResult(operation((uint)lane.S0, (uint)lane.S1, (uint)lane.S2));

    /// <summary>An op computed exactly (wide) and either wrapped to the width or, with CLAMP,
    /// saturated to the width's signed or unsigned range (ISA 6.5).</summary>
    private static LaneFunction Saturating(int width, bool signed, Func<LaneInputs, Int128> exact) =>
        (in LaneInputs lane) =>
        {
            var value = exact(lane);
            if (lane.Clamp) return new LaneResult(signed ? SaturateSigned(value, width) : SaturateUnsigned(value, width));
            var mask = width == 64 ? ulong.MaxValue : (1ul << width) - 1;
            return new LaneResult((ulong)(value & mask));
        };

    private static Int128 S16(ulong value) => (short)(ushort)value;
    private static Int128 U16(ulong value) => (ushort)value;
    private static Int128 S32(ulong value) => (int)(uint)value;
    private static Int128 U32Of(ulong value) => (uint)value;
    private static long S24(ulong value) => ((int)((uint)value << 8)) >> 8;
    private static ulong U24(ulong value) => value & 0xFFFFFF;

    private static uint AbsDiff(uint x, uint y) => x > y ? x - y : y - x;

    /// <summary>V_SAD_U8 (ISA 12.12): S2 plus the absolute differences of the four bytes.</summary>
    private static Int128 SadU8(uint s0, uint s1, Int128 accumulator, bool masked)
    {
        var sum = accumulator;
        for (var b = 0; b < 4; b++)
        {
            var x = (s0 >> (8 * b)) & 0xFF;
            var y = (s1 >> (8 * b)) & 0xFF;
            if (masked && y == 0) continue;
            sum += AbsDiff(x, y);
        }

        return sum;
    }

    /// <summary>V_QSAD_PK_U16_U8 / V_MQSAD_PK_U16_U8 (ISA 12.12): four SADs of the byte windows
    /// S0[31:0], S0[39:8], S0[47:16], S0[55:24] against S1, each accumulated into a 16-bit field
    /// of S2; CLAMP saturates each field.</summary>
    private static LaneFunction QuadSad(bool masked) =>
        (in LaneInputs lane) =>
        {
            ulong result = 0;
            for (var field = 0; field < 4; field++)
            {
                var window = (uint)(lane.S0 >> (8 * field));
                var sum = SadU8(window, (uint)lane.S1, (lane.S2 >> (16 * field)) & 0xFFFF, masked);
                var bits = lane.Clamp ? SaturateUnsigned(sum, 16) : (ulong)(sum & 0xFFFF);
                result |= bits << (16 * field);
            }

            return new LaneResult(result);
        };

    private static uint BitReverse(uint value)
    {
        uint result = 0;
        for (var bit = 0; bit < 32; bit++)
        {
            if ((value >> bit & 1) != 0) result |= 1u << (31 - bit);
        }

        return result;
    }

    private static long Min3(long a, long b, long c) => Math.Min(Math.Min(a, b), c);
    private static long Max3(long a, long b, long c) => Math.Max(Math.Max(a, b), c);

    /// <summary>V_MED3 (ISA 12.12): when S0 is the maximum the result is max(S1, S2), when S1 is
    /// the maximum max(S0, S2), otherwise max(S0, S1).</summary>
    private static long Med3(long a, long b, long c)
    {
        var max = Max3(a, b, c);
        if (max == a) return Math.Max(b, c);
        if (max == b) return Math.Max(a, c);
        return Math.Max(a, b);
    }

    /// <summary>byte_permute of V_PERM_B32 (ISA 12.12): in[0..3] are S1's bytes, in[4..7] S0's.</summary>
    private static ulong PermByte(ulong bytes, uint select)
    {
        if (select >= 13) return 0xFF;
        if (select == 12) return 0x00;
        if (select >= 8)
        {
            var source = (int)(select - 8) * 2 + 1; // 8 -> in[1], 9 -> in[3], 10 -> in[5], 11 -> in[7]
            return (bytes >> (8 * source + 7) & 1) != 0 ? 0xFFul : 0;
        }

        return (bytes >> (8 * (int)select)) & 0xFF;
    }

    [OpCategory("vint")]
    private static IEnumerable<object> VectorInt()
    {
        const VectorEncoding Vop1 = VectorEncoding.Vop1;
        const VectorEncoding Vop2 = VectorEncoding.Vop2;
        const VectorEncoding Vop3 = VectorEncoding.Vop3;
        const VectorFlags Clamp = VectorFlags.IntegerClamp;
        // Ops whose VOP3 clamp the translator rejects outright ("clamped integer results are not
        // supported"): no clamp form is generated, the semantics keep the saturation so the form
        // comes back by turning this into IntegerClamp once the translator implements it.
        const VectorFlags Unclamped = VectorFlags.None;
        const VectorFlags Half = VectorFlags.OpSelDestination;
        OpType[] b32 = [OpType.B32];
        OpType[] u32x2 = [OpType.U32, OpType.U32];
        OpType[] i32x2 = [OpType.I32, OpType.I32];
        OpType[] b32x2 = [OpType.B32, OpType.B32];
        OpType[] u32x3 = [OpType.U32, OpType.U32, OpType.U32];
        OpType[] i32x3 = [OpType.I32, OpType.I32, OpType.I32];
        OpType[] b32x3 = [OpType.B32, OpType.B32, OpType.B32];
        OpType[] u16x2 = [OpType.U16, OpType.U16];
        OpType[] i16x2 = [OpType.I16, OpType.I16];
        OpType[] b16x2 = [OpType.B16, OpType.B16];
        OpType[] u16x3 = [OpType.U16, OpType.U16, OpType.U16];
        OpType[] i16x3 = [OpType.I16, OpType.I16, OpType.I16];

        // ---- VOP1 (ISA 12.8) ----
        yield return new VectorOp("v_mov_b32", Vop1, OpType.B32, b32, U32(static (a, _, _) => a))
            { Citation = "ISA 12.8 V_MOV_B32" };
        yield return new VectorOp("v_not_b32", Vop1, OpType.B32, b32, U32(static (a, _, _) => ~a))
            { Citation = "ISA 12.8 V_NOT_B32" };
        yield return new VectorOp("v_bfrev_b32", Vop1, OpType.B32, b32, U32(static (a, _, _) => BitReverse(a)))
            { Citation = "ISA 12.8 V_BFREV_B32" };
        yield return new VectorOp("v_ffbh_u32", Vop1, OpType.U32, [OpType.U32],
            U32(static (a, _, _) => a == 0 ? 0xFFFFFFFFu : (uint)BitOperations.LeadingZeroCount(a)))
            { Citation = "ISA 12.8 V_FFBH_U32" };
        yield return new VectorOp("v_ffbl_b32", Vop1, OpType.B32, b32,
            U32(static (a, _, _) => a == 0 ? 0xFFFFFFFFu : (uint)BitOperations.TrailingZeroCount(a)))
            { Citation = "ISA 12.8 V_FFBL_B32" };
        yield return new VectorOp("v_ffbh_i32", Vop1, OpType.I32, [OpType.I32],
            U32(static (a, _, _) =>
            {
                // D = the first i in 1..31 where S0[31 - i] differs from the sign bit, else -1.
                var sign = a >> 31;
                for (var i = 1; i <= 31; i++)
                {
                    if ((a >> (31 - i) & 1) != sign) return (uint)i;
                }

                return 0xFFFFFFFFu;
            }))
            { Citation = "ISA 12.8 V_FFBH_I32" };
        yield return new VectorOp("v_sat_pk_u8_i16", Vop1, OpType.B32, b32,
            U32(static (a, _, _) =>
            {
                // D.u32 = {16'b0, sat8(S.u[31:16]), sat8(S.u[15:0])}, each half a signed short.
                static uint Sat8(uint half) => (uint)Math.Clamp((int)(short)(ushort)half, 0, 255);
                return Sat8(a >> 16) << 8 | Sat8(a & 0xFFFF);
            }))
            { Citation = "ISA 12.8 V_SAT_PK_U8_I16" };

        // ---- VOP2 (ISA 12.7) ----
        yield return new VectorOp("v_cndmask_b32", Vop2, OpType.B32, b32x2,
            static (in LaneInputs lane) => new LaneResult(lane.CarryIn ? (uint)lane.S1 : (uint)lane.S0), VectorFlags.ReadsMask)
            { Citation = "ISA 12.7 V_CNDMASK_B32" };
        // V_CNDMASK_B32 with float input modifiers (valid when the sources are f32, ISA 12.7):
        // abs and neg act on the sign bit only.
        var cndmaskFloat = new VectorOp("v_cndmask_b32", Vop2, OpType.B32, [OpType.F32, OpType.F32],
            static (in LaneInputs lane) => new LaneResult(lane.CarryIn ? (uint)lane.S1 : (uint)lane.S0), VectorFlags.ReadsMask)
            { Citation = "ISA 12.7 V_CNDMASK_B32 (float input modifiers)" };
        yield return new VectorCase(new VectorForm(cndmaskFloat, FormKind.E64) { Neg = 0b01, Abs = 0b10 }, FormGenerator.BothWaves, FormGenerator.DefaultMode);
        yield return new VectorCase(new VectorForm(cndmaskFloat, FormKind.E64) { Neg = 0b10, Abs = 0b11 }, FormGenerator.Wave32, FormGenerator.DefaultMode);

        yield return new VectorOp("v_mul_i32_i24", Vop2, OpType.I32, i32x2,
            Saturating(32, true, static lane => (Int128)S24(lane.S0) * S24(lane.S1)), Unclamped)
            { Citation = "ISA 12.7 V_MUL_I32_I24", Pool = PoolAt(0, Int24Edges, 1, Int24Edges) };
        yield return new VectorOp("v_mul_hi_i32_i24", Vop2, OpType.I32, i32x2,
            static (in LaneInputs lane) => new LaneResult((uint)((S24(lane.S0) * S24(lane.S1)) >> 32)))
            { Citation = "ISA 12.7 V_MUL_HI_I32_I24", Pool = PoolAt(0, Int24Edges, 1, Int24Edges) };
        yield return new VectorOp("v_mul_u32_u24", Vop2, OpType.U32, u32x2,
            Saturating(32, false, static lane => (Int128)U24(lane.S0) * U24(lane.S1)), Unclamped)
            { Citation = "ISA 12.7 V_MUL_U32_U24", Pool = PoolAt(0, Int24Edges, 1, Int24Edges) };
        yield return new VectorOp("v_mul_hi_u32_u24", Vop2, OpType.U32, u32x2,
            static (in LaneInputs lane) => new LaneResult((uint)((U24(lane.S0) * U24(lane.S1)) >> 32)))
            { Citation = "ISA 12.7 V_MUL_HI_U32_U24", Pool = PoolAt(0, Int24Edges, 1, Int24Edges) };
        yield return new VectorOp("v_min_i32", Vop2, OpType.I32, i32x2, U32(static (a, b, _) => (int)a < (int)b ? a : b))
            { Citation = "ISA 12.7 V_MIN_I32" };
        yield return new VectorOp("v_max_i32", Vop2, OpType.I32, i32x2, U32(static (a, b, _) => (int)a >= (int)b ? a : b))
            { Citation = "ISA 12.7 V_MAX_I32" };
        yield return new VectorOp("v_min_u32", Vop2, OpType.U32, u32x2, U32(static (a, b, _) => a < b ? a : b))
            { Citation = "ISA 12.7 V_MIN_U32" };
        yield return new VectorOp("v_max_u32", Vop2, OpType.U32, u32x2, U32(static (a, b, _) => a >= b ? a : b))
            { Citation = "ISA 12.7 V_MAX_U32" };
        yield return new VectorOp("v_lshrrev_b32", Vop2, OpType.B32, b32x2, U32(static (a, b, _) => b >> (int)(a & 31)))
            { Citation = "ISA 12.7 V_LSHRREV_B32" };
        yield return new VectorOp("v_ashrrev_i32", Vop2, OpType.I32, [OpType.U32, OpType.I32], U32(static (a, b, _) => (uint)((int)b >> (int)(a & 31))))
            { Citation = "ISA 12.7 V_ASHRREV_I32" };
        yield return new VectorOp("v_lshlrev_b32", Vop2, OpType.B32, b32x2, U32(static (a, b, _) => b << (int)(a & 31)))
            { Citation = "ISA 12.7 V_LSHLREV_B32" };
        yield return new VectorOp("v_and_b32", Vop2, OpType.B32, b32x2, U32(static (a, b, _) => a & b))
            { Citation = "ISA 12.7 V_AND_B32" };
        yield return new VectorOp("v_or_b32", Vop2, OpType.B32, b32x2, U32(static (a, b, _) => a | b))
            { Citation = "ISA 12.7 V_OR_B32" };
        yield return new VectorOp("v_xor_b32", Vop2, OpType.B32, b32x2, U32(static (a, b, _) => a ^ b))
            { Citation = "ISA 12.7 V_XOR_B32" };
        yield return new VectorOp("v_xnor_b32", Vop2, OpType.B32, b32x2, U32(static (a, b, _) => ~(a ^ b)))
            { Citation = "ISA 12.7 V_XNOR_B32" };
        yield return new VectorOp("v_add_nc_u32", Vop2, OpType.U32, u32x2,
            Saturating(32, false, static lane => U32Of(lane.S0) + U32Of(lane.S1)), Clamp)
            { Citation = "ISA 12.7 V_ADD_NC_U32, 6.5" };
        yield return new VectorOp("v_sub_nc_u32", Vop2, OpType.U32, u32x2,
            Saturating(32, false, static lane => U32Of(lane.S0) - U32Of(lane.S1)), Clamp)
            { Citation = "ISA 12.7 V_SUB_NC_U32, 6.5" };
        yield return new VectorOp("v_subrev_nc_u32", Vop2, OpType.U32, u32x2,
            Saturating(32, false, static lane => U32Of(lane.S1) - U32Of(lane.S0)), Clamp)
            { Citation = "ISA 12.7 V_SUBREV_NC_U32, 6.5" };
        // Carry in from VCC (VOP3: the SGPR mask in S2), carry out to VCC (VOP3: SDST).
        yield return new VectorOp("v_add_co_ci_u32", Vop2, OpType.U32, u32x2,
            static (in LaneInputs lane) =>
            {
                var sum = U32Of(lane.S0) + U32Of(lane.S1) + (lane.CarryIn ? 1 : 0);
                var value = lane.Clamp ? SaturateUnsigned(sum, 32) : (ulong)(sum & 0xFFFFFFFF);
                return new LaneResult(value, sum >= 0x100000000L);
            }, VectorFlags.CarryIn | VectorFlags.CarryOut | Clamp)
            { Citation = "ISA 12.7 V_ADD_CO_CI_U32" };
        yield return new VectorOp("v_sub_co_ci_u32", Vop2, OpType.U32, u32x2,
            static (in LaneInputs lane) =>
            {
                var difference = U32Of(lane.S0) - U32Of(lane.S1) - (lane.CarryIn ? 1 : 0);
                var value = lane.Clamp ? SaturateUnsigned(difference, 32) : (ulong)(difference & 0xFFFFFFFF);
                return new LaneResult(value, U32Of(lane.S1) + (lane.CarryIn ? 1 : 0) > U32Of(lane.S0));
            }, VectorFlags.CarryIn | VectorFlags.CarryOut | Clamp)
            { Citation = "ISA 12.7 V_SUB_CO_CI_U32" };
        // The ISA (12.7, and RDNA3's) repeats V_SUB_CO_CI's carry condition here; the borrow of
        // S1 - S0 - VCC is S0 + VCC > S1, as LLVM's commuting of V_SUBB_U32 <-> V_SUBBREV_U32
        // (VOP2Instructions.td, RevOp) relies on.
        yield return new VectorOp("v_subrev_co_ci_u32", Vop2, OpType.U32, u32x2,
            static (in LaneInputs lane) =>
            {
                var difference = U32Of(lane.S1) - U32Of(lane.S0) - (lane.CarryIn ? 1 : 0);
                var value = lane.Clamp ? SaturateUnsigned(difference, 32) : (ulong)(difference & 0xFFFFFFFF);
                return new LaneResult(value, U32Of(lane.S0) + (lane.CarryIn ? 1 : 0) > U32Of(lane.S1));
            }, VectorFlags.CarryIn | VectorFlags.CarryOut | Clamp)
            { Citation = "ISA 12.7 V_SUBREV_CO_CI_U32 (borrow per LLVM SUBB/SUBBREV RevOp)" };

        // ---- VOP3-only (ISA 12.12) ----
        yield return new VectorOp("v_mad_i32_i24", Vop3, OpType.I32, i32x3,
            Saturating(32, true, static lane => (Int128)S24(lane.S0) * S24(lane.S1) + S32(lane.S2)), Unclamped)
            { Citation = "ISA 12.12 V_MAD_I32_I24", Pool = PoolAt(0, Int24Edges, 1, Int24Edges) };
        yield return new VectorOp("v_mad_u32_u24", Vop3, OpType.U32, u32x3,
            Saturating(32, false, static lane => (Int128)U24(lane.S0) * U24(lane.S1) + U32Of(lane.S2)), Unclamped)
            { Citation = "ISA 12.12 V_MAD_U32_U24", Pool = PoolAt(0, Int24Edges, 1, Int24Edges) };
        yield return new VectorOp("v_bfe_u32", Vop3, OpType.U32, u32x3,
            U32(static (a, offset, width) =>
            {
                var w = (int)(width & 31);
                return (uint)((a >> (int)(offset & 31)) & ((1ul << w) - 1));
            }))
            { Citation = "ISA 12.12 V_BFE_U32", Pool = PoolAt(1, SmallAmounts, 2, SmallAmounts) };
        // The ISA 12.12 pseudo-code omits the sign extension the name promises; RDNA3's
        // (16.12 V_BFE_I32) spells it: tmp = (S0.i >> S1[4:0]) & mask; D = signextFromBit(tmp, S2[4:0]).
        yield return new VectorOp("v_bfe_i32", Vop3, OpType.I32, [OpType.I32, OpType.U32, OpType.U32],
            U32(static (a, offset, width) =>
            {
                var w = (int)(width & 31);
                if (w == 0) return 0;
                var field = (uint)(((int)a >> (int)(offset & 31)) & (int)((1u << w) - 1));
                return (uint)((int)(field << (32 - w)) >> (32 - w));
            }))
            { Citation = "ISA 12.12 V_BFE_I32 (sign extension per RDNA3 ISA 16.12)", Pool = PoolAt(1, SmallAmounts, 2, SmallAmounts) };
        yield return new VectorOp("v_bfi_b32", Vop3, OpType.B32, b32x3, U32(static (a, b, c) => (a & b) | (~a & c)))
            { Citation = "ISA 12.12 V_BFI_B32" };
        yield return new VectorOp("v_lerp_u8", Vop3, OpType.U32, u32x3,
            U32(static (a, b, c) =>
            {
                uint result = 0;
                for (var shift = 0; shift < 32; shift += 8)
                {
                    var sum = ((a >> shift) & 0xFF) + ((b >> shift) & 0xFF) + ((c >> shift) & 1);
                    result += (sum >> 1) << shift;
                }

                return result;
            }))
            { Citation = "ISA 12.12 V_LERP_U8" };
        yield return new VectorOp("v_alignbit_b32", Vop3, OpType.B32, b32x3,
            U32(static (a, b, c) => (uint)((((ulong)a << 32) | b) >> (int)(c & 31))))
            { Citation = "ISA 12.12 V_ALIGNBIT_B32", Pool = PoolAt(2, SmallAmounts) };
        // ISA 12.12 writes the byte count as S2.u[4:0]; RDNA3's (16.12) as S2.u[1:0], which is
        // what LLVM's and Mesa's uses of the instruction assume (a byte offset 0..3).
        yield return new VectorOp("v_alignbyte_b32", Vop3, OpType.B32, b32x3,
            U32(static (a, b, c) => (uint)((((ulong)a << 32) | b) >> (int)(8 * (c & 3)))))
            { Citation = "ISA 12.12 V_ALIGNBYTE_B32 (S2[1:0] per RDNA3 ISA 16.12)", Pool = PoolAt(2, SmallAmounts) };
        yield return new VectorOp("v_min3_i32", Vop3, OpType.I32, i32x3, U32(static (a, b, c) => (uint)Min3((int)a, (int)b, (int)c)))
            { Citation = "ISA 12.12 V_MIN3_I32" };
        yield return new VectorOp("v_min3_u32", Vop3, OpType.U32, u32x3, U32(static (a, b, c) => (uint)Min3(a, b, c)))
            { Citation = "ISA 12.12 V_MIN3_U32" };
        yield return new VectorOp("v_max3_i32", Vop3, OpType.I32, i32x3, U32(static (a, b, c) => (uint)Max3((int)a, (int)b, (int)c)))
            { Citation = "ISA 12.12 V_MAX3_I32" };
        yield return new VectorOp("v_max3_u32", Vop3, OpType.U32, u32x3, U32(static (a, b, c) => (uint)Max3(a, b, c)))
            { Citation = "ISA 12.12 V_MAX3_U32" };
        yield return new VectorOp("v_med3_i32", Vop3, OpType.I32, i32x3, U32(static (a, b, c) => (uint)Med3((int)a, (int)b, (int)c)))
            { Citation = "ISA 12.12 V_MED3_I32" };
        yield return new VectorOp("v_med3_u32", Vop3, OpType.U32, u32x3, U32(static (a, b, c) => (uint)Med3(a, b, c)))
            { Citation = "ISA 12.12 V_MED3_U32" };
        yield return new VectorOp("v_sad_u32", Vop3, OpType.U32, u32x3,
            Saturating(32, false, static lane => (Int128)AbsDiff((uint)lane.S0, (uint)lane.S1) + U32Of(lane.S2)), Unclamped)
            { Citation = "ISA 12.12 V_SAD_U32" };
        yield return new VectorOp("v_msad_u8", Vop3, OpType.U32, u32x3,
            Saturating(32, false, static lane => SadU8((uint)lane.S0, (uint)lane.S1, U32Of(lane.S2), masked: true)), Unclamped)
            { Citation = "ISA 12.12 V_MSAD_U8" };
        yield return new VectorOp("v_qsad_pk_u16_u8", Vop3, OpType.B64, [OpType.B64, OpType.B32, OpType.B64], QuadSad(masked: false), Unclamped)
            { Citation = "ISA 12.12 V_QSAD_PK_U16_U8" };
        yield return new VectorOp("v_mqsad_pk_u16_u8", Vop3, OpType.B64, [OpType.B64, OpType.B32, OpType.B64], QuadSad(masked: true), Unclamped)
            { Citation = "ISA 12.12 V_MQSAD_PK_U16_U8" };
        yield return new VectorOp("v_mul_lo_u32", Vop3, OpType.U32, u32x2, U32(static (a, b, _) => a * b))
            { Citation = "ISA 12.12 V_MUL_LO_U32" };
        yield return new VectorOp("v_mul_hi_u32", Vop3, OpType.U32, u32x2, U32(static (a, b, _) => (uint)(((ulong)a * b) >> 32)))
            { Citation = "ISA 12.12 V_MUL_HI_U32" };
        yield return new VectorOp("v_mul_hi_i32", Vop3, OpType.I32, i32x2, U32(static (a, b, _) => (uint)(((long)(int)a * (int)b) >> 32)))
            { Citation = "ISA 12.12 V_MUL_HI_I32" };
        // {vcc_out, D.u64} = S0.u32 * S1.u32 + S2.u64: the carry is bit 64 of the 65-bit sum.
        yield return new VectorOp("v_mad_u64_u32", Vop3, OpType.U64, [OpType.U32, OpType.U32, OpType.U64],
            static (in LaneInputs lane) =>
            {
                var exact = (Int128)(uint)lane.S0 * (uint)lane.S1 + (Int128)lane.S2;
                var value = lane.Clamp ? SaturateUnsigned(exact, 64) : (ulong)(exact & ulong.MaxValue);
                return new LaneResult(value, (exact >> 64 & 1) != 0);
            }, VectorFlags.CarryOut | Unclamped)
            { Citation = "ISA 12.12 V_MAD_U64_U32" };
        // {vcc_out, D.i64} = S0.i32 * S1.i32 + S2.i64: bit 64 of the 65-bit two's-complement sum
        // (RDNA3 16.12 spells the 65-bit signed arithmetic).
        yield return new VectorOp("v_mad_i64_i32", Vop3, OpType.I64, [OpType.I32, OpType.I32, OpType.I64],
            static (in LaneInputs lane) =>
            {
                var exact = (Int128)(int)(uint)lane.S0 * (int)(uint)lane.S1 + (long)lane.S2;
                var value = lane.Clamp ? SaturateSigned(exact, 64) : (ulong)(exact & ulong.MaxValue);
                return new LaneResult(value, (exact >> 64 & 1) != 0);
            }, VectorFlags.CarryOut | Unclamped)
            { Citation = "ISA 12.12 V_MAD_I64_I32" };
        yield return new VectorOp("v_xor3_b32", Vop3, OpType.B32, b32x3, U32(static (a, b, c) => a ^ b ^ c))
            { Citation = "ISA 12.12 V_XOR3_B32" };
        yield return new VectorOp("v_lshlrev_b64", Vop3, OpType.B64, [OpType.U32, OpType.B64],
            static (in LaneInputs lane) => new LaneResult(lane.S1 << (int)(lane.S0 & 63)))
            { Citation = "ISA 12.12 V_LSHLREV_B64" };
        yield return new VectorOp("v_lshrrev_b64", Vop3, OpType.B64, [OpType.U32, OpType.B64],
            static (in LaneInputs lane) => new LaneResult(lane.S1 >> (int)(lane.S0 & 63)))
            { Citation = "ISA 12.12 V_LSHRREV_B64" };
        yield return new VectorOp("v_ashrrev_i64", Vop3, OpType.I64, [OpType.U32, OpType.I64],
            static (in LaneInputs lane) => new LaneResult((ulong)((long)lane.S1 >> (int)(lane.S0 & 63))))
            { Citation = "ISA 12.12 V_ASHRREV_I64" };

        // 16-bit (gfx10: VOP3 only; the result goes to the low half, or the high half by
        // op_sel[3], and the other half is preserved).
        yield return new VectorOp("v_add_nc_u16", Vop3, OpType.U16, u16x2,
            Saturating(16, false, static lane => U16(lane.S0) + U16(lane.S1)), Unclamped | Half)
            { Citation = "ISA 12.12 V_ADD_NC_U16" };
        yield return new VectorOp("v_sub_nc_u16", Vop3, OpType.U16, u16x2,
            Saturating(16, false, static lane => U16(lane.S0) - U16(lane.S1)), Unclamped | Half)
            { Citation = "ISA 12.12 V_SUB_NC_U16" };
        yield return new VectorOp("v_mul_lo_u16", Vop3, OpType.U16, u16x2,
            Saturating(16, false, static lane => U16(lane.S0) * U16(lane.S1)), Clamp | Half)
            { Citation = "ISA 12.12 V_MUL_LO_U16" };
        yield return new VectorOp("v_lshrrev_b16", Vop3, OpType.B16, b16x2,
            static (in LaneInputs lane) => new LaneResult((lane.S1 & 0xFFFF) >> (int)(lane.S0 & 15)), Half)
            { Citation = "ISA 12.12 V_LSHRREV_B16" };
        yield return new VectorOp("v_ashrrev_i16", Vop3, OpType.I16, [OpType.U16, OpType.I16],
            static (in LaneInputs lane) => new LaneResult((ulong)(ushort)((short)(ushort)lane.S1 >> (int)(lane.S0 & 15))), Half)
            { Citation = "ISA 12.12 V_ASHRREV_I16" };
        yield return new VectorOp("v_lshlrev_b16", Vop3, OpType.B16, b16x2,
            static (in LaneInputs lane) => new LaneResult(((lane.S1 & 0xFFFF) << (int)(lane.S0 & 15)) & 0xFFFF), Half)
            { Citation = "ISA 12.12 V_LSHLREV_B16" };
        yield return new VectorOp("v_max_u16", Vop3, OpType.U16, u16x2,
            static (in LaneInputs lane) => new LaneResult((ushort)lane.S0 >= (ushort)lane.S1 ? lane.S0 & 0xFFFF : lane.S1 & 0xFFFF), Half)
            { Citation = "ISA 12.12 V_MAX_U16" };
        yield return new VectorOp("v_max_i16", Vop3, OpType.I16, i16x2,
            static (in LaneInputs lane) => new LaneResult((short)(ushort)lane.S0 >= (short)(ushort)lane.S1 ? lane.S0 & 0xFFFF : lane.S1 & 0xFFFF), Half)
            { Citation = "ISA 12.12 V_MAX_I16" };
        yield return new VectorOp("v_min_u16", Vop3, OpType.U16, u16x2,
            static (in LaneInputs lane) => new LaneResult((ushort)lane.S0 < (ushort)lane.S1 ? lane.S0 & 0xFFFF : lane.S1 & 0xFFFF), Half)
            { Citation = "ISA 12.12 V_MIN_U16" };
        yield return new VectorOp("v_min_i16", Vop3, OpType.I16, i16x2,
            static (in LaneInputs lane) => new LaneResult((short)(ushort)lane.S0 < (short)(ushort)lane.S1 ? lane.S0 & 0xFFFF : lane.S1 & 0xFFFF), Half)
            { Citation = "ISA 12.12 V_MIN_I16" };
        yield return new VectorOp("v_add_nc_i16", Vop3, OpType.I16, i16x2,
            Saturating(16, true, static lane => S16(lane.S0) + S16(lane.S1)), Unclamped | Half)
            { Citation = "ISA 12.12 V_ADD_NC_I16" };
        // ISA 12.12 says "unsigned 16-bit integer domain" for this signed op's saturation, a
        // copy of V_SUB_NC_U16's text; the signed domain is the op's (RDNA3 16.12, ISA 6.5).
        yield return new VectorOp("v_sub_nc_i16", Vop3, OpType.I16, i16x2,
            Saturating(16, true, static lane => S16(lane.S0) - S16(lane.S1)), Unclamped | Half)
            { Citation = "ISA 12.12 V_SUB_NC_I16" };
        yield return new VectorOp("v_mad_u16", Vop3, OpType.U16, u16x3,
            Saturating(16, false, static lane => U16(lane.S0) * U16(lane.S1) + U16(lane.S2)), Clamp | Half)
            { Citation = "ISA 12.12 V_MAD_U16" };
        yield return new VectorOp("v_mad_i16", Vop3, OpType.I16, i16x3,
            Saturating(16, true, static lane => S16(lane.S0) * S16(lane.S1) + S16(lane.S2)), Clamp | Half)
            { Citation = "ISA 12.12 V_MAD_I16" };
        yield return new VectorOp("v_min3_i16", Vop3, OpType.I16, i16x3,
            static (in LaneInputs lane) => new LaneResult((ulong)Min3((short)lane.S0, (short)lane.S1, (short)lane.S2) & 0xFFFF), Half)
            { Citation = "ISA 12.12 V_MIN3_I16" };
        yield return new VectorOp("v_min3_u16", Vop3, OpType.U16, u16x3,
            static (in LaneInputs lane) => new LaneResult((ulong)Min3((ushort)lane.S0, (ushort)lane.S1, (ushort)lane.S2)), Half)
            { Citation = "ISA 12.12 V_MIN3_U16" };
        yield return new VectorOp("v_max3_i16", Vop3, OpType.I16, i16x3,
            static (in LaneInputs lane) => new LaneResult((ulong)Max3((short)lane.S0, (short)lane.S1, (short)lane.S2) & 0xFFFF), Half)
            { Citation = "ISA 12.12 V_MAX3_I16" };
        yield return new VectorOp("v_max3_u16", Vop3, OpType.U16, u16x3,
            static (in LaneInputs lane) => new LaneResult((ulong)Max3((ushort)lane.S0, (ushort)lane.S1, (ushort)lane.S2)), Half)
            { Citation = "ISA 12.12 V_MAX3_U16" };
        yield return new VectorOp("v_med3_i16", Vop3, OpType.I16, i16x3,
            static (in LaneInputs lane) => new LaneResult((ulong)Med3((short)lane.S0, (short)lane.S1, (short)lane.S2) & 0xFFFF), Half)
            { Citation = "ISA 12.12 V_MED3_I16" };
        yield return new VectorOp("v_med3_u16", Vop3, OpType.U16, u16x3,
            static (in LaneInputs lane) => new LaneResult((ulong)Med3((ushort)lane.S0, (ushort)lane.S1, (ushort)lane.S2)), Half)
            { Citation = "ISA 12.12 V_MED3_U16" };
        yield return new VectorOp("v_mad_u32_u16", Vop3, OpType.U32, [OpType.U16, OpType.U16, OpType.U32],
            Saturating(32, false, static lane => U16(lane.S0) * U16(lane.S1) + U32Of(lane.S2)), Clamp)
            { Citation = "ISA 12.12 V_MAD_U32_U16" };
        yield return new VectorOp("v_mad_i32_i16", Vop3, OpType.I32, [OpType.I16, OpType.I16, OpType.I32],
            Saturating(32, true, static lane => S16(lane.S0) * S16(lane.S1) + S32(lane.S2)), Clamp)
            { Citation = "ISA 12.12 V_MAD_I32_I16" };

        yield return new VectorOp("v_add_co_u32", Vop3, OpType.U32, u32x2,
            static (in LaneInputs lane) =>
            {
                var sum = U32Of(lane.S0) + U32Of(lane.S1);
                return new LaneResult(lane.Clamp ? SaturateUnsigned(sum, 32) : (ulong)(sum & 0xFFFFFFFF), sum >= 0x100000000L);
            }, VectorFlags.CarryOut | Clamp)
            { Citation = "ISA 12.12 V_ADD_CO_U32" };
        yield return new VectorOp("v_sub_co_u32", Vop3, OpType.U32, u32x2,
            static (in LaneInputs lane) =>
            {
                var difference = U32Of(lane.S0) - U32Of(lane.S1);
                return new LaneResult(lane.Clamp ? SaturateUnsigned(difference, 32) : (ulong)(difference & 0xFFFFFFFF),
                    (uint)lane.S1 > (uint)lane.S0);
            }, VectorFlags.CarryOut | Clamp)
            { Citation = "ISA 12.12 V_SUB_CO_U32" };
        yield return new VectorOp("v_subrev_co_u32", Vop3, OpType.U32, u32x2,
            static (in LaneInputs lane) =>
            {
                var difference = U32Of(lane.S1) - U32Of(lane.S0);
                return new LaneResult(lane.Clamp ? SaturateUnsigned(difference, 32) : (ulong)(difference & 0xFFFFFFFF),
                    (uint)lane.S0 > (uint)lane.S1);
            }, VectorFlags.CarryOut | Clamp)
            { Citation = "ISA 12.12 V_SUBREV_CO_U32" };

        yield return new VectorOp("v_perm_b32", Vop3, OpType.B32, b32x3,
            static (in LaneInputs lane) =>
            {
                var bytes = ((lane.S0 & 0xFFFFFFFF) << 32) | (lane.S1 & 0xFFFFFFFF);
                ulong result = 0;
                for (var b = 0; b < 4; b++)
                {
                    result |= PermByte(bytes, (uint)(lane.S2 >> (8 * b)) & 0xFF) << (8 * b);
                }

                return new LaneResult(result);
            })
            { Citation = "ISA 12.12 V_PERM_B32", Pool = PoolAt(2, PermSelectors) };
        yield return new VectorOp("v_xad_u32", Vop3, OpType.U32, u32x3, U32(static (a, b, c) => (a ^ b) + c))
            { Citation = "ISA 12.12 V_XAD_U32" };
        yield return new VectorOp("v_lshl_add_u32", Vop3, OpType.U32, u32x3, U32(static (a, b, c) => (a << (int)(b & 31)) + c))
            { Citation = "ISA 12.12 V_LSHL_ADD_U32", Pool = PoolAt(1, SmallAmounts) };
        yield return new VectorOp("v_add_lshl_u32", Vop3, OpType.U32, u32x3, U32(static (a, b, c) => (a + b) << (int)(c & 31)))
            { Citation = "ISA 12.12 V_ADD_LSHL_U32", Pool = PoolAt(2, SmallAmounts) };
        yield return new VectorOp("v_bfm_b32", Vop3, OpType.B32, b32x2,
            U32(static (a, b, _) => (uint)(((1ul << (int)(a & 31)) - 1) << (int)(b & 31))))
            { Citation = "ISA 12.12 V_BFM_B32", Pool = PoolAt(0, SmallAmounts, 1, SmallAmounts) };
        yield return new VectorOp("v_bcnt_u32_b32", Vop3, OpType.U32, [OpType.B32, OpType.U32],
            U32(static (a, b, _) => b + (uint)BitOperations.PopCount(a)))
            { Citation = "ISA 12.12 V_BCNT_U32_B32" };
        // ThreadMask = (1 << lane) - 1: the LO form counts S0 under its bits 31:0, the HI form
        // under bits 63:32 (zero in wave32).
        yield return new VectorOp("v_mbcnt_lo_u32_b32", Vop3, OpType.U32, [OpType.B32, OpType.U32],
            static (in LaneInputs lane) =>
            {
                var threadMask = (UInt128.One << lane.Lane) - 1;
                var masked = (uint)lane.S0 & (uint)(threadMask & 0xFFFFFFFF);
                return new LaneResult((uint)lane.S1 + (uint)BitOperations.PopCount(masked));
            })
            { Citation = "ISA 12.12 V_MBCNT_LO_U32_B32" };
        yield return new VectorOp("v_mbcnt_hi_u32_b32", Vop3, OpType.U32, [OpType.B32, OpType.U32],
            static (in LaneInputs lane) =>
            {
                var threadMask = (UInt128.One << lane.Lane) - 1;
                var masked = (uint)lane.S0 & (uint)((threadMask >> 32) & 0xFFFFFFFF);
                return new LaneResult((uint)lane.S1 + (uint)BitOperations.PopCount(masked));
            })
            { Citation = "ISA 12.12 V_MBCNT_HI_U32_B32" };
        // u32_to_u16 / i32_to_i16 are not spelled out by the ISA; Mesa ACO selects these ops
        // for NIR's saturating pack_uint_2x16 / pack_sint_2x16 (aco_select_nir_alu.cpp), and
        // folds constants with the same clamp: the conversions saturate.
        yield return new VectorOp("v_cvt_pk_u16_u32", Vop3, OpType.B32, u32x2,
            U32(static (a, b, _) => Math.Min(a, 0xFFFFu) | Math.Min(b, 0xFFFFu) << 16))
            { Citation = "ISA 12.12 V_CVT_PK_U16_U32 (saturating, per Mesa ACO)" };
        yield return new VectorOp("v_cvt_pk_i16_i32", Vop3, OpType.B32, i32x2,
            U32(static (a, b, _) =>
                (uint)(ushort)Math.Clamp((int)a, short.MinValue, short.MaxValue) |
                (uint)(ushort)Math.Clamp((int)b, short.MinValue, short.MaxValue) << 16))
            { Citation = "ISA 12.12 V_CVT_PK_I16_I32 (saturating, per Mesa ACO)" };
        yield return new VectorOp("v_add3_u32", Vop3, OpType.U32, u32x3, U32(static (a, b, c) => a + b + c))
            { Citation = "ISA 12.12 V_ADD3_U32" };
        yield return new VectorOp("v_lshl_or_b32", Vop3, OpType.B32, b32x3, U32(static (a, b, c) => (a << (int)(b & 31)) | c))
            { Citation = "ISA 12.12 V_LSHL_OR_B32", Pool = PoolAt(1, SmallAmounts) };
        yield return new VectorOp("v_and_or_b32", Vop3, OpType.B32, b32x3, U32(static (a, b, c) => (a & b) | c))
            { Citation = "ISA 12.12 V_AND_OR_B32" };
        yield return new VectorOp("v_or3_b32", Vop3, OpType.B32, b32x3, U32(static (a, b, c) => a | b | c))
            { Citation = "ISA 12.12 V_OR3_B32" };
        yield return new VectorOp("v_add_nc_i32", Vop3, OpType.I32, i32x2,
            Saturating(32, true, static lane => S32(lane.S0) + S32(lane.S1)), Clamp)
            { Citation = "ISA 12.12 V_ADD_NC_I32" };
        yield return new VectorOp("v_sub_nc_i32", Vop3, OpType.I32, i32x2,
            Saturating(32, true, static lane => S32(lane.S0) - S32(lane.S1)), Clamp)
            { Citation = "ISA 12.12 V_SUB_NC_I32" };
    }
}
