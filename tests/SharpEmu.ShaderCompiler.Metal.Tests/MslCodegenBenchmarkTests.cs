// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Measures what the structured emitter and the register allocator do on the device,
/// by compiling the same guest program twice — once the way the translator used to emit
/// it (PC dispatcher, both register files at their architectural size) and once the way
/// it emits it now — and timing both.
///
/// Two numbers per program. GPU time comes from the command buffer's own clock over a
/// dispatch wide enough to hide launch cost. maxTotalThreadsPerThreadgroup is the
/// occupancy ceiling the driver derives from the function's register footprint, which is
/// the thing the dispatcher was wrecking: it cannot be argued with and it barely varies
/// run to run.
///
/// The programs are synthetic but shaped like the ones that hurt: a long chain over many
/// live VGPRs, a counted loop with state that lives across it, and a run of scalar
/// branches. Run with SHARPEMU_TEST_REQUIRE_DEVICE=1 to get the timings; without it the
/// test still reports the codegen shape (declared register dwords, dispatcher or not).
/// </summary>
public sealed class MslCodegenBenchmarkTests(ITestOutputHelper output)
{
    // ---- instruction encodings (GFX10, matching the fixtures in Gen5ComputeFixtures) ----

    private static uint VMovLiteral(uint destination) => 0x7E0002FFu | (destination << 17);

    private static uint VFmac(uint destination, uint source0, uint source1) =>
        0x56000000u | (destination << 17) | (source1 << 9) | (256u + source0);

    private static uint SMovInline(uint destination, uint value) =>
        0xBE800380u | (destination << 16) | value;

    private static uint SSubInline(uint destination, uint source, uint value) =>
        0x81800000u | (destination << 16) | ((128u + value) << 8) | source;

    private static uint SCmpLgZero(uint source) => 0xBF078000u | source;

    private static uint SCbranchScc1(short offset) => 0xBF850000u | (ushort)offset;

    private static uint SCbranchScc0(short offset) => 0xBF840000u | (ushort)offset;

    private static uint BufferStoreLow(uint byteOffset) => 0xE0700000u | byteOffset;

    private static uint BufferStoreHigh(uint vectorData) => 0x80020000u | (vectorData << 8);

    private const uint SEndpgm = 0xBF810000u;

    // ---- programs ----

    /// <summary>Straight-line: 24 VGPRs seeded from the thread id, a long FMA chain over
    /// all of them, then every one stored so none can be folded away. No control flow at
    /// all, so the dispatcher's while loop is pure overhead.</summary>
    private static Gen5ComputeFixture AluChain()
    {
        const uint count = 24;
        var words = new List<uint>();
        for (var register = 1u; register <= count; register++)
        {
            words.Add(VMovLiteral(register));
            words.Add(0x3F800000u + (register << 16));
            // Make it depend on the thread id so nothing is a compile-time constant.
            words.Add(VFmac(register, 0, register));
        }

        for (var round = 0; round < 6; round++)
        {
            for (var register = 1u; register <= count; register++)
            {
                words.Add(VFmac(register, (register % count) + 1, ((register + 1) % count) + 1));
            }
        }

        AppendStores(words, count);
        return new Gen5ComputeFixture("alu-chain", [.. words], 8, (int)(count * 4) + 16);
    }

    /// <summary>A counted scalar loop whose body touches four VGPRs, with twenty more
    /// live across it and consumed afterwards. The dispatcher has to keep all of them
    /// alive over its back edge because any block can follow any other; a real while
    /// loop carries only the four the body writes.</summary>
    private static Gen5ComputeFixture LoopWithLiveState()
    {
        const uint count = 24;
        var words = new List<uint>();
        for (var register = 1u; register <= count; register++)
        {
            words.Add(VMovLiteral(register));
            words.Add(0x3F800000u + (register << 16));
            words.Add(VFmac(register, 0, register));
        }

        words.Add(SMovInline(0, 32));

        // The loop body: four FMAs, the counter, and the branch back.
        var bodyStart = words.Count;
        for (var register = 1u; register <= 4u; register++)
        {
            words.Add(VFmac(register, register % 4u + 1u, 0));
        }

        words.Add(SSubInline(0, 0, 1));
        words.Add(SCmpLgZero(0));
        var branchIndex = words.Count;
        words.Add(SCbranchScc1(0));
        words[branchIndex] = SCbranchScc1(checked((short)(bodyStart - (branchIndex + 1))));

        for (var round = 0; round < 4; round++)
        {
            for (var register = 5u; register <= count; register++)
            {
                words.Add(VFmac(register, (register % count) + 1, ((register + 1) % count) + 1));
            }
        }

        AppendStores(words, count);
        return new Gen5ComputeFixture("loop-live-state", [.. words], 8, (int)(count * 4) + 16);
    }

    /// <summary>Eight scalar if/else diamonds in a row. There is no loop in the guest
    /// program at all, so every dword the dispatcher's while loop keeps live is waste;
    /// the structured form is a flat run of ifs.</summary>
    private static Gen5ComputeFixture BranchChain()
    {
        const uint count = 16;
        var words = new List<uint>();
        for (var register = 1u; register <= count; register++)
        {
            words.Add(VMovLiteral(register));
            words.Add(0x3F800000u + (register << 16));
            words.Add(VFmac(register, 0, register));
        }

        words.Add(SMovInline(0, 3));
        for (var diamond = 0u; diamond < 8; diamond++)
        {
            words.Add(SSubInline(0, 0, 1));
            words.Add(SCmpLgZero(0));
            // scc0 skips the "taken" arm, which is three FMAs; the arms rejoin after.
            var branchIndex = words.Count;
            words.Add(SCbranchScc0(0));
            for (var step = 0u; step < 3; step++)
            {
                words.Add(VFmac((diamond % count) + 1, step + 1, step + 2));
            }

            var skip = words.Count;
            words[branchIndex] = SCbranchScc0(checked((short)(skip + 1 - (branchIndex + 1))));
            // The not-taken arm: three different FMAs, then both arms meet.
            words.Add(0xBF820003u); // s_branch over the else arm
            for (var step = 0u; step < 3; step++)
            {
                words.Add(VFmac(((diamond + 1) % count) + 1, step + 2, step + 1));
            }
        }

        AppendStores(words, count);
        return new Gen5ComputeFixture("branch-chain", [.. words], 8, (int)(count * 4) + 16);
    }

    private static void AppendStores(List<uint> words, uint count)
    {
        for (var register = 1u; register <= count; register++)
        {
            words.Add(BufferStoreLow((register - 1) * 4));
            words.Add(BufferStoreHigh(register));
        }

        words.Add(SEndpgm);
    }

    // ---- the measurement ----

    [Fact]
    public void StructuredCodegenBeatsTheDispatcher()
    {
        var programs = new[] { AluChain(), LoopWithLiveState(), BranchChain() };
        var device = Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1";
        if (device)
        {
            Assert.True(MetalNative.IsAvailable, "The benchmark gate needs a Metal device.");
        }
        else
        {
            output.WriteLine("No device gate: reporting codegen shape only.");
        }

        var report = new StringBuilder();
        report.AppendLine(
            "program            form         reg dwords   max threads   gpu ms   speedup");
        var regressions = new List<string>();
        foreach (var program in programs)
        {
            var dispatcher = Measure(program, structured: false, device);
            var structured = Measure(program, structured: true, device);
            Append(report, program.Name, "dispatcher", dispatcher, 1.0);
            var speedup = dispatcher.GpuMilliseconds > 0 && structured.GpuMilliseconds > 0
                ? dispatcher.GpuMilliseconds / structured.GpuMilliseconds
                : 0.0;
            Append(report, program.Name, "structured", structured, speedup);

            Assert.True(
                structured.RegisterDwords < dispatcher.RegisterDwords,
                $"[{program.Name}] the structured form declares no fewer register dwords");
            Assert.False(structured.UsesDispatcher, $"[{program.Name}] still emits a PC dispatcher");
            // The report is the point; the gate only catches a collapse. The GPU is
            // shared with whatever else is running, and a program with no control flow
            // is expected to come out even anyway, because the Metal compiler already
            // promotes the register-file array when nothing indexes it dynamically.
            if (device && speedup != 0.0 && speedup < 0.6)
            {
                regressions.Add($"{program.Name} {speedup:F2}x");
            }
        }

        output.WriteLine(report.ToString());
        Assert.True(regressions.Count == 0, "slower than the dispatcher: " + string.Join(", ", regressions));
    }

    /// <summary>The structured emitter has to cover the shapes real shaders are made of,
    /// not just the synthetic ones: every fixture in the suite must structure.</summary>
    [Fact]
    public void EveryFixtureStructures()
    {
        var failures = new List<string>();
        foreach (var fixture in Gen5ComputeFixtures.All)
        {
            var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture);
            if (shader.Source.Contains("switch (pc)", StringComparison.Ordinal))
            {
                failures.Add(fixture.Name);
            }
        }

        output.WriteLine($"{Gen5ComputeFixtures.All.Length - failures.Count}/{Gen5ComputeFixtures.All.Length} fixtures structured");
        Assert.Empty(failures);
    }

    private readonly record struct Measurement(
        int RegisterDwords,
        bool UsesDispatcher,
        uint MaxThreadsPerThreadgroup,
        double GpuMilliseconds);

    private static Measurement Measure(Gen5ComputeFixture program, bool structured, bool device)
    {
        // Options travel with the compile, so the two forms can be built in the same
        // process without disturbing anything else compiling alongside.
        var options = new Gen5MslCodegenOptions(
            StructuredControlFlow: structured,
            RegisterAllocation: structured);
        var request = Gen5ComputeFixtures.CreateComputeRequest(program);
        Assert.True(
            Gen5MslTranslator.TryCompileProgram(request, out var shader, out var compileError, options),
            $"[{program.Name}] {compileError}");

        var dwords = DeclaredRegisterDwords(shader.Source);
        var usesDispatcher = shader.Source.Contains("switch (pc)", StringComparison.Ordinal);
        if (!device)
        {
            return new Measurement(dwords, usesDispatcher, 0, 0);
        }

        Assert.True(
            MetalNative.TryBenchmark(
                shader,
                new byte[Math.Max(program.StoreBackingBytes, 16)],
                new byte[Resources.PushData.ByteSize],
                threadgroups: 4096,
                threadsPerThreadgroup: 32,
                iterations: 16,
                out var result,
                out var error),
            $"[{program.Name}] {error}");
        Assert.False(result.StepCapHit, $"[{program.Name}] ran out of dispatcher steps");
        return new Measurement(dwords, usesDispatcher, result.MaxThreadsPerThreadgroup, result.GpuMilliseconds);
    }

    /// <summary>Per-thread guest register storage the emitted MSL declares, in dwords:
    /// the array sizes when a file is still an array, otherwise one per named local.</summary>
    private static int DeclaredRegisterDwords(string source)
    {
        var dwords = 0;
        foreach (var line in source.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed == "uint s[128] = {};")
            {
                dwords += 128;
            }
            else if (trimmed == "uint v[256] = {};")
            {
                dwords += 256;
            }
            else if (trimmed.Length > 6 && trimmed.StartsWith("uint ", StringComparison.Ordinal) &&
                trimmed[5] is 's' or 'v' && char.IsAsciiDigit(trimmed[6]))
            {
                dwords += trimmed.Count(character => character == ',') + 1;
            }
        }

        return dwords;
    }

    private static void Append(
        StringBuilder report,
        string name,
        string form,
        Measurement measurement,
        double speedup)
    {
        report.AppendLine(
            $"{name,-18} {form,-12} {measurement.RegisterDwords,10}   {measurement.MaxThreadsPerThreadgroup,11}   " +
            $"{measurement.GpuMilliseconds,6:F3}   {(speedup == 0.0 ? "-" : $"{speedup:F2}x"),7}");
    }
}
