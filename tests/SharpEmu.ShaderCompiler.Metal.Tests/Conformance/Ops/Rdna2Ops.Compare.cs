// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using static SharpEmu.ShaderCompiler.Metal.Tests.Conformance.Semantics;

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

/// <summary>
/// Vector compares (ISA 12.9): every V_CMP_* and V_CMPX_* opcode gfx1030 has, written from
/// Tables 57-61 and the V_CMP_CLASS descriptions, in the VOPC (VCC), VOP3 (SGPR mask; EXEC only
/// for V_CMPX on gfx10) and SDWAB (VCC or SGPR) encodings at wave32 and wave64 under a random
/// EXEC. The mask semantics (VCC[n] = EXEC[n] &amp; test, the whole mask written, a wave32 mask
/// one SGPR) are the wave model's (ISA 3.9). VOPC has no DPP form on gfx1030 (ISA 6.9: DPP is
/// VOP1/VOP2 only; llvm-mc rejects it), no op_sel (ISA 6.2.5), and SDWA only for 16/32-bit
/// operands.
/// </summary>
internal static partial class Rdna2Ops
{
    // Table 57, opcode offsets 0..15.
    private static readonly string[] FloatCompareNames =
        ["f", "lt", "eq", "le", "gt", "lg", "ge", "o", "u", "nge", "nlg", "ngt", "nle", "neq", "nlt", "tru"];

    // Table 59, opcode offsets 0..7.
    private static readonly string[] IntCompareNames = ["f", "lt", "eq", "le", "gt", "ne", "ge", "t"];

    /// <summary>Class masks (S1.u[9:0], ISA 12.9 V_CMP_CLASS_F32): every single class, the
    /// combinations shaders use (NaN, infinities, zeros, denormals, normals, finite), all, none,
    /// and bits above 9 that select nothing.</summary>
    private static readonly ulong[] ClassMasks =
    [
        0x001, 0x002, 0x004, 0x008, 0x010, 0x020, 0x040, 0x080, 0x100, 0x200,
        0x003, 0x204, 0x060, 0x090, 0x108, 0x1F8, 0x3FC, 0x3FF, 0x000, 0x0F0, 0x00F, 0x2AA, 0x155,
        0x400, 0xFFFFFC00, 0xFFFFFFFF, 0x80000202,
    ];

    [OpCategory("compare")]
    private static IEnumerable<object> Compares()
    {
        foreach (var exec in (bool[])[false, true])
        {
            foreach (var type in (OpType[])[OpType.F16, OpType.F32, OpType.F64])
            {
                for (var index = 0; index < FloatCompareNames.Length; index++)
                {
                    foreach (var item in CompareCases(FloatCompare(type, index, exec))) yield return item;
                }

                foreach (var item in CompareCases(ClassCompare(type, exec))) yield return item;
            }

            foreach (var type in (OpType[])[OpType.I16, OpType.U16, OpType.I32, OpType.U32, OpType.I64, OpType.U64])
            {
                // gfx10 has no V_CMP_F/T for 16-bit integers (Table 61: opcodes 0x89-0x8E, 0xA9-0xAE).
                var first = type.Width() == 16 ? 1 : 0;
                var last = type.Width() == 16 ? 6 : 7;
                for (var index = first; index <= last; index++)
                {
                    foreach (var item in CompareCases(IntCompare(type, index, exec))) yield return item;
                }
            }
        }
    }

    private static string TypeSuffix(OpType type) => type.ToString().ToLowerInvariant();

    private static VectorOp CompareOp(string name, OpType type, OpType[] sources, LaneFunction function, bool exec,
        string citation, Func<int, IReadOnlyList<ulong>>? pool = null) =>
        new($"{(exec ? "v_cmpx" : "v_cmp")}_{name}_{TypeSuffix(type)}", VectorEncoding.Vopc, type, sources, function,
            exec ? VectorFlags.CompareExec : VectorFlags.Compare)
        {
            Citation = citation,
            Pool = pool,
        };

    /// <summary>Table 57. LT..GE are false when either operand is a NaN; O is "neither is NaN"
    /// and U "either is NaN"; the N* forms are the negations. The table prints U as
    /// "!isNaN(S0) || !isNaN(S1)", which would make U true for ordinary numbers and not the
    /// complement of O; U is taken as the unordered test (6.3: "u = unordered", LLVM's setuo).
    /// Denormal inputs are flushed to zero per FP_DENORM (6.4).</summary>
    private static VectorOp FloatCompare(OpType type, int index, bool exec)
    {
        var format = type.Format();
        var name = FloatCompareNames[index];
        LaneFunction function = (in LaneInputs lane) =>
        {
            var a = lane.Mode.FlushIn(lane.S0 & format.ValueMask, format);
            var b = lane.Mode.FlushIn(lane.S1 & format.ValueMask, format);
            int? order = SoftFloat.IsNaN(a, format) || SoftFloat.IsNaN(b, format) ? null : SoftFloat.Compare(a, b, format);
            return LaneResult.Bool(FloatCondition(index, order));
        };
        return CompareOp(name, type, [type, type], function, exec,
            $"ISA 12.9 Table 57/58 V_{(exec ? "CMPX" : "CMP")}_{name.ToUpperInvariant()}_{type}, 3.9, 6.4");
    }

    private static bool FloatCondition(int index, int? order) => index switch
    {
        0 => false,
        1 => order is < 0,
        2 => order is 0,
        3 => order is <= 0,
        4 => order is > 0,
        5 => order is < 0 or > 0,
        6 => order is >= 0,
        7 => order is not null,
        8 => order is null,
        9 => order is not >= 0,
        10 => order is not (< 0 or > 0),
        11 => order is not > 0,
        12 => order is not <= 0,
        13 => order is not 0,
        14 => order is not < 0,
        _ => true,
    };

    /// <summary>Table 59: signed or unsigned compare of the type's width.</summary>
    private static VectorOp IntCompare(OpType type, int index, bool exec)
    {
        var width = type.Width();
        var signed = type.IsSigned();
        var name = IntCompareNames[index];
        LaneFunction function = (in LaneInputs lane) =>
        {
            var a = lane.S0 & type.Mask();
            var b = lane.S1 & type.Mask();
            var order = signed ? Signed(a, width).CompareTo(Signed(b, width)) : a.CompareTo(b);
            return LaneResult.Bool(index switch
            {
                0 => false,
                1 => order < 0,
                2 => order == 0,
                3 => order <= 0,
                4 => order > 0,
                5 => order != 0,
                6 => order >= 0,
                _ => true,
            });
        };
        return CompareOp(name, type, [type, type], function, exec,
            $"ISA 12.9 Table 59/60 V_{(exec ? "CMPX" : "CMP")}_{name.ToUpperInvariant()}_{type}, 3.9");
    }

    /// <summary>V_CMP_CLASS_{F16,F32,F64}: true when S0 is any class selected in S1.u[9:0]
    /// (ISA 12.9 opcodes 136, 143, 168). The test classifies the operand's bits as given
    /// (after abs/neg): the list names denormals as classes of their own, so FP_DENORM does
    /// not turn them into zeros.</summary>
    private static VectorOp ClassCompare(OpType type, bool exec)
    {
        var format = type.Format();
        LaneFunction function = (in LaneInputs lane) =>
        {
            var value = lane.S0 & format.ValueMask;
            var negative = SoftFloat.IsNegative(value, format);
            var bit = SoftFloat.IsSignalingNaN(value, format) ? 0
                : SoftFloat.IsQuietNaN(value, format) ? 1
                : SoftFloat.IsInfinity(value, format) ? (negative ? 2 : 9)
                : SoftFloat.IsZero(value, format) ? (negative ? 5 : 6)
                : SoftFloat.IsDenormal(value, format) ? (negative ? 4 : 7)
                : (negative ? 3 : 8);
            return LaneResult.Bool((lane.S1 >> bit & 1) != 0);
        };
        return CompareOp("class", type, [type, OpType.U32], function, exec,
            $"ISA 12.9 V_{(exec ? "CMPX" : "CMP")}_CLASS_{type}, 3.9",
            index => index == 0 ? OperandPools.Edges(type) : ClassMasks);
    }

    /// <summary>The forms a compare is run in: VOPC (VCC) at both wave sizes, float compares
    /// under the MODE variants that change denormal inputs; VOP3 (SGPR mask) at both wave sizes;
    /// VOP3 abs/neg and the clamp bit (exception signalling only, ISA 6.5, 12.9.1) for float
    /// sources; and two SDWAB forms for 16/32-bit operands: VCC with one set of selects and
    /// modifiers at wave32, the SGPR with another at wave64.</summary>
    private static IEnumerable<VectorCase> CompareCases(VectorOp op)
    {
        var type = op.Sources[0];
        var isFloat = type.IsFloat();
        var isClass = op.Sources[1] != type;
        var floatModes = isFloat
            ? (AluMode[])[AluMode.Default, AluMode.Default with { FloatMode = 0x00 }, AluMode.Default with { FloatMode = 0xF0 }]
            : FormGenerator.DefaultMode;
        yield return new VectorCase(new VectorForm(op, FormKind.E32), FormGenerator.Wave32, floatModes);
        yield return new VectorCase(new VectorForm(op, FormKind.E32), [64], FormGenerator.DefaultMode);
        yield return new VectorCase(new VectorForm(op, FormKind.E64), FormGenerator.BothWaves, FormGenerator.DefaultMode);
        if (isFloat)
        {
            // Source modifiers apply to the float operands only (S1 of a class test is a mask).
            var floatMask = isClass ? 1u : 3u;
            yield return new VectorCase(new VectorForm(op, FormKind.E64) { Neg = floatMask, Abs = floatMask & 2 }, FormGenerator.Wave32, FormGenerator.DefaultMode);
            yield return new VectorCase(new VectorForm(op, FormKind.E64) { Abs = floatMask, Neg = 1 }, [64], FormGenerator.DefaultMode);
            if (!isClass)
            {
                yield return new VectorCase(new VectorForm(op, FormKind.E64) { Clamp = true }, FormGenerator.Wave32, FormGenerator.DefaultMode);
            }
        }

        if (type.Width() == 64) yield break;
        var (first, second) = CompareSdwa(type, isClass);
        yield return new VectorCase(new VectorForm(op, FormKind.Sdwa) { Sdwa = first.Control, Abs = first.Abs, Neg = first.Neg },
            FormGenerator.Wave32, FormGenerator.DefaultMode);
        yield return new VectorCase(new VectorForm(op, FormKind.Sdwa) { Sdwa = second.Control, Abs = second.Abs, Neg = second.Neg },
            [64], FormGenerator.DefaultMode);
    }

    private static ((SdwaControl Control, uint Abs, uint Neg) First, (SdwaControl Control, uint Abs, uint Neg) Second)
        CompareSdwa(OpType type, bool isClass) => type switch
    {
        OpType.F32 when isClass => ((new SdwaControl(VccDestination: true), 1, 1), (new SdwaControl(Src1Sel: 4), 1, 0)),
        OpType.F16 when isClass => ((new SdwaControl(Src0Sel: 5, Src1Sel: 4, VccDestination: true), 1, 0),
            (new SdwaControl(Src0Sel: 4, Src1Sel: 0), 0, 1)),
        OpType.F32 => ((new SdwaControl(VccDestination: true), 2, 1), (new SdwaControl(), 3, 2)),
        OpType.F16 => ((new SdwaControl(Src0Sel: 5, Src1Sel: 4, VccDestination: true), 0, 1),
            (new SdwaControl(Src0Sel: 4, Src1Sel: 5), 2, 0)),
        OpType.I16 or OpType.U16 => ((new SdwaControl(Src0Sel: 1, Src1Sel: 5, Src0Sext: true, VccDestination: true), 0, 0),
            (new SdwaControl(Src0Sel: 5, Src1Sel: 2, Src1Sext: true), 0, 0)),
        _ => ((new SdwaControl(Src0Sel: 5, Src1Sel: 0, Src0Sext: true, VccDestination: true), 0, 0),
            (new SdwaControl(Src0Sel: 3, Src1Sel: 1, Src1Sext: true), 0, 0)),
    };
}
