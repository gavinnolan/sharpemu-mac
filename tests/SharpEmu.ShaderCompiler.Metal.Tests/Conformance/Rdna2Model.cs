// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

/// <summary>Operand data types as named by the instruction (RDNA2 ISA 6.2: "the data-size of
/// the operands is explicitly defined in the name of the instruction").</summary>
internal enum OpType
{
    B16, U16, I16, F16,
    B32, U32, I32, F32,
    B64, U64, I64, F64,
}

internal static class OpTypes
{
    public static int Width(this OpType type) => type switch
    {
        OpType.B16 or OpType.U16 or OpType.I16 or OpType.F16 => 16,
        OpType.B64 or OpType.U64 or OpType.I64 or OpType.F64 => 64,
        _ => 32,
    };

    public static bool IsFloat(this OpType type) => type is OpType.F16 or OpType.F32 or OpType.F64;

    public static bool IsSigned(this OpType type) => type is OpType.I16 or OpType.I32 or OpType.I64;

    public static FloatFormat Format(this OpType type) => type switch
    {
        OpType.F16 => FloatFormat.F16,
        OpType.F64 => FloatFormat.F64,
        OpType.F32 => FloatFormat.F32,
        _ => throw new InvalidOperationException($"{type} is not a float type"),
    };

    public static ulong Mask(this OpType type) => type.Width() == 64 ? ulong.MaxValue : (1ul << type.Width()) - 1;

    public static int Registers(this OpType type) => type.Width() == 64 ? 2 : 1;
}

/// <summary>The MODE state the translator receives: FLOAT_MODE (RSRC1 bits 7:0: FP_ROUND 3:0,
/// FP_DENORM 7:4), IEEE_MODE and DX10_CLAMP (ISA 3.5, 6.4).</summary>
internal readonly record struct AluMode(uint FloatMode = 0xC0, bool Ieee = false, bool Dx10Clamp = true)
{
    // A record struct's parameterless constructor ignores the primary constructor defaults.
    public static readonly AluMode Default = new(0xC0, false, true);

    /// <summary>FP_DENORM bit 0 of the format's field as the program set it.</summary>
    public bool RequestsInputDenormals(FloatFormat format) =>
        (FloatMode & (format == FloatFormat.F32 ? 0x10u : 0x40u)) != 0;

    /// <summary>FP_DENORM bit 1 of the format's field as the program set it. OMOD keys off
    /// this bit (ISA 6.2.2), whether or not the host can keep the denormals.</summary>
    public bool RequestsOutputDenormals(FloatFormat format) =>
        (FloatMode & (format == FloatFormat.F32 ? 0x20u : 0x80u)) != 0;

    /// <summary>Input denormals of the format are kept: the requested mode, less the documented
    /// deviation that f32 denormals are always flushed (<see cref="Deviations.F32DenormalsFlushed"/>).</summary>
    public bool KeepsInputDenormals(FloatFormat format) =>
        RequestsInputDenormals(format) && !(format == FloatFormat.F32 && Deviations.F32DenormalsFlushed);

    /// <summary>Output denormals of the format are kept, less the same deviation.</summary>
    public bool KeepsOutputDenormals(FloatFormat format) =>
        RequestsOutputDenormals(format) && !(format == FloatFormat.F32 && Deviations.F32DenormalsFlushed);

    public ulong FlushIn(ulong bits, FloatFormat format) =>
        KeepsInputDenormals(format) ? bits : SoftFloat.FlushDenormal(bits, format);

    public override string ToString() =>
        $"mode=0x{FloatMode:X2}{(Ieee ? ",ieee" : "")}{(Dx10Clamp ? "" : ",nodx10")}";
}

/// <summary>One lane's view of an instruction: the sources after selection and input
/// modifiers, the destination's previous contents, the carry in and the controls the
/// semantics read.</summary>
internal struct LaneInputs
{
    public ulong S0;
    public ulong S1;
    public ulong S2;
    public ulong Old;
    public bool CarryIn;
    public bool Clamp;
    public AluMode Mode;
    public int Lane;
    public int WaveSize;
}

/// <summary>A lane result. <see cref="Loose"/> marks the result bits holding a NaN whose payload
/// the ISA does not define: the comparison only requires a NaN there.</summary>
internal readonly record struct LaneResult(ulong Value, bool Flag = false, ulong Loose = 0)
{
    public static LaneResult Bool(bool flag) => new(0, flag);
}

internal delegate LaneResult LaneFunction(in LaneInputs lane);

/// <summary>Encodings an opcode has: VOP1/VOP2/VOPC opcodes also have a VOP3 form;
/// VOP3-only opcodes have no 32-bit form.</summary>
internal enum VectorEncoding { Vop1, Vop2, Vopc, Vop3, Vop3P }

[Flags]
internal enum VectorFlags
{
    None = 0,
    /// <summary>Writes a carry/borrow out: VCC in VOP2, an SGPR mask in VOP3B.</summary>
    CarryOut = 1,
    /// <summary>Reads a carry in: VCC in VOP2, an SGPR mask (VCC here) in VOP3.</summary>
    CarryIn = 2,
    /// <summary>V_CMP: writes the lane bit to VCC (VOPC) or an SGPR mask (VOP3).</summary>
    Compare = 4,
    /// <summary>V_CMPX: writes the lane bit to EXEC only.</summary>
    CompareExec = 8,
    /// <summary>V_CNDMASK: reads the lane's VCC bit.</summary>
    ReadsMask = 16,
    /// <summary>The destination is also the addend (V_FMAC, V_MAC, V_DOT2C, V_PK_FMAC).</summary>
    Accumulates = 32,
    /// <summary>The integer result is computed wide and saturated by the op when Clamp is set.</summary>
    IntegerClamp = 64,
    /// <summary>A float result whose VOP3 clamp/omod are not applied by the generic path
    /// (the op handles them, or the opcode has no such modifier).</summary>
    NoOutputModifiers = 128,
    /// <summary>The op_sel destination bit of a 16-bit VOP3 op selects the high half.</summary>
    OpSelDestination = 256,
    /// <summary>The op's result is approximate in hardware: compared within <see cref="VectorOp.UlpTolerance"/>.</summary>
    Approximate = 512,
    /// <summary>16-bit result written into a 32-bit register zero-extended rather than preserving the high half.</summary>
    ZeroHigh = 1024,
    /// <summary>A 16-bit result always written to the high half, preserving the low (V_FMA_MIXHI_F16).</summary>
    WritesHighHalf = 2048,
}

/// <summary>A vector opcode: its LLVM mnemonic (without encoding suffix), the result and source
/// types, and the per-lane semantics written from the ISA (chapter 12 pseudo-code).</summary>
internal sealed record VectorOp(
    string Mnemonic,
    VectorEncoding Encoding,
    OpType Result,
    OpType[] Sources,
    LaneFunction Function,
    VectorFlags Flags = VectorFlags.None)
{
    /// <summary>A literal constant some forms carry (V_FMAAK/V_FMAMK): it is S1 or S2.</summary>
    public uint? Literal { get; init; }

    /// <summary>Allowed distance in units in the last place for approximate ops.</summary>
    public int UlpTolerance { get; init; }

    /// <summary>The reference citation (ISA section) recorded with mismatches.</summary>
    public string Citation { get; init; } = "";

    /// <summary>Operand pools override per source (otherwise chosen by type).</summary>
    public Func<int, IReadOnlyList<ulong>>? Pool { get; init; }

    public bool Has(VectorFlags flag) => (Flags & flag) != 0;

    public bool FloatResult => Result.IsFloat() && !Has(VectorFlags.Compare) && !Has(VectorFlags.CompareExec);

    public override string ToString() => Mnemonic;
}

/// <summary>Intentional deviations from the ISA the model asserts, each recorded in
/// docs/deviations.md ("Shader ALU").</summary>
internal static class Deviations
{
    /// <summary>Metal flushes f32 denormals (inputs and results) whatever FP_DENORM says, so a
    /// program asking to keep them gets them flushed: docs/deviations.md, "f32 denormals".</summary>
    public const bool F32DenormalsFlushed = true;
}
