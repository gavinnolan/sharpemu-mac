// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Metal;

public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        // The GFX10 F64 opcodes the decoder names. MSL has no double, so each one runs
        // on the register pair's bits through the software binary64 helpers in
        // Templates/float64_prelude.msl, which only shaders using them carry. The F32 and
        // F16 division steps (V_DIV_SCALE/FMAS/FIXUP) run on the same core: their fused,
        // scaled single rounding and exact case analysis have no MSL float equivalent.
        private static bool IsFloat64Opcode(string opcode) => opcode is
            "VFmaF64" or "VMulF64" or "VAddF64" or "VMinF64" or "VMaxF64" or "VLdexpF64" or
            "VRcpF64" or "VRsqF64" or "VSqrtF64" or
            "VCvtF64I32" or "VCvtF64U32" or "VCvtF32F64" or "VCvtF64F32" or "VCvtI32F64" or "VCvtU32F64" or
            "VTruncF64" or "VCeilF64" or "VRndneF64" or "VFloorF64" or "VFractF64" or
            "VFrexpMantF64" or "VFrexpExpI32F64" or "VTrigPreopF64" or
            "VDivScaleF64" or "VDivFmasF64" or "VDivFixupF64" or
            "VDivScaleF32" or "VDivFmasF32" or "VDivFixupF32" or "VDivFixupF16" ||
            IsFloat64Compare(opcode);

        private static bool IsFloat64Compare(string opcode) =>
            opcode.StartsWith("VCmp", StringComparison.Ordinal) && opcode.EndsWith("F64", StringComparison.Ordinal);

        private bool UsesFloat64Prelude()
        {
            if (_usesFloat64Atomics)
            {
                return true;
            }

            foreach (var instruction in _request.Program.Instructions)
            {
                if (IsFloat64Opcode(instruction.Opcode))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryEmitFloat64(Gen5ShaderInstruction instruction, out string error)
        {
            error = string.Empty;
            if (instruction.Control is not (null or Gen5Vop3Control))
            {
                error = $"FP64 control {instruction.Control.GetType().Name} is not implemented in {instruction.Opcode}";
                return false;
            }

            if (IsFloat64Compare(instruction.Opcode))
            {
                return TryEmitFloat64Compare(instruction, out error);
            }

            var destination = DestinationVector(instruction);
            switch (instruction.Opcode)
            {
                case "VCvtF32F64":
                {
                    if (!TryFloat64Source(instruction, 0, out var narrowed, out error))
                    {
                        return false;
                    }

                    var bits = $"sharpemu_f64_to_f32({narrowed})";
                    StoreVector(
                        destination,
                        instruction.Control is Gen5Vop3Control { OutputModifier: not 0 } or Gen5Vop3Control { Clamp: true }
                            ? FloatResult(instruction, $"as_type<float>({bits})")
                            : bits);
                    return true;
                }
                case "VCvtI32F64":
                case "VCvtU32F64":
                case "VFrexpExpI32F64":
                {
                    // Integer results: the float output modifiers do not apply.
                    if (!TryFloat64Source(instruction, 0, out var source, out error))
                    {
                        return false;
                    }

                    StoreVector(destination, instruction.Opcode switch
                    {
                        "VCvtI32F64" => $"sharpemu_f64_to_i32({source})",
                        "VCvtU32F64" => $"sharpemu_f64_to_u32({source})",
                        _ => $"as_type<uint>(sharpemu_f64_frexp_exp({source}))",
                    });
                    return true;
                }
                case "VDivScaleF32":
                {
                    // VOP3B: the scale flag goes to SDST (VCC when the encoding names it).
                    var flag = Temp("bool", "false");
                    var scaled = Temp(
                        "uint",
                        $"sharpemu_f32_div_scale({FlushedFloat32Bits(instruction, 0)}, {FlushedFloat32Bits(instruction, 1)}, " +
                        $"{FlushedFloat32Bits(instruction, 2)}, {flag})");
                    StoreCarryOut(instruction, flag);
                    StoreVector(destination, FloatResult(instruction, AsFloat(FlushOutputDenormalF32(scaled))));
                    return true;
                }
                case "VDivFmasF32":
                    StoreVector(destination, FloatResult(instruction, AsFloat(FlushOutputDenormalF32(
                        $"sharpemu_f32_div_fmas({FlushedFloat32Bits(instruction, 0)}, {FlushedFloat32Bits(instruction, 1)}, " +
                        $"{FlushedFloat32Bits(instruction, 2)}, vcc)"))));
                    return true;
                case "VDivFixupF32":
                    StoreVector(destination, FloatResult(instruction, AsFloat(
                        $"(uint)sharpemu_div_fixup({FlushedFloat32Bits(instruction, 0)}, {FlushedFloat32Bits(instruction, 1)}, " +
                        $"{FlushedFloat32Bits(instruction, 2)}, 32u, 23u, -150, 0)")));
                    return true;
                case "VDivFixupF16":
                    StoreVector(destination, Float16Result(
                        instruction,
                        destination,
                        $"(float)as_type<half>((ushort)sharpemu_div_fixup({Float16Bits(instruction, 0)}, " +
                        $"{Float16Bits(instruction, 1)}, {Float16Bits(instruction, 2)}, 16u, 10u, 0, 0))"));
                    return true;
            }

            string value;
            switch (instruction.Opcode)
            {
                case "VCvtF64I32":
                    value = $"sharpemu_f64_from_i32(as_type<int>({RawSource(instruction, 0)}))";
                    break;
                case "VCvtF64U32":
                    value = $"sharpemu_f64_from_u32({RawSource(instruction, 0)}, 0u)";
                    break;
                case "VCvtF64F32":
                    value = $"sharpemu_f64_from_f32({FlushInputDenormalF32(Float32Bits(instruction, 0))})";
                    break;
                case "VRcpF64":
                case "VRsqF64":
                case "VSqrtF64":
                case "VTruncF64":
                case "VFloorF64":
                case "VCeilF64":
                case "VRndneF64":
                case "VFractF64":
                case "VFrexpMantF64":
                {
                    if (!TryFloat64Source(instruction, 0, out var source, out error))
                    {
                        return false;
                    }

                    // rcp/rsq/sqrt are the SPIR-V translator's 1/x, 1/sqrt(x) and
                    // sqrt(x), each step rounded on its own.
                    value = instruction.Opcode switch
                    {
                        "VRcpF64" => $"sharpemu_f64_div(0x3FF0000000000000ul, {source})",
                        "VRsqF64" => $"sharpemu_f64_div(0x3FF0000000000000ul, sharpemu_f64_sqrt({source}))",
                        "VSqrtF64" => $"sharpemu_f64_sqrt({source})",
                        "VTruncF64" => $"sharpemu_f64_round_integral({source}, 0u)",
                        "VFloorF64" => $"sharpemu_f64_round_integral({source}, 1u)",
                        "VCeilF64" => $"sharpemu_f64_round_integral({source}, 2u)",
                        "VRndneF64" => $"sharpemu_f64_round_integral({source}, 3u)",
                        "VFractF64" => $"sharpemu_f64_fract({source})",
                        _ => $"sharpemu_f64_frexp_mant({source})",
                    };
                    break;
                }
                case "VLdexpF64":
                case "VTrigPreopF64":
                {
                    // The second operand is an integer: the exponent or the segment.
                    if (!TryFloat64Source(instruction, 0, out var source, out error))
                    {
                        return false;
                    }

                    var integer = RawSource(instruction, 1);
                    value = instruction.Opcode == "VLdexpF64"
                        ? $"sharpemu_f64_ldexp({source}, as_type<int>({integer}))"
                        : $"sharpemu_f64_trig_preop({source}, {integer})";
                    break;
                }
                case "VMulF64":
                case "VAddF64":
                case "VMinF64":
                case "VMaxF64":
                {
                    if (!TryFloat64Source(instruction, 0, out var left, out error) ||
                        !TryFloat64Source(instruction, 1, out var right, out error))
                    {
                        return false;
                    }

                    var ieee = _request.IeeeMode ? "true" : "false";
                    value = instruction.Opcode switch
                    {
                        "VMulF64" => $"sharpemu_f64_mul({left}, {right})",
                        "VAddF64" => $"sharpemu_f64_add({left}, {right})",
                        "VMinF64" => $"sharpemu_f64_min({left}, {right}, {ieee})",
                        _ => $"sharpemu_f64_max({left}, {right}, {ieee})",
                    };
                    break;
                }
                case "VFmaF64":
                case "VDivScaleF64":
                case "VDivFmasF64":
                case "VDivFixupF64":
                {
                    if (!TryFloat64Source(instruction, 0, out var first, out error) ||
                        !TryFloat64Source(instruction, 1, out var second, out error) ||
                        !TryFloat64Source(instruction, 2, out var third, out error))
                    {
                        return false;
                    }

                    switch (instruction.Opcode)
                    {
                        case "VFmaF64":
                            value = $"sharpemu_f64_fma({first}, {second}, {third})";
                            break;
                        case "VDivFmasF64":
                            value = $"sharpemu_f64_div_fmas({first}, {second}, {third}, vcc)";
                            break;
                        case "VDivFixupF64":
                            value = $"sharpemu_div_fixup({first}, {second}, {third}, 64u, 52u, -1075, 0)";
                            break;
                        default:
                        {
                            // VOP3B: the scale flag goes to SDST (VCC when the encoding names it).
                            var flag = Temp("bool", "false");
                            value = Temp("ulong", $"sharpemu_f64_div_scale({first}, {second}, {third}, {flag})");
                            StoreCarryOut(instruction, flag);
                            break;
                        }
                    }

                    break;
                }
                default:
                    error = $"unhandled FP64 opcode {instruction.Opcode}";
                    return false;
            }

            // Output modifier, then clamp. omod applies only outside IEEE mode with f64 output
            // denormals flushed, and then flushes a zero or denormal result to +0; a clamped NaN
            // gives 0 under DX10_CLAMP and passes through without it.
            if (instruction.Control is Gen5Vop3Control)
            {
                var (outputModifier, clamp) = FloatOutputModifiers(instruction, halfOrDouble: true);
                if (outputModifier != 0)
                {
                    var scale = outputModifier switch
                    {
                        1 => "0x4000000000000000ul",
                        2 => "0x4010000000000000ul",
                        _ => "0x3FE0000000000000ul",
                    };
                    var scaled = Temp("ulong", $"sharpemu_f64_mul({value}, {scale})");
                    value = $"(({scaled} & 0x7FF0000000000000ul) == 0ul ? 0ul : {scaled})";
                }

                if (clamp)
                {
                    var clamped = Temp("ulong", value);
                    value = _request.Dx10Clamp
                        ? $"sharpemu_f64_clamp01({clamped})"
                        : $"(sharpemu_f64_is_nan({clamped}) ? {clamped} : sharpemu_f64_clamp01({clamped}))";
                }
            }

            // Both halves come from one temporary, so a destination pair that
            // overlaps a source pair still reads the sources first.
            var result = Temp("ulong", value);
            StoreVector(destination, $"(uint){result}");
            StoreVector(destination + 1, $"(uint)({result} >> 32)");
            return true;
        }

        // V_CMP(X)_*_F64 and V_CMP(X)_CLASS_F64: the IEEE predicate on the pair's bits,
        // stored like every other compare (EXEC for the X forms, else the mask SGPRs).
        private bool TryEmitFloat64Compare(Gen5ShaderInstruction instruction, out string error)
        {
            var opcode = instruction.Opcode;
            var exchange = opcode.StartsWith("VCmpx", StringComparison.Ordinal);
            var predicate = opcode[(exchange ? "VCmpx" : "VCmp").Length..^"F64".Length];
            if (!TryFloat64Source(instruction, 0, out var left, out error))
            {
                return false;
            }

            string condition;
            if (predicate == "Class")
            {
                condition = $"sharpemu_f64_class({left}, {RawSource(instruction, 1)})";
            }
            else
            {
                if (!TryFloat64Source(instruction, 1, out var right, out error))
                {
                    return false;
                }

                var a = Temp("ulong", left);
                var b = Temp("ulong", right);
                var less = $"sharpemu_f64_lt({a}, {b})";
                var greater = $"sharpemu_f64_lt({b}, {a})";
                var equal = $"sharpemu_f64_eq({a}, {b})";
                var unordered = $"sharpemu_f64_unordered({a}, {b})";
                string? test = predicate switch
                {
                    "F" => "false",
                    "Lt" => less,
                    "Eq" => equal,
                    "Le" => $"({less} || {equal})",
                    "Gt" => greater,
                    "Lg" => $"({less} || {greater})",
                    "Ge" => $"({greater} || {equal})",
                    "O" => $"!{unordered}",
                    "U" => unordered,
                    "Nge" => $"!({greater} || {equal})",
                    "Nlg" => $"!({less} || {greater})",
                    "Ngt" => $"!{greater}",
                    "Nle" => $"!({less} || {equal})",
                    "Neq" => $"!{equal}",
                    "Nlt" => $"!{less}",
                    "Tru" => "true",
                    _ => null,
                };
                if (test is null)
                {
                    error = $"unsupported FP64 compare {opcode}";
                    return false;
                }

                condition = test;
            }

            var active = Temp("bool", ExecCondition($"({condition})"));
            if (exchange)
            {
                // GFX10 VCMPX writes EXEC only.
                Line($"exec = {active};");
                EmitBallotStore(ExecLoRegister, "exec");
            }
            else
            {
                StoreMaskBit(CompareMaskDestination(instruction), active);
            }

            return true;
        }

        /// <summary>An F32 operand's bits with its abs/neg modifiers applied.</summary>
        private string Float32Bits(Gen5ShaderInstruction instruction, int sourceIndex) =>
            Temp("uint", AsUInt(F(instruction, sourceIndex)));

        /// <summary>An F32 operand's bits with a denormal flushed to a zero of its sign when FP_DENORM
        /// flushes f32 inputs (RDNA2 ISA 6.4), for the helpers that read the bits.</summary>
        private string FlushedFloat32Bits(Gen5ShaderInstruction instruction, int sourceIndex) =>
            FlushInputDenormalF32(Float32Bits(instruction, sourceIndex));

        /// <summary>An F16 operand's bits (its op_sel half, abs/neg applied), zero-extended.</summary>
        private string Float16Bits(Gen5ShaderInstruction instruction, int sourceIndex) =>
            Temp("uint", $"(uint)as_type<ushort>(half({F16(instruction, sourceIndex)}))");

        /// <summary>An F64 operand as its 64 bits, with the VOP3 abs and neg modifiers
        /// on the sign bit. Registers are read as a pair; an inline constant is the
        /// double of its value, and a 32-bit literal supplies the high dword.</summary>
        private bool TryFloat64Source(
            Gen5ShaderInstruction instruction,
            int sourceIndex,
            out string value,
            out string error)
        {
            error = string.Empty;
            var operand = instruction.Sources[sourceIndex];
            switch (operand.Kind)
            {
                case Gen5OperandKind.VectorRegister:
                case Gen5OperandKind.ScalarRegister:
                    value = RawSource64(instruction, sourceIndex);
                    break;
                case Gen5OperandKind.LiteralConstant:
                    value = $"0x{(ulong)operand.Value << 32:X}ul";
                    break;
                case Gen5OperandKind.EncodedConstant when TryFloat64InlineConstant(operand.Value, out var constant):
                    value = $"0x{BitConverter.DoubleToUInt64Bits(constant):X}ul";
                    break;
                default:
                    value = string.Empty;
                    error = $"FP64 source {sourceIndex} of {instruction.Opcode} has unsupported operand {operand.Kind} {operand.Value}";
                    return false;
            }

            if (instruction.Control is Gen5Vop3Control control)
            {
                if ((control.AbsoluteMask & (1u << sourceIndex)) != 0)
                {
                    value = $"(({value}) & 0x7FFFFFFFFFFFFFFFul)";
                }

                if ((control.NegateMask & (1u << sourceIndex)) != 0)
                {
                    value = $"(({value}) ^ 0x8000000000000000ul)";
                }
            }

            return true;
        }

        private static bool TryFloat64InlineConstant(uint encoded, out double value)
        {
            value = encoded switch
            {
                >= 128 and <= 192 => encoded - 128,
                >= 193 and <= 208 => -(double)(encoded - 192),
                240 => 0.5,
                241 => -0.5,
                242 => 1.0,
                243 => -1.0,
                244 => 2.0,
                245 => -2.0,
                246 => 4.0,
                247 => -4.0,
                248 => BitConverter.UInt64BitsToDouble(0x3FC45F306DC9C882),
                _ => double.NaN,
            };
            return !double.IsNaN(value);
        }
    }
}
