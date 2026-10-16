// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

/// <summary>VOP3P packed math (ISA 6.7, 12.10, 13.3.4): each 16-bit half of the result is the
/// op applied to the halves op_sel (low result) or op_sel_hi (high result) pick from each
/// source, after neg_lo / neg_hi; clamp applies per half.</summary>
internal static class PackedModel
{
    public static LaneResult Evaluate(VectorForm form, ulong[] raw, LaneInputs inputs)
    {
        var op = form.Op;
        ulong value = 0;
        ulong loose = 0;
        for (var half = 0; half < 2; half++)
        {
            var lane = inputs;
            var selects = half == 0 ? form.OpSel : form.OpSelHi;
            var negates = half == 0 ? form.Neg : form.NegHi;
            var halves = new ulong[3];
            for (var index = 0; index < op.Sources.Length; index++)
            {
                var piece = (raw[index] >> ((int)(selects >> index & 1) * 16)) & 0xFFFF;
                if (op.Sources[index].IsFloat() && (negates >> index & 1) != 0)
                {
                    piece ^= 0x8000;
                }

                halves[index] = piece;
            }

            lane.S0 = halves[0];
            lane.S1 = halves[1];
            lane.S2 = halves[2];
            var result = op.Function(lane);
            var bits = result.Value & 0xFFFF;
            var resultLoose = result.Loose & 0xFFFF;
            if (form.Clamp && op.Result.IsFloat())
            {
                (bits, resultLoose) = WaveModel.ClampUnit(bits, resultLoose, FloatFormat.F16, inputs.Mode);
            }

            value |= bits << (16 * half);
            loose |= resultLoose << (16 * half);
        }

        return new LaneResult(value, false, loose);
    }
}
