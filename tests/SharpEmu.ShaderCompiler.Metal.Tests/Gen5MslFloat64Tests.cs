// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>The GFX10 F64 opcodes on Metal, where MSL has no double and the translator
/// lowers them to software binary64. The helpers run on the GPU over thousands of
/// random and edge-case inputs and must match C# double bit for bit; the translated
/// opcodes then run end to end through the decoder with their VOP3 modifiers.
/// Execution needs the exclusive device gate, like the other parity tests.</summary>
public sealed class Gen5MslFloat64Tests(ITestOutputHelper output)
{
    private const ulong DefaultNaN = 0x7FF8000000000000;
    private const uint Threads = 256;
    private const uint Sentinel = 0xABCD1234;

    private static readonly Lazy<(nint Library, string Error)> HelperLibrary = new(CompileHelperLibrary);

    public static IEnumerable<object[]> Operations =>
        Enum.GetNames<Operation>().Select(static name => new object[] { name });

    [Theory]
    [MemberData(nameof(Operations))]
    public void HelpersMatchHostDoublesBitForBit(string name)
    {
        if (!DeviceGateOpen())
        {
            return;
        }

        var operation = Enum.Parse<Operation>(name);
        var (library, compileError) = HelperLibrary.Value;
        Assert.True(library != 0, compileError);

        var inputs = Inputs(operation);
        var data = new byte[(2 + inputs.Count * 4) * sizeof(ulong)];
        var words = new ulong[2 + inputs.Count * 4];
        words[0] = (ulong)operation;
        words[1] = (ulong)inputs.Count;
        for (var index = 0; index < inputs.Count; index++)
        {
            var (a, b, c) = inputs[index];
            words[2 + index * 4] = a;
            words[3 + index * 4] = b;
            words[4 + index * 4] = c;
            words[5 + index * 4] = 0xDEADBEEFDEADBEEF;
        }

        Buffer.BlockCopy(words, 0, data, 0, data.Length);
        Assert.True(
            MetalNative.TryExecuteOnBuffer(library, "f64_parity", data, Threads, out var result, out var error),
            error);

        var mismatches = new StringBuilder();
        var failures = 0;
        for (var index = 0; index < inputs.Count; index++)
        {
            var (a, b, c) = inputs[index];
            var actual = BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan((5 + index * 4) * sizeof(ulong)));
            var expected = Reference(operation, a, b, c);
            if (actual == expected)
            {
                continue;
            }

            if (failures++ < 16)
            {
                mismatches.AppendLine(
                    $"{operation}(0x{a:X16}, 0x{b:X16}, 0x{c:X16}) = 0x{actual:X16}, expected 0x{expected:X16}");
            }
        }

        output.WriteLine($"{operation}: {inputs.Count} inputs, {failures} mismatches");
        Assert.True(failures == 0, $"{failures} of {inputs.Count} mismatched:\n{mismatches}");
    }

    public static IEnumerable<object[]> TranslatedCases => new List<object[]>
    {
        // name, op words (each encoding's VOP3 fields), a, b, c, expected v6, expected v7
        Case("fma", Vop3(0x14C, 6, 256, 258, 260), 1.5, 2.25, 10.0, 1.5 * 2.25 + 10.0),
        Case("fma abs src0, neg src2", Vop3(0x14C, 6, 256, 258, 260, abs: 1, neg: 4), -1.5, 2.0, 1.0, 2.0),
        Case("fma single rounding", Vop3(0x14C, 6, 256, 258, 260),
            1.0 + Math.Pow(2, -30), 1.0 - Math.Pow(2, -30), -1.0,
            Math.FusedMultiplyAdd(1.0 + Math.Pow(2, -30), 1.0 - Math.Pow(2, -30), -1.0)),
        Case("mul clamp", Vop3(0x165, 6, 256, 258, 0, clamp: true), 0.75, 2.0, 0.0, 1.0),
        Case("mul clamp negative", Vop3(0x165, 6, 256, 258, 0, clamp: true), -0.75, 2.0, 0.0, 0.0),
        Case("mul clamp NaN", Vop3(0x165, 6, 256, 258, 0, clamp: true), double.NaN, 2.0, 0.0, 0.0),
        Case("mul inline 4.0", Vop3(0x165, 6, 256, 246, 0), 1.25, 0.0, 0.0, 5.0),
        Case("mul inline -3", Vop3(0x165, 6, 256, 195, 0), 1.25, 0.0, 0.0, -3.75),
        Case("mul literal high dword", Vop3(0x165, 6, 256, 255, 0, literal: 0x40080000), 1.25, 0.0, 0.0, 3.75),
        Case("rcp", Vop1(0x2F, 6, 256), 3.0, 0.0, 0.0, 1.0 / 3.0),
        Case("rcp vop3 neg", Vop3(0x1AF, 6, 256, 0, 0, neg: 1), 4.0, 0.0, 0.0, -0.25),
        Case("rsq", Vop1(0x31, 6, 256), 3.0, 0.0, 0.0, 1.0 / Math.Sqrt(3.0)),
        Case("sqrt", Vop1(0x34, 6, 256), 2.0, 0.0, 0.0, Math.Sqrt(2.0)),
        Case("sqrt denormal", Vop1(0x34, 6, 256), double.Epsilon * 3, 0.0, 0.0, Math.Sqrt(double.Epsilon * 3)),
        Case("cvt_f64_i32", Vop1(0x04, 6, 256), BitConverter.UInt64BitsToDouble(0xFFFFFFF9), 0.0, 0.0, -7.0),
        Case("cvt_f64_u32", Vop1(0x16, 6, 256), BitConverter.UInt64BitsToDouble(0xFFFFFFFF), 0.0, 0.0, 4294967295.0),
        new object[]
        {
            "cvt_f32_f64 keeps v7", Vop1(0x0F, 6, 256), 1.0 / 3.0, 0.0, 0.0,
            BitConverter.SingleToUInt32Bits((float)(1.0 / 3.0)), Sentinel,
        },
        new object[]
        {
            "cvt_f32_f64 clamp", Vop3(0x18F, 6, 256, 0, 0, clamp: true), 7.0, 0.0, 0.0,
            BitConverter.SingleToUInt32Bits(1.0f), Sentinel,
        },
        Case("add cancels exactly", Vop3(0x164, 6, 256, 258, 0), 1.0 + Math.Pow(2, -52), -1.0, 0.0, Math.Pow(2, -52)),
        Case("add neg src1", Vop3(0x164, 6, 256, 258, 0, neg: 2), 0.1, 0.2, 0.0, 0.1 - 0.2),
        Case("min -0 below +0", Vop3(0x166, 6, 256, 258, 0), 0.0, -0.0, 0.0, -0.0),
        Case("max quiet NaN yields the other", Vop3(0x167, 6, 256, 258, 0), double.NaN, 3.0, 0.0, 3.0),
        Case("ldexp inline 2", Vop3(0x168, 6, 256, 130, 0), 1.5, 0.0, 0.0, 6.0),
        Case("ldexp to a denormal tie", Vop3(0x168, 6, 256, 255, 0, literal: unchecked((uint)-1075)), 3.0, 0.0, 0.0,
            BitConverter.UInt64BitsToDouble(2)),
        Case("trunc", Vop1(0x17, 6, 256), -2.7, 0.0, 0.0, -2.0),
        Case("ceil keeps the sign of zero", Vop1(0x18, 6, 256), -0.5, 0.0, 0.0, -0.0),
        Case("rndne tie to even", Vop1(0x19, 6, 256), 2.5, 0.0, 0.0, 2.0),
        Case("floor", Vop1(0x1A, 6, 256), -0.5, 0.0, 0.0, -1.0),
        Case("fract", Vop1(0x3E, 6, 256), -1.25, 0.0, 0.0, 0.75),
        Case("frexp_mant", Vop1(0x3D, 6, 256), 12.0, 0.0, 0.0, 0.75),
        Case("frexp_mant vop3 abs", Vop3(0x1BD, 6, 256, 0, 0, abs: 1), -12.0, 0.0, 0.0, 0.75),
        new object[] { "frexp_exp keeps v7", Vop1(0x3C, 6, 256), 12.0, 0.0, 0.0, 4u, Sentinel },
        new object[] { "cvt_i32_f64 truncates", Vop1(0x03, 6, 256), -3.99, 0.0, 0.0, 0xFFFFFFFDu, Sentinel },
        new object[] { "cvt_u32_f64 saturates", Vop1(0x15, 6, 256), 5e9, 0.0, 0.0, 0xFFFFFFFFu, Sentinel },
        Case("cvt_f64_f32", Vop1(0x10, 6, 256), BitConverter.UInt64BitsToDouble(0x3FC00000), 0.0, 0.0, 1.5),
        Case("cvt_f64_f32 vop3 neg", Vop3(0x190, 6, 256, 0, 0, neg: 1), BitConverter.UInt64BitsToDouble(0x3FC00000), 0.0, 0.0, -1.5),
        Case("trig_preop segment 0", Vop3(0x174, 6, 256, 128, 0), 1.0, 0.0, 0.0, BitConverter.UInt64BitsToDouble(0x3FE45F306DC9C882)),
        Case("div_fixup sign", Vop3(0x160, 6, 256, 258, 260), 7.0, -2.0, 3.0, -7.0),
        Case("div_fixup x/0", Vop3(0x160, 6, 256, 258, 260), 0.5, -0.0, 3.0, double.NegativeInfinity),
        Case("div_scale scales a denormal denominator", Vop3(0x16E, 6, 256, 256, 258, abs: 0x6A),
            Math.Pow(2, -1030), 1.0, 0.0, Math.Pow(2, -902)),
        Case("div_fmas without vcc", Vop3(0x170, 6, 256, 258, 260), 1.5, 2.0, 0.25, 3.25),
        new object[]
        {
            "div_fixup_f32", Vop3(0x15F, 6, 256, 258, 260),
            BitConverter.UInt64BitsToDouble(0x40E00000), BitConverter.UInt64BitsToDouble(0xC0000000),
            BitConverter.UInt64BitsToDouble(0x40400000), 0xC0E00000u, Sentinel,
        },
        new object[]
        {
            "div_fixup_f16 keeps the high half", Vop3(0x35F, 6, 256, 258, 260),
            BitConverter.UInt64BitsToDouble(0x4700), BitConverter.UInt64BitsToDouble(0xC000),
            BitConverter.UInt64BitsToDouble(0x4200), (Sentinel & 0xFFFF0000u) | 0xC700u, Sentinel,
        },
        // v_cmp(x) then a select into v6: 1 when the compare held.
        new object[] { "cmp_lt", (uint[])[Vopc(0x21), .. Select(106)], 1.0, 2.0, 0.0, 1u, Sentinel },
        new object[] { "cmp_eq signed zeros", (uint[])[Vopc(0x22), .. Select(106)], 0.0, -0.0, 0.0, 1u, Sentinel },
        new object[] { "cmp_nge NaN", (uint[])[Vopc(0x29), .. Select(106)], double.NaN, 1.0, 0.0, 1u, Sentinel },
        new object[] { "cmp_lg NaN", (uint[])[Vopc(0x25), .. Select(106)], double.NaN, 1.0, 0.0, 0u, Sentinel },
        new object[]
        {
            "cmp_class e64 to an SGPR", (uint[])[.. Vop3(0xA8, 12, 256, 255, 0, literal: 0x80), .. Select(12)],
            Math.Pow(2, -1060), 0.0, 0.0, 1u, Sentinel,
        },
        new object[]
        {
            "cmpx_gt passes", (uint[])[Vopc(0x34), 0x7E0C0281, 0xBEFE03C1], 3.0, 2.0, 0.0, 1u, Sentinel,
        },
        new object[]
        {
            "cmpx_gt masks", (uint[])[Vopc(0x34), 0x7E0C0281, 0xBEFE03C1], 1.0, 2.0, 0.0, Sentinel, Sentinel,
        },
    };

    // omod scales an f64 result only while FLOAT_MODE flushes f16/f64 output denormals
    // (bit 7 clear); 0x40 keeps input denormals and flushes output ones.
    public static IEnumerable<object[]> OutputModifierCases => new List<object[]>
    {
        Case("mul omod x2", Vop3(0x165, 6, 256, 258, 0, omod: 1), 0.3, 1.0, 0.0, 0.6),
        Case("mul omod x0.5", Vop3(0x165, 6, 256, 258, 0, omod: 3), 3.0, 5.0, 0.0, 7.5),
    };

    [Theory]
    [MemberData(nameof(OutputModifierCases))]
    public void OutputModifierScalesWhenFloat64OutputDenormalsFlush(string name, uint[] op, double a, double b, double c,
        uint expectedLow, uint expectedHigh)
    {
        var shader = CompileTranslated(op, a, b, c, store: 6, floatMode: 0x40);
        if (!DeviceGateOpen())
        {
            return;
        }

        var result = Execute(shader);
        output.WriteLine($"{name}: 0x{result.High:X8}{result.Low:X8}");
        Assert.Equal(expectedHigh, result.High);
        Assert.Equal(expectedLow, result.Low);
    }

    // v_cmp(x)_*_f64 vcc, v[0:1], v[2:3]
    private static uint Vopc(uint opcode) => 0x7C000000 | (opcode << 17) | (2u << 9) | 256u;

    // v_cndmask_b32_e64 v6, 0, 1, mask
    private static uint[] Select(uint mask) => Vop3(0x101, 6, 128, 129, mask);

    [Theory]
    [MemberData(nameof(TranslatedCases))]
    public void TranslatedOpcodesExecuteOnMetal(string name, uint[] op, double a, double b, double c,
        uint expectedLow, uint expectedHigh)
    {
        var shader = CompileTranslated(op, a, b, c, store: 6);
        Assert.Contains("sharpemu_f64_", shader.Source, StringComparison.Ordinal);
        if (!DeviceGateOpen())
        {
            return;
        }

        var result = Execute(shader);
        output.WriteLine($"{name}: 0x{result.High:X8}{result.Low:X8}");
        Assert.Equal(expectedHigh, result.High);
        Assert.Equal(expectedLow, result.Low);
    }

    [Fact]
    public void DestinationPairOverlappingItsSourcesReadsThemFirst()
    {
        // v_fma_f64 v[2:3], v[0:1], v[2:3], v[4:5]
        var shader = CompileTranslated(Vop3(0x14C, 2, 256, 258, 260), 3.0, 0.5, 0.25, store: 2);
        if (!DeviceGateOpen())
        {
            return;
        }

        var result = Execute(shader);
        Assert.Equal(BitConverter.DoubleToUInt64Bits(1.75), ((ulong)result.High << 32) | result.Low);
    }

    [Fact]
    public void OnlyShadersUsingFloat64CarryTheSoftwareHelpers()
    {
        Assert.Contains(
            "sharpemu_f64_mul(",
            CompileTranslated(Vop3(0x165, 6, 256, 258, 0), 1.0, 1.0, 0.0, store: 6).Source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sharpemu_f64_",
            Gen5ComputeFixtures.CompileRequestOrThrow(Gen5ComputeFixtures.ExecStore).Source,
            StringComparison.Ordinal);
    }

    private enum Operation
    {
        Mul,
        Fma,
        Rcp,
        Rsq,
        Sqrt,
        CvtF32F64,
        CvtF64I32,
        CvtF64U32,
        Add,
        MinIeee,
        MaxIeee,
        MinLegacy,
        MaxLegacy,
        Ldexp,
        Trunc,
        Floor,
        Ceil,
        Rndne,
        Fract,
        FrexpMant,
        FrexpExp,
        CvtF64F32,
        CvtI32F64,
        CvtU32F64,
        Lt,
        Eq,
        Unordered,
        Class,
        TrigPreop,
        DivFixup,
        FmaF32,
    }

    private bool DeviceGateOpen()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Numerical execution requires the exclusive Metal device gate.");
            return false;
        }

        Assert.True(MetalNative.IsAvailable, "The required execution gate needs a Metal device.");
        return true;
    }

    private static (nint, string) CompileHelperLibrary()
    {
        using var stream = typeof(Gen5MslTranslator).Assembly.GetManifestResourceStream(
            "SharpEmu.ShaderCompiler.Metal.Templates.float64_prelude.msl")
            ?? throw new InvalidOperationException("float64_prelude.msl is not embedded");
        using var reader = new StreamReader(stream);
        var source = $$"""
            #include <metal_stdlib>
            using namespace metal;
            {{reader.ReadToEnd()}}
            kernel void f64_parity(device ulong* data [[buffer(0)]], uint lane [[thread_position_in_grid]])
            {
                uint operation = (uint)data[0];
                uint count = (uint)data[1];
                for (uint index = lane; index < count; index += {{Threads}}u)
                {
                    device ulong* record = data + 2 + index * 4;
                    ulong a = record[0];
                    ulong b = record[1];
                    ulong c = record[2];
                    ulong z = 0ul;
                    switch (operation)
                    {
                        case {{(int)Operation.Mul}}: z = sharpemu_f64_mul(a, b); break;
                        case {{(int)Operation.Fma}}: z = sharpemu_f64_fma(a, b, c); break;
                        case {{(int)Operation.Rcp}}: z = sharpemu_f64_div(0x3FF0000000000000ul, a); break;
                        case {{(int)Operation.Rsq}}: z = sharpemu_f64_div(0x3FF0000000000000ul, sharpemu_f64_sqrt(a)); break;
                        case {{(int)Operation.Sqrt}}: z = sharpemu_f64_sqrt(a); break;
                        case {{(int)Operation.CvtF32F64}}: z = sharpemu_f64_to_f32(a); break;
                        case {{(int)Operation.CvtF64I32}}: z = sharpemu_f64_from_i32(as_type<int>((uint)a)); break;
                        case {{(int)Operation.CvtF64U32}}: z = sharpemu_f64_from_u32((uint)a, 0u); break;
                        case {{(int)Operation.Add}}: z = sharpemu_f64_add(a, b); break;
                        case {{(int)Operation.MinIeee}}: z = sharpemu_f64_min(a, b, true); break;
                        case {{(int)Operation.MaxIeee}}: z = sharpemu_f64_max(a, b, true); break;
                        case {{(int)Operation.MinLegacy}}: z = sharpemu_f64_min(a, b, false); break;
                        case {{(int)Operation.MaxLegacy}}: z = sharpemu_f64_max(a, b, false); break;
                        case {{(int)Operation.Ldexp}}: z = sharpemu_f64_ldexp(a, as_type<int>((uint)b)); break;
                        case {{(int)Operation.Trunc}}: z = sharpemu_f64_round_integral(a, 0u); break;
                        case {{(int)Operation.Floor}}: z = sharpemu_f64_round_integral(a, 1u); break;
                        case {{(int)Operation.Ceil}}: z = sharpemu_f64_round_integral(a, 2u); break;
                        case {{(int)Operation.Rndne}}: z = sharpemu_f64_round_integral(a, 3u); break;
                        case {{(int)Operation.Fract}}: z = sharpemu_f64_fract(a); break;
                        case {{(int)Operation.FrexpMant}}: z = sharpemu_f64_frexp_mant(a); break;
                        case {{(int)Operation.FrexpExp}}: z = as_type<uint>(sharpemu_f64_frexp_exp(a)); break;
                        case {{(int)Operation.CvtF64F32}}: z = sharpemu_f64_from_f32((uint)a); break;
                        case {{(int)Operation.CvtI32F64}}: z = sharpemu_f64_to_i32(a); break;
                        case {{(int)Operation.CvtU32F64}}: z = sharpemu_f64_to_u32(a); break;
                        case {{(int)Operation.Lt}}: z = sharpemu_f64_lt(a, b) ? 1ul : 0ul; break;
                        case {{(int)Operation.Eq}}: z = sharpemu_f64_eq(a, b) ? 1ul : 0ul; break;
                        case {{(int)Operation.Unordered}}: z = sharpemu_f64_unordered(a, b) ? 1ul : 0ul; break;
                        case {{(int)Operation.Class}}: z = sharpemu_f64_class(a, (uint)b) ? 1ul : 0ul; break;
                        case {{(int)Operation.TrigPreop}}: z = sharpemu_f64_trig_preop(a, (uint)b); break;
                        case {{(int)Operation.DivFixup}}: z = sharpemu_div_fixup(a, b, c, 64u, 52u, -1075, 1024); break;
                        case {{(int)Operation.FmaF32}}: z = sharpemu_f32_div_fmas((uint)a, (uint)b, (uint)c, false); break;
                    }
                    record[3] = z;
                }
            }
            """;
        return MetalNative.TryCompileLibrary(source, out var library, out var error)
            ? (library, string.Empty)
            : (0, error);
    }

    // ---- reference ----

    private static ulong Reference(Operation operation, ulong a, ulong b, ulong c)
    {
        if (ExtendedReference(operation, a, b, c) is { } extended)
        {
            return extended;
        }

        var x = BitConverter.UInt64BitsToDouble(a);
        var y = BitConverter.UInt64BitsToDouble(b);
        var z = BitConverter.UInt64BitsToDouble(c);
        switch (operation)
        {
            case Operation.CvtF32F64:
            {
                var narrowed = (float)x;
                return float.IsNaN(narrowed)
                    ? (uint)((a >> 63) << 31) | 0x7FC00000u | (uint)((a & 0x000FFFFFFFFFFFFF) >> 29)
                    : BitConverter.SingleToUInt32Bits(narrowed);
            }
            case Operation.CvtF64I32:
                return BitConverter.DoubleToUInt64Bits(unchecked((int)(uint)a));
            case Operation.CvtF64U32:
                return BitConverter.DoubleToUInt64Bits((uint)a);
        }

        var value = operation switch
        {
            Operation.Mul => x * y,
            Operation.Fma => Math.FusedMultiplyAdd(x, y, z),
            Operation.Rcp => 1.0 / x,
            Operation.Rsq => 1.0 / Math.Sqrt(x),
            _ => Math.Sqrt(x),
        };
        if (!double.IsNaN(value))
        {
            return BitConverter.DoubleToUInt64Bits(value);
        }

        // NaN payloads are not portable across host ISAs (ARM64 FMADD checks the
        // addend first; x86 and ARM disagree on the default NaN's sign), so a NaN
        // result is held to the rule the helpers document: the first NaN source,
        // quieted, else the default NaN.
        ReadOnlySpan<ulong> sources = operation switch
        {
            Operation.Mul => [a, b],
            Operation.Fma => [a, b, c],
            _ => [a],
        };
        foreach (var source in sources)
        {
            if (double.IsNaN(BitConverter.UInt64BitsToDouble(source)))
            {
                return source | 0x0008000000000000;
            }
        }

        return DefaultNaN;
    }

    private static ulong? ExtendedReference(Operation operation, ulong a, ulong b, ulong c)
    {
        var x = BitConverter.UInt64BitsToDouble(a);
        var y = BitConverter.UInt64BitsToDouble(b);
        return operation switch
        {
            Operation.Add => Arithmetic(x + y, a, b),
            Operation.MinIeee => MinMax(a, b, ieee: true, maximum: false),
            Operation.MaxIeee => MinMax(a, b, ieee: true, maximum: true),
            Operation.MinLegacy => MinMax(a, b, ieee: false, maximum: false),
            Operation.MaxLegacy => MinMax(a, b, ieee: false, maximum: true),
            Operation.Ldexp => double.IsNaN(x) ? a | QuietBit : Bits(Math.ScaleB(x, unchecked((int)(uint)b))),
            Operation.Trunc => Integral(a, Math.Truncate),
            Operation.Floor => Integral(a, Math.Floor),
            Operation.Ceil => Integral(a, Math.Ceiling),
            Operation.Rndne => Integral(a, value => Math.Round(value, MidpointRounding.ToEven)),
            Operation.Fract => Fract(a),
            Operation.FrexpMant => double.IsNaN(x) || double.IsInfinity(x) || x == 0 ? a : Bits(Math.ScaleB(x, -(Math.ILogB(x) + 1))),
            Operation.FrexpExp => double.IsNaN(x) || double.IsInfinity(x) || x == 0 ? 0 : (uint)(Math.ILogB(x) + 1),
            Operation.CvtF64F32 => WidenFloat32((uint)a),
            Operation.CvtI32F64 => double.IsNaN(x) ? 0 : (uint)(int)Math.Clamp(Math.Truncate(x), int.MinValue, int.MaxValue),
            Operation.CvtU32F64 => double.IsNaN(x) ? 0 : (uint)Math.Clamp(Math.Truncate(x), 0, uint.MaxValue),
            Operation.Lt => x < y ? 1ul : 0ul,
            Operation.Eq => x == y ? 1ul : 0ul,
            Operation.Unordered => double.IsNaN(x) || double.IsNaN(y) ? 1ul : 0ul,
            Operation.Class => ((uint)b >> ClassBit(a) & 1) != 0 ? 1ul : 0ul,
            Operation.TrigPreop => TrigPreop(a, (uint)b),
            Operation.DivFixup => DivFixup(a, b, c),
            Operation.FmaF32 => FusedFloat32((uint)a, (uint)b, (uint)c),
            _ => null,
        };
    }

    private const ulong QuietBit = 0x0008000000000000;

    private static ulong Bits(double value) => BitConverter.DoubleToUInt64Bits(value);

    // A NaN result follows the helpers' rule: the first NaN source quieted, else the default NaN.
    private static ulong Arithmetic(double value, params ulong[] sources)
    {
        if (!double.IsNaN(value))
        {
            return Bits(value);
        }

        foreach (var source in sources)
        {
            if (double.IsNaN(BitConverter.UInt64BitsToDouble(source)))
            {
                return source | QuietBit;
            }
        }

        return DefaultNaN;
    }

    // ISA 12.12 V_MIN_F64 / V_MAX_F64, case by case.
    private static ulong MinMax(ulong a, ulong b, bool ieee, bool maximum)
    {
        static bool IsSignaling(ulong value) =>
            double.IsNaN(BitConverter.UInt64BitsToDouble(value)) && (value & QuietBit) == 0;
        var x = BitConverter.UInt64BitsToDouble(a);
        var y = BitConverter.UInt64BitsToDouble(b);
        if (ieee && IsSignaling(a))
        {
            return a | QuietBit;
        }

        if (ieee && IsSignaling(b))
        {
            return b | QuietBit;
        }

        if (double.IsNaN(x))
        {
            return b;
        }

        if (double.IsNaN(y))
        {
            return a;
        }

        if (x == 0 && y == 0)
        {
            // -0 orders below +0.
            var aNegative = double.IsNegative(x);
            return maximum == aNegative ? b : a;
        }

        return maximum ? (x >= y ? a : b) : (x < y ? a : b);
    }

    private static ulong Integral(ulong a, Func<double, double> round)
    {
        var x = BitConverter.UInt64BitsToDouble(a);
        return double.IsNaN(x) ? a | QuietBit : Bits(round(x));
    }

    private static ulong Fract(ulong a)
    {
        var x = BitConverter.UInt64BitsToDouble(a);
        if (double.IsNaN(x))
        {
            return a | QuietBit;
        }

        if (double.IsInfinity(x))
        {
            return DefaultNaN;
        }

        var value = x - Math.Floor(x);
        return value >= 1.0 ? 0x3FEFFFFFFFFFFFFF : Bits(value);
    }

    private static ulong WidenFloat32(uint bits)
    {
        var value = BitConverter.UInt32BitsToSingle(bits);
        return float.IsNaN(value)
            ? ((ulong)(bits >> 31) << 63) | 0x7FF8000000000000 | ((ulong)(bits & 0x007FFFFF) << 29)
            : Bits(value);
    }

    private static int ClassBit(ulong a)
    {
        var x = BitConverter.UInt64BitsToDouble(a);
        var negative = (a >> 63) != 0;
        if (double.IsNaN(x))
        {
            return (a & QuietBit) != 0 ? 1 : 0;
        }

        if (double.IsInfinity(x))
        {
            return negative ? 2 : 9;
        }

        if (x == 0)
        {
            return negative ? 5 : 6;
        }

        if (double.IsSubnormal(x))
        {
            return negative ? 4 : 7;
        }

        return negative ? 3 : 8;
    }

    // ISA 12.12 V_TRIG_PREOP_F64 over the 2/pi bits LLVM folds it with (computed here
    // from Machin's formula, not copied), scaled toward zero.
    private static ulong TrigPreop(ulong a, uint segment)
    {
        var exponent = (int)((a >> 52) & 0x7FF);
        var shift = (int)(segment & 31) * 53 + (exponent > 1077 ? exponent - 1077 : 0);
        if (shift >= 1201)
        {
            return 0;
        }

        // Bits [shift, shift + 53) of the 1201-bit table, as an integer; bits past it read 0.
        var value = (ulong)(((TwoOverPiBits << 53) >> (1201 - shift)) & ((System.Numerics.BigInteger.One << 53) - 1));
        if (value == 0)
        {
            return 0;
        }

        var scale = -53 - shift + (exponent >= 1968 ? 128 : 0);
        // value * 2^scale toward zero: exact when normal; a denormal keeps whole 2^-1074 steps.
        var exact = new System.Numerics.BigInteger(value);
        if (scale + 1074 >= 0)
        {
            var leading = 63 - System.Numerics.BitOperations.LeadingZeroCount(value);
            if (leading + scale + 1023 >= 1)
            {
                return Bits(Math.ScaleB(value, scale));
            }

            return (ulong)(exact << (scale + 1074));
        }

        return -(scale + 1074) >= 64 ? 0 : value >> -(scale + 1074);
    }

    // 2/pi * 2^1201, truncated: the ISA's 1201 fraction bits (12.12 V_TRIG_PREOP_F64).
    private static readonly System.Numerics.BigInteger TwoOverPiBits = ComputeTwoOverPi(1201);

    private static System.Numerics.BigInteger ComputeTwoOverPi(int bits)
    {
        // pi = 16 atan(1/5) - 4 atan(1/239), in fixed point with guard bits.
        var guard = 64;
        var one = System.Numerics.BigInteger.One << (bits + guard + 8);
        System.Numerics.BigInteger Atan(int inverse)
        {
            var sum = System.Numerics.BigInteger.Zero;
            var power = one / inverse;
            var square = (System.Numerics.BigInteger)inverse * inverse;
            for (var n = 0; !power.IsZero; n++)
            {
                var term = power / (2 * n + 1);
                sum += n % 2 == 0 ? term : -term;
                power /= square;
            }

            return sum;
        }

        var pi = 16 * Atan(5) - 4 * Atan(239);
        // 2/pi * 2^bits = 2 * one * 2^bits / pi / one... keep it as (2 << bits) * one / pi.
        return ((System.Numerics.BigInteger.One << (bits + 1)) * one) / pi;
    }

    // ISA 12.12 V_DIV_FIXUP_F64, with the overflow case read as the mirror of the underflow one.
    private static ulong DivFixup(ulong q, ulong d, ulong n)
    {
        var denominator = BitConverter.UInt64BitsToDouble(d);
        var numerator = BitConverter.UInt64BitsToDouble(n);
        var sign = (d ^ n) & 0x8000000000000000;
        if (double.IsNaN(numerator))
        {
            return n | QuietBit;
        }

        if (double.IsNaN(denominator))
        {
            return d | QuietBit;
        }

        if ((denominator == 0 && numerator == 0) || (double.IsInfinity(denominator) && double.IsInfinity(numerator)))
        {
            return 0xFFF8000000000000;
        }

        if (denominator == 0 || double.IsInfinity(numerator))
        {
            return sign | 0x7FF0000000000000;
        }

        if (double.IsInfinity(denominator) || numerator == 0)
        {
            return sign;
        }

        var difference = (int)((n >> 52) & 0x7FF) - (int)((d >> 52) & 0x7FF);
        if (difference < -1075)
        {
            return sign;
        }

        return difference > 1024 ? sign | 0x7FF0000000000000 : sign | (q & 0x7FFFFFFFFFFFFFFF);
    }

    private static ulong FusedFloat32(uint a, uint b, uint c)
    {
        var value = MathF.FusedMultiplyAdd(
            BitConverter.UInt32BitsToSingle(a), BitConverter.UInt32BitsToSingle(b), BitConverter.UInt32BitsToSingle(c));
        if (!float.IsNaN(value))
        {
            return BitConverter.SingleToUInt32Bits(value);
        }

        foreach (var source in new[] { a, b, c })
        {
            if (float.IsNaN(BitConverter.UInt32BitsToSingle(source)))
            {
                return source | 0x00400000u;
            }
        }

        return 0x7FC00000u;
    }

    // ---- inputs ----

    private static readonly ulong[] Specials =
    [
        0x0000000000000000, 0x8000000000000000, // +-0
        0x7FF0000000000000, 0xFFF0000000000000, // +-inf
        0x7FF8000000000000, 0xFFF8000000000123, // quiet NaNs
        0x7FF0000000000001, 0xFFF4000000000000, // signaling NaNs
        0x0000000000000001, 0x8000000000000001, // +-min denormal
        0x000FFFFFFFFFFFFF, 0x800FFFFFFFFFFFFF, // +-max denormal
        0x0008000000000000, 0x0000000080000001, // mid denormals
        0x0010000000000000, 0x8010000000000000, // +-min normal
        0x7FEFFFFFFFFFFFFF, 0xFFEFFFFFFFFFFFFF, // +-max
        0x3FF0000000000000, 0xBFF0000000000000, // +-1
        0x3FF0000000000001, 0x3FEFFFFFFFFFFFFF, // 1 +- ulp
        0x3FE0000000000000, 0x4000000000000000, // 0.5, 2
        0x7FE0000000000000, 0x0020000000000000, // 2^1023, 2^-1021
        0x3CA0000000000000, 0x4340000000000000, // 2^-53, 2^53
        0x400921FB54442D18, 0xC00921FB54442D18, // +-pi
    ];

    private static List<(ulong A, ulong B, ulong C)> Inputs(Operation operation)
    {
        var random = new Random(0x5EED0000 + (int)operation);
        var inputs = new List<(ulong, ulong, ulong)>();
        switch (operation)
        {
            case Operation.Mul:
                foreach (var a in Specials)
                {
                    foreach (var b in Specials)
                    {
                        inputs.Add((a, b, 0));
                    }
                }

                for (var i = 0; i < 1500; i++)
                {
                    inputs.Add((Bits(random), Bits(random), 0));
                }

                for (var i = 0; i < 1500; i++)
                {
                    // 27-bit significands: the 54-bit exact product often lands on a tie.
                    inputs.Add((Short(random, 900, 1150, 27), Short(random, 900, 1150, 27), 0));
                }

                for (var i = 0; i < 1500; i++)
                {
                    // Products straddling the denormal range and the underflow to zero.
                    var exponentA = random.Next(1, 1024);
                    var target = random.Next(-1080, -1010);
                    var exponentB = Math.Clamp(target + 1023 - exponentA + 1023, 0, 2046);
                    inputs.Add((Short(random, exponentA, exponentA, random.Next(1, 53)), Short(random, exponentB, exponentB, 27), 0));
                }

                for (var i = 0; i < 2000; i++)
                {
                    // Sparse significands with one deep bit, landing in the denormal
                    // range: ties and near-ties decided by bits shifted far out.
                    var exponentA = random.Next(1, 1024);
                    var exponentB = Math.Clamp(random.Next(-1078, -1018) + 2046 - exponentA, 1, 2046);
                    var a = Short(random, exponentA, exponentA, random.Next(0, 9));
                    var b = Short(random, exponentB, exponentB, random.Next(0, 9)) | (1ul << random.Next(0, 44));
                    inputs.Add((a, b, 0));
                }

                for (var i = 0; i < 1000; i++)
                {
                    // Products at the overflow boundary.
                    var exponentA = random.Next(1023, 2047);
                    var exponentB = Math.Clamp(random.Next(1020, 1026) + 1023 - exponentA + 1023, 1, 2046);
                    inputs.Add((Normal(random, exponentA, exponentA), Normal(random, exponentB, exponentB), 0));
                }

                break;
            case Operation.Fma:
                foreach (var a in Specials)
                {
                    foreach (var b in Specials)
                    {
                        inputs.Add((a, b, Specials[random.Next(Specials.Length)]));
                    }
                }

                for (var i = 0; i < 1000; i++)
                {
                    inputs.Add((Bits(random), Bits(random), Bits(random)));
                }

                for (var i = 0; i < 2000; i++)
                {
                    // Cancellation: c within a few ulps of -(a * b).
                    var a = Normal(random, 600, 1400);
                    var b = Normal(random, 600, 1400);
                    var product = BitConverter.DoubleToUInt64Bits(
                        BitConverter.UInt64BitsToDouble(a) * BitConverter.UInt64BitsToDouble(b));
                    var c = (product ^ 0x8000000000000000) + (ulong)(long)random.Next(-3, 4);
                    inputs.Add((a, b, c));
                }

                for (var i = 0; i < 1500; i++)
                {
                    // c at every alignment against the product, short significands for ties.
                    var a = Short(random, 950, 1100, 27);
                    var b = Short(random, 950, 1100, 27);
                    var exponent = (int)((a >> 52) & 0x7FF) + (int)((b >> 52) & 0x7FF) - 1023 + random.Next(-120, 120);
                    var c = Short(random, Math.Clamp(exponent, 0, 2046), Math.Clamp(exponent, 0, 2046), random.Next(1, 53));
                    inputs.Add((a, b, c));
                }

                for (var i = 0; i < 1500; i++)
                {
                    // Results in and around the denormal range.
                    var exponentA = random.Next(1, 1024);
                    var exponentB = Math.Clamp(random.Next(-1080, -1000) + 2046 - exponentA, 0, 2046);
                    inputs.Add((
                        Normal(random, exponentA, exponentA),
                        Short(random, exponentB, exponentB, 40),
                        Short(random, 0, 3, random.Next(1, 53))));
                }

                for (var i = 0; i < 1000; i++)
                {
                    // A product on a tie (an odd significand times 1.5 needs 54 bits),
                    // nudged off it by an addend far below the rounding position:
                    // only the sticky bit decides.
                    var a = Normal(random, 900, 1100) | 1;
                    var b = ((ulong)random.Next(2) << 63) | ((ulong)random.Next(900, 1100) << 52) | 0x0008000000000000;
                    var exponent = (int)((a >> 52) & 0x7FF) + (int)((b >> 52) & 0x7FF) - 1023 - random.Next(56, 400);
                    inputs.Add((a, b, Normal(random, Math.Max(exponent, 1), Math.Max(exponent, 1))));
                }

                for (var i = 0; i < 1000; i++)
                {
                    // The mirror case: c with a product just over or under half its ulp.
                    var exponent = random.Next(200, 1800);
                    var c = Short(random, exponent, exponent, random.Next(0, 53));
                    var a = ((ulong)random.Next(2) << 63) | ((ulong)(exponent - 53) << 52) |
                        (random.Next(2) == 0 ? 0 : 1ul << random.Next(0, 52));
                    inputs.Add((a, 0x3FF0000000000000, c));
                }

                for (var i = 0; i < 500; i++)
                {
                    // Overflow boundary with a large addend.
                    var exponentA = random.Next(1023, 2047);
                    var exponentB = Math.Clamp(random.Next(1021, 1025) + 2046 - exponentA, 1, 2046);
                    inputs.Add((Normal(random, exponentA, exponentA), Normal(random, exponentB, exponentB), Normal(random, 2040, 2046)));
                }

                break;
            case Operation.Rcp:
            case Operation.Rsq:
            case Operation.Sqrt:
                foreach (var a in Specials)
                {
                    inputs.Add((a, 0, 0));
                }

                for (var i = 0; i < 2000; i++)
                {
                    inputs.Add((Bits(random), 0, 0));
                }

                for (var i = 0; i < 1500; i++)
                {
                    inputs.Add((Short(random, 0, 0, random.Next(1, 53)) & 0x7FFFFFFFFFFFFFFF, 0, 0));
                }

                for (var i = 0; i < 2000; i++)
                {
                    // The whole exponent range, rcp's denormal results included.
                    inputs.Add((Normal(random, 1, 2046) & 0x7FFFFFFFFFFFFFFF, 0, 0));
                }

                for (var i = 0; i < 500; i++)
                {
                    // Exact squares.
                    var root = BitConverter.UInt64BitsToDouble(Short(random, 600, 1400, 26) & 0x7FFFFFFFFFFFFFFF);
                    inputs.Add((BitConverter.DoubleToUInt64Bits(root * root), 0, 0));
                }

                break;
            case Operation.CvtF32F64:
                foreach (var a in Specials)
                {
                    inputs.Add((a, 0, 0));
                }

                for (var i = 0; i < 1500; i++)
                {
                    inputs.Add((Bits(random), 0, 0));
                }

                for (var i = 0; i < 2500; i++)
                {
                    // The F32 range, its denormals and both boundaries.
                    inputs.Add((Normal(random, 850, 1160), 0, 0));
                }

                for (var i = 0; i < 2000; i++)
                {
                    // Exact ties and near-ties at the F32 rounding position, normal and denormal.
                    var exponent = random.Next(860, 1152);
                    var keep = exponent >= 897 ? 23 : Math.Max(0, 23 - (897 - exponent));
                    var tie = 1ul << (52 - keep - 1);
                    var bits = Short(random, exponent, exponent, keep) | tie;
                    inputs.Add((bits + (ulong)(long)random.Next(-1, 2), 0, 0));
                }

                break;
            case Operation.Add:
            case Operation.MinIeee:
            case Operation.MaxIeee:
            case Operation.MinLegacy:
            case Operation.MaxLegacy:
            case Operation.Lt:
            case Operation.Eq:
            case Operation.Unordered:
                foreach (var a in Specials)
                {
                    foreach (var b in Specials)
                    {
                        inputs.Add((a, b, 0));
                    }
                }

                for (var i = 0; i < 2000; i++)
                {
                    inputs.Add((Bits(random), Bits(random), 0));
                }

                for (var i = 0; i < 2000; i++)
                {
                    // Cancellation: b within a few ulps of -a, or of a.
                    var a = Normal(random, 0, 2046);
                    var b = (random.Next(2) == 0 ? a ^ 0x8000000000000000 : a) + (ulong)(long)random.Next(-3, 4);
                    inputs.Add((a, b, 0));
                }

                for (var i = 0; i < 2000; i++)
                {
                    // Every alignment, short significands for exact ties.
                    var exponent = random.Next(0, 2047);
                    var other = Math.Clamp(exponent + random.Next(-60, 61), 0, 2046);
                    inputs.Add((Short(random, exponent, exponent, random.Next(0, 53)), Short(random, other, other, random.Next(0, 53)), 0));
                }

                for (var i = 0; i < 1000; i++)
                {
                    // Denormals and the denormal/normal boundary.
                    inputs.Add((Normal(random, 0, 2), Normal(random, 0, 2), 0));
                }

                break;
            case Operation.Ldexp:
                foreach (var a in Specials)
                {
                    foreach (var n in new[] { 0, 1, -1, 52, -52, 1023, -1022, -1074, -1075, 2046, -2098, 2100, -2200, int.MinValue, int.MaxValue })
                    {
                        inputs.Add((a, unchecked((uint)n), 0));
                    }
                }

                for (var i = 0; i < 4000; i++)
                {
                    inputs.Add((Bits(random), unchecked((uint)random.Next(-2200, 2200)), 0));
                }

                for (var i = 0; i < 2000; i++)
                {
                    // Results rounding into the denormal range.
                    var exponent = random.Next(1, 2047);
                    inputs.Add((Normal(random, exponent, exponent), unchecked((uint)(random.Next(-1080, -1018) - (exponent - 1023))), 0));
                }

                break;
            case Operation.Trunc:
            case Operation.Floor:
            case Operation.Ceil:
            case Operation.Rndne:
            case Operation.Fract:
                foreach (var a in Specials)
                {
                    inputs.Add((a, 0, 0));
                }

                for (var i = 0; i < 1500; i++)
                {
                    inputs.Add((Bits(random), 0, 0));
                }

                for (var i = 0; i < 3000; i++)
                {
                    // Values with a fraction, |x| from 2^-30 to 2^60.
                    inputs.Add((Normal(random, 993, 1083), 0, 0));
                }

                for (var i = 0; i < 1000; i++)
                {
                    // Exact halves, the round-to-even ties.
                    var whole = random.NextInt64(-(1L << 40), 1L << 40);
                    inputs.Add((BitConverter.DoubleToUInt64Bits(whole + 0.5), 0, 0));
                }

                for (var i = 0; i < 500; i++)
                {
                    // Just below 1 and just above -1 (fract's clamp), and tiny negatives.
                    inputs.Add((Short(random, 1020, 1022, 52) | 0x000FFFFFFFFFFF00, 0, 0));
                    inputs.Add((Normal(random, 1, 600) | 0x8000000000000000, 0, 0));
                }

                break;
            case Operation.FrexpMant:
            case Operation.FrexpExp:
            case Operation.CvtI32F64:
            case Operation.CvtU32F64:
                foreach (var a in Specials)
                {
                    inputs.Add((a, 0, 0));
                }

                foreach (var edge in new[] { 2147483647.0, 2147483647.5, 2147483648.0, -2147483648.0, -2147483648.9, -2147483649.0,
                    4294967295.0, 4294967295.75, 4294967296.0, -0.75, 0.75, -1.0, 1e300, -1e300 })
                {
                    inputs.Add((BitConverter.DoubleToUInt64Bits(edge), 0, 0));
                }

                for (var i = 0; i < 2000; i++)
                {
                    inputs.Add((Bits(random), 0, 0));
                }

                for (var i = 0; i < 3000; i++)
                {
                    inputs.Add((Normal(random, 1000, 1060), 0, 0));
                }

                for (var i = 0; i < 1000; i++)
                {
                    inputs.Add((Normal(random, 0, 0), 0, 0));
                }

                break;
            case Operation.CvtF64F32:
                foreach (var edge in new uint[] { 0, 0x80000000, 0x7F800000, 0xFF800000, 0x7FC00000, 0xFFC00123, 0x7F800001,
                    0xFFA00000, 1, 0x80000001, 0x007FFFFF, 0x00400000, 0x00800000, 0x7F7FFFFF, 0x3F800000 })
                {
                    inputs.Add((edge, 0, 0));
                }

                for (var i = 0; i < 4000; i++)
                {
                    inputs.Add(((uint)random.NextInt64(0, 1L << 32), 0, 0));
                }

                for (var i = 0; i < 1000; i++)
                {
                    inputs.Add(((uint)random.Next(0, 0x00800000) | ((uint)random.Next(2) << 31), 0, 0));
                }

                break;
            case Operation.Class:
                foreach (var a in Specials)
                {
                    for (var bit = 0; bit < 10; bit++)
                    {
                        inputs.Add((a, 1u << bit, 0));
                    }

                    inputs.Add((a, 0x3FF, 0));
                }

                for (var i = 0; i < 3000; i++)
                {
                    inputs.Add((Bits(random), (uint)random.Next(0, 0x400), 0));
                }

                break;
            case Operation.TrigPreop:
                for (var i = 0; i < 6000; i++)
                {
                    var exponent = random.Next(4) == 0 ? random.Next(1960, 2048) : random.Next(0, 2048);
                    inputs.Add((((ulong)exponent << 52) | ((ulong)random.NextInt64() & 0x800FFFFFFFFFFFFF), (uint)random.Next(0, 64), 0));
                }

                break;
            case Operation.DivFixup:
                foreach (var d in Specials)
                {
                    foreach (var n in Specials)
                    {
                        inputs.Add((Bits(random), d, n));
                    }
                }

                for (var i = 0; i < 4000; i++)
                {
                    // Exponent differences around the underflow and overflow bounds.
                    var exponentD = random.Next(0, 2047);
                    var difference = random.Next(2) == 0 ? random.Next(-1080, -1070) : random.Next(1020, 1030);
                    var exponentN = Math.Clamp(exponentD + difference, 0, 2046);
                    inputs.Add((Bits(random), Normal(random, exponentD, exponentD), Normal(random, exponentN, exponentN)));
                }

                break;
            case Operation.FmaF32:
                for (var i = 0; i < 4000; i++)
                {
                    inputs.Add(((uint)random.NextInt64(0, 1L << 32), (uint)random.NextInt64(0, 1L << 32), (uint)random.NextInt64(0, 1L << 32)));
                }

                for (var i = 0; i < 3000; i++)
                {
                    // Products near -c, landing among the F32 denormals and at ties.
                    var a = BitConverter.SingleToUInt32Bits((float)(random.NextDouble() * Math.Pow(2, random.Next(-80, 10))));
                    var b = BitConverter.SingleToUInt32Bits((float)(random.NextDouble() * Math.Pow(2, random.Next(-80, 10))));
                    var product = BitConverter.UInt32BitsToSingle(a) * BitConverter.UInt32BitsToSingle(b);
                    var c = (BitConverter.SingleToUInt32Bits(product) ^ 0x80000000u) + (uint)random.Next(-2, 3);
                    inputs.Add((a, b, c));
                }

                break;
            default:
                foreach (var edge in new uint[] { 0, 1, 2, 3, 0x7FFFFFFF, 0x80000000, 0x80000001, 0xFFFFFFFF, 0xFFFFFFFE, 0x00FFFFFF, 0x01000001 })
                {
                    inputs.Add((edge, 0, 0));
                }

                for (var i = 0; i < 4000; i++)
                {
                    inputs.Add(((uint)random.NextInt64(0, 1L << 32) >> random.Next(0, 32), 0, 0));
                }

                break;
        }

        return inputs;
    }

    private static ulong Bits(Random random) => (ulong)random.NextInt64() ^ ((ulong)random.Next(2) << 63);

    private static ulong Normal(Random random, int minimumExponent, int maximumExponent) =>
        ((ulong)random.Next(2) << 63) |
        ((ulong)random.Next(minimumExponent, maximumExponent + 1) << 52) |
        ((ulong)random.NextInt64() & 0x000FFFFFFFFFFFFF);

    // A value whose significand has only its top `bits` fraction bits set at random.
    private static ulong Short(Random random, int minimumExponent, int maximumExponent, int bits)
    {
        var fraction = ((ulong)random.NextInt64() & 0x000FFFFFFFFFFFFF) >> (52 - bits) << (52 - bits);
        return ((ulong)random.Next(2) << 63) |
            ((ulong)random.Next(minimumExponent, maximumExponent + 1) << 52) |
            fraction;
    }

    // ---- translated programs ----

    private static object[] Case(string name, uint[] op, double a, double b, double c, double expected)
    {
        var bits = BitConverter.DoubleToUInt64Bits(expected);
        return [name, op, a, b, c, (uint)bits, (uint)(bits >> 32)];
    }

    private static uint[] Vop1(uint opcode, uint destination, uint source) =>
        [0x7E000000 | (destination << 17) | (opcode << 9) | source];

    private static uint[] Vop3(uint opcode, uint destination, uint source0, uint source1, uint source2,
        uint abs = 0, uint neg = 0, uint omod = 0, bool clamp = false, uint? literal = null)
    {
        uint[] words =
        [
            0xD4000000 | (opcode << 16) | (clamp ? 1u << 15 : 0) | (abs << 8) | destination,
            source0 | (source1 << 9) | (source2 << 18) | (omod << 27) | (neg << 29),
        ];
        return literal is { } value ? [.. words, value] : words;
    }

    // v[0:1], v[2:3], v[4:5] hold a, b and c and v[6:7] a sentinel; the op runs;
    // v[store] and v[store+1] land at bytes 0 and 4.
    private static Gen5MslShader CompileTranslated(uint[] op, double a, double b, double c, uint store,
        uint floatMode = ShaderCompileRequest.DefaultFloatMode)
    {
        var words = new List<uint>();
        uint register = 0;
        foreach (var value in new[] { a, b, c })
        {
            var bits = BitConverter.DoubleToUInt64Bits(value);
            words.AddRange([0x7E0002FFu | (register << 17), (uint)bits]);
            words.AddRange([0x7E0002FFu | ((register + 1) << 17), (uint)(bits >> 32)]);
            register += 2;
        }

        words.AddRange([0x7E0002FFu | (6u << 17), Sentinel, 0x7E0002FFu | (7u << 17), Sentinel]);
        words.AddRange(op);
        words.AddRange([0xE0700000, 0x80020000 | (store << 8)]);
        words.AddRange([0xE0700004, 0x80020000 | ((store + 1) << 8)]);
        words.Add(0xBF810000);
        return Gen5ComputeFixtures.CompileRequestOrThrow(
            new Gen5ComputeFixture("f64", [.. words], StoreScalarResourceBase: 8, StoreBackingBytes: 16), floatMode: floatMode);
    }

    private static (uint Low, uint High) Execute(Gen5MslShader shader)
    {
        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var error), error + shader.Source);
        var buffer = new byte[16];
        buffer.AsSpan().Fill(0xCD);
        Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, buffer,
            new byte[PushData.ByteSize], shader.ArgumentLayout!, 1, out var result, out error), error);
        return (
            BinaryPrimitives.ReadUInt32LittleEndian(result),
            BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4)));
    }
}
