// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

public sealed partial class MslRuntimeFormatLoadTests
{
    // Only synthetic instructions and data. Adjacent input records vary each iteration;
    // every lane writes its own output, outside the input area, including across groups.
    private static Gen5ComputeFixture BenchmarkFixture()
    {
        var words = new List<uint>
        {
            0x34120084, // v_lshlrev_b32 v9, 4, v0
            0x7E020280, // v_mov_b32 v1, 0
            0xBE8003FF, 256, // s_mov_b32 s0, 256
        };
        var loop = words.Count;
        words.AddRange([
            0xE00C1400, 0x80020409, // buffer_load_format_xyzw v[4:7], v9, s[8:11], offset:1024 offen
            0xBF8C0070,
            0x4A020304, 0x4A020305, 0x4A020306, 0x4A020307, // sum all four words
            0x4A121290, // v_add_nc_u32 v9, 16, v9
            0x361212FF, 0x3F0, // v_and_b32 v9, 0x3f0, v9
            0x81808100, // s_sub_i32 s0, s0, 1
            0xBF078000, // s_cmp_lg_u32 s0, 0
        ]);
        words.Add(0xBF850000 | (ushort)(short)(loop - words.Count - 1));
        words.AddRange([
            0x34140082, // v_lshlrev_b32 v10, 2, v0
            0x7E120210, // v_mov_b32 v9, s16 (group x)
            0x34121287, // v_lshlrev_b32 v9, 7, v9
            0x4A121509, // v_add_nc_u32 v9, v9, v10
            0x4A1212FF, 4096, // v_add_nc_u32 v9, 4096, v9
            0xE0701000, 0x80030109, // buffer_store_dword v1, v9, s[12:15] offen
            0xBF810000,
        ]);
        return new("runtime-format-benchmark", words.ToArray(), 12, 4096 + 1024 * 128);
    }

    [Fact]
    public void BenchmarkDynamicFormats()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_GPU_BENCH") != "1") return;
        if (!RequireDevice()) return;
        var fixture = BenchmarkFixture();
        var (before, layout) = Compile(fixture, false, 32);
        var (after, _) = Compile(fixture, true, 32);
        var data = Enumerable.Range(0, fixture.StoreBackingBytes).Select(i => (byte)(i * 37 + 11)).ToArray();
        foreach (var (format, swizzle, name) in new (uint, uint, string)[]
        {
            (77, 0xFAC, "32x4-float-identity"),
            (75, 0xFAC, "32x4-uint-identity"),
            (74, 0xFAC, "32x3-float-missing-w"),
            (22, 0xFAC, "32x1-float-missing-yzw"),
            (71, 0xFAC, "16x4-float-fallback"),
            (77, 0x977, "32x4-float-swizzle-fallback"),
        })
        {
            var push = Push(layout, (format << 12) | swizzle, 0);
            // Prove equal outputs and that the fixture really touched all output lanes.
            Assert.True(MetalNative.TryCompileLibrary(before.Source, out var oldLibrary, out var oldError), oldError);
            Assert.True(MetalNative.TryCompileLibrary(after.Source, out var newLibrary, out var newError), newError);
            Assert.True(MetalNative.TryExecuteWithArgumentBuffer(oldLibrary, before.EntryPoint, data, push,
                before.ArgumentLayout!, 32, out var expected, out var oldRunError), oldRunError);
            Assert.True(MetalNative.TryExecuteWithArgumentBuffer(newLibrary, after.EntryPoint, data, push,
                after.ArgumentLayout!, 32, out var actual, out var newRunError), newRunError);
            Assert.Equal(expected, actual);
            Assert.False(actual.AsSpan(4096, 128).SequenceEqual(data.AsSpan(4096, 128)));
            for (var round = 0; round < 4; round++)
            {
                foreach (var fast in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    Assert.True(MetalNative.TryBenchmark(fast ? after : before, data, push, 1024, 32, 1,
                        out var result, out var error), error);
                    Assert.False(result.StepCapHit);
                    output.WriteLine(FormattableString.Invariant($"{name} round={round} fast={fast} gpu_ms={result.GpuMilliseconds:F6}"));
                }
            }
        }
    }
}
