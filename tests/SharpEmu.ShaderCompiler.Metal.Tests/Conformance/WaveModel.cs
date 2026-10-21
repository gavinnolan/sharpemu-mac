// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

/// <summary>A wave's inputs to one vector instruction: per lane the three sources (64-bit
/// register pairs), the destination's previous contents, and the wave's VCC and EXEC.</summary>
internal sealed class VectorWave
{
    // Uniform presets the harness program writes before the instruction.
    public const uint ScalarDestinationLow = 0xA5A5A5A5u;
    public const uint ScalarDestinationHigh = 0x5A5A5A5Au;
    public const uint Wave32VccHigh = 0x3C3C3C3Cu;

    public VectorWave(int size)
    {
        Size = size;
        Sources = [new ulong[size], new ulong[size], new ulong[size]];
        Old = new ulong[size];
    }

    public int Size { get; }
    public ulong[][] Sources { get; }
    public ulong[] Old { get; }
    /// <summary>VCC before the instruction (wave32: the low half; VCC_HI holds <see cref="Wave32VccHigh"/>).</summary>
    public ulong Vcc { get; set; }
    public ulong Exec { get; set; }

    public ulong LaneMask => Size == 64 ? ulong.MaxValue : 0xFFFFFFFFul;

    public ulong VccBefore => Size == 32 ? (Vcc & 0xFFFFFFFFul) | ((ulong)Wave32VccHigh << 32) : Vcc;

    public ulong ScalarDestinationBefore => ScalarDestinationLow | ((ulong)ScalarDestinationHigh << 32);
}

/// <summary>The observable state after the instruction: v7/v8 per lane, VCC, EXEC and the
/// VOP3 scalar destination s[24:25]. <see cref="Loose"/> marks unspecified NaN payload bits.</summary>
internal sealed class VectorOutcome(int size)
{
    public ulong[] Destination { get; } = new ulong[size];
    public ulong[] Loose { get; } = new ulong[size];
    public ulong Vcc { get; set; }
    public ulong Exec { get; set; }
    public ulong ScalarDestination { get; set; }
}

/// <summary>The reference evaluation of a vector form over a whole wave: operand selection and
/// input modifiers, cross-lane DPP fetches, the per-lane semantics, output modifiers and the
/// sub-dword destination write, and mask results, all as the ISA describes them.</summary>
internal static class WaveModel
{
    public static VectorOutcome Evaluate(VectorForm form, VectorWave wave, AluMode mode)
    {
        var op = form.Op;
        var outcome = new VectorOutcome(wave.Size);
        if (op.WaveSemantics is { } semantics)
        {
            Array.Copy(wave.Old, outcome.Destination, wave.Size);
            outcome.Vcc = wave.VccBefore;
            outcome.Exec = wave.Exec;
            outcome.ScalarDestination = wave.ScalarDestinationBefore;
            semantics(wave, outcome);
            return outcome;
        }

        ulong maskResult = 0;
        var maskWritten = false;
        for (var lane = 0; lane < wave.Size; lane++)
        {
            outcome.Destination[lane] = wave.Old[lane];
            var active = (wave.Exec >> lane & 1) != 0;
            if (!active) continue;

            var write = true;
            var raw = new ulong[3];
            for (var index = 0; index < op.Sources.Length; index++)
            {
                raw[index] = wave.Sources[index][lane];
            }

            if (op.Has(VectorFlags.Accumulates))
            {
                raw[op.Sources.Length - 1] = wave.Old[lane];
            }

            if (op.Literal is uint literal)
            {
                raw[form.LiteralSourceIndex] = literal;
            }

            if (form.Dpp is { } dpp && form.Kind is FormKind.Dpp16 or FormKind.Dpp8)
            {
                var (sourceLane, valid) = Dpp.SourceLane(dpp, lane, wave.Size);
                var sourceActive = valid && (wave.Exec >> sourceLane & 1) != 0;
                if (valid && (sourceActive || dpp.FetchInactive))
                {
                    raw[0] = wave.Sources[0][sourceLane];
                }
                else if (Dpp.UsesZero(dpp, valid))
                {
                    raw[0] = 0;
                }
                else
                {
                    write = false;
                }

                if (!Dpp.LaneWriteEnabled(dpp, lane)) write = false;
            }

            var inputs = new LaneInputs
            {
                Clamp = form.Clamp,
                Mode = mode,
                Lane = lane,
                WaveSize = wave.Size,
                Old = wave.Old[lane],
                CarryIn = (wave.VccBefore >> lane & 1) != 0,
            };
            inputs.S0 = SelectSource(form, 0, raw[0]);
            inputs.S1 = SelectSource(form, 1, raw[1]);
            inputs.S2 = SelectSource(form, 2, raw[2]);

            LaneResult result;
            if (form.Kind == FormKind.Packed && op.Encoding == VectorEncoding.Vop3P && !IsMix(op))
            {
                result = PackedModel.Evaluate(form, raw, inputs);
            }
            else
            {
                result = op.Function(inputs);
            }

            if (op.Has(VectorFlags.Compare) || op.Has(VectorFlags.CompareExec) || op.Has(VectorFlags.CarryOut))
            {
                maskWritten = true;
                if (result.Flag && write) maskResult |= 1ul << lane;
                if (op.Has(VectorFlags.Compare) || op.Has(VectorFlags.CompareExec)) continue;
            }

            if (!write) continue;
            if (op.FloatResult && !op.Has(VectorFlags.NoOutputModifiers) && form.Kind != FormKind.Packed)
            {
                result = OutputModifiers(form, op.Result.Format(), result, mode);
            }

            (outcome.Destination[lane], outcome.Loose[lane]) = WriteDestination(form, wave.Old[lane], result);
        }

        outcome.Vcc = wave.VccBefore;
        outcome.Exec = wave.Exec;
        outcome.ScalarDestination = wave.ScalarDestinationBefore;
        if (maskWritten)
        {
            // V_CMP_* => VCC[n] = EXEC[n] & test (ISA 3.9): the whole mask is written. A wave32
            // mask is one SGPR: the high half (VCC_HI, s25) is untouched.
            var keepHigh = wave.Size == 32 ? 0xFFFFFFFF00000000ul : 0;
            if (op.Has(VectorFlags.CompareExec))
            {
                outcome.Exec = maskResult | (wave.Exec & keepHigh);
            }
            else if (form.Kind is FormKind.E32 or FormKind.Dpp16 or FormKind.Dpp8 ||
                     (form.Kind == FormKind.Sdwa && (!op.Has(VectorFlags.Compare) || form.Sdwa is { VccDestination: true })))
            {
                outcome.Vcc = maskResult | (wave.VccBefore & keepHigh);
            }
            else
            {
                outcome.ScalarDestination = maskResult | (wave.ScalarDestinationBefore & keepHigh);
            }
        }

        return outcome;
    }

    private static bool IsMix(VectorOp op) => op.Mnemonic.StartsWith("v_fma_mix", StringComparison.Ordinal);

    /// <summary>Source selection then input modifiers: SDWA byte/word select with optional sign
    /// extension, VOP3 op_sel for 16-bit operands, then abs and neg on the operand's float
    /// format.</summary>
    private static ulong SelectSource(VectorForm form, int index, ulong raw)
    {
        var op = form.Op;
        if (index >= op.Sources.Length) return 0;
        var type = op.Sources[index];
        var value = raw;
        if (form.Kind == FormKind.Sdwa && form.Sdwa is { } sdwa && index < 2)
        {
            var select = index == 0 ? sdwa.Src0Sel : sdwa.Src1Sel;
            var sext = index == 0 ? sdwa.Src0Sext : sdwa.Src1Sext;
            value = Sdwa.Extract((uint)raw, select, sext);
        }
        else if (form.Kind == FormKind.E64 && type.Width() == 16 && (form.OpSel >> index & 1) != 0)
        {
            value = raw >> 16;
        }

        if (form.Kind == FormKind.Packed) return raw;
        value &= type.Width() == 16 && form.Kind != FormKind.Sdwa ? 0xFFFF : type.Mask();
        if (type.IsFloat())
        {
            var format = type.Format();
            if ((form.Abs >> index & 1) != 0) value = SoftFloat.Abs(value, format);
            if ((form.Neg >> index & 1) != 0) value = SoftFloat.Negate(value, format);
        }

        return value;
    }

    /// <summary>VOP3/SDWA OMOD and CLAMP on a float result (ISA 6.2.2, 6.5): OMOD is ignored when
    /// IEEE mode or output denormals are on; otherwise the result is scaled and a zero or
    /// denormal becomes +0. CLAMP saturates to [0, 1]; a NaN becomes 0 under DX10_CLAMP.</summary>
    public static LaneResult OutputModifiers(VectorForm form, FloatFormat format, LaneResult result, AluMode mode)
    {
        var value = result.Value & format.ValueMask;
        var loose = result.Loose;
        var omodApplies = form.Omod != 0 && !mode.Ieee && !mode.RequestsOutputDenormals(format);
        if (omodApplies && SoftFloat.IsNaN(value, format))
        {
            // The scaling multiply gives a NaN of unspecified sign and payload (docs/deviations.md, "NaN payloads").
            loose = format.ValueMask;
        }
        else if (omodApplies)
        {
            var scale = form.Omod switch
            {
                1 => SoftFloat.Convert(SoftFloat.F32(2f), FloatFormat.F32, format),
                2 => SoftFloat.Convert(SoftFloat.F32(4f), FloatFormat.F32, format),
                _ => SoftFloat.Convert(SoftFloat.F32(0.5f), FloatFormat.F32, format),
            };
            value = SoftFloat.Mul(value, scale, format, keepDenormal: false);
            if (SoftFloat.IsZero(value, format)) value = 0;
        }

        if (form.Clamp)
        {
            (value, loose) = ClampUnit(value, loose, format, mode);
        }

        return result with { Value = value, Loose = loose };
    }

    public static (ulong Value, ulong Loose) ClampUnit(ulong value, ulong loose, FloatFormat format, AluMode mode)
    {
        if (SoftFloat.IsNaN(value, format))
        {
            return mode.Dx10Clamp ? (0, 0) : (value, loose);
        }

        if (SoftFloat.IsNegative(value, format)) return (0, 0);
        return SoftFloat.Compare(value, format.One, format) > 0 ? (format.One, 0ul) : (value, 0ul);
    }

    /// <summary>Writes the result into the destination register(s): 64-bit pairs, 32-bit, a
    /// 16-bit result into the low half (or the high half by VOP3 op_sel[3]) preserving the
    /// rest (gfx10 16-bit ops preserve the high bits), or an SDWA destination select.</summary>
    private static (ulong Value, ulong Loose) WriteDestination(VectorForm form, ulong old, LaneResult result)
    {
        var op = form.Op;
        var width = op.Result.Width();
        if (width == 64) return (result.Value, result.Loose);

        var high = old & 0xFFFFFFFF00000000ul;
        var low = (uint)old;
        if (form.Kind == FormKind.Sdwa && form.Sdwa is { } sdwa)
        {
            var (written, looseBits) = Sdwa.Insert(low, (uint)result.Value, (uint)result.Loose, sdwa.DstSel, sdwa.DstUnused);
            return (high | written, looseBits);
        }

        if (form.Kind == FormKind.Packed && !IsMix(op))
        {
            // Packed math writes both halves.
            return (high | (result.Value & 0xFFFFFFFFul), result.Loose & 0xFFFFFFFFul);
        }

        if (width == 16)
        {
            var half = result.Value & 0xFFFF;
            var halfLoose = result.Loose & 0xFFFF;
            if (op.Has(VectorFlags.WritesHighHalf) ||
                (form.Kind == FormKind.E64 && op.Has(VectorFlags.OpSelDestination) && (form.OpSel & 8) != 0))
            {
                return (high | (low & 0xFFFFu) | (half << 16), halfLoose << 16);
            }

            if (op.Has(VectorFlags.ZeroHigh)) return (high | half, halfLoose);
            return (high | (low & 0xFFFF0000u) | half, halfLoose);
        }

        return (high | (result.Value & 0xFFFFFFFFul), result.Loose & 0xFFFFFFFFul);
    }
}

/// <summary>SDWA operand selection and destination insertion (ISA 13.3.7).</summary>
internal static class Sdwa
{
    public static ulong Extract(uint raw, int select, bool signExtend)
    {
        var (shift, bits) = Field(select);
        var value = bits == 32 ? raw : (raw >> (int)shift) & ((1u << bits) - 1);
        if (signExtend && bits < 32 && (value >> (bits - 1) & 1) != 0)
        {
            value |= ~((1u << bits) - 1);
        }

        return value;
    }

    public static (uint Shift, int Bits) Field(int select) => select switch
    {
        <= 3 => ((uint)select * 8, 8),
        4 => (0, 16),
        5 => (16, 16),
        _ => (0, 32),
    };

    /// <summary>DST_SEL places the low bits of the result in the selected field; DST_UNUSED pads
    /// the other bits with zeros, sign-extends the field upward (zeros below), or preserves them.</summary>
    public static (uint Value, uint Loose) Insert(uint old, uint result, uint loose, int select, int unused)
    {
        var (shift, bits) = Field(select);
        if (bits == 32) return (result, loose);
        var fieldMask = ((1u << bits) - 1) << (int)shift;
        var field = (result << (int)shift) & fieldMask;
        var looseField = (loose << (int)shift) & fieldMask;
        var value = unused switch
        {
            2 => (old & ~fieldMask) | field,
            1 => field | ((field >> (int)(shift + (uint)bits - 1) & 1) != 0 ? ~(fieldMask | ((1u << (int)shift) - 1)) : 0),
            _ => field,
        };
        return (value, looseField);
    }
}

/// <summary>DPP16 and DPP8 lane selection (ISA 13.3.9, 13.3.10).</summary>
internal static class Dpp
{
    /// <summary>The lane src0 is fetched from, and whether that lane is in range.</summary>
    public static (int Lane, bool Valid) SourceLane(DppControl dpp, int lane, int waveSize)
    {
        if (dpp.Lanes8 is { } lanes8)
        {
            return ((lane & ~7) | lanes8[lane & 7], true);
        }

        var ctrl = dpp.Ctrl;
        var row = lane & ~15;
        var inRow = lane & 15;
        switch (ctrl)
        {
            case <= 0xFF:
                return ((lane & ~3) | ((ctrl >> ((lane & 3) * 2)) & 3), true);
            case >= 0x101 and <= 0x10F:
            {
                var shift = ctrl & 0xF;
                return inRow + shift < 16 ? (lane + shift, true) : (lane, false);
            }
            case >= 0x111 and <= 0x11F:
            {
                var shift = ctrl & 0xF;
                return inRow >= shift ? (lane - shift, true) : (lane, false);
            }
            case >= 0x121 and <= 0x12F:
            {
                var shift = ctrl & 0xF;
                return (row | ((inRow - shift + 16) & 15), true);
            }
            case 0x140:
                return (row | (15 - inRow), true);
            case 0x141:
                return ((lane & ~7) | (7 - (lane & 7)), true);
            case >= 0x150 and <= 0x15F:
                // DPP_ROW_SHARE (gfx10; not in the RDNA2 ISA table, semantics per LLVM's
                // SIDefines DppCtrl): every lane of the row reads lane cntl[3:0] of its row.
                return (row | (ctrl & 0xF), true);
            case >= 0x160 and <= 0x16F:
                // DPP_ROW_XMASK: lane reads lane (n ^ cntl[3:0]) of its row.
                return (row | (inRow ^ (ctrl & 0xF)), true);
            default:
                throw new InvalidOperationException($"DPP_CTRL 0x{ctrl:X}");
        }
    }

    /// <summary>An invalid DPP16 fetch (out of range, or an inactive lane without FI) reads zero
    /// when BOUND_CTRL is set; otherwise the lane does not write (ISA 13.3.9 BC; for an inactive
    /// source the ISA's FI text says "read zero", LLVM's AMDGPUModifierSyntax fi16 says FI=0
    /// leaves it to bound_ctrl, which is the reading taken here). DPP8 has no BOUND_CTRL and every
    /// lane select is in range: an inactive source without FI reads zero (ISA 13.3.9 FI; LLVM fi8
    /// "Fetch zero when accessing data from inactive lanes").</summary>
    public static bool UsesZero(DppControl dpp, bool valid) => dpp.Lanes8 is not null || dpp.BoundCtrl;

    /// <summary>ROW_MASK and BANK_MASK disable destination writes of whole rows / banks.</summary>
    public static bool LaneWriteEnabled(DppControl dpp, int lane)
    {
        if (dpp.Lanes8 is not null) return true;
        return (dpp.RowMask >> (lane >> 4) & 1) != 0 && (dpp.BankMask >> ((lane >> 2) & 3) & 1) != 0;
    }
}
