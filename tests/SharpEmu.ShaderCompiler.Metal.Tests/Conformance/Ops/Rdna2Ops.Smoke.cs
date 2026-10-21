// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using static SharpEmu.ShaderCompiler.Metal.Tests.Conformance.Semantics;

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

/// <summary>Seed opcodes proving the harness end to end; each category file adds its own.</summary>
internal static partial class Rdna2Ops
{
    [OpCategory("smoke")]
    private static IEnumerable<object> Smoke()
    {
        var f32 = FloatFormat.F32;
        // V_ADD_F32: D.f = S0.f + S1.f (ISA 12.7).
        yield return new VectorOp("v_add_f32", VectorEncoding.Vop2, OpType.F32, [OpType.F32, OpType.F32],
            Binary(f32, static (a, b, f, keep) => SoftFloat.Add(a, b, f, keep))) { Citation = "ISA 12.7 V_ADD_F32" };
        // V_ADD_NC_U32: D.u = S0.u + S1.u, clamp saturates (ISA 12.7, 6.5).
        yield return new VectorOp("v_add_nc_u32", VectorEncoding.Vop2, OpType.U32, [OpType.U32, OpType.U32],
            static (in LaneInputs lane) =>
            {
                var sum = (Int128)(uint)lane.S0 + (uint)lane.S1;
                return new LaneResult(lane.Clamp ? SaturateUnsigned(sum, 32) : (ulong)(uint)sum);
            }, VectorFlags.IntegerClamp) { Citation = "ISA 12.7 V_ADD_NC_U32" };
        // V_CMP_LT_F32: D[lane] = S0 < S1 (ISA 12.9).
        yield return new VectorOp("v_cmp_lt_f32", VectorEncoding.Vopc, OpType.F32, [OpType.F32, OpType.F32],
            static (in LaneInputs lane) =>
            {
                var f = FloatFormat.F32;
                var a = lane.Mode.FlushIn(lane.S0, f);
                var b = lane.Mode.FlushIn(lane.S1, f);
                return LaneResult.Bool(!SoftFloat.IsNaN(a, f) && !SoftFloat.IsNaN(b, f) && SoftFloat.Compare(a, b, f) < 0);
            }, VectorFlags.Compare) { Citation = "ISA 12.9 V_CMP_LT_F32" };
        // S_ADD_U32: D = S0 + S1, SCC = carry out (ISA 12.1).
        yield return new ScalarOp("s_add_u32", ScalarEncoding.Sop2, OpType.U32, [OpType.U32, OpType.U32],
            static (in ScalarInputs s) =>
            {
                var sum = (ulong)(uint)s.S0 + (uint)s.S1;
                return new ScalarResult(sum & 0xFFFFFFFF, sum >> 32 != 0);
            }, ScalarFlags.WritesDestination | ScalarFlags.WritesScc) { Citation = "ISA 12.1 S_ADD_U32" };
    }
}
