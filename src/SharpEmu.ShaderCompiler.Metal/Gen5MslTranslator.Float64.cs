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
        // Templates/float64_prelude.msl, which only shaders using them carry.
        private static bool IsFloat64Opcode(string opcode) => opcode is
            "VFmaF64" or "VMulF64" or "VRcpF64" or "VRsqF64" or "VSqrtF64" or
            "VCvtF64I32" or "VCvtF64U32" or "VCvtF32F64";

        private void EmitFloat64PreludeIfUsed(StringBuilder source)
        {
            foreach (var instruction in _request.Program.Instructions)
            {
                if (IsFloat64Opcode(instruction.Opcode))
                {
                    source.Append(MslTemplates.Render("float64_prelude"));
                    return;
                }
            }
        }

        private bool TryEmitFloat64(Gen5ShaderInstruction instruction, out string error)
        {
            error = string.Empty;
            if (instruction.Control is not (null or Gen5Vop3Control))
            {
                error = $"FP64 control {instruction.Control.GetType().Name} is not implemented in {instruction.Opcode}";
                return false;
            }

            var destination = DestinationVector(instruction);
            if (instruction.Opcode == "VCvtF32F64")
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

            string value;
            switch (instruction.Opcode)
            {
                case "VCvtF64I32":
                    value = $"sharpemu_f64_from_i32(as_type<int>({RawSource(instruction, 0)}))";
                    break;
                case "VCvtF64U32":
                    value = $"sharpemu_f64_from_u32({RawSource(instruction, 0)}, 0u)";
                    break;
                case "VRcpF64":
                case "VRsqF64":
                case "VSqrtF64":
                {
                    if (!TryFloat64Source(instruction, 0, out var source, out error))
                    {
                        return false;
                    }

                    // The SPIR-V translator's 1/x, 1/sqrt(x) and sqrt(x), each step
                    // rounded on its own.
                    value = instruction.Opcode switch
                    {
                        "VRcpF64" => $"sharpemu_f64_div(0x3FF0000000000000ul, {source})",
                        "VRsqF64" => $"sharpemu_f64_div(0x3FF0000000000000ul, sharpemu_f64_sqrt({source}))",
                        _ => $"sharpemu_f64_sqrt({source})",
                    };
                    break;
                }
                case "VMulF64":
                {
                    if (!TryFloat64Source(instruction, 0, out var left, out error) ||
                        !TryFloat64Source(instruction, 1, out var right, out error))
                    {
                        return false;
                    }

                    value = $"sharpemu_f64_mul({left}, {right})";
                    break;
                }
                case "VFmaF64":
                {
                    if (!TryFloat64Source(instruction, 0, out var first, out error) ||
                        !TryFloat64Source(instruction, 1, out var second, out error) ||
                        !TryFloat64Source(instruction, 2, out var third, out error))
                    {
                        return false;
                    }

                    value = $"sharpemu_f64_fma({first}, {second}, {third})";
                    break;
                }
                default:
                    error = $"unhandled FP64 opcode {instruction.Opcode}";
                    return false;
            }

            // Output modifier, then clamp, as the SPIR-V translator applies them.
            if (instruction.Control is Gen5Vop3Control control)
            {
                value = control.OutputModifier switch
                {
                    1 => $"sharpemu_f64_mul({value}, 0x4000000000000000ul)",
                    2 => $"sharpemu_f64_mul({value}, 0x4010000000000000ul)",
                    3 => $"sharpemu_f64_mul({value}, 0x3FE0000000000000ul)",
                    _ => value,
                };
                if (control.Clamp)
                {
                    value = $"sharpemu_f64_clamp01({value})";
                }
            }

            // Both halves come from one temporary, so a destination pair that
            // overlaps a source pair still reads the sources first.
            var result = Temp("ulong", value);
            StoreVector(destination, $"(uint){result}");
            StoreVector(destination + 1, $"(uint)({result} >> 32)");
            return true;
        }

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
