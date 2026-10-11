// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Metal;

public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        private const string TauLiteral = "6.2831853071795862f";

        // ---- vector ALU ----

        private bool TryEmitVectorAlu(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Opcode == "VNop")
            {
                return true;
            }

            if (instruction.Control is Gen5SdwaControl sdwa &&
                (sdwa.Source0Select == 7 ||
                 sdwa.Source1Select == 7 ||
                 sdwa.DestinationSelect == 7 ||
                 sdwa.DestinationUnused == 3))
            {
                error = $"reserved SDWA selector/modifier in {instruction.Opcode}";
                return false;
            }

            if (instruction.Control is Gen5DppControl dppControl &&
                !IsSupportedDppControl(dppControl.Control))
            {
                error = $"unsupported DPP16 control 0x{dppControl.Control:X3}";
                return false;
            }

            if (instruction.Opcode.StartsWith("VCmp", StringComparison.Ordinal))
            {
                return TryEmitVectorCompare(instruction, out error);
            }

            switch (instruction.Opcode)
            {
                case "VReadfirstlaneB32":
                {
                    if (instruction.Destinations.Count == 0 ||
                        instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister ||
                        instruction.Sources.Count == 0)
                    {
                        error = "invalid read-first-lane operands";
                        return false;
                    }

                    // Under the single-lane graphics model the "first active
                    // lane" is always this lane; a real simd_shuffle would read
                    // another fragment's value. Compute broadcasts from the
                    // first guest-active lane (the ballot of EXEC), matching
                    // the SPIR-V translator — SPIR-V's own BroadcastFirst uses
                    // the first host-active invocation, which may be a lane the
                    // guest has masked off.
                    var value = RawSource(instruction, 0);
                    if (IsSingleLaneStage)
                    {
                        StoreScalar(instruction.Destinations[0].Value, Temp("uint", value));
                        return true;
                    }

                    if (IsWave64)
                    {
                        StoreScalar(
                            instruction.Destinations[0].Value,
                            EmitWave64ReadFirstLane(value));
                        return true;
                    }

                    var mask = Temp("uint", "sharpemu_ballot(exec)");
                    var firstLane = Temp("uint", $"{mask} == 0u ? 0u : (uint)ctz({mask})");
                    StoreScalar(
                        instruction.Destinations[0].Value,
                        Temp("uint", ShuffleLane(value, firstLane)));
                    return true;
                }
                case "VReadlaneB32":
                {
                    if (instruction.Destinations.Count == 0 ||
                        instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
                    {
                        error = "VReadlaneB32 expects scalar destination";
                        return false;
                    }

                    var value = RawSource(instruction, 0);
                    var lane = Temp("uint", $"({RawSource(instruction, 1)}) & 31u");
                    StoreScalar(
                        instruction.Destinations[0].Value,
                        Temp("uint", ShuffleLane(value, lane)));
                    return true;
                }
                case "VMovrelsB32":
                case "VMovreldB32":
                case "VMovrelsdB32":
                case "VMovrelsd2B32":
                    return TryEmitMoveRelative(instruction, out error);
                case "VWritelaneB32":
                {
                    // vdst[lane(src1)] = src0; a writelane lands regardless of EXEC.
                    var destination = DestinationVector(instruction);
                    var source = RawSource(instruction, 0);
                    var lane = RawSource(instruction, 1);
                    StoreVector(
                        destination,
                        $"(sharpemu_lane == (({lane}) & 31u)) ? ({source}) : v[{destination}]",
                        guardWithExec: false);
                    return true;
                }
                case "VCndmaskB32":
                {
                    // dst = mask-bit(lane) ? src1 : src0. VOP3 abs/neg act on the
                    // sign bit, matching the SPIR-V translator; the mask is VCC
                    // for VOP2 and an explicit SGPR operand for VOP3.
                    var mask = instruction.Sources.Count > 2
                        ? MaskBitExpression(instruction.Sources[2])
                        : "vcc";
                    StoreVector(
                        DestinationVector(instruction),
                        $"({mask}) ? ({SignModifiedSource(instruction, 1)}) : ({SignModifiedSource(instruction, 0)})");
                    return true;
                }
            }

            return TryEmitVectorValue(instruction, out error);
        }

        private bool TryEmitVectorValue(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            var destination = DestinationVector(instruction);
            string? expression = instruction.Opcode switch
            {
                "VMovB32" => RawSource(instruction, 0),

                // ---- float arithmetic ----
                "VAddF32" => FloatResult(instruction, $"{F(instruction, 0)} + {F(instruction, 1)}"),
                "VAddF16" => Float16Result(
                    instruction,
                    destination,
                    $"{F16(instruction, 0)} + {F16(instruction, 1)}"),
                "VSubF32" => FloatResult(instruction, $"{F(instruction, 0)} - {F(instruction, 1)}"),
                "VSubrevF32" => FloatResult(instruction, $"{F(instruction, 1)} - {F(instruction, 0)}"),
                "VSubF16" => Float16Result(
                    instruction,
                    destination,
                    $"{F16(instruction, 0)} - {F16(instruction, 1)}"),
                "VSubrevF16" => Float16Result(
                    instruction,
                    destination,
                    $"{F16(instruction, 1)} - {F16(instruction, 0)}"),
                "VMulF32" => FloatResult(instruction, $"{F(instruction, 0)} * {F(instruction, 1)}"),
                "VMulF16" => Float16Result(
                    instruction,
                    destination,
                    $"{F16(instruction, 0)} * {F16(instruction, 1)}"),
                "VMinF32" => FloatResult(instruction, $"fmin({F(instruction, 0)}, {F(instruction, 1)})"),
                "VMaxF32" => FloatResult(instruction, $"fmax({F(instruction, 0)}, {F(instruction, 1)})"),
                "VMinF16" => Float16Result(
                    instruction,
                    destination,
                    $"fmin({F16(instruction, 0)}, {F16(instruction, 1)})"),
                "VMaxF16" => Float16Result(
                    instruction,
                    destination,
                    $"fmax({F16(instruction, 0)}, {F16(instruction, 1)})"),
                // The decoder normalizes mk/ak literal placement, so every MAD/FMA
                // form is fma(src0, src1, src2) exactly like the SPIR-V translator.
                "VFmaF32" or "VMadF32" or "VMadAkF32" or "VMadMkF32" or "VFmaAkF32" or "VFmaMkF32" =>
                    FloatResult(instruction, $"fma({F(instruction, 0)}, {F(instruction, 1)}, {F(instruction, 2)})"),
                "VFmaF16" or "VFmaMkF16" or "VFmaAkF16" => Float16Result(
                    instruction,
                    destination,
                    $"fma({F16(instruction, 0)}, {F16(instruction, 1)}, {F16(instruction, 2)})"),
                "VFmacF32" or "VMacF32" =>
                    FloatResult(instruction, $"fma({F(instruction, 0)}, {F(instruction, 1)}, as_type<float>(v[{destination}]))"),
                "VPkFmacF16" => EmitPackedF16Fmac(instruction, destination),
                "VDot2cF32F16" => EmitDot2cF32F16(instruction, destination),
                "VFloorF32" => FloatResult(instruction, $"floor({F(instruction, 0)})"),
                "VCeilF32" => FloatResult(instruction, $"ceil({F(instruction, 0)})"),
                "VTruncF32" => FloatResult(instruction, $"trunc({F(instruction, 0)})"),
                "VRndneF32" => FloatResult(instruction, $"rint({F(instruction, 0)})"),
                "VFractF32" => FloatResult(instruction, $"fract({F(instruction, 0)})"),
                "VSqrtF32" => FloatResult(instruction, $"sqrt({F(instruction, 0)})"),
                "VRsqF32" => FloatResult(instruction, $"rsqrt({F(instruction, 0)})"),
                "VRcpF16" => Float16Result(instruction, destination, $"(1.0f / {F16(instruction, 0)})"),
                "VSqrtF16" => Float16Result(instruction, destination, $"sqrt({F16(instruction, 0)})"),
                "VLogF16" => Float16Result(instruction, destination, $"log2({F16(instruction, 0)})"),
                "VExpF16" => Float16Result(instruction, destination, $"exp2({F16(instruction, 0)})"),
                "VFloorF16" => Float16Result(instruction, destination, $"floor({F16(instruction, 0)})"),
                "VCeilF16" => Float16Result(instruction, destination, $"ceil({F16(instruction, 0)})"),
                "VTruncF16" => Float16Result(instruction, destination, $"trunc({F16(instruction, 0)})"),
                "VRndneF16" => Float16Result(instruction, destination, $"rint({F16(instruction, 0)})"),
                "VFractF16" => Float16Result(instruction, destination, $"fract({F16(instruction, 0)})"),
                "VSinF16" => Float16Result(instruction, destination, $"sin({F16(instruction, 0)} * {TauLiteral})"),
                "VCosF16" => Float16Result(instruction, destination, $"cos({F16(instruction, 0)} * {TauLiteral})"),
                "VRsqF16" => Float16Result(
                    instruction,
                    destination,
                    $"rsqrt({F16(instruction, 0)})"),
                "VRcpF32" or "VRcpIflagF32" => FloatResult(instruction, $"(1.0f / {F(instruction, 0)})"),
                "VLogF32" => FloatResult(instruction, $"log2({F(instruction, 0)})"),
                "VExpF32" => FloatResult(instruction, $"exp2({F(instruction, 0)})"),
                // GCN sin/cos take revolutions; mirror the SPIR-V Tau prescale.
                "VSinF32" => FloatResult(instruction, $"sin({F(instruction, 0)} * {TauLiteral})"),
                "VCosF32" => FloatResult(instruction, $"cos({F(instruction, 0)} * {TauLiteral})"),
                "VLdexpF32" =>
                    FloatResult(instruction, $"ldexp({F(instruction, 0)}, as_type<int>({RawSource(instruction, 1)}))"),
                "VMin3F16" => Float16Result(instruction, destination,
                    $"fmin(fmin({F16(instruction, 0)}, {F16(instruction, 1)}), {F16(instruction, 2)})"),
                "VMax3F16" => Float16Result(instruction, destination,
                    $"fmax(fmax({F16(instruction, 0)}, {F16(instruction, 1)}), {F16(instruction, 2)})"),
                "VMed3F16" => Float16Result(instruction, destination,
                    $"fmax(fmin({F16(instruction, 0)}, {F16(instruction, 1)}), fmin(fmax({F16(instruction, 0)}, {F16(instruction, 1)}), {F16(instruction, 2)}))"),
                "VMin3F32" =>
                    FloatResult(instruction, $"fmin(fmin({F(instruction, 0)}, {F(instruction, 1)}), {F(instruction, 2)})"),
                "VMax3F32" =>
                    FloatResult(instruction, $"fmax(fmax({F(instruction, 0)}, {F(instruction, 1)}), {F(instruction, 2)})"),
                "VMed3F32" =>
                    FloatResult(instruction, $"fmax(fmin({F(instruction, 0)}, {F(instruction, 1)}), fmin(fmax({F(instruction, 0)}, {F(instruction, 1)}), {F(instruction, 2)}))"),

                // ---- conversions ----
                "VCvtF32I32" => FloatResult(instruction, $"(float)as_type<int>({RawSource(instruction, 0)})"),
                "VCvtF32U32" => FloatResult(instruction, $"(float)({RawSource(instruction, 0)})"),
                "VCvtU32F32" => $"(uint)({F(instruction, 0)})",
                "VCvtU16F16" =>
                    $"(uint)clamp(trunc(isnan({F16(instruction, 0)}) ? 0.0f : {F16(instruction, 0)}), 0.0f, 65535.0f)",
                "VCvtI32F32" => AsUInt($"(int)({F(instruction, 0)})"),
                // RPI rounds toward positive infinity; FLR toward negative.
                "VCvtRpiI32F32" => AsUInt($"(int)ceil({F(instruction, 0)})"),
                "VCvtFlrI32F32" => AsUInt($"(int)floor({F(instruction, 0)})"),
                "VCvtF32Ubyte0" => FloatResult(instruction, $"(float)(({RawSource(instruction, 0)}) & 0xFFu)"),
                "VCvtF32Ubyte1" => FloatResult(instruction, $"(float)((({RawSource(instruction, 0)}) >> 8) & 0xFFu)"),
                "VCvtF32Ubyte2" => FloatResult(instruction, $"(float)((({RawSource(instruction, 0)}) >> 16) & 0xFFu)"),
                "VCvtF32Ubyte3" => FloatResult(instruction, $"(float)((({RawSource(instruction, 0)}) >> 24) & 0xFFu)"),
                "VCvtF16F32" =>
                    $"((uint)as_type<ushort>(half({F(instruction, 0)})))",
                "VCvtF32F16" =>
                    AsUInt($"(float)as_type<half>((ushort)(({RawSource(instruction, 0)}) & 0xFFFFu))"),
                "VCvtOffF32I4" =>
                    AsUInt($"sharpemu_off_i4_table[({RawSource(instruction, 0)}) & 15u]"),
                "VCvtPkU8F32" =>
                    EmitCvtPkU8F32(instruction),
                "VCvtPkrtzF16F32" =>
                    EmitCvtPkrtzF16F32(instruction),
                "VCvtPkU16U32" =>
                    $"(min({RawSource(instruction, 0)}, 65535u) | (min({RawSource(instruction, 1)}, 65535u) << 16u))",
                "VCvtPkI16I32" =>
                    $"((as_type<uint>(clamp(as_type<int>({RawSource(instruction, 0)}), -32768, 32767)) & 0xFFFFu) | " +
                    $"(as_type<uint>(clamp(as_type<int>({RawSource(instruction, 1)}), -32768, 32767)) << 16u))",
                "VCvtPknormI16F32" =>
                    $"pack_float_to_snorm2x16(float2({F(instruction, 0)}, {F(instruction, 1)}))",
                "VCvtPknormU16F32" =>
                    $"pack_float_to_unorm2x16(float2({F(instruction, 0)}, {F(instruction, 1)}))",

                // ---- integer arithmetic ----
                "VAddU32" or "VAddI32" =>
                    $"(({RawSource(instruction, 0)}) + ({RawSource(instruction, 1)}))",
                "VSubU32" or "VSubI32" =>
                    $"(({RawSource(instruction, 0)}) - ({RawSource(instruction, 1)}))",
                "VSubrevU32" or "VSubrevI32" =>
                    $"(({RawSource(instruction, 1)}) - ({RawSource(instruction, 0)}))",
                "VMulI32I24" =>
                    EmitSignedMultiply24(instruction),
                // The 24-bit product wraps in 32 bits before the addend joins it.
                "VMadI32I24" =>
                    $"({EmitSignedMultiply24(instruction)} + ({RawSource(instruction, 2)}))",
                // The SPIR-V translator treats the U24 multiply as a full 32-bit
                // multiply (only the Hi/Mad forms mask); mirror it exactly.
                "VMulLoU32" or "VMulLoI32" or "VMulU32U24" =>
                    $"(({RawSource(instruction, 0)}) * ({RawSource(instruction, 1)}))",
                "VMulHiU32" =>
                    $"mulhi({RawSource(instruction, 0)}, {RawSource(instruction, 1)})",
                "VMulHiU32U24" =>
                    $"mulhi(({RawSource(instruction, 0)}) & 0xFFFFFFu, ({RawSource(instruction, 1)}) & 0xFFFFFFu)",
                "VMulHiI32" =>
                    AsUInt($"mulhi(as_type<int>({RawSource(instruction, 0)}), as_type<int>({RawSource(instruction, 1)}))"),
                "VMadU32U24" =>
                    $"(((({RawSource(instruction, 0)}) & 0xFFFFFFu) * (({RawSource(instruction, 1)}) & 0xFFFFFFu)) + ({RawSource(instruction, 2)}))",
                "VMadU32U16" =>
                    $"(((({RawSource(instruction, 0)}) & 0xFFFFu) * (({RawSource(instruction, 1)}) & 0xFFFFu)) + ({RawSource(instruction, 2)}))",
                "VAdd3U32" =>
                    $"(({RawSource(instruction, 0)}) + ({RawSource(instruction, 1)}) + ({RawSource(instruction, 2)}))",
                "VXadU32" =>
                    $"((({RawSource(instruction, 0)}) ^ ({RawSource(instruction, 1)})) + ({RawSource(instruction, 2)}))",
                "VSadU32" =>
                    $"((max({RawSource(instruction, 0)}, {RawSource(instruction, 1)}) - min({RawSource(instruction, 0)}, {RawSource(instruction, 1)})) + ({RawSource(instruction, 2)}))",
                "VAddLshlU32" =>
                    $"((({RawSource(instruction, 0)}) + ({RawSource(instruction, 1)})) << (({RawSource(instruction, 2)}) & 31u))",
                "VLshlAddU32" =>
                    $"((({RawSource(instruction, 0)}) << (({RawSource(instruction, 1)}) & 31u)) + ({RawSource(instruction, 2)}))",
                "VMinU32" => $"min({RawSource(instruction, 0)}, {RawSource(instruction, 1)})",
                "VMaxU32" => $"max({RawSource(instruction, 0)}, {RawSource(instruction, 1)})",
                "VMinI32" =>
                    AsUInt($"min(as_type<int>({RawSource(instruction, 0)}), as_type<int>({RawSource(instruction, 1)}))"),
                "VMaxI32" =>
                    AsUInt($"max(as_type<int>({RawSource(instruction, 0)}), as_type<int>({RawSource(instruction, 1)}))"),
                "VMin3U32" =>
                    $"min(min({RawSource(instruction, 0)}, {RawSource(instruction, 1)}), {RawSource(instruction, 2)})",
                "VMax3U32" =>
                    $"max(max({RawSource(instruction, 0)}, {RawSource(instruction, 1)}), {RawSource(instruction, 2)})",
                "VMin3I32" =>
                    AsUInt($"min(min(as_type<int>({RawSource(instruction, 0)}), as_type<int>({RawSource(instruction, 1)})), as_type<int>({RawSource(instruction, 2)}))"),
                "VMax3I32" =>
                    AsUInt($"max(max(as_type<int>({RawSource(instruction, 0)}), as_type<int>({RawSource(instruction, 1)})), as_type<int>({RawSource(instruction, 2)}))"),
                "VMed3U32" =>
                    $"max(min({RawSource(instruction, 0)}, {RawSource(instruction, 1)}), min(max({RawSource(instruction, 0)}, {RawSource(instruction, 1)}), {RawSource(instruction, 2)}))",
                "VMed3I32" =>
                    AsUInt($"max(min(as_type<int>({RawSource(instruction, 0)}), as_type<int>({RawSource(instruction, 1)})), min(max(as_type<int>({RawSource(instruction, 0)}), as_type<int>({RawSource(instruction, 1)})), as_type<int>({RawSource(instruction, 2)})))"),

                // ---- bitwise ----
                "VAndB32" => $"(({RawSource(instruction, 0)}) & ({RawSource(instruction, 1)}))",
                "VOrB32" => $"(({RawSource(instruction, 0)}) | ({RawSource(instruction, 1)}))",
                "VXorB32" => $"(({RawSource(instruction, 0)}) ^ ({RawSource(instruction, 1)}))",
                "VXor3B32" => $"(({RawSource(instruction, 0)}) ^ ({RawSource(instruction, 1)}) ^ ({RawSource(instruction, 2)}))",
                "VXnorB32" => $"~(({RawSource(instruction, 0)}) ^ ({RawSource(instruction, 1)}))",
                "VNotB32" => $"~({RawSource(instruction, 0)})",
                "VAndOrB32" =>
                    $"((({RawSource(instruction, 0)}) & ({RawSource(instruction, 1)})) | ({RawSource(instruction, 2)}))",
                "VOr3U32" =>
                    $"(({RawSource(instruction, 0)}) | ({RawSource(instruction, 1)}) | ({RawSource(instruction, 2)}))",
                "VLshlOrU32" =>
                    $"((({RawSource(instruction, 0)}) << (({RawSource(instruction, 1)}) & 31u)) | ({RawSource(instruction, 2)}))",
                "VLshlB32" => $"(({RawSource(instruction, 0)}) << (({RawSource(instruction, 1)}) & 31u))",
                "VLshlrevB32" => $"(({RawSource(instruction, 1)}) << (({RawSource(instruction, 0)}) & 31u))",
                "VLshrB32" => $"(({RawSource(instruction, 0)}) >> (({RawSource(instruction, 1)}) & 31u))",
                "VLshrrevB32" => $"(({RawSource(instruction, 1)}) >> (({RawSource(instruction, 0)}) & 31u))",
                "VLshrrevB64" => EmitShiftRev64(instruction, destination, left: false),
                "VLshlrevB64" => EmitShiftRev64(instruction, destination, left: true),
                "VAshrI32" =>
                    AsUInt($"(as_type<int>({RawSource(instruction, 0)}) >> (({RawSource(instruction, 1)}) & 31u))"),
                "VAshrrevI32" =>
                    AsUInt($"(as_type<int>({RawSource(instruction, 1)}) >> (({RawSource(instruction, 0)}) & 31u))"),
                "VBfeU32" =>
                    $"extract_bits({RawSource(instruction, 0)}, ({RawSource(instruction, 1)}) & 31u, ({RawSource(instruction, 2)}) & 31u)",
                // Same field as VBfeU32; the signed overload sign-extends from the
                // field's top bit, so the source has to enter extract_bits as int.
                "VBfeI32" =>
                    AsUInt($"extract_bits(as_type<int>({RawSource(instruction, 0)}), ({RawSource(instruction, 1)}) & 31u, ({RawSource(instruction, 2)}) & 31u)"),
                "VBfiB32" =>
                    $"((({RawSource(instruction, 0)}) & ({RawSource(instruction, 1)})) | (~({RawSource(instruction, 0)}) & ({RawSource(instruction, 2)})))",
                "VAlignbitB32" =>
                    $"uint(((ulong({RawSource(instruction, 0)}) << 32) | ulong({RawSource(instruction, 1)})) >> (({RawSource(instruction, 2)}) & 31u))",
                "VAlignbyteB32" =>
                    $"uint(((ulong({RawSource(instruction, 0)}) << 32) | ulong({RawSource(instruction, 1)})) >> ((({RawSource(instruction, 2)}) & 3u) * 8u))",
                "VBfmB32" =>
                    $"(((1u << (({RawSource(instruction, 0)}) & 31u)) - 1u) << (({RawSource(instruction, 1)}) & 31u))",
                "VBfrevB32" => $"reverse_bits({RawSource(instruction, 0)})",
                "VBcntU32B32" => $"(popcount({RawSource(instruction, 0)}) + ({RawSource(instruction, 1)}))",
                "VFfblB32" =>
                    $"(({RawSource(instruction, 0)}) == 0u ? 0xFFFFFFFFu : (uint)ctz({RawSource(instruction, 0)}))",
                "VFfbhU32" => EmitFindFirstBitHigh(instruction, signed: false),
                "VFfbhI32" => EmitFindFirstBitHigh(instruction, signed: true),

                // ---- wave / lane ----
                // mbcnt reads the mask dword the guest passes (no cross-lane
                // op), so only the per-lane thread-mask math differs by wave
                // size. Wave64 lanes 32..63 count the whole low half in mbcnt_lo
                // and their own partial in mbcnt_hi; a 1u << lane for lane>=32
                // would be undefined, so those are split out.
                "VMbcntLoU32B32" => IsWave64
                    ? $"((sharpemu_lane >= 32u ? popcount({RawSource(instruction, 0)}) : popcount(({RawSource(instruction, 0)}) & ((1u << sharpemu_lane) - 1u))) + ({RawSource(instruction, 1)}))"
                    : $"(popcount(({RawSource(instruction, 0)}) & ((1u << sharpemu_lane) - 1u)) + ({RawSource(instruction, 1)}))",
                "VMbcntHiU32B32" => IsWave64
                    ? $"((sharpemu_lane >= 32u ? popcount(({RawSource(instruction, 0)}) & ((1u << (sharpemu_lane - 32u)) - 1u)) : 0u) + ({RawSource(instruction, 1)}))"
                    // Wave32: the high mask half holds no lanes; pass the addend.
                    : RawSource(instruction, 1),
                "VPermlane16B32" => EmitPermlane16(instruction, exchangeRows: false),
                "VPermlanex16B32" => EmitPermlane16(instruction, exchangeRows: true),

                // ---- cube map helpers ----
                "VCubeidF32" => EmitCubeCoordinate(instruction, CubeCoordinate.Id),
                "VCubescF32" => EmitCubeCoordinate(instruction, CubeCoordinate.Sc),
                "VCubetcF32" => EmitCubeCoordinate(instruction, CubeCoordinate.Tc),
                "VCubemaF32" => EmitCubeCoordinate(instruction, CubeCoordinate.Ma),

                _ => null,
            };

            if (expression is null)
            {
                switch (instruction.Opcode)
                {
                    case "VAddCoU32":
                    {
                        var left = Temp("uint", RawSource(instruction, 0));
                        var right = Temp("uint", RawSource(instruction, 1));
                        var sum = Temp("uint", $"{left} + {right}");
                        StoreCarryOut(instruction, $"{sum} < {left}");
                        expression = sum;
                        break;
                    }
                    case "VSubCoU32":
                    case "VSubrevCoU32":
                    {
                        var reverse = instruction.Opcode == "VSubrevCoU32";
                        var left = Temp("uint", RawSource(instruction, reverse ? 1 : 0));
                        var right = Temp("uint", RawSource(instruction, reverse ? 0 : 1));
                        StoreCarryOut(instruction, $"{left} < {right}");
                        expression = $"({left} - {right})";
                        break;
                    }
                    case "VAddcU32":
                    case "VAddCoCiU32":
                    {
                        var left = Temp("uint", RawSource(instruction, 0));
                        var right = Temp("uint", RawSource(instruction, 1));
                        var carryIn = instruction.Sources.Count > 2
                            ? MaskBitExpression(instruction.Sources[2])
                            : "vcc";
                        var partial = Temp("uint", $"{left} + {right}");
                        var sum = Temp("uint", $"{partial} + (({carryIn}) ? 1u : 0u)");
                        StoreCarryOut(instruction, $"({partial} < {left}) || ({sum} < {partial})");
                        expression = sum;
                        break;
                    }
                    case "VSubbU32":
                    case "VSubbrevU32":
                    case "VSubCoCiU32":
                    case "VSubrevCoCiU32":
                    {
                        var reverse = instruction.Opcode is "VSubbrevU32" or "VSubrevCoCiU32";
                        var left = Temp("uint", RawSource(instruction, reverse ? 1 : 0));
                        var right = Temp("uint", RawSource(instruction, reverse ? 0 : 1));
                        var borrowIn = instruction.Sources.Count > 2
                            ? MaskBitExpression(instruction.Sources[2])
                            : "vcc";
                        var borrow = Temp("uint", $"({borrowIn}) ? 1u : 0u");
                        var partial = Temp("uint", $"{left} - {right}");
                        StoreCarryOut(instruction, $"({left} < {right}) || ({partial} < {borrow})");
                        expression = $"({partial} - {borrow})";
                        break;
                    }
                    case "VMadU64U32":
                    {
                        // 64-bit product+addend into a VGPR pair, carry to SDST.
                        var product = Temp(
                            "ulong",
                            $"(ulong)({RawSource(instruction, 0)}) * (ulong)({RawSource(instruction, 1)})");
                        var addend = Temp("ulong", RawSource64(instruction, 2));
                        var wide = Temp("ulong", $"{product} + {addend}");
                        StoreCarryOut(instruction, $"{wide} < {addend}");
                        StoreVector(destination + 1, $"(uint)({wide} >> 32)");
                        expression = $"(uint){wide}";
                        break;
                    }
                    case "VPkAddF16":
                    case "VPkMulF16":
                    case "VPkMinF16":
                    case "VPkMaxF16":
                    case "VPkFmaF16":
                    case "VPkAddI16":
                    case "VPkSubI16":
                    case "VFmaMixF32":
                    case "VFmaMixloF16":
                    case "VFmaMixhiF16":
                    {
                        if (instruction.Control is not Gen5Vop3pControl packed)
                        {
                            error = $"missing vop3p control for {instruction.Opcode}";
                            return false;
                        }

                        expression = instruction.Opcode switch
                        {
                            "VPkAddI16" => EmitPackedI16Arithmetic(instruction, packed, subtract: false),
                            "VPkSubI16" => EmitPackedI16Arithmetic(instruction, packed, subtract: true),
                            "VFmaMixF32" or "VFmaMixloF16" or "VFmaMixhiF16" =>
                                EmitFmaMix(instruction, packed, destination),
                            _ => EmitPackedF16(instruction, packed),
                        };
                        break;
                    }
                    case "VAddNcU16":
                    case "VAddNcI16":
                    case "VSubNcU16":
                    case "VSubNcI16":
                    case "VLshrrevB16":
                    case "VLshlrevB16":
                    case "VAshrrevI16":
                    case "VMaxU16":
                    case "VMinU16":
                    case "VMaxI16":
                    case "VMinI16":
                    {
                        // The hardware saturates these to the 16-bit signed or
                        // unsigned range when clamp is set; the SPIR-V translator
                        // refuses that case rather than mis-emitting it, and both
                        // backends have to agree on what a program may rely on.
                        if (instruction.Control is Gen5Vop3Control { Clamp: true })
                        {
                            error = $"{instruction.Opcode}: clamped 16-bit integer results are not supported";
                            return false;
                        }

                        var signed = instruction.Opcode.EndsWith("I16", StringComparison.Ordinal);
                        var left = Int16Source(instruction, 0, signed);
                        var right = Int16Source(instruction, 1, signed);
                        expression = Int16Result(
                            instruction,
                            destination,
                            instruction.Opcode switch
                            {
                                "VAddNcU16" or "VAddNcI16" => $"(({left}) + ({right}))",
                                "VSubNcU16" or "VSubNcI16" => $"(({left}) - ({right}))",
                                // The rev shifts take the shift amount from src0 and
                                // the value from src1, with only four bits of count.
                                "VLshrrevB16" => $"(({right}) >> (({left}) & 15u))",
                                "VLshlrevB16" => $"(({right}) << (({left}) & 15u))",
                                "VAshrrevI16" => AsUInt($"(as_type<int>({right}) >> (({left}) & 15u))"),
                                "VMaxU16" => $"max({left}, {right})",
                                "VMinU16" => $"min({left}, {right})",
                                "VMaxI16" => AsUInt($"max(as_type<int>({left}), as_type<int>({right}))"),
                                _ => AsUInt($"min(as_type<int>({left}), as_type<int>({right}))"),
                            });
                        break;
                    }
                    default:
                        error = $"unsupported vector opcode {instruction.Opcode}";
                        return false;
                }
            }

            var result = Temp("uint", expression);
            if (instruction.Control is Gen5DppControl dpp)
            {
                var writeEnabled = EmitDppWriteEnabled(dpp);
                result = Temp("uint", $"({writeEnabled}) ? {result} : v[{destination}]");
            }

            if (instruction.Control is Gen5SdwaControl { ScalarDestination: null } sdwaDestination)
            {
                result = ApplySdwaDestination(sdwaDestination, result, $"v[{destination}]");
            }

            StoreVector(destination, result);
            return true;
        }

        private string EmitSignedMultiply24(Gen5ShaderInstruction instruction)
        {
            var signedLeft = $"(as_type<int>(({RawSource(instruction, 0)}) << 8u) >> 8)";
            var signedRight = $"(as_type<int>(({RawSource(instruction, 1)}) << 8u) >> 8)";
            // Unsigned multiplication preserves the low result bits on overflow.
            return $"({AsUInt(signedLeft)} * {AsUInt(signedRight)})";
        }

        // The relative moves address the VGPR file relative to the wave's own
        // allocation base, so the encoded register number and M0 simply add.
        // V_MOVRELSD2 is the one form that takes separate source and destination
        // offsets, out of M0's two halves.
        //
        // Indexing the register file at run time is what costs: it forces the file to
        // stay an array, so none of it can be scalarized and all 256 dwords sit in
        // per-thread memory. In Octopath Traveler II that happens to exactly one shader
        // out of 67 — and it is the one that spends 44% of the frame's GPU time, a
        // vertex program run 12.5 million times a frame with four relative moves in it.
        //
        // Gen5MoveRelativeOffsets (shared IR, already used by the SPIR-V backend) says
        // which values M0 can hold at each relative move when straight-line scalar code
        // produces it. Knowing them turns the move into a select among constant register
        // numbers, and the file scalarizes like any other program's.
        private bool TryEmitMoveRelative(Gen5ShaderInstruction instruction, out string error)
        {
            error = string.Empty;
            var opcode = instruction.Opcode;
            if (instruction.Control is not null ||
                instruction.Sources.Count == 0 ||
                instruction.Destinations.Count == 0 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.VectorRegister)
            {
                error = $"{opcode} expects an unmodified operand and a VGPR destination";
                return false;
            }

            if (_options.BoundedRelativeMoves)
            {
                _moveRelativeOffsets ??= Ir.Gen5MoveRelativeOffsets.Analyze(_request.Program);
                if (_moveRelativeOffsets.TryGetValue(instruction.Pc, out var offsets) &&
                    offsets.Length != 0)
                {
                    return TryEmitBoundedMoveRelative(instruction, offsets, out error);
                }
            }

            // M0 that no straight-line reasoning bounds — the waterfall idiom
            // (v_readfirstlane into M0, v_cmpx, v_movrels) is the common source, and it
            // is what the hot Octopath vertex shader does. The register number is then
            // only known at run time, so the access is marked here and resolved once the
            // set of registers the program touches is known: see Gen5MslTranslator.Registers.
            var m0 = Temp("uint", ScalarExpression(M0Register));
            var sourceOffset = m0;
            var destinationOffset = m0;
            if (opcode == "VMovrelsd2B32")
            {
                sourceOffset = Temp("uint", $"{m0} & 0x3FFu");
                destinationOffset = Temp("uint", $"({m0} >> 16u) & 0x3FFu");
            }

            string value;
            if (opcode == "VMovreldB32")
            {
                // Only the destination is relative here: src0 is an ordinary
                // operand and may be an SGPR or an inline/literal constant.
                value = Temp("uint", RawSource(instruction, 0));
            }
            else
            {
                var source = instruction.Sources[0];
                if (source.Kind != Gen5OperandKind.VectorRegister)
                {
                    error = $"{opcode} source must be a vector register";
                    return false;
                }

                var sourceIndex = Temp(
                    "uint",
                    $"({source.Value}u + {sourceOffset}) & {VectorRegisterFileCount - 1}u");
                value = Temp("uint", _options.BoundedRelativeMoves
                    ? $"{DynamicVectorRead}({sourceIndex})"
                    : $"v[{sourceIndex}]");
            }

            var destination = instruction.Destinations[0].Value;
            if (opcode == "VMovrelsB32")
            {
                StoreVector(destination, value);
                return true;
            }

            var target = Temp(
                "uint",
                $"({destination}u + {destinationOffset}) & {VectorRegisterFileCount - 1}u");
            Line(ExecGuarded(_options.BoundedRelativeMoves
                ? $"{DynamicVectorWrite}({target}, {value});"
                : $"v[{target}] = {value};"));
            return true;
        }

        private IReadOnlyDictionary<uint, uint[]>? _moveRelativeOffsets;

        /// <summary>A relative move whose M0 values are known: the source becomes a chain
        /// of selects over constant register numbers and the destination a write per
        /// reachable register, so nothing indexes the register file at run time.</summary>
        private bool TryEmitBoundedMoveRelative(
            Gen5ShaderInstruction instruction,
            uint[] offsets,
            out string error)
        {
            error = string.Empty;
            var opcode = instruction.Opcode;
            var m0 = Temp("uint", ScalarExpression(M0Register));
            var sourceOffsets = offsets;
            var destinationOffsets = offsets;
            var sourceSelector = m0;
            var destinationSelector = m0;
            if (opcode == "VMovrelsd2B32")
            {
                // M0's halves are independent offsets, so each side selects on its own.
                sourceSelector = Temp("uint", $"{m0} & 0x3FFu");
                destinationSelector = Temp("uint", $"({m0} >> 16u) & 0x3FFu");
                sourceOffsets = Distinct(offsets, value => value & 0x3FFu);
                destinationOffsets = Distinct(offsets, value => (value >> 16) & 0x3FFu);
            }

            string value;
            if (opcode == "VMovreldB32")
            {
                value = Temp("uint", RawSource(instruction, 0));
            }
            else
            {
                var source = instruction.Sources[0];
                if (source.Kind != Gen5OperandKind.VectorRegister)
                {
                    error = $"{opcode} source must be a vector register";
                    return false;
                }

                var selected = VectorExpression(source.Value + sourceOffsets[0]);
                for (var index = 1; index < sourceOffsets.Length; index++)
                {
                    selected =
                        $"({sourceSelector} == {FormatUInt(sourceOffsets[index])} " +
                        $"? {VectorExpression(source.Value + sourceOffsets[index])} : {selected})";
                }

                value = Temp("uint", selected);
            }

            var destination = instruction.Destinations[0].Value;
            if (opcode == "VMovrelsB32")
            {
                StoreVector(destination, value);
                return true;
            }

            // Only the register M0 actually selects takes the value; the others keep
            // theirs, which is what writing through the index would have done.
            foreach (var offset in destinationOffsets)
            {
                var register = (destination + offset) & (VectorRegisterFileCount - 1);
                StoreVector(
                    register,
                    $"{destinationSelector} == {FormatUInt(offset)} ? {value} : {VectorExpression(register)}");
            }

            return true;
        }

        private static uint[] Distinct(uint[] values, Func<uint, uint> project)
        {
            var projected = new SortedSet<uint>();
            foreach (var value in values)
            {
                projected.Add(project(value));
            }

            return [.. projected];
        }

        // D.u64 = S1.u64 << or >> (S0.u32 & 0x3F), landing in the register pair.
        private string EmitShiftRev64(Gen5ShaderInstruction instruction, uint destination, bool left)
        {
            var shift = Temp("uint", $"({RawSource(instruction, 0)}) & 63u");
            var shifted = Temp(
                "ulong",
                $"({RawSource64(instruction, 1)}) {(left ? "<<" : ">>")} {shift}");
            StoreVector(destination + 1, $"(uint)({shifted} >> 32)");
            return $"(uint){shifted}";
        }

        private string EmitCvtPkU8F32(Gen5ShaderInstruction instruction)
        {
            var converted = Temp("uint", $"(uint)({F(instruction, 0)})");
            var offset = Temp("uint", $"(({RawSource(instruction, 1)}) & 3u) << 3");
            var baseValue = Temp("uint", RawSource(instruction, 2));
            return $"(({baseValue} & ~(0xFFu << {offset})) | (({converted} & 0xFFu) << {offset}))";
        }

        private string EmitCvtPkrtzF16F32(Gen5ShaderInstruction instruction)
        {
            // Round-to-zero via mantissa truncation before the half conversion,
            // mirroring the SPIR-V translator's TruncateFloat32ForPack.
            var first = Temp(
                "float",
                $"as_type<float>({AsUInt(F(instruction, 0))} & 0xFFFFE000u)");
            var second = Temp(
                "float",
                $"as_type<float>({AsUInt(F(instruction, 1))} & 0xFFFFE000u)");
            return $"(((uint)as_type<ushort>(half({first}))) | (((uint)as_type<ushort>(half({second}))) << 16))";
        }

        private string EmitFindFirstBitHigh(Gen5ShaderInstruction instruction, bool signed)
        {
            var source = Temp("uint", RawSource(instruction, 0));
            if (!signed)
            {
                // V_FFBH_U32 counts from bit 31, so 31 - findMSB is just the
                // leading-zero count; a zero source has no set bit and reports ~0.
                return $"({source} == 0u ? 0xFFFFFFFFu : (uint)clz({source}))";
            }

            // V_FFBH_I32 looks for the first bit that differs from the sign, which
            // is the first clear bit of a negative source: complementing turns it
            // back into a leading-zero count. 0 and -1 have no such bit at all.
            var magnitude = Temp(
                "uint",
                $"(({source} & 0x80000000u) != 0u) ? ~{source} : {source}");
            return $"({magnitude} == 0u ? 0xFFFFFFFFu : (uint)clz({magnitude}))";
        }

        // ---- VOP3P: packed and mixed 16-bit math ----

        // Packed f16 (VOP3P). Each source register carries two f16 values, one per
        // result lane, and the two lanes are computed independently: op_sel /
        // op_sel_hi pick which half of each source feeds the lane, neg_lo / neg_hi
        // negate it, the op runs in f32 and the result is rounded back to f16. The
        // SPIR-V backend has to open-code every f16 <-> f32 conversion because
        // SPIR-V without the Float16 capability cannot express them exactly; Metal
        // has a native `half` whose conversions are IEEE (the translator's libraries
        // are built with fast math off), so the conversions here are plain casts.
        // VOP3P carries neither abs nor omod, and F/FloatResult only look at
        // Gen5Vop3Control, so every modifier below is decoded by hand.
        private string EmitPackedF16(Gen5ShaderInstruction instruction, Gen5Vop3pControl control)
        {
            var sources = PackedSources(instruction, control, instruction.Opcode == "VPkFmaF16" ? 3 : 2);
            var low = EmitPackedF16Lane(instruction, control, sources, highLane: false);
            var high = EmitPackedF16Lane(instruction, control, sources, highLane: true);
            return $"(({low}) | (({high}) << 16))";
        }

        // Both lanes read the same sources, so each one is hoisted into a single
        // temp. A constant operand is one f32 value rather than a packed pair: both
        // lanes take it whole, which is why it is held as a float and not as raw bits.
        private string[] PackedSources(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            int count)
        {
            var sources = new string[count];
            for (var index = 0; index < count; index++)
            {
                sources[index] = IsPackedConstant(instruction.Sources[index])
                    ? Temp("float", F(instruction, index))
                    : Temp("uint", RawSource(instruction, index));
            }

            return sources;
        }

        private static bool IsPackedConstant(Gen5Operand operand) =>
            operand.Kind is Gen5OperandKind.EncodedConstant or Gen5OperandKind.LiteralConstant;

        private string EmitPackedF16Lane(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            string[] sources,
            bool highLane)
        {
            var left = PackedF16Operand(instruction, control, sources, 0, highLane);
            var right = PackedF16Operand(instruction, control, sources, 1, highLane);
            var value = instruction.Opcode switch
            {
                "VPkAddF16" => $"({left} + {right})",
                "VPkMulF16" => $"({left} * {right})",
                // fminnum_like / fmaxnum_like: a NaN operand yields the other one,
                // which is exactly what MSL's fmin/fmax are specified to do.
                "VPkMinF16" => $"fmin({left}, {right})",
                "VPkMaxF16" => $"fmax({left}, {right})",
                // Use half fma itself: float fma followed by half conversion can
                // double-round at a half midpoint (MSL specification section 6.6).
                _ => $"(float)fma(half({left}), half({right}), half({PackedF16Operand(instruction, control, sources, 2, highLane)}))",
            };
            if (control.Clamp)
            {
                value = ClampToUnitInterval(value);
            }

            // 0 and 1 are exact in both formats and the clamp is monotonic, so
            // clamping ahead of the narrowing gives the f16 the hardware clamps to.
            return $"((uint)as_type<ushort>(half({value})))";
        }

        /// <summary>One packed-f16 source half widened to f32, with its lane's negate applied.</summary>
        private string PackedF16Operand(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            string[] sources,
            int index,
            bool highLane)
        {
            var negateMask = highLane ? control.NegHiMask : control.NegLoMask;
            // A constant has no half to select; only the lane's negate applies.
            var value = IsPackedConstant(instruction.Sources[index])
                ? sources[index]
                : HalfToFloat(
                    sources[index],
                    (((highLane ? control.OpSelHiMask : control.OpSelMask) >> index) & 1) != 0);
            return ((negateMask >> index) & 1) != 0 ? $"(-{value})" : value;
        }

        /// <summary>Widens one 16-bit half of a packed register to f32.</summary>
        private static string HalfToFloat(string raw, bool highHalf) =>
            $"(float)as_type<half2>({raw}).{(highHalf ? "y" : "x")}";

        /// <summary>
        /// The VOP3P clamp modifier saturates to [0, 1] and turns NaN into 0. MSL's
        /// clamp() is unspecified for a NaN input, so the bounds are applied as
        /// ordered compares, which are false for NaN and therefore pick the 0 bound.
        /// </summary>
        private string ClampToUnitInterval(string value)
        {
            var bounded = Temp("float", value);
            var lowerBounded = Temp("float", $"({bounded} > 0.0f) ? {bounded} : 0.0f");
            return $"(({lowerBounded} < 1.0f) ? {lowerBounded} : 1.0f)";
        }

        // V_PK_ADD/SUB_I16 operate on the op_sel-selected halves of src0 and src1
        // and wrap in 16 bits per lane, so the signed and unsigned readings of the
        // operands share one bit-level implementation.
        private string EmitPackedI16Arithmetic(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            bool subtract)
        {
            var left = Temp("uint", RawSource(instruction, 0));
            var right = Temp("uint", RawSource(instruction, 1));
            var op = subtract ? "-" : "+";

            static string SelectHalf(string value, uint mask, int index) =>
                ((mask >> index) & 1) != 0
                    ? $"(({value} >> 16) & 0xFFFFu)"
                    : $"({value} & 0xFFFFu)";

            string Lane(uint mask) =>
                $"(({SelectHalf(left, mask, 0)} {op} {SelectHalf(right, mask, 1)}) & 0xFFFFu)";

            return $"({Lane(control.OpSelMask)} | ({Lane(control.OpSelHiMask)} << 16))";
        }

        // V_FMA_MIX_F32 / _MIXLO_F16 / _MIXHI_F16. Unlike the v_pk_* ops these are a
        // single f32 fma whose three sources are decoded independently: op_sel_hi
        // says a register source is an f16 half (picked by op_sel) widened to f32
        // rather than a whole f32, neg_hi is the absolute-value modifier and neg_lo
        // negates. _MIXLO / _MIXHI round the f32 result to f16 and merge it into one
        // half of vdst, leaving the other half as it was.
        private string EmitFmaMix(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            uint destination)
        {
            var value =
                $"fma({FmaMixOperand(instruction, control, 0)}, " +
                $"{FmaMixOperand(instruction, control, 1)}, " +
                $"{FmaMixOperand(instruction, control, 2)})";
            if (control.Clamp)
            {
                value = ClampToUnitInterval(value);
            }

            if (instruction.Opcode == "VFmaMixF32")
            {
                return AsUInt($"({value})");
            }

            var packed = $"((uint)as_type<ushort>(half({value})))";
            return instruction.Opcode == "VFmaMixloF16"
                ? $"((v[{destination}] & 0xFFFF0000u) | {packed})"
                : $"((v[{destination}] & 0x0000FFFFu) | ({packed} << 16))";
        }

        private string FmaMixOperand(
            Gen5ShaderInstruction instruction,
            Gen5Vop3pControl control,
            int index)
        {
            var operand = instruction.Sources[index];
            // Inline constants and literals are always read as a whole f32; only a
            // register source can be narrowed to one of its halves.
            var readAsHalf = ((control.OpSelHiMask >> index) & 1) != 0 &&
                operand.Kind is Gen5OperandKind.VectorRegister or Gen5OperandKind.ScalarRegister;
            var value = readAsHalf
                ? HalfToFloat(
                    Temp("uint", RawSource(instruction, index)),
                    ((control.OpSelMask >> index) & 1) != 0)
                : F(instruction, index);

            // abs first, then negate — the order the hardware applies them in.
            if (((control.NegHiMask >> index) & 1) != 0)
            {
                value = $"fabs({value})";
            }

            return ((control.NegLoMask >> index) & 1) != 0 ? $"(-{value})" : value;
        }

        // V_PK_FMAC_F16 (VOP2 0x3C): each half of the destination accumulates its own
        // fused f16 multiply-add, D.h = fma(S0.h, S1.h, D.h). There is no op_sel and
        // no neg on this encoding, so the halves are read straight out of the raw
        // bits — including for an inline-constant source, whose 32 bits are read as a
        // packed pair like any register. Metal's half fma rounds exactly once, which
        // is what makes the fused form expressible directly.
        private string EmitPackedF16Fmac(Gen5ShaderInstruction instruction, uint destination)
        {
            var left = Temp("half2", $"as_type<half2>({RawSource(instruction, 0)})");
            var right = Temp("half2", $"as_type<half2>({RawSource(instruction, 1)})");
            var accumulator = Temp("half2", $"as_type<half2>(v[{destination}])");
            return AsUInt($"fma({left}, {right}, {accumulator})");
        }

        // RDNA2 ISA section 12.7: DOT2C accumulates the two half products
        // into the old float destination. The C means accumulate, not clamp.
        private string EmitDot2cF32F16(Gen5ShaderInstruction instruction, uint destination)
        {
            var left = Temp("float2", $"(float2)as_type<half2>({RawSource(instruction, 0)})");
            var right = Temp("float2", $"(float2)as_type<half2>({RawSource(instruction, 1)})");
            var dot = $"({left}.x * {right}.x + {left}.y * {right}.y + as_type<float>(v[{destination}]))";
            return FloatResult(instruction, dot);
        }

        // ---- 16-bit integer VALU ----

        /// <summary>
        /// The 16-bit half of a source feeding a VOP3 16-bit integer op: op_sel bit
        /// `sourceIndex` takes the high half, and the signed forms sign-extend it so
        /// the surrounding 32-bit op sees the right value.
        /// </summary>
        private string Int16Source(Gen5ShaderInstruction instruction, int sourceIndex, bool signed)
        {
            var raw = RawSource(instruction, sourceIndex, applySdwaIntegerModifiers: false);
            var shift = instruction.Control is Gen5Vop3Control control &&
                (control.OperandSelect & (1u << sourceIndex)) != 0
                    ? 16
                    : 0;
            var half = $"((({raw}) >> {shift}) & 0xFFFFu)";
            return signed ? AsUInt($"(as_type<int>({half} << 16) >> 16)") : half;
        }

        /// <summary>Writes the low 16 bits of a result into the op_sel-selected
        /// destination half, preserving the other half.</summary>
        private static string Int16Result(
            Gen5ShaderInstruction instruction,
            uint destination,
            string value)
        {
            var half = $"(({value}) & 0xFFFFu)";
            return ((instruction.Control as Gen5Vop3Control)?.OperandSelect ?? 0) switch
            {
                var select when (select & 8) != 0 =>
                    $"((v[{destination}] & 0x0000FFFFu) | ({half} << 16))",
                _ => $"((v[{destination}] & 0xFFFF0000u) | {half})",
            };
        }

        // ---- DPP / SDWA machinery ----

        private static bool IsSupportedDppControl(uint control) =>
            control <= 0xFF ||
            control is >= 0x101 and <= 0x10F or
                >= 0x111 and <= 0x11F or
                >= 0x121 and <= 0x12F or
                0x140 or 0x141 or
                >= 0x150 and <= 0x15F or
                >= 0x160 and <= 0x16F;

        /// <summary>Target lane + in-range flag for a DPP16 control.</summary>
        private (string TargetLane, string InRange) EmitDppSourceLane(Gen5DppControl control)
        {
            var dpp = control.Control;
            if (dpp <= 0xFF)
            {
                // Quad permute: two selector bits per lane-in-quad.
                var selected = Temp(
                    "uint",
                    $"({dpp}u >> ((sharpemu_lane & 3u) * 2u)) & 3u");
                return (Temp("uint", $"(sharpemu_lane & 0xFFFFFFFCu) + {selected}"), "true");
            }

            if (dpp is >= 0x101 and <= 0x10F)
            {
                // row_shl
                var shifted = Temp("uint", $"(sharpemu_lane & 15u) + {dpp & 15}u");
                var inRange = Temp("bool", $"{shifted} < 16u");
                return (Temp("uint", $"(sharpemu_lane & 0xFFFFFFF0u) + ({shifted} & 15u)"), inRange);
            }

            if (dpp is >= 0x111 and <= 0x11F)
            {
                // row_shr
                var inRange = Temp("bool", $"(sharpemu_lane & 15u) >= {dpp & 15}u");
                return (
                    Temp("uint", $"(sharpemu_lane & 0xFFFFFFF0u) + (((sharpemu_lane & 15u) - {dpp & 15}u) & 15u)"),
                    inRange);
            }

            if (dpp is >= 0x121 and <= 0x12F)
            {
                // row_ror
                return (
                    Temp("uint", $"(sharpemu_lane & 0xFFFFFFF0u) + (((sharpemu_lane & 15u) - {dpp & 15}u) & 15u)"),
                    "true");
            }

            var target = dpp switch
            {
                0x140 => "(sharpemu_lane & 0xFFFFFFF0u) + (15u - (sharpemu_lane & 15u))",
                0x141 => "(sharpemu_lane & 0xFFFFFFF8u) + (7u - (sharpemu_lane & 7u))",
                >= 0x150 and <= 0x15F => $"(sharpemu_lane & 0xFFFFFFF0u) + {dpp & 15}u",
                >= 0x160 and <= 0x16F => $"(sharpemu_lane & 0xFFFFFFF0u) + ((sharpemu_lane & 15u) ^ {dpp & 15}u)",
                _ => "sharpemu_lane",
            };
            return (Temp("uint", target), "true");
        }

        // Under the single-lane graphics model every shuffle-select resolves
        // to the lane's own value (the register conceptually holds this
        // thread's value in every lane); compute lanes are real simdgroup
        // threads and shuffle for real. Mirrors the SPIR-V translator's
        // no-subgroup fallback for graphics stages.
        private bool IsSingleLaneStage => !HasRealWaveLanes;

        private string ShuffleLane(string value, string targetLane) =>
            IsSingleLaneStage ? value : $"simd_shuffle({value}, (ushort){targetLane})";

        private string LaneActiveExpression(string targetLane) =>
            IsSingleLaneStage ? "exec" : $"simd_shuffle(exec ? 1u : 0u, (ushort){targetLane}) != 0u";

        private string ApplyDppSource(Gen5DppControl control, string value)
        {
            var stored = Temp("uint", value);
            var (targetLane, inRange) = EmitDppSourceLane(control);
            var safeTarget = Temp("uint", $"(({inRange}) ? {targetLane} : sharpemu_lane) & 31u");
            var shuffled = Temp("uint", ShuffleLane(stored, safeTarget));
            if (control.FetchInactive)
            {
                return shuffled;
            }

            var sourceActive = Temp("bool", LaneActiveExpression(safeTarget));
            return Temp("uint", $"(({inRange}) && {sourceActive}) ? {shuffled} : 0u");
        }

        private string ApplyDpp8Source(Gen5Dpp8Control control, string value)
        {
            var stored = Temp("uint", value);
            var selector = Temp(
                "uint",
                $"({control.LaneSelectors}u >> ((sharpemu_lane & 7u) * 3u)) & 7u");
            var targetLane = Temp("uint", $"((sharpemu_lane & 0xFFFFFFF8u) + {selector}) & 31u");
            var shuffled = Temp("uint", ShuffleLane(stored, targetLane));
            if (control.FetchInactive)
            {
                return shuffled;
            }

            var sourceActive = Temp("bool", LaneActiveExpression(targetLane));
            return Temp("uint", $"{sourceActive} ? {shuffled} : 0u");
        }

        private string EmitDppWriteEnabled(Gen5DppControl control)
        {
            var (_, inRange) = EmitDppSourceLane(control);
            var rowEnabled = $"(({control.RowMask}u >> (sharpemu_lane >> 4)) & 1u) != 0u";
            var bankEnabled = $"(({control.BankMask}u >> (sharpemu_lane & 3u)) & 1u) != 0u";
            var sourceAllows = control.BoundControl ? "true" : inRange;
            return Temp("bool", $"({rowEnabled}) && ({bankEnabled}) && ({sourceAllows})");
        }

        private string ApplySdwaDestination(
            Gen5SdwaControl control,
            string value,
            string previous)
        {
            var (shift, width) = control.DestinationSelect switch
            {
                0 => (0u, 8u),
                1 => (8u, 8u),
                2 => (16u, 8u),
                3 => (24u, 8u),
                4 => (0u, 16u),
                5 => (16u, 16u),
                _ => (0u, 32u),
            };
            if (width == 32)
            {
                return value;
            }

            var lowMask = width == 8 ? 0xFFu : 0xFFFFu;
            var fieldMask = lowMask << (int)shift;
            var upperStart = shift + width;
            var upperMask = upperStart == 32 ? 0u : uint.MaxValue << (int)upperStart;
            var positioned = Temp("uint", $"(({value}) & 0x{lowMask:X}u) << {shift}");
            return control.DestinationUnused switch
            {
                // 0: unused bits zeroed. 1: sign-extend upward. 2: preserve.
                0 => positioned,
                1 => Temp(
                    "uint",
                    $"{positioned} | ((({positioned} & 0x{1u << (int)(shift + width - 1):X}u) != 0u) ? 0x{upperMask:X}u : 0u)"),
                2 => Temp("uint", $"(({previous}) & 0x{~fieldMask:X}u) | {positioned}"),
                _ => throw new InvalidOperationException("reserved SDWA destination-unused mode"),
            };
        }

        // ---- compares ----

        private bool TryEmitVectorCompare(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            var opcode = instruction.Opcode;
            string condition;
            if (opcode is "VCmpClassF32" or "VCmpxClassF32")
            {
                condition = EmitCompareClass(instruction);
            }
            else if (opcode is
                     "VCmpTruF32" or "VCmpxTruF32" or
                     "VCmpTruF16" or "VCmpxTruF16" or
                     "VCmpTI32" or "VCmpTU32" or
                     // The 64-bit F/T forms are constant compares like their 32-bit
                     // siblings; without them TrimCompare hands "F"/"T" to the
                     // integer operator table and the opcode reads as unsupported.
                     "VCmpTI64" or "VCmpxTI64" or "VCmpTU64" or "VCmpxTU64")
            {
                condition = "true";
            }
            else if (opcode is
                     "VCmpFF32" or "VCmpxFF32" or
                     "VCmpFF16" or "VCmpxFF16" or
                     "VCmpFI32" or "VCmpFU32" or
                     "VCmpFI64" or "VCmpxFI64" or "VCmpFU64" or "VCmpxFU64")
            {
                condition = "false";
            }
            else if (opcode is
                     "VCmpOF32" or "VCmpxOF32" or
                     "VCmpOF16" or "VCmpxOF16")
            {
                var left = opcode.EndsWith("F16", StringComparison.Ordinal)
                    ? F16(instruction, 0)
                    : F(instruction, 0);
                var right = opcode.EndsWith("F16", StringComparison.Ordinal)
                    ? F16(instruction, 1)
                    : F(instruction, 1);
                condition = $"(!isnan({left}) && !isnan({right}))";
            }
            else if (opcode is
                     "VCmpUF32" or "VCmpxUF32" or
                     "VCmpUF16" or "VCmpxUF16")
            {
                var left = opcode.EndsWith("F16", StringComparison.Ordinal)
                    ? F16(instruction, 0)
                    : F(instruction, 0);
                var right = opcode.EndsWith("F16", StringComparison.Ordinal)
                    ? F16(instruction, 1)
                    : F(instruction, 1);
                condition = $"(isnan({left}) || isnan({right}))";
            }
            else if (opcode.EndsWith("F32", StringComparison.Ordinal) ||
                     opcode.EndsWith("F16", StringComparison.Ordinal))
            {
                // Ordered compares are the plain C operators (false on NaN);
                // the Nxx forms are their unordered negations (true on NaN).
                var (op, unordered) = TrimCompare(opcode) switch
                {
                    "Lt" => ("<", false),
                    "Eq" => ("==", false),
                    "Le" => ("<=", false),
                    "Gt" => (">", false),
                    "Lg" => ("!=", false),
                    "Ge" => (">=", false),
                    "Neq" => ("==", true),
                    "Nlt" => ("<", true),
                    "Nle" => ("<=", true),
                    "Ngt" => (">", true),
                    "Nge" => (">=", true),
                    "Nlg" => ("!=", true),
                    _ => (string.Empty, false),
                };
                if (op.Length == 0)
                {
                    error = $"unsupported float compare {opcode}";
                    return false;
                }

                var left = opcode.EndsWith("F16", StringComparison.Ordinal)
                    ? F16(instruction, 0)
                    : F(instruction, 0);
                var right = opcode.EndsWith("F16", StringComparison.Ordinal)
                    ? F16(instruction, 1)
                    : F(instruction, 1);
                var comparison = $"({left} {op} {right})";
                condition = unordered ? $"(!{comparison})" : comparison;
            }
            else
            {
                var signed16 = opcode.EndsWith("I16", StringComparison.Ordinal);
                var unsigned16 = opcode.EndsWith("U16", StringComparison.Ordinal);
                var signed = signed16 ||
                    opcode.EndsWith("I32", StringComparison.Ordinal) ||
                    opcode.EndsWith("I64", StringComparison.Ordinal);
                var wide = opcode.EndsWith("I64", StringComparison.Ordinal) ||
                    opcode.EndsWith("U64", StringComparison.Ordinal);
                var op = TrimCompare(opcode) switch
                {
                    "Eq" => "==",
                    "Ne" => "!=",
                    "Lt" => "<",
                    "Le" => "<=",
                    "Gt" => ">",
                    "Ge" => ">=",
                    _ => string.Empty,
                };
                if (op.Length == 0)
                {
                    error = $"unsupported integer compare {opcode}";
                    return false;
                }

                var left = wide ? RawSource64(instruction, 0) : RawSource(instruction, 0);
                var right = wide ? RawSource64(instruction, 1) : RawSource(instruction, 1);
                condition = signed
                    ? signed16
                        ? $"(int(short(({left}) & 0xFFFFu)) {op} int(short(({right}) & 0xFFFFu)))"
                        : wide
                        ? $"(as_type<long>({left}) {op} as_type<long>({right}))"
                        : $"(as_type<int>({left}) {op} as_type<int>({right}))"
                    // A 16-bit compare reads only the low half; whatever the
                    // upper half carries must not change the result.
                    : unsigned16
                        ? $"((({left}) & 0xFFFFu) {op} (({right}) & 0xFFFFu))"
                        : $"(({left}) {op} ({right}))";
            }

            // Only EXEC-enabled lanes can pass; balloting the raw condition
            // would leak results from disabled lanes into saveexec/branches.
            var active = Temp("bool", ExecCondition(condition));
            if (instruction.Control is Gen5DppControl compareDpp)
            {
                var writeEnabled = EmitDppWriteEnabled(compareDpp);
                active = Temp("bool", $"({writeEnabled}) ? {active} : vcc");
            }

            if (opcode.StartsWith("VCmpx", StringComparison.Ordinal))
            {
                // GFX10 VCMPX writes EXEC only.
                Line($"exec = {active};");
                EmitBallotStore(ExecLoRegister, "exec");
            }
            else
            {
                var target = instruction.Control is Gen5SdwaControl
                    { ScalarDestination: { } scalarDestination }
                    ? scalarDestination
                    : VccLoRegister;
                StoreMaskBit(target, active);
            }

            return true;
        }

        private string EmitCompareClass(Gen5ShaderInstruction instruction)
        {
            var source = Temp("float", F(instruction, 0));
            var raw = Temp("uint", RawSource(instruction, 0));
            var mask = Temp("uint", RawSource(instruction, 1));
            var negative = Temp("bool", $"({raw} & 0x80000000u) != 0u");
            var nan = Temp("bool", $"isnan({source})");
            var infinite = Temp("bool", $"isinf({source})");
            var zero = Temp("bool", $"{source} == 0.0f");
            var subnormal = Temp(
                "bool",
                $"fabs({source}) > 0.0f && fabs({source}) < as_type<float>(0x00800000u)");
            var normal = Temp(
                "bool",
                $"!({nan} || {infinite} || {zero} || {subnormal})");
            // Class bits: 0 sNaN, 1 qNaN, 2 -inf, 3 -normal, 4 -subnormal,
            // 5 -zero, 6 +zero, 7 +subnormal, 8 +normal, 9 +inf.
            return Temp(
                "bool",
                $"((({mask} & 3u) != 0u) && {nan}) || " +
                $"((({mask} >> 2) & 1u) != 0u && {infinite} && {negative}) || " +
                $"((({mask} >> 3) & 1u) != 0u && {normal} && {negative}) || " +
                $"((({mask} >> 4) & 1u) != 0u && {subnormal} && {negative}) || " +
                $"((({mask} >> 5) & 1u) != 0u && {zero} && {negative}) || " +
                $"((({mask} >> 6) & 1u) != 0u && {zero} && !{negative}) || " +
                $"((({mask} >> 7) & 1u) != 0u && {subnormal} && !{negative}) || " +
                $"((({mask} >> 8) & 1u) != 0u && {normal} && !{negative}) || " +
                $"((({mask} >> 9) & 1u) != 0u && {infinite} && !{negative})");
        }

        private static string TrimCompare(string opcode)
        {
            var trimmed = opcode.StartsWith("VCmpx", StringComparison.Ordinal)
                ? opcode["VCmpx".Length..]
                : opcode["VCmp".Length..];
            return trimmed[..^3];
        }

        private void StoreCarryOut(Gen5ShaderInstruction instruction, string carryCondition)
        {
            var active = Temp("bool", ExecCondition($"({carryCondition})"));
            var target = instruction.Control is Gen5Vop3Control { ScalarDestination: { } register }
                ? register
                : VccLoRegister;
            StoreMaskBit(target, active);
        }

        /// <summary>
        /// Writes this lane's bit of a wave mask: VCC/EXEC update the per-lane
        /// bool and mirror the ballot into their architectural SGPRs; a plain
        /// SGPR receives the ballot of the per-lane condition.
        /// </summary>
        private void StoreMaskBit(uint register, string condition)
        {
            switch (register)
            {
                case VccLoRegister:
                    Line($"vcc = {condition};");
                    EmitBallotStore(VccLoRegister, "vcc");
                    return;
                case ExecLoRegister:
                    Line($"exec = {condition};");
                    EmitBallotStore(ExecLoRegister, "exec");
                    return;
                default:
                    if (register < ScalarRegisterFileCount)
                    {
                        EmitBallotStore(register, condition);
                    }

                    return;
            }
        }

        /// <summary>Broadcasts <paramref name="value"/> from the first guest-active
        /// lane (lowest set bit of the 64-lane EXEC mask) to all lanes, through the
        /// threadgroup broadcast slot — mirroring the SPIR-V translator's
        /// BroadcastFirstWave64Active. Returns the temp holding the result.</summary>
        private string EmitWave64ReadFirstLane(string value)
        {
            Line("if (sharpemu_lane == 0u) { sharpemu_wave_scratch[2] = 0u; }");
            // 64-lane EXEC mask across both halves (slots 0/1), broadcast in 2.
            Line("sharpemu_wave_scratch[(sharpemu_lane >> 5) & 1u] = sharpemu_ballot(exec);");
            Line("threadgroup_barrier(mem_flags::mem_threadgroup);");
            var lo = Temp("uint", "sharpemu_wave_scratch[0]");
            var hi = Temp("uint", "sharpemu_wave_scratch[1]");
            var first = Temp(
                "uint",
                $"({lo} != 0u) ? (uint)ctz({lo}) : (({hi} != 0u) ? (32u + (uint)ctz({hi})) : 0u)");
            var anyActive = Temp("bool", $"(({lo}) | ({hi})) != 0u");
            Line($"if ({anyActive} && sharpemu_lane == {first}) {{ sharpemu_wave_scratch[2] = {value}; }}");
            Line("threadgroup_barrier(mem_flags::mem_threadgroup);");
            var result = Temp("uint", "sharpemu_wave_scratch[2]");
            Line("threadgroup_barrier(mem_flags::mem_threadgroup);");
            return result;
        }

        /// <summary>Stores the wave ballot of <paramref name="condition"/> into the
        /// mask register pair (low, low+1). Wave32 fills the low dword and clears
        /// the high; wave64 bridges both 32-wide halves through threadgroup
        /// scratch so the pair holds the full 64-lane mask. The bridging barriers
        /// are safe because the guest program's scalar PC keeps all 64 lanes in
        /// lockstep through the dispatcher (one wave per threadgroup).</summary>
        private void EmitBallotStore(uint loRegister, string condition)
        {
            var hiRegister = loRegister + 1;
            if (!IsWave64)
            {
                Line($"s[{loRegister}] = sharpemu_ballot({condition});");
                if (hiRegister < ScalarRegisterFileCount)
                {
                    Line($"s[{hiRegister}] = 0u;");
                }

                return;
            }

            // simd_ballot is uniform across a simdgroup, so every lane of a half
            // writes the same 32-bit value to that half's slot — no first-lane
            // guard needed. Barrier, read both halves, barrier before the slot
            // can be reused by the next ballot.
            Line($"sharpemu_wave_scratch[(sharpemu_lane >> 5) & 1u] = sharpemu_ballot({condition});");
            Line("threadgroup_barrier(mem_flags::mem_threadgroup);");
            Line($"s[{loRegister}] = sharpemu_wave_scratch[0];");
            if (hiRegister < ScalarRegisterFileCount)
            {
                Line($"s[{hiRegister}] = sharpemu_wave_scratch[1];");
            }

            Line("threadgroup_barrier(mem_flags::mem_threadgroup);");
        }

        // ---- permlane / cube ----

        private string EmitPermlane16(Gen5ShaderInstruction instruction, bool exchangeRows)
        {
            if (instruction.Control is not Gen5Vop3Control control ||
                (control.OperandSelect & ~3u) != 0 ||
                control.AbsoluteMask != 0 ||
                control.NegateMask != 0 ||
                control.OutputModifier != 0 ||
                control.Clamp)
            {
                throw new NotSupportedException(
                    $"invalid permlane modifiers for {instruction.Opcode}");
            }

            var value = Temp("uint", RawSource(instruction, 0));
            var selectorLow = Temp("uint", RawSource(instruction, 1));
            var selectorHigh = Temp("uint", RawSource(instruction, 2));
            var localLane = Temp("uint", "sharpemu_lane & 15u");
            var selector = Temp(
                "uint",
                $"({localLane} < 8u ? ({selectorLow} >> ({localLane} << 2)) : ({selectorHigh} >> (({localLane} - 8u) << 2))) & 15u");
            var rowBase = exchangeRows
                ? "((sharpemu_lane & 0xFFFFFFF0u) ^ 16u)"
                : "(sharpemu_lane & 0xFFFFFFF0u)";
            var targetLane = Temp("uint", $"({rowBase} + {selector}) & 31u");
            var shuffled = Temp("uint", ShuffleLane(value, targetLane));
            var fetchInactive = (control.OperandSelect & 1) != 0;
            if (fetchInactive)
            {
                return shuffled;
            }

            var sourceActive = Temp("bool", LaneActiveExpression(targetLane));
            return Temp("uint", $"{sourceActive} ? {shuffled} : 0u");
        }

        private enum CubeCoordinate
        {
            Id,
            Sc,
            Tc,
            Ma,
        }

        private string EmitCubeCoordinate(
            Gen5ShaderInstruction instruction,
            CubeCoordinate coordinate)
        {
            var x = Temp("float", F(instruction, 0));
            var y = Temp("float", F(instruction, 1));
            var z = Temp("float", F(instruction, 2));
            var amaxXY = Temp("float", $"fmax(fabs({x}), fabs({y}))");
            var amax = Temp("float", $"fmax(fabs({z}), {amaxXY})");
            if (coordinate == CubeCoordinate.Ma)
            {
                return FloatResult(instruction, $"2.0f * {amax}");
            }

            var isZMax = Temp("bool", $"fabs({z}) >= {amaxXY}");
            var yGeX = Temp("bool", $"fabs({y}) >= fabs({x})");
            var isYMax = Temp("bool", $"!{isZMax} && {yGeX}");
            switch (coordinate)
            {
                case CubeCoordinate.Id:
                {
                    var zCase = $"({z} < 0.0f ? 5.0f : 4.0f)";
                    var yCase = $"({y} < 0.0f ? 3.0f : 2.0f)";
                    var xCase = $"({x} < 0.0f ? 1.0f : 0.0f)";
                    return FloatResult(
                        instruction,
                        $"({isZMax} ? {zCase} : ({yGeX} ? {yCase} : {xCase}))");
                }
                case CubeCoordinate.Sc:
                {
                    var zCase = $"({z} < 0.0f ? (-{x}) : {x})";
                    var xCase = $"({x} < 0.0f ? {z} : (-{z}))";
                    return FloatResult(
                        instruction,
                        $"({isZMax} ? {zCase} : ({isYMax} ? {x} : {xCase}))");
                }
                default:
                {
                    var yCase = $"({y} < 0.0f ? (-{z}) : {z})";
                    return FloatResult(
                        instruction,
                        $"({isYMax} ? {yCase} : (-{y}))");
                }
            }
        }

        // ---- scalar ALU ----

        private bool TryEmitScalarAlu(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Encoding == Gen5ShaderEncoding.Sopc)
            {
                return TryEmitScalarCompare(instruction, out error);
            }

            if (instruction.Destinations.Count == 0 ||
                instruction.Destinations[0].Kind != Gen5OperandKind.ScalarRegister)
            {
                error = "missing scalar destination";
                return false;
            }

            var destination = instruction.Destinations[0].Value;
            if (instruction.Encoding == Gen5ShaderEncoding.Sopk)
            {
                var immediate = unchecked((uint)(short)(instruction.Words[0] & 0xFFFF));
                if (instruction.Opcode.StartsWith("SCmpk", StringComparison.Ordinal))
                {
                    // The unsigned compares take SIMM16 zero-extended; only the signed ones sign-extend it.
                    if (instruction.Opcode.EndsWith("U32", StringComparison.Ordinal))
                    {
                        immediate = instruction.Words[0] & 0xFFFF;
                    }

                    return TryEmitScalarCompareK(instruction, destination, immediate, out error);
                }

                var current = ScalarExpression(destination);
                var value = instruction.Opcode switch
                {
                    "SMovkI32" => FormatUInt(immediate),
                    "SAddkI32" => $"({current} + {FormatUInt(immediate)})",
                    "SMulkI32" => $"({current} * {FormatUInt(immediate)})",
                    _ => string.Empty,
                };
                if (value.Length == 0)
                {
                    error = $"unsupported scalar immediate {instruction.Opcode}";
                    return false;
                }

                var stored = Temp("uint", value);
                // RDNA2 ISA: S_ADDK_I32 writes SCC = signed overflow, exactly like
                // S_ADD_I32. S_MOVK_I32 and S_MULK_I32 leave SCC alone.
                if (instruction.Opcode == "SAddkI32")
                {
                    var addend = FormatUInt(immediate);
                    Line(
                        $"scc = ((~({current} ^ {addend}) & ({current} ^ {stored})) >> 31) != 0u;");
                }

                StoreScalar(destination, stored);
                return true;
            }

            if (instruction.Opcode == "SGetpcB64")
            {
                {
                    // The shader base is pushed per draw; the program offset is added to it.
                    var (baseLow, baseHigh) = ShaderBaseWords();
                    var offset = unchecked(instruction.ProgramOffset + (ulong)(instruction.Words.Count * sizeof(uint)));
                    var address = Temp("ulong", $"((ulong){baseLow} | ((ulong){baseHigh} << 32)) + {offset}ul");
                    StoreScalar(destination, $"(uint){address}");
                    StoreScalar(destination + 1, $"(uint)({address} >> 32)");
                    return true;
                }
            }

            if (instruction.Opcode is "SQuadmaskB32" or "SQuadmaskB64")
            {
                var wide = instruction.Opcode == "SQuadmaskB64";
                var source = Temp(wide ? "ulong" : "uint", wide ? RawSource64(instruction, 0) : RawSource(instruction, 0));
                var result = Temp("uint", "0u");
                for (var quad = 0; quad < (wide ? 16 : 8); quad++)
                    Line($"{result} |= (({source} >> {quad * 4}) & 15{(wide ? "ul" : "u")}) != 0 ? {1u << quad}u : 0u;");
                StoreScalar(destination, result);
                if (wide) StoreScalar(destination + 1, "0u");
                Line($"scc = {result} != 0u;");
                return true;
            }

            if (instruction.Opcode == "SBcnt1I32B64")
            {
                var wide = Temp("ulong", RawSource64(instruction, 0));
                var bitCountResult = Temp("uint", $"(uint)popcount({wide})");
                StoreScalar(destination, bitCountResult);
                Line($"scc = {bitCountResult} != 0u;");
                return true;
            }

            if (instruction.Opcode == "SFF1I32B64")
            {
                var wide = Temp("ulong", RawSource64(instruction, 0));
                var result = Temp(
                    "uint",
                    $"{wide} == 0ul ? 0xFFFFFFFFu : (uint)ctz({wide})");
                StoreScalar(destination, result);
                return true;
            }

            if (instruction.Opcode == "SFlbitI32B64")
            {
                var wide = Temp("ulong", RawSource64(instruction, 0));
                var result = Temp(
                    "uint",
                    $"{wide} == 0ul ? 0xFFFFFFFFu : (uint)clz({wide})");
                StoreScalar(destination, result);
                return true;
            }

            if (instruction.Opcode.EndsWith("B64", StringComparison.Ordinal) ||
                instruction.Opcode == "SAshrI64" ||
                instruction.Opcode is "SBfeU64" or "SBfeI64")
            {
                return TryEmitScalar64(instruction, destination, out error);
            }

            var left = Temp("uint", RawSource(instruction, 0));
            if (instruction.Opcode.EndsWith("SaveexecB32", StringComparison.Ordinal))
            {
                var oldExec = Temp("uint", $"s[{ExecLoRegister}]");
                var operation = instruction.Opcode[1..instruction.Opcode.IndexOf(
                    "Saveexec",
                    StringComparison.Ordinal)];
                var combined = operation switch
                {
                    "And" => $"({left} & {oldExec})",
                    "Or" => $"({left} | {oldExec})",
                    "Xor" => $"({left} ^ {oldExec})",
                    "Nand" => $"~({left} & {oldExec})",
                    "Nor" => $"~({left} | {oldExec})",
                    "Xnor" => $"~({left} ^ {oldExec})",
                    "Andn1" => $"(~{left} & {oldExec})",
                    "Andn2" => $"({left} & ~{oldExec})",
                    "Orn1" => $"(~{left} | {oldExec})",
                    "Orn2" => $"({left} | ~{oldExec})",
                    _ => string.Empty,
                };
                if (combined.Length == 0)
                {
                    error = $"unsupported scalar 32-bit saveexec opcode {instruction.Opcode}";
                    return false;
                }

                var mask = Temp("uint", combined);
                StoreScalar(destination, oldExec);
                Line($"s[{ExecLoRegister}] = {mask};");
                Line($"s[{ExecHiRegister}] = 0u;");
                Line($"exec = (({mask} >> sharpemu_lane) & 1u) != 0u;");
                Line($"scc = {mask} != 0u;");
                return true;
            }

            switch (instruction.Opcode)
            {
                case "SMovB32":
                    StoreScalar(destination, left);
                    return true;
                case "SNotB32":
                {
                    var result = Temp("uint", $"~{left}");
                    StoreScalar(destination, result);
                    Line($"scc = {result} != 0u;");
                    return true;
                }
                case "SAbsI32":
                {
                    var sign = Temp("uint", $"0u - ({left} >> 31u)");
                    var result = Temp("uint", $"({left} ^ {sign}) - {sign}");
                    StoreScalar(destination, result);
                    Line($"scc = {result} != 0u;");
                    return true;
                }
                case "SWqmB32":
                {
                    var quadAny = Temp(
                        "uint",
                        $"({left} | ({left} >> 1) | ({left} >> 2) | ({left} >> 3)) & 0x11111111u");
                    var result = Temp("uint", $"{quadAny} * 0xFu");
                    StoreScalar(destination, result);
                    Line($"scc = {result} != 0u;");
                    return true;
                }
                case "SBrevB32":
                {
                    var result = Temp("uint", $"reverse_bits({left})");
                    StoreScalar(destination, result);
                    Line($"scc = {result} != 0u;");
                    return true;
                }
                case "SBcnt1I32B32":
                {
                    var result = Temp("uint", $"popcount({left})");
                    StoreScalar(destination, result);
                    Line($"scc = {result} != 0u;");
                    return true;
                }
                case "SFF1I32B32":
                {
                    var result = Temp(
                        "uint",
                        $"{left} == 0u ? 0xFFFFFFFFu : (uint)ctz({left})");
                    StoreScalar(destination, result);
                    return true;
                }
                case "SFlbitI32B32":
                {
                    // Leading zero count, 0xFFFFFFFF when the source is zero.
                    var result = Temp(
                        "uint",
                        $"{left} == 0u ? 0xFFFFFFFFu : (uint)clz({left})");
                    StoreScalar(destination, result);
                    return true;
                }
                case "SBitreplicateB64B32":
                    // S_BITREPLICATE_B64_B32 broadcasts the source dword into both
                    // halves of its 64-bit SGPR destination and does not write SCC.
                    StoreScalar(destination, left);
                    StoreScalar(destination + 1, left);
                    return true;
                case "SBitset0B32":
                    StoreScalar(
                        destination,
                        $"{ScalarExpression(destination)} & ~(1u << ({left} & 31u))");
                    return true;
                case "SBitset1B32":
                    StoreScalar(
                        destination,
                        $"{ScalarExpression(destination)} | (1u << ({left} & 31u))");
                    return true;
            }

            if (instruction.Sources.Count < 2)
            {
                error = $"missing scalar source for {instruction.Opcode}";
                return false;
            }

            var right = Temp("uint", RawSource(instruction, 1));
            string resultExpression;
            string sccStatement;
            switch (instruction.Opcode)
            {
                case "SAddU32":
                    resultExpression = $"({left} + {right})";
                    sccStatement = "RESULT < " + left;
                    break;
                case "SSubU32":
                    resultExpression = $"({left} - {right})";
                    sccStatement = $"{right} > {left}";
                    break;
                case "SAddI32":
                    resultExpression = $"({left} + {right})";
                    sccStatement = $"((~({left} ^ {right}) & ({left} ^ RESULT)) >> 31) != 0u";
                    break;
                case "SSubI32":
                    resultExpression = $"({left} - {right})";
                    sccStatement = $"(((({left} ^ {right})) & ({left} ^ RESULT)) >> 31) != 0u";
                    break;
                case "SAddF32":
                    resultExpression = $"as_type<uint>(as_type<float>({left}) + as_type<float>({right}))";
                    sccStatement = string.Empty;
                    break;
                case "SSubF32":
                    resultExpression = $"as_type<uint>(as_type<float>({left}) - as_type<float>({right}))";
                    sccStatement = string.Empty;
                    break;
                case "SAddcU32":
                {
                    var partial = Temp("uint", $"{left} + {right}");
                    var sum = Temp("uint", $"{partial} + (scc ? 1u : 0u)");
                    Line($"scc = ({partial} < {left}) || ({sum} < {partial});");
                    StoreScalar(destination, sum);
                    return true;
                }
                case "SSubbU32":
                {
                    var borrow = Temp("uint", "scc ? 1u : 0u");
                    var partial = Temp("uint", $"{left} - {right}");
                    var difference = Temp("uint", $"{partial} - {borrow}");
                    Line($"scc = ({right} > {left}) || (({borrow} == 1u) && ({right} == {left}));");
                    StoreScalar(destination, difference);
                    return true;
                }
                case "SMulI32":
                    resultExpression = $"({left} * {right})";
                    sccStatement = string.Empty;
                    break;
                case "SMulHiU32":
                    resultExpression = $"mulhi({left}, {right})";
                    sccStatement = string.Empty;
                    break;
                case "SMulHiI32":
                    resultExpression = $"as_type<uint>(mulhi(as_type<int>({left}), as_type<int>({right})))";
                    sccStatement = string.Empty;
                    break;
                case "SAndB32":
                    resultExpression = $"({left} & {right})";
                    sccStatement = "NONZERO";
                    break;
                case "SOrB32":
                    resultExpression = $"({left} | {right})";
                    sccStatement = "NONZERO";
                    break;
                case "SXorB32":
                    resultExpression = $"({left} ^ {right})";
                    sccStatement = "NONZERO";
                    break;
                case "SNandB32":
                    resultExpression = $"~({left} & {right})";
                    sccStatement = "NONZERO";
                    break;
                case "SNorB32":
                    resultExpression = $"~({left} | {right})";
                    sccStatement = "NONZERO";
                    break;
                case "SXnorB32":
                    resultExpression = $"~({left} ^ {right})";
                    sccStatement = "NONZERO";
                    break;
                case "SAndn2B32":
                    resultExpression = $"({left} & ~{right})";
                    sccStatement = "NONZERO";
                    break;
                case "SOrn2B32":
                    resultExpression = $"({left} | ~{right})";
                    sccStatement = "NONZERO";
                    break;
                case "SLshlB32":
                    resultExpression = $"({left} << ({right} & 31u))";
                    sccStatement = "NONZERO";
                    break;
                case "SLshrB32":
                    resultExpression = $"({left} >> ({right} & 31u))";
                    sccStatement = "NONZERO";
                    break;
                case "SAshrI32":
                    resultExpression = $"(uint)(as_type<int>({left}) >> ({right} & 31u))";
                    sccStatement = "NONZERO";
                    break;
                case "SBfmB32":
                    resultExpression = $"(((1u << ({left} & 31u)) - 1u) << ({right} & 31u))";
                    sccStatement = string.Empty;
                    break;
                case "SBfeU32":
                case "SBfeI32":
                {
                    // Width clamps to the bits remaining above the offset.
                    var offset = Temp("uint", $"{right} & 31u");
                    var width = Temp(
                        "uint",
                        $"min(({right} >> 16) & 0x7Fu, 32u - {offset})");
                    var result = instruction.Opcode == "SBfeI32"
                        ? Temp(
                            "uint",
                            $"{width} == 0u ? 0u : (uint)extract_bits(as_type<int>({left}), {offset}, {width})")
                        : Temp(
                            "uint",
                            $"{width} == 0u ? 0u : extract_bits({left}, {offset}, {width})");
                    StoreScalar(destination, result);
                    Line($"scc = {result} != 0u;");
                    return true;
                }
                case "SCselectB32":
                    resultExpression = $"(scc ? {left} : {right})";
                    sccStatement = string.Empty;
                    break;
                case "SMinU32":
                    resultExpression = $"min({left}, {right})";
                    sccStatement = $"{left} < {right}";
                    break;
                case "SMaxU32":
                    resultExpression = $"max({left}, {right})";
                    sccStatement = $"{left} > {right}";
                    break;
                case "SMinI32":
                    resultExpression = $"(uint)min(as_type<int>({left}), as_type<int>({right}))";
                    sccStatement = $"as_type<int>({left}) < as_type<int>({right})";
                    break;
                case "SMaxI32":
                    resultExpression = $"(uint)max(as_type<int>({left}), as_type<int>({right}))";
                    sccStatement = $"as_type<int>({left}) > as_type<int>({right})";
                    break;
                case "SLshl1AddU32":
                case "SLshl2AddU32":
                case "SLshl3AddU32":
                case "SLshl4AddU32":
                {
                    var shift = (uint)(instruction.Opcode[5] - '0');
                    resultExpression = $"(({left} << {shift}) + {right})";
                    sccStatement = string.Empty;
                    break;
                }
                case "SPackLlB32B16":
                    resultExpression = $"(({left} & 0xFFFFu) | ({right} << 16))";
                    sccStatement = string.Empty;
                    break;
                case "SPackLhB32B16":
                    resultExpression = $"(({left} & 0xFFFFu) | ({right} & 0xFFFF0000u))";
                    sccStatement = string.Empty;
                    break;
                case "SPackHhB32B16":
                    resultExpression = $"(({left} >> 16) | ({right} & 0xFFFF0000u))";
                    sccStatement = string.Empty;
                    break;
                default:
                    error = $"unsupported scalar opcode {instruction.Opcode}";
                    return false;
            }

            var value2 = Temp("uint", resultExpression);
            StoreScalar(destination, value2);
            if (sccStatement == "NONZERO")
            {
                Line($"scc = {value2} != 0u;");
            }
            else if (sccStatement.Length != 0)
            {
                Line($"scc = {sccStatement.Replace("RESULT", value2)};");
            }

            return true;
        }

        private bool TryEmitScalarCompare(
            Gen5ShaderInstruction instruction,
            out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count < 2)
            {
                error = "missing scalar compare source";
                return false;
            }

            if (instruction.Opcode is "SCmpEqU64" or "SCmpLgU64")
            {
                var wideLeft = Temp("ulong", RawSource64(instruction, 0));
                var wideRight = Temp("ulong", RawSource64(instruction, 1));
                var comparison = instruction.Opcode == "SCmpEqU64" ? "==" : "!=";
                Line($"scc = {wideLeft} {comparison} {wideRight};");
                return true;
            }

            var left = Temp("uint", RawSource(instruction, 0));
            var right = Temp("uint", RawSource(instruction, 1));
            if (instruction.Opcode is "SBitcmp0B32" or "SBitcmp1B32")
            {
                var isSet = $"(({left} >> ({right} & 31u)) & 1u) != 0u";
                Line(instruction.Opcode == "SBitcmp1B32"
                    ? $"scc = {isSet};"
                    : $"scc = !({isSet});");
                return true;
            }

            return TryEmitScalarCompareCore(instruction.Opcode, "SCmp", left, right, out error);
        }

        private bool TryEmitScalarCompareK(
            Gen5ShaderInstruction instruction,
            uint destination,
            uint immediate,
            out string error) =>
            TryEmitScalarCompareCore(
                instruction.Opcode,
                "SCmpk",
                ScalarExpression(destination),
                FormatUInt(immediate),
                out error);

        private bool TryEmitScalarCompareCore(
            string opcode,
            string prefix,
            string left,
            string right,
            out string error)
        {
            error = string.Empty;
            var suffix = opcode[prefix.Length..];
            var signed = suffix.EndsWith("I32", StringComparison.Ordinal);
            var op = suffix[..^3] switch
            {
                "Eq" => "==",
                "Lg" => "!=",
                "Gt" => ">",
                "Ge" => ">=",
                "Lt" => "<",
                "Le" => "<=",
                _ => string.Empty,
            };
            if (op.Length == 0)
            {
                error = $"unsupported scalar compare {opcode}";
                return false;
            }

            Line(signed
                ? $"scc = as_type<int>({left}) {op} as_type<int>({right});"
                : $"scc = ({left}) {op} ({right});");
            return true;
        }

        // ---- 64-bit scalar ops over register pairs ----

        private bool TryEmitScalar64(
            Gen5ShaderInstruction instruction,
            uint destination,
            out string error)
        {
            error = string.Empty;

            // S_SETPC_B64 and S_SWAPPC_B64 are dynamic call/return operations.
            // The Gen5 translator has already linearized the reachable shader
            // body, so there is no host program counter to exchange here. Their
            // single pair operand must not be treated as a binary 64-bit op.
            if (instruction.Opcode is "SSetpcB64" or "SSwappcB64")
            {
                return true;
            }

            // S_BITSET0/1_B64 take a 32-bit bit index and update the 64-bit value
            // already held at the destination: they have no 64-bit source operand
            // and neither form writes SCC.
            if (instruction.Opcode is "SBitset0B64" or "SBitset1B64")
            {
                var bit = Temp("ulong", $"1ul << (ulong)({RawSource(instruction, 0)} & 63u)");
                StoreScalar64(
                    destination,
                    instruction.Opcode == "SBitset1B64"
                        ? $"{Scalar64Expression(destination)} | {bit}"
                        : $"{Scalar64Expression(destination)} & ~{bit}");
                return true;
            }

            var left = Temp("ulong", RawSource64(instruction, 0));
            if (instruction.Opcode.EndsWith("SaveexecB64", StringComparison.Ordinal))
            {
                var oldExec = Temp("ulong", Scalar64Expression(ExecLoRegister));
                var operation = instruction.Opcode[1..instruction.Opcode.IndexOf(
                    "Saveexec",
                    StringComparison.Ordinal)];
                var combined = operation switch
                {
                    "And" => $"({left} & {oldExec})",
                    "Or" => $"({left} | {oldExec})",
                    "Xor" => $"({left} ^ {oldExec})",
                    "Nand" => $"~({left} & {oldExec})",
                    "Nor" => $"~({left} | {oldExec})",
                    "Xnor" => $"~({left} ^ {oldExec})",
                    "Andn1" => $"(~{left} & {oldExec})",
                    "Andn2" => $"({left} & ~{oldExec})",
                    "Orn1" => $"(~{left} | {oldExec})",
                    "Orn2" => $"({left} | ~{oldExec})",
                    _ => string.Empty,
                };
                if (combined.Length == 0)
                {
                    error = $"unsupported scalar 64-bit saveexec opcode {instruction.Opcode}";
                    return false;
                }

                var mask = Temp("ulong", combined);
                StoreScalar64(destination, oldExec);
                Line($"s[{ExecLoRegister}] = (uint){mask};");
                Line($"s[{ExecHiRegister}] = (uint)({mask} >> 32);");
                Line($"exec = ((((uint){mask}) >> sharpemu_lane) & 1u) != 0u;");
                Line($"scc = {mask} != 0ul;");
                return true;
            }

            string value;
            var setsScc = true;
            switch (instruction.Opcode)
            {
                case "SMovB64":
                    value = left;
                    setsScc = false;
                    break;
                case "SNotB64":
                    value = $"~{left}";
                    break;
                case "SWqmB64":
                {
                    // Whole-quad mode: each 4-lane group becomes all-ones if any
                    // of its bits is set.
                    var quadAny = Temp(
                        "ulong",
                        $"({left} | ({left} >> 1) | ({left} >> 2) | ({left} >> 3)) & 0x1111111111111111ul");
                    value = $"({quadAny} * 0xFul)";
                    break;
                }
                case "SLshlB64" or "SLshrB64" or "SAshrI64":
                {
                    var shift = Temp("uint", $"({RawSource(instruction, 1)}) & 63u");
                    value = instruction.Opcode switch
                    {
                        "SLshlB64" => $"({left} << {shift})",
                        "SLshrB64" => $"({left} >> {shift})",
                        _ => $"(ulong)(as_type<long>({left}) >> {shift})",
                    };
                    break;
                }
                case "SBfmB64":
                {
                    var width = Temp("ulong", $"(ulong)(({RawSource(instruction, 0)}) & 63u)");
                    var offset = Temp("ulong", $"(ulong)(({RawSource(instruction, 1)}) & 63u)");
                    value = $"((((1ul << {width}) - 1ul)) << {offset})";
                    break;
                }
                case "SBfeU64" or "SBfeI64":
                {
                    var control = Temp("uint", RawSource(instruction, 1));
                    var offset = Temp("uint", $"{control} & 63u");
                    var width = Temp("uint", $"min(({control} >> 16) & 0x7Fu, 64u - {offset})");
                    var mask = Temp(
                        "ulong",
                        $"{width} >= 64u ? 0xFFFFFFFFFFFFFFFFul : ((1ul << {width}) - 1ul)");
                    var extracted = Temp("ulong", $"({left} >> {offset}) & {mask}");
                    if (instruction.Opcode == "SBfeI64")
                    {
                        var signBit = Temp(
                            "ulong",
                            $"{width} == 0u ? 0ul : (1ul << ({width} - 1u))");
                        extracted = Temp(
                            "ulong",
                            $"{width} == 0u ? 0ul : (({extracted} ^ {signBit}) - {signBit})");
                    }

                    value = extracted;
                    break;
                }
                default:
                {
                    if (instruction.Sources.Count < 2)
                    {
                        error = "missing scalar 64-bit source";
                        return false;
                    }

                    var right = Temp("ulong", RawSource64(instruction, 1));
                    value = instruction.Opcode switch
                    {
                        "SAndB64" => $"({left} & {right})",
                        "SOrB64" => $"({left} | {right})",
                        "SXorB64" => $"({left} ^ {right})",
                        "SNandB64" => $"~({left} & {right})",
                        "SNorB64" => $"~({left} | {right})",
                        "SXnorB64" => $"~({left} ^ {right})",
                        "SAndn1B64" => $"(~{left} & {right})",
                        "SAndn2B64" => $"({left} & ~{right})",
                        "SOrn1B64" => $"(~{left} | {right})",
                        "SOrn2B64" => $"({left} | ~{right})",
                        "SCselectB64" => $"(scc ? {left} : {right})",
                        _ => string.Empty,
                    };
                    if (value.Length == 0)
                    {
                        error = $"unsupported scalar 64-bit opcode {instruction.Opcode}";
                        return false;
                    }

                    setsScc = instruction.Opcode != "SCselectB64";
                    break;
                }
            }

            var stored = Temp("ulong", value);
            StoreScalar64(destination, stored);
            if (setsScc)
            {
                Line($"scc = {stored} != 0ul;");
            }

            return true;
        }

        // ---- operand helpers ----

        private uint DestinationVector(Gen5ShaderInstruction instruction)
        {
            var destination = instruction.Destinations[0];
            return destination.Kind == Gen5OperandKind.VectorRegister
                ? destination.Value
                : throw new NotSupportedException(
                    $"vector destination expected in {instruction.Opcode}");
        }

        /// <summary>
        /// Raw 32-bit source with DPP/DPP8 lane remapping on src0 and SDWA
        /// byte/word selection + integer modifiers, mirroring GetRawSource.
        /// </summary>
        private string RawSource(
            Gen5ShaderInstruction instruction,
            int sourceIndex,
            bool applySdwaIntegerModifiers = true)
        {
            var value = SourceExpression(instruction.Sources[sourceIndex], instruction);
            if (sourceIndex == 0 && instruction.Control is Gen5DppControl dpp)
            {
                value = ApplyDppSource(dpp, value);
            }
            else if (sourceIndex == 0 && instruction.Control is Gen5Dpp8Control dpp8)
            {
                value = ApplyDpp8Source(dpp8, value);
            }

            if (instruction.Control is Gen5SdwaControl sdwa)
            {
                var selector = sourceIndex switch
                {
                    0 => sdwa.Source0Select,
                    1 => sdwa.Source1Select,
                    _ => 6u,
                };
                value = selector switch
                {
                    0 => $"(({value}) & 0xFFu)",
                    1 => $"((({value}) >> 8) & 0xFFu)",
                    2 => $"((({value}) >> 16) & 0xFFu)",
                    3 => $"((({value}) >> 24) & 0xFFu)",
                    4 => $"(({value}) & 0xFFFFu)",
                    5 => $"((({value}) >> 16) & 0xFFFFu)",
                    _ => value,
                };
                var signExtend = sourceIndex switch
                {
                    0 => sdwa.Source0SignExtend,
                    1 => sdwa.Source1SignExtend,
                    _ => false,
                };
                if (signExtend && selector != 6)
                {
                    var width = selector <= 3 ? 8u : 16u;
                    value = $"(uint)extract_bits(as_type<int>({value}), 0u, {width}u)";
                }

                if (applySdwaIntegerModifiers)
                {
                    // SDWA ABS/NEG are floating-point sign-bit modifiers even on
                    // a bit-move opcode: ABS clears the sign bit, NEG flips it.
                    // Two's-complement negating the raw bits instead turns 1.0
                    // into -4.0 and -3.0 into 1.5, which silently skews every
                    // pass that y-flips its clip position with an SDWA-negated
                    // V_MOV_B32 - the whole of UE's DrawRectangle.
                    var signBit = selector switch
                    {
                        <= 3 => 0x80u,
                        4 or 5 => 0x8000u,
                        _ => 0x80000000u,
                    };

                    if ((sdwa.AbsoluteMask & (1u << sourceIndex)) != 0)
                    {
                        value = $"(({value}) & {FormatUInt(~signBit)})";
                    }

                    if ((sdwa.NegateMask & (1u << sourceIndex)) != 0)
                    {
                        value = $"(({value}) ^ {FormatUInt(signBit)})";
                    }
                }
            }

            return value;
        }

        private string SignModifiedSource(Gen5ShaderInstruction instruction, int sourceIndex)
        {
            var value = RawSource(instruction, sourceIndex);
            if (instruction.Control is not Gen5Vop3Control control)
            {
                return value;
            }

            if ((control.AbsoluteMask & (1u << sourceIndex)) != 0)
            {
                value = $"(({value}) & 0x7FFFFFFFu)";
            }

            return (control.NegateMask & (1u << sourceIndex)) != 0
                ? $"(({value}) ^ 0x80000000u)"
                : value;
        }

        /// <summary>64-bit source: SGPR/VGPR pair, sign-extended inline, or zero-extended 32-bit.</summary>
        private string RawSource64(Gen5ShaderInstruction instruction, int sourceIndex)
        {
            var operand = instruction.Sources[sourceIndex];
            switch (operand.Kind)
            {
                case Gen5OperandKind.ScalarRegister:
                    return Scalar64Expression(operand.Value);
                case Gen5OperandKind.VectorRegister:
                    return $"((ulong)v[{operand.Value}] | ((ulong)v[{operand.Value + 1}] << 32))";
                case Gen5OperandKind.EncodedConstant when operand.Value is >= 193 and <= 208:
                {
                    // Inline negatives sign-extend: -1 denotes a full 64-bit mask.
                    var signed = -(long)(operand.Value - 192);
                    return $"0x{unchecked((ulong)signed):X}ul";
                }
                default:
                    return $"(ulong)({RawSource(instruction, sourceIndex)})";
            }
        }

        private string Scalar64Expression(uint register) => register switch
        {
            // VCC/EXEC read their architectural SGPR pairs like any other
            // register — programs park plain data there (see StoreScalar).
            _ when register + 1 < ScalarRegisterFileCount =>
                $"((ulong)s[{register}] | ((ulong)s[{register + 1}] << 32))",
            _ => "0ul",
        };

        private void StoreScalar64(uint register, string ulongValue)
        {
            StoreScalar(register, $"(uint)({ulongValue})");
            StoreScalar(register + 1, $"(uint)(({ulongValue}) >> 32)");
        }

        /// <summary>Float view of a source with abs/neg modifiers from VOP3/SDWA/DPP.</summary>
        private string F(Gen5ShaderInstruction instruction, int sourceIndex)
        {
            var expression = AsFloat(
                RawSource(instruction, sourceIndex, applySdwaIntegerModifiers: false));
            var (absoluteMask, negateMask) = instruction.Control switch
            {
                Gen5Vop3Control control => (control.AbsoluteMask, control.NegateMask),
                Gen5SdwaControl control => (control.AbsoluteMask, control.NegateMask),
                Gen5DppControl control => (control.AbsoluteMask, control.NegateMask),
                _ => (0u, 0u),
            };
            if ((absoluteMask & (1u << sourceIndex)) != 0)
            {
                expression = $"fabs({expression})";
            }

            if ((negateMask & (1u << sourceIndex)) != 0)
            {
                expression = $"(-{expression})";
            }

            return expression;
        }

        /// <summary>Reads the selected 16-bit half as a widened float.</summary>
        private string F16(Gen5ShaderInstruction instruction, int sourceIndex)
        {
            var operand = instruction.Sources[sourceIndex];
            string expression;
            if (operand.Kind == Gen5OperandKind.EncodedConstant &&
                Gen5InlineConstants.TryDecode(operand.Value, out var inline))
            {
                expression = operand.Value switch
                {
                    >= 128 and <= 192 => $"{operand.Value - 128}.0f",
                    >= 193 and <= 208 => $"(-{operand.Value - 192}.0f)",
                    _ => AsFloat(FormatUInt(inline)),
                };
            }
            else
            {
                var raw = RawSource(
                    instruction,
                    sourceIndex,
                    applySdwaIntegerModifiers: false);
                var shift = instruction.Control is Gen5Vop3Control control &&
                    (control.OperandSelect & (1u << sourceIndex)) != 0
                        ? 16
                        : 0;
                expression =
                    $"(float)as_type<half>((ushort)((({raw}) >> {shift}) & 0xFFFFu))";
            }

            var (absoluteMask, negateMask) = instruction.Control switch
            {
                Gen5Vop3Control control => (control.AbsoluteMask, control.NegateMask),
                Gen5SdwaControl control => (control.AbsoluteMask, control.NegateMask),
                Gen5DppControl control => (control.AbsoluteMask, control.NegateMask),
                _ => (0u, 0u),
            };
            if ((absoluteMask & (1u << sourceIndex)) != 0)
            {
                expression = $"fabs({expression})";
            }

            if ((negateMask & (1u << sourceIndex)) != 0)
            {
                expression = $"(-{expression})";
            }

            return expression;
        }

        /// <summary>Rounds to f16 and preserves the unselected VGPR half.</summary>
        private string Float16Result(
            Gen5ShaderInstruction instruction,
            uint destination,
            string expression)
        {
            var control = instruction.Control as Gen5Vop3Control;
            expression = (control?.OutputModifier ?? 0) switch
            {
                1 => $"(({expression}) * 2.0f)",
                2 => $"(({expression}) * 4.0f)",
                3 => $"(({expression}) * 0.5f)",
                _ => expression,
            };
            if (control?.Clamp == true)
            {
                expression = $"clamp({expression}, 0.0f, 1.0f)";
            }

            var packed = $"(uint)as_type<ushort>(half({expression}))";
            return ((control?.OperandSelect ?? 0) & 8) != 0
                ? $"((v[{destination}] & 0x0000FFFFu) | (({packed}) << 16))"
                : $"((v[{destination}] & 0xFFFF0000u) | ({packed}))";
        }

        /// <summary>
        /// Wraps a float expression with VOP3/SDWA output modifiers and clamp,
        /// then bitcasts back to the register file's uint domain.
        /// </summary>
        private string FloatResult(Gen5ShaderInstruction instruction, string expression)
        {
            var (outputModifier, clamp) = instruction.Control switch
            {
                Gen5Vop3Control control => (control.OutputModifier, control.Clamp),
                Gen5SdwaControl control => (control.OutputModifier, control.Clamp),
                _ => (0u, false),
            };
            expression = outputModifier switch
            {
                1 => $"(({expression}) * 2.0f)",
                2 => $"(({expression}) * 4.0f)",
                3 => $"(({expression}) * 0.5f)",
                _ => expression,
            };
            if (clamp)
            {
                expression = $"clamp({expression}, 0.0f, 1.0f)";
            }

            return AsUInt($"({expression})");
        }

        /// <summary>The lane's bit of a mask operand (VCC/EXEC/SGPR mask).</summary>
        private string MaskBitExpression(Gen5Operand operand) => operand switch
        {
            { Kind: Gen5OperandKind.ScalarRegister, Value: VccLoRegister } => "vcc",
            { Kind: Gen5OperandKind.ScalarRegister, Value: ExecLoRegister } => "exec",
            { Kind: Gen5OperandKind.ScalarRegister } scalar =>
                $"(((s[{scalar.Value}] >> sharpemu_lane) & 1u) != 0u)",
            _ => throw new NotSupportedException("mask operand must be a scalar register"),
        };
    }
}
