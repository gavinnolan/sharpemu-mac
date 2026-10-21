// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

/// <summary>Marks a static method of <see cref="Rdna2Ops"/> returning a category's opcodes
/// (<see cref="VectorOp"/>, <see cref="ScalarOp"/> or <see cref="VectorCase"/> sequences).</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class OpCategoryAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>The reference model's opcode tables, one partial file per category.</summary>
internal static partial class Rdna2Ops;

/// <summary>A vector form to run, at the wave sizes and MODEs it should be checked under.</summary>
internal sealed record VectorCase(VectorForm Form, int[] Waves, AluMode[] Modes)
{
    public override string ToString() => Form.Label;
}

/// <summary>Every category's opcodes and the forms generated for them.</summary>
internal static class ConformanceCatalog
{
    private static readonly Lazy<IReadOnlyList<(string Category, object Item)>> Items = new(Collect);

    public static IReadOnlyList<string> Categories =>
        Items.Value.Select(static item => item.Category).Distinct().ToList();

    private static List<(string, object)> Collect()
    {
        var items = new List<(string, object)>();
        foreach (var method in typeof(Rdna2Ops).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (method.GetCustomAttribute<OpCategoryAttribute>() is not { } category) continue;
            foreach (var item in (System.Collections.IEnumerable)method.Invoke(null, null)!)
            {
                items.Add((category.Name, item));
            }
        }

        return items;
    }

    public static IEnumerable<VectorCase> VectorCases(string category) =>
        Items.Value.Where(item => item.Category == category).SelectMany(static item => item.Item switch
        {
            VectorOp op => FormGenerator.Cases(op),
            VectorCase vectorCase => [vectorCase],
            _ => [],
        });

    public static IEnumerable<ScalarForm> ScalarForms(string category) =>
        Items.Value.Where(item => item.Category == category).SelectMany(static item => item.Item switch
        {
            ScalarOp op when op.Encoding == ScalarEncoding.Sopk => op.Immediates.Select(imm => new ScalarForm(op, imm)),
            ScalarOp op => [new ScalarForm(op)],
            _ => [],
        });

    /// <summary>Every assembly line any program in the catalog needs.</summary>
    public static IEnumerable<(int Wave, string Asm)> AllLines()
    {
        foreach (var category in Categories)
        {
            foreach (var vectorCase in VectorCases(category))
            {
                foreach (var wave in vectorCase.Waves)
                {
                    foreach (var line in ConformancePrograms.Lines(ConformancePrograms.Vector(vectorCase.Form.Asm(wave), wave)))
                    {
                        yield return (wave, line);
                    }
                }
            }

            foreach (var form in ScalarForms(category))
            {
                foreach (var wave in form.Op.Waves)
                {
                    foreach (var line in ConformancePrograms.Lines(ConformancePrograms.Scalar(form.Asm(wave), wave, form.Op.Has(ScalarFlags.Exec))))
                    {
                        yield return (wave, line);
                    }
                }
            }
        }
    }
}

/// <summary>The default set of forms per opcode: each encoding the opcode has, with the
/// modifiers that encoding offers, at wave32 (and wave64 where masks or lanes matter).</summary>
internal static class FormGenerator
{
    public static readonly int[] Wave32 = [32];
    public static readonly int[] BothWaves = [32, 64];
    public static readonly AluMode[] DefaultMode = [AluMode.Default];

    /// <summary>The MODE variants a float op is checked under: the titles' default (f32
    /// denormals flushed, f16/f64 kept, DX10 clamp), IEEE mode, all denormals kept, and all
    /// flushed.</summary>
    public static readonly AluMode[] FloatModes =
    [
        AluMode.Default,
        AluMode.Default with { Ieee = true },
        AluMode.Default with { FloatMode = 0xF0 },
        AluMode.Default with { FloatMode = 0x00 },
    ];

    public static IEnumerable<VectorCase> Cases(VectorOp op)
    {
        var floatSources = op.Sources.Select(static type => type.IsFloat()).ToArray();
        var anyFloatSource = floatSources.Any(static f => f);
        var masks = op.Has(VectorFlags.Compare) || op.Has(VectorFlags.CompareExec) || op.Has(VectorFlags.CarryOut) ||
            op.Has(VectorFlags.CarryIn) || op.Has(VectorFlags.ReadsMask);
        var waves = masks ? BothWaves : Wave32;
        var floatOp = anyFloatSource || op.Result.IsFloat();
        var modes = floatOp ? FloatModes : DefaultMode;
        var explicitSources = op.Sources.Length - (op.Has(VectorFlags.Accumulates) ? 1 : 0);
        var vop3Modifiers = op.Encoding != VectorEncoding.Vop3P;

        if (op.Encoding == VectorEncoding.Vop3P)
        {
            yield return new VectorCase(new VectorForm(op, FormKind.Packed), Wave32, modes);
            yield break;
        }

        if (op.Encoding is VectorEncoding.Vop1 or VectorEncoding.Vop2 or VectorEncoding.Vopc)
        {
            yield return new VectorCase(new VectorForm(op, FormKind.E32), BothWaves, modes);
            if (op.Literal is not null) yield break;
        }

        // VOP3: the plain form, and the input/output modifiers a float op has.
        yield return new VectorCase(new VectorForm(op, FormKind.E64), op.Encoding == VectorEncoding.Vop3 ? BothWaves : waves, modes);
        if (!vop3Modifiers) yield break;
        var floatMask = 0u;
        for (var index = 0; index < explicitSources; index++)
        {
            if (floatSources[index]) floatMask |= 1u << index;
        }

        if (floatMask != 0)
        {
            yield return new VectorCase(new VectorForm(op, FormKind.E64) { Neg = floatMask & 0b101, Abs = floatMask & 0b110 }, Wave32, DefaultMode);
            yield return new VectorCase(new VectorForm(op, FormKind.E64) { Neg = floatMask, Abs = floatMask & 0b001 }, Wave32, DefaultMode);
        }

        if (op.FloatResult && !op.Has(VectorFlags.NoOutputModifiers))
        {
            yield return new VectorCase(new VectorForm(op, FormKind.E64) { Clamp = true }, Wave32,
                [AluMode.Default, AluMode.Default with { Dx10Clamp = false }]);
            yield return new VectorCase(new VectorForm(op, FormKind.E64) { Omod = 1 }, Wave32, modes);
            yield return new VectorCase(new VectorForm(op, FormKind.E64) { Omod = 3, Clamp = true }, Wave32, DefaultMode);
        }
        else if (op.Has(VectorFlags.IntegerClamp))
        {
            yield return new VectorCase(new VectorForm(op, FormKind.E64) { Clamp = true }, Wave32, DefaultMode);
        }

        if (op.Has(VectorFlags.OpSelDestination) || (op.Encoding == VectorEncoding.Vop3 && op.Sources.Any(static t => t.Width() == 16)))
        {
            // op_sel: high halves of the 16-bit sources, and (where allowed) the high destination.
            var sourceBits = 0u;
            for (var index = 0; index < explicitSources; index++)
            {
                if (op.Sources[index].Width() == 16) sourceBits |= 1u << index;
            }

            yield return new VectorCase(new VectorForm(op, FormKind.E64) { OpSel = sourceBits & 0b101 | (op.Has(VectorFlags.OpSelDestination) ? 8u : 0) }, Wave32, DefaultMode);
            yield return new VectorCase(new VectorForm(op, FormKind.E64) { OpSel = sourceBits & 0b010 }, Wave32, DefaultMode);
        }
    }
}
