// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Text;

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

internal enum ScalarEncoding { Sop1, Sop2, Sopc, Sopk }

[Flags]
internal enum ScalarFlags
{
    None = 0,
    /// <summary>Writes the destination SGPR(s).</summary>
    WritesDestination = 1,
    /// <summary>Writes SCC.</summary>
    WritesScc = 2,
    /// <summary>Reads and/or writes EXEC (the *_SAVEEXEC / *_WREXEC family).</summary>
    Exec = 4,
    /// <summary>SOPK whose destination is also its first operand (S_ADDK, S_MULK, S_CMOVK). The
    /// runner puts the first source's operand values in the destination's low dword.</summary>
    DestinationIsSource = 8,
}

/// <summary>One case's scalar inputs. S0/S1 are register pairs; Imm is the SOPK simm16.</summary>
internal struct ScalarInputs
{
    public ulong S0;
    public ulong S1;
    public ulong Old;
    public bool SccIn;
    public ulong Exec;
    public uint Imm;
    public int WaveSize;
}

/// <summary>A scalar result: the destination (when written), SCC (null = unchanged) and EXEC
/// (null = unchanged).</summary>
internal readonly record struct ScalarResult(ulong Value, bool? Scc = null, ulong? Exec = null);

internal delegate ScalarResult ScalarFunction(in ScalarInputs inputs);

/// <summary>A scalar opcode with semantics written from ISA 12.1-12.4.</summary>
internal sealed record ScalarOp(
    string Mnemonic,
    ScalarEncoding Encoding,
    OpType Result,
    OpType[] Sources,
    ScalarFunction Function,
    ScalarFlags Flags)
{
    /// <summary>SOPK immediates to test (one form per value).</summary>
    public ushort[] Immediates { get; init; } = [];

    /// <summary>Operand pool per source index (null, or a null pool, = the type's edges).</summary>
    public Func<int, IReadOnlyList<ulong>>? Pool { get; init; }

    /// <summary>The wave sizes the op runs at (a wave32-only EXEC op, say, runs at 32 only).</summary>
    public int[] Waves { get; init; } = [32, 64];

    public string Citation { get; init; } = "";

    public bool Has(ScalarFlags flag) => (Flags & flag) != 0;

    public override string ToString() => Mnemonic;
}

/// <summary>A scalar opcode at one SOPK immediate. Registers: S0 = s[0:1], S1 = s[4:5],
/// D = s[14:15] (holding the case's old value first), SCC from the case.</summary>
internal sealed record ScalarForm(ScalarOp Op, ushort Imm = 0)
{
    public string Label => Op.Encoding == ScalarEncoding.Sopk ? $"{Op.Mnemonic} 0x{Imm:x}" : Op.Mnemonic;

    public override string ToString() => Label;

    private static string Register(int index, OpType type) => type.Width() == 64 ? $"s[{index}:{index + 1}]" : $"s{index}";

    public string Asm(int wave)
    {
        var builder = new StringBuilder(Op.Mnemonic).Append(' ');
        var operands = new List<string>();
        switch (Op.Encoding)
        {
            case ScalarEncoding.Sopk:
                operands.Add(Op.Has(ScalarFlags.WritesDestination) || Op.Has(ScalarFlags.DestinationIsSource)
                    ? Register(14, Op.Result)
                    : Register(0, Op.Sources[0]));
                operands.Add(string.Create(CultureInfo.InvariantCulture, $"0x{Imm:x}"));
                break;
            default:
                if (Op.Encoding != ScalarEncoding.Sopc) operands.Add(Register(14, Op.Result));
                for (var index = 0; index < Op.Sources.Length; index++)
                {
                    operands.Add(Register(index == 0 ? 0 : 4, Op.Sources[index]));
                }

                break;
        }

        return builder.Append(string.Join(", ", operands)).ToString();
    }

    public (ulong Destination, bool Scc, ulong Exec) Evaluate(ulong s0, ulong s1, ulong old, bool scc, ulong exec, int wave)
    {
        var inputs = new ScalarInputs
        {
            S0 = s0 & Op.Sources.ElementAtOrDefault(0).Mask(),
            S1 = Op.Sources.Length > 1 ? s1 & Op.Sources[1].Mask() : 0,
            Old = old,
            SccIn = scc,
            Exec = exec,
            Imm = Imm,
            WaveSize = wave,
        };
        if (Op.Sources.Length == 0) inputs.S0 = 0;
        var result = Op.Function(inputs);
        var destination = old;
        if (Op.Has(ScalarFlags.WritesDestination))
        {
            destination = Op.Result.Width() == 64
                ? result.Value
                : (old & 0xFFFFFFFF00000000ul) | (result.Value & 0xFFFFFFFFul);
        }

        return (destination, result.Scc ?? scc, result.Exec ?? exec);
    }
}
