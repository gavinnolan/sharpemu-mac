// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

internal enum FormKind { E32, E64, Sdwa, Dpp16, Dpp8, Packed }

/// <summary>SDWA selects (ISA 13.3.7): 0..3 a byte, 4 word 0, 5 word 1, 6 the dword.
/// DST_UNUSED: 0 pad with zeros, 1 sign-extend upper, 2 preserve. A compare's SDWAB word names its
/// mask destination (ISA 13.3.8): VCC (SD = 0, <see cref="VccDestination"/>) or the SGPR s24/s[24:25].</summary>
internal sealed record SdwaControl(int DstSel = 6, int DstUnused = 0, int Src0Sel = 6, int Src1Sel = 6,
    bool Src0Sext = false, bool Src1Sext = false, bool VccDestination = false)
{
    public static string SelName(int select) => select switch
    {
        0 => "BYTE_0", 1 => "BYTE_1", 2 => "BYTE_2", 3 => "BYTE_3", 4 => "WORD_0", 5 => "WORD_1", _ => "DWORD",
    };
}

/// <summary>DPP16 (ISA 13.3.9) or, with <see cref="Lanes8"/>, DPP8 (13.3.10). Ctrl is the 9-bit
/// DPP_CTRL; BoundCtrl is the BC bit (LLVM spells it bound_ctrl:1, or bound_ctrl:0 in legacy
/// syntax), FetchInactive the FI bit.</summary>
internal sealed record DppControl(int Ctrl = 0xE4, uint RowMask = 0xF, uint BankMask = 0xF,
    bool BoundCtrl = false, bool FetchInactive = false, int[]? Lanes8 = null)
{
    public string Syntax()
    {
        var builder = new StringBuilder();
        if (Lanes8 is not null)
        {
            builder.Append(" dpp8:[").Append(string.Join(',', Lanes8)).Append(']');
            if (FetchInactive) builder.Append(" fi:1");
            return builder.ToString();
        }

        builder.Append(Ctrl switch
        {
            <= 0xFF => $" quad_perm:[{Ctrl & 3},{(Ctrl >> 2) & 3},{(Ctrl >> 4) & 3},{(Ctrl >> 6) & 3}]",
            >= 0x101 and <= 0x10F => $" row_shl:{Ctrl & 0xF}",
            >= 0x111 and <= 0x11F => $" row_shr:{Ctrl & 0xF}",
            >= 0x121 and <= 0x12F => $" row_ror:{Ctrl & 0xF}",
            0x140 => " row_mirror",
            0x141 => " row_half_mirror",
            >= 0x150 and <= 0x15F => $" row_share:{Ctrl & 0xF}",
            >= 0x160 and <= 0x16F => $" row_xmask:{Ctrl & 0xF}",
            _ => throw new InvalidOperationException($"DPP_CTRL 0x{Ctrl:X}"),
        });
        builder.Append(CultureInfo.InvariantCulture, $" row_mask:0x{RowMask:x} bank_mask:0x{BankMask:x}");
        if (BoundCtrl) builder.Append(" bound_ctrl:1");
        if (FetchInactive) builder.Append(" fi:1");
        return builder.ToString();
    }
}

/// <summary>One encoding of an opcode with a fixed set of modifiers, tested at one wave size and
/// MODE. Registers are fixed: sources v1/v[1:2], v3/v[3:4], v5/v[5:6]; destination v7/v[7:8];
/// the VOP3 scalar destination s24/s[24:25]; the mask operand VCC.</summary>
internal sealed record VectorForm(VectorOp Op, FormKind Kind)
{
    /// <summary>Per source: absolute value (VOP3, SDWA, DPP).</summary>
    public uint Abs { get; init; }
    /// <summary>Per source: negate (VOP3, SDWA, DPP); neg_lo for VOP3P.</summary>
    public uint Neg { get; init; }
    /// <summary>VOP3P neg_hi.</summary>
    public uint NegHi { get; init; }
    /// <summary>VOP3 op_sel (bit 3 = destination) or VOP3P op_sel.</summary>
    public uint OpSel { get; init; }
    /// <summary>VOP3P op_sel_hi (defaults to every source's high half).</summary>
    public uint OpSelHi { get; init; } = 7;
    public bool Clamp { get; init; }
    /// <summary>OMOD: 1 = *2, 2 = *4, 3 = /2.</summary>
    public int Omod { get; init; }
    public SdwaControl? Sdwa { get; init; }
    public DppControl? Dpp { get; init; }

    public int ExplicitSources => Op.Sources.Length - (Op.Has(VectorFlags.Accumulates) ? 1 : 0);

    public bool IsCompare => Op.Has(VectorFlags.Compare) || Op.Has(VectorFlags.CompareExec);

    public string Label
    {
        get
        {
            var asm = Asm(32);
            var space = asm.IndexOf(' ');
            var name = asm[..space];
            var tail = new StringBuilder(name);
            if (Abs != 0) tail.Append(CultureInfo.InvariantCulture, $" abs{Abs}");
            if (Neg != 0) tail.Append(CultureInfo.InvariantCulture, $" neg{Neg}");
            var operands = asm[(space + 1)..];
            // Modifiers follow the operand list.
            var modifiers = operands.Split(' ').Where(static part => part.Contains(':') || part is "clamp" or "row_mirror" or "row_half_mirror").ToArray();
            if (modifiers.Length > 0) tail.Append(' ').Append(string.Join(' ', modifiers));
            return tail.ToString();
        }
    }

    public override string ToString() => Label;

    public static string Register(int index, OpType type) =>
        type.Width() == 64 ? $"v[{index}:{index + 1}]" : $"v{index}";

    private string Source(int index, int wave)
    {
        var type = Op.Sources[index];
        if (Op.Literal is uint literal && LiteralSourceIndex == index)
        {
            return $"0x{literal:x}";
        }

        var text = Register(1 + 2 * index, type);
        if (Kind == FormKind.Sdwa && Sdwa is { } sdwa && (index == 0 ? sdwa.Src0Sext : sdwa.Src1Sext))
        {
            text = $"sext({text})";
        }

        if ((Abs >> index & 1) != 0) text = $"|{text}|";
        if (Kind != FormKind.Packed && (Neg >> index & 1) != 0) text = "-" + text;
        return text;
    }

    /// <summary>V_FMAAK puts its literal at S2, V_FMAMK at S1.</summary>
    public int LiteralSourceIndex => Op.Mnemonic.EndsWith("mk_f32", StringComparison.Ordinal) ||
        Op.Mnemonic.EndsWith("mk_f16", StringComparison.Ordinal) ? 1 : 2;

    public string Asm(int wave)
    {
        if (Op.Assembly is { } assembly) return assembly(wave);
        var vcc = wave == 32 ? "vcc_lo" : "vcc";
        var sdst = wave == 32 ? "s24" : "s[24:25]";
        var suffix = Kind switch
        {
            FormKind.E32 => "_e32",
            FormKind.E64 when Op.Encoding is VectorEncoding.Vop1 or VectorEncoding.Vop2 or VectorEncoding.Vopc => "_e64",
            FormKind.Sdwa => "_sdwa",
            FormKind.Dpp16 or FormKind.Dpp8 => "_dpp",
            _ => "",
        };
        var operands = new List<string>();
        var compareExec = Op.Has(VectorFlags.CompareExec);
        if (Op.Has(VectorFlags.Compare))
        {
            operands.Add(Kind is FormKind.E32 or FormKind.Dpp16 or FormKind.Dpp8 ||
                (Kind == FormKind.Sdwa && Sdwa is { VccDestination: true }) ? vcc : sdst);
        }
        else if (!compareExec)
        {
            operands.Add(Register(7, Op.Result));
            if (Op.Has(VectorFlags.CarryOut))
            {
                operands.Add(Kind is FormKind.E32 or FormKind.Sdwa or FormKind.Dpp16 or FormKind.Dpp8 ? vcc : sdst);
            }
        }

        for (var index = 0; index < ExplicitSources; index++)
        {
            operands.Add(Source(index, wave));
        }

        if (Op.Has(VectorFlags.CarryIn) || Op.Has(VectorFlags.ReadsMask))
        {
            operands.Add(vcc);
        }

        var builder = new StringBuilder(Op.Mnemonic).Append(suffix).Append(' ').Append(string.Join(", ", operands));
        if (Kind == FormKind.Packed)
        {
            var count = ExplicitSources;
            builder.Append(Bits(" op_sel", OpSel, count, 0));
            builder.Append(Bits(" op_sel_hi", OpSelHi, count, (1u << count) - 1));
            builder.Append(Bits(" neg_lo", Neg, count, 0));
            builder.Append(Bits(" neg_hi", NegHi, count, 0));
        }
        else if (Kind == FormKind.E64 && OpSel != 0)
        {
            builder.Append(Bits(" op_sel", OpSel, ExplicitSources + 1, uint.MaxValue, destinationLast: true));
        }

        if (Clamp) builder.Append(" clamp");
        if (Omod != 0) builder.Append(Omod switch { 1 => " mul:2", 2 => " mul:4", _ => " div:2" });
        if (Kind == FormKind.Sdwa && Sdwa is { } control)
        {
            if (!IsCompare)
            {
                builder.Append(" dst_sel:").Append(SdwaControl.SelName(control.DstSel));
                builder.Append(" dst_unused:").Append(control.DstUnused switch
                {
                    0 => "UNUSED_PAD", 1 => "UNUSED_SEXT", _ => "UNUSED_PRESERVE",
                });
            }

            builder.Append(" src0_sel:").Append(SdwaControl.SelName(control.Src0Sel));
            if (ExplicitSources > 1) builder.Append(" src1_sel:").Append(SdwaControl.SelName(control.Src1Sel));
        }

        if (Kind is FormKind.Dpp16 or FormKind.Dpp8 && Dpp is { } dpp)
        {
            builder.Append(dpp.Syntax());
        }

        return builder.ToString();
    }

    private static string Bits(string name, uint bits, int count, uint defaultBits, bool destinationLast = false)
    {
        if (!destinationLast && (bits & ((1u << count) - 1)) == (defaultBits & ((1u << count) - 1))) return "";
        var values = new List<string>();
        for (var index = 0; index < count; index++)
        {
            // VOP3 op_sel lists the sources and then the destination (bit 3).
            var bit = destinationLast && index == count - 1 ? 3 : index;
            values.Add(((bits >> bit) & 1).ToString(CultureInfo.InvariantCulture));
        }

        return $"{name}:[{string.Join(',', values)}]";
    }
}
