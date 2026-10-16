// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

/// <summary>
/// Differential conformance of the translated ALU against an independent reference model of
/// the RDNA2 ISA (the <c>Rdna2Ops</c> tables, written from the ISA document). For every
/// opcode form the harness assembles a small compute program with llvm-mc's encodings, runs
/// our decoder, translator and the Metal compiler on it, executes it over operand sets that
/// hit every edge, and compares each lane's destination, VCC, EXEC and SGPR mask bit-exactly
/// with the model. Execution is a device test: it runs under SHARPEMU_TEST_REQUIRE_DEVICE=1,
/// like the other numerical suites; without it every form is still decoded and translated.
/// Set SHARPEMU_CONFORMANCE_REPORT to a directory to write the per-opcode table and failures.
/// </summary>
public sealed class Rdna2AluConformanceTests(ITestOutputHelper output)
{
    private const int CaseLimit = 1024;
    private const int RandomCases = 128;

    public static TheoryData<string> Categories()
    {
        var data = new TheoryData<string>();
        foreach (var category in ConformanceCatalog.Categories) data.Add(category);
        return data;
    }

    [Theory]
    [MemberData(nameof(Categories))]
    public void TranslatedAluMatchesTheReferenceModel(string category)
    {
        // SHARPEMU_CONFORMANCE_CATEGORY=a,b runs only those categories (a development aid).
        if (Environment.GetEnvironmentVariable("SHARPEMU_CONFORMANCE_CATEGORY") is { Length: > 0 } only &&
            !only.Split(',').Contains(category))
        {
            return;
        }

        var execute = Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1";
        if (execute) Assert.True(MetalNative.IsAvailable, "The required execution gate needs a Metal device.");
        var reports = Run(category, execute);
        var text = Report(category, reports);
        output.WriteLine(text);
        var failing = reports.Where(static report => !report.Passed).ToList();
        Assert.True(failing.Count == 0, $"{failing.Count} of {reports.Count} forms disagree with the reference model:\n" +
            string.Join("\n", failing.Take(40)));
    }

    /// <summary>Every program line has an llvm-mc encoding in the checked-in table. With
    /// SHARPEMU_REGEN_RDNA2_ENCODINGS=/path/to/llvm-mc the table is regenerated first.</summary>
    [Fact]
    public void EncodingTableCoversTheCatalog()
    {
        var lines = ConformanceCatalog.AllLines().Distinct().ToList();
        if (Environment.GetEnvironmentVariable("SHARPEMU_REGEN_RDNA2_ENCODINGS") is { Length: > 0 } llvmMc)
        {
            var rejected = LlvmMcEncodings.Regenerate(llvmMc, lines);
            Assert.True(rejected.Count == 0, "llvm-mc rejected:\n" + string.Join("\n", rejected));
            return;
        }

        var missing = lines.Where(static line => !LlvmMcEncodings.TryEncode(line.Wave, line.Asm, out _))
            .Select(static line => $"w{line.Wave}: {line.Asm}").ToList();
        Assert.True(missing.Count == 0,
            $"{missing.Count} lines lack an encoding; rerun with SHARPEMU_REGEN_RDNA2_ENCODINGS=<llvm-mc>:\n" +
            string.Join("\n", missing.Take(50)));
    }

    private static List<FormReport> Run(string category, bool execute)
    {
        var reports = new ConcurrentBag<FormReport>();
        var work = new List<Func<FormReport>>();
        var seed = 0;
        foreach (var vectorCase in ConformanceCatalog.VectorCases(category))
        {
            var form = vectorCase.Form;
            var tuples = OperandPools.Tuples(form.Op.Sources, form.Op.Pool, CaseLimit, RandomCases, ++seed);
            foreach (var wave in vectorCase.Waves)
            {
                foreach (var mode in vectorCase.Modes)
                {
                    var formSeed = ++seed;
                    work.Add(() => ConformanceRunner.RunVector(form, wave, mode, tuples, formSeed, execute));
                }
            }
        }

        foreach (var form in ConformanceCatalog.ScalarForms(category))
        {
            var tuples = OperandPools.Tuples(form.Op.Sources, null, CaseLimit, RandomCases, ++seed);
            foreach (var wave in (int[])[32, 64])
            {
                var formSeed = ++seed;
                work.Add(() => ConformanceRunner.RunScalar(form, wave, tuples, formSeed, execute));
            }
        }

        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = 6 }, item => reports.Add(item()));
        return [.. reports.OrderBy(static report => report.Opcode, StringComparer.Ordinal)
            .ThenBy(static report => report.Label, StringComparer.Ordinal).ThenBy(static report => report.Wave)];
    }

    private static string Report(string category, List<FormReport> reports)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"[{category}] {reports.Count} forms, {reports.Sum(static r => r.Cases)} lane cases\n");
        builder.Append(ConformanceRunner.Table(reports));
        foreach (var report in reports.Where(static report => !report.Passed))
        {
            builder.Append(report).Append('\n');
        }

        var text = builder.ToString();
        if (Environment.GetEnvironmentVariable("SHARPEMU_CONFORMANCE_REPORT") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{category}.txt"), text);
        }

        return text;
    }
}
