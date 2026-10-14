// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// V_EXP_F32 and V_LOG_F32 lower to metal::fast:: exp2 and log2 around the same denormal
// flush the precise form used. This runs both forms over a sweep on the device and pins
// that no input changes class, and reports the worst ulp difference per op.
// V_SIN_F32 and V_COS_F32 stay precise: fast::sin and fast::cos return 0 for an infinite
// or NaN cycle where the precise form returns NaN, so their rows are reported only.
public sealed class MslFastTranscendentalTests
{
    private const int Threads = 1024;
    private const int OutputBase = Threads;
    private const int OpCount = 4;

    private readonly ITestOutputHelper _output;

    public MslFastTranscendentalTests(ITestOutputHelper output) => _output = output;

    private const string Kernel = @"
kernel void fast_transcendental_test(device uint* b [[buffer(0)]], uint t [[thread_position_in_threadgroup]])
{
    float x = as_type<float>(b[t]);
    float lo = sharpemu_flush_f32_denorm(x);
    float sinCycle = sharpemu_trig_cycle_f32(x, true);
    float cosCycle = sharpemu_trig_cycle_f32(x, false);
    uint o = 1024u + t * 8u;
    b[o + 0u] = as_type<uint>(exp2(lo));
    b[o + 1u] = as_type<uint>(fast::exp2(lo));
    b[o + 2u] = as_type<uint>(log2(lo));
    b[o + 3u] = as_type<uint>(fast::log2(lo));
    b[o + 4u] = as_type<uint>(sin(sinCycle * 6.2831853071795862f));
    b[o + 5u] = as_type<uint>(fast::sin(sinCycle * 6.2831853071795862f));
    b[o + 6u] = as_type<uint>(cos(cosCycle * 6.2831853071795862f));
    b[o + 7u] = as_type<uint>(fast::cos(cosCycle * 6.2831853071795862f));
}
";

    private static readonly string[] OpNames = ["exp2", "log2", "sin", "cos"];

    [Fact]
    public void TranslatorEmitsTheFastFormForTheFourOpcodes()
    {
        foreach (var (opcode, expected) in new[]
        {
            ("VExpF32", "fast::exp2("), ("VLogF32", "fast::log2("),
        })
        {
            var instruction = new Gen5ShaderInstruction(
                0, Gen5ShaderEncoding.Vop1, opcode, [0u],
                [Gen5Operand.Vector(0)], [Gen5Operand.Vector(1)], null);
            var request = Gen5ComputeFixtures.RequestOrThrow(
                new Gen5ShaderProgram(0x1000, [instruction]), ShaderStage.Compute, localSizeX: 1);
            Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
            Assert.Contains(expected, shader.Source, StringComparison.Ordinal);
        }

        foreach (var opcode in new[] { "VSinF32", "VCosF32" })
        {
            var instruction = new Gen5ShaderInstruction(
                0, Gen5ShaderEncoding.Vop1, opcode, [0u],
                [Gen5Operand.Vector(0)], [Gen5Operand.Vector(1)], null);
            var request = Gen5ComputeFixtures.RequestOrThrow(
                new Gen5ShaderProgram(0x1000, [instruction]), ShaderStage.Compute, localSizeX: 1);
            Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
            Assert.DoesNotContain("fast::", shader.Source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FastFormKeepsEveryInputInItsClassAndReportsUlp()
    {
        if (!MetalNative.IsAvailable)
        {
            Assert.False(
                OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1",
                "The required gate cannot run the fast-math test without a Metal device.");
            return;
        }

        var inputs = BuildInputs();
        var prelude = Gen5NggFixtures.CompileOrThrow(Gen5NggFixtures.CullingTriangle()).Source;
        Assert.True(MetalNative.TryCompileLibrary(prelude + Kernel, out var library, out var compileError), compileError);

        var words = new uint[OutputBase + Threads * 8];
        for (var i = 0; i < Threads; i++)
        {
            words[i] = BitConverter.SingleToUInt32Bits(inputs[i]);
        }

        var contents = new byte[words.Length * sizeof(uint)];
        Buffer.BlockCopy(words, 0, contents, 0, contents.Length);
        Assert.True(
            MetalNative.TryExecuteOnBuffer(library, "fast_transcendental_test", contents, Threads, out var result, out var error),
            error);
        Buffer.BlockCopy(result, 0, words, 0, result.Length);

        var worst = new long[OpCount];
        var worstInput = new float[OpCount];
        var changed = new List<string>();
        for (var i = 0; i < Threads; i++)
        {
            for (var op = 0; op < OpCount; op++)
            {
                var precise = BitConverter.UInt32BitsToSingle(words[OutputBase + i * 8 + op * 2]);
                var fast = BitConverter.UInt32BitsToSingle(words[OutputBase + i * 8 + op * 2 + 1]);
                var ulp = UlpDistance(precise, fast);
                if (ulp < 0)
                {
                    if (op >= 2)
                    {
                        _output.WriteLine($"(not used) {OpNames[op]}({inputs[i]:R}) precise={precise:R} fast={fast:R}");
                        continue;
                    }

                    changed.Add($"{OpNames[op]}({inputs[i]:R}) precise={precise:R} fast={fast:R}");
                    continue;
                }

                if (ulp > worst[op])
                {
                    worst[op] = ulp;
                    worstInput[op] = inputs[i];
                }
            }
        }

        for (var op = 0; op < OpCount; op++)
        {
            _output.WriteLine($"{OpNames[op]}: max ulp {worst[op]} at input {worstInput[op]:R}");
        }

        foreach (var line in changed)
        {
            _output.WriteLine("CLASS CHANGE " + line);
        }

        Assert.Empty(changed);
    }

    // The ulp distance between two results of the same class, or -1 when their class
    // differs: NaN against a number, an infinity against anything else, a zero against a
    // non-zero (a flushed denormal result), or a sign flip.
    private static long UlpDistance(float precise, float fast)
    {
        if (float.IsNaN(precise) || float.IsNaN(fast))
        {
            return float.IsNaN(precise) && float.IsNaN(fast) ? 0 : -1;
        }

        if (float.IsInfinity(precise) || float.IsInfinity(fast) || precise == 0f || fast == 0f)
        {
            return precise == fast ? 0 : -1;
        }

        if ((precise < 0f) != (fast < 0f))
        {
            return -1;
        }

        return Math.Abs((long)BitConverter.SingleToInt32Bits(precise) - BitConverter.SingleToInt32Bits(fast));
    }

    private static float[] BuildInputs()
    {
        var list = new List<float>
        {
            0f, -0f, 1f, -1f, 0.5f, -0.5f, 0.25f, 0.75f, 2f, -2f, 10f, -10f, 100f, -100f,
            126f, -126f, 127f, -127f, 128f, -128f, 129f, -149f, -150f, 1e10f, -1e10f,
            8388608f, 8388607.5f, -8388608f, 16777216f, 1e20f, -1e20f,
            float.PositiveInfinity, float.NegativeInfinity, float.NaN,
            float.MaxValue, -float.MaxValue, float.Epsilon, -float.Epsilon,
            BitConverter.UInt32BitsToSingle(0x007FFFFFu), BitConverter.UInt32BitsToSingle(0x80123456u),
            BitConverter.UInt32BitsToSingle(0x00800000u), BitConverter.UInt32BitsToSingle(0x00000400u),
            BitConverter.UInt32BitsToSingle(0x7FC00001u), BitConverter.UInt32BitsToSingle(0xFFC00000u),
        };

        var random = new Random(0x5EED);
        while (list.Count < Threads / 2)
        {
            // Log-uniform magnitudes from 2^-30 to 2^30, either sign.
            var magnitude = (float)Math.Pow(2.0, random.NextDouble() * 60.0 - 30.0);
            list.Add(random.Next(2) == 0 ? magnitude : -magnitude);
        }

        while (list.Count < Threads)
        {
            // The range real shaders feed exp, log and trig: [-8, 8).
            list.Add((float)(random.NextDouble() * 16.0 - 8.0));
        }

        return [.. list];
    }
}
