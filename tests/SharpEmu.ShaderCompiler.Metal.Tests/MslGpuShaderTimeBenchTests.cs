// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// GPU time of translated shaders, before and after the three shader-time changes, on the
/// device: each test compiles the same guest program both ways in one process and times both
/// with the command buffer's own clock (best of several runs). Opt-in, because a timing is not
/// a pass/fail property: SHARPEMU_GPU_BENCH=1 with SHARPEMU_TEST_REQUIRE_DEVICE=1, and read the
/// output (dotnet test --logger "console;verbosity=detailed").
///
///   * math mode: an ALU-heavy loop (fmac, mul+add, sqrt, rcp, sin) in the safe and the relaxed
///     mode (<see cref="MslMathMode"/>);
///   * device-address spans: a compute loop of GLOBAL_LOAD_DWORDX4 / GLOBAL_STORE_DWORDX4
///     through the guest page table, resolved per dword and per record;
///   * early Z: an eight-layer overdraw of a fragment function that stores to a device buffer,
///     with and without [[early_fragment_tests]].
/// </summary>
public sealed class MslGpuShaderTimeBenchTests(ITestOutputHelper output)
{
    private const uint Groups = 2048;
    private const uint Threads = 64;
    private const uint Iterations = 4;
    private const ulong GuestBase = 0x4000_0000;
    private const int GuestBytes = 1 << 20;

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_BENCH") == "1" &&
        Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1" &&
        MetalNative.IsAvailable;

    // v1 = float(lane); a loop of 512 trips: v3 += v1 * v2 (fmac), v4 = v3 * v3 + v1 (a multiply
    // and an add the relaxed mode can fuse), v5 = sqrt(v4), v6 = 1 / v5, v7 = sin(v6),
    // v1 += v7 * v6; then the result is stored.
    private static readonly Gen5ComputeFixture AluLoop = new(
        "bench-alu-loop",
        [
            0x7E020D00,             // v_cvt_f32_u32 v1, v0
            0x7E0402F2,             // v_mov_b32 v2, 1.0
            0x7E060280,             // v_mov_b32 v3, 0
            0xBE8003FF, 0x00000200, // s_mov_b32 s0, 512
            // loop:
            0x56060501,             // v_fmac_f32 v3, v1, v2
            0x10080703,             // v_mul_f32 v4, v3, v3
            0x06080204,             // v_add_f32 v4, v4, v1
            0x7E0A6704,             // v_sqrt_f32 v5, v4
            0x7E0C5505,             // v_rcp_f32 v6, v5
            0x7E0E6B06,             // v_sin_f32 v7, v6
            0x10100D07,             // v_mul_f32 v8, v7, v6
            0x06020308,             // v_add_f32 v1, v8, v1
            0x81808100,             // s_sub_i32 s0, s0, 1
            0xBF078000,             // s_cmp_lg_u32 s0, 0
            0xBF85FFF5,             // s_cbranch_scc1 loop
            0xE0700000, 0x80020100, // buffer_store_dword v1, off, s[8:11], 0
            0xBF810000,             // s_endpgm
        ],
        StoreScalarResourceBase: 8,
        StoreBackingBytes: 16);

    // 256 trips: global_load_dwordx4 v[4:7], v[9:10], off; the four dwords summed into v1; the
    // same record stored back with global_store_dwordx4; the address advances 1 KB.
    private static readonly Gen5ComputeFixture GlobalRecords = new(
        "bench-global-records",
        [
            0x34120084,             // v_lshlrev_b32 v9, 4, v0
            0x381212FF, 0x40000000, // v_or_b32 v9, 0x40000000, v9
            0x7E140280,             // v_mov_b32 v10, 0
            0x7E020280,             // v_mov_b32 v1, 0
            0xBE8003FF, 0x00000100, // s_mov_b32 s0, 256
            // loop:
            0xDC388000, 0x047D0009, // global_load_dwordx4 v[4:7], v[9:10], off
            0xBF8C3F70,             // s_waitcnt vmcnt(0)
            0x4A020304,             // v_add_nc_u32 v1, v4, v1
            0x4A020305,             // v_add_nc_u32 v1, v5, v1
            0x4A020306,             // v_add_nc_u32 v1, v6, v1
            0x4A020307,             // v_add_nc_u32 v1, v7, v1
            0xDC788000, 0x007D0409, // global_store_dwordx4 v[9:10], v[4:7], off
            0x4A1212FF, 0x00000400, // v_add_nc_u32 v9, 0x400, v9
            0x81808100,             // s_sub_i32 s0, s0, 1
            0xBF078000,             // s_cmp_lg_u32 s0, 0
            0xBF85FFF2,             // s_cbranch_scc1 loop
            0xE0700000, 0x80020100, // buffer_store_dword v1, off, s[8:11], 0
            0xBF810000,             // s_endpgm
        ],
        StoreScalarResourceBase: 8,
        StoreBackingBytes: 16);

    private static Gen5MslShader Compile(Gen5ComputeFixture fixture, Gen5MslCodegenOptions? options = null)
    {
        var request = Gen5ComputeFixtures.CreateComputeRequest(fixture, localSizeX: Threads);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error, options), error);
        return shader;
    }

    private MetalNative.BenchmarkResult Time(
        Gen5MslShader shader, string label, IReadOnlyList<DeviceTable>? tables = null, nuint? mathMode = null)
    {
        Assert.True(
            MetalNative.TryBenchmark(
                shader, new byte[1024], new byte[PushData.ByteSize], Groups, Threads, Iterations,
                out var result, out var error, tables, mathMode),
            error);
        output.WriteLine($"{label,-34} {result.GpuMilliseconds,9:F3} ms GPU   maxThreads {result.MaxThreadsPerThreadgroup}");
        return result;
    }

    [Fact]
    public void AluLoopSafeVersusRelaxedMath()
    {
        if (!Enabled)
        {
            return;
        }

        var shader = Compile(AluLoop);
        for (var round = 0; round < 3; round++)
        {
            Time(shader, "alu loop, safe math", mathMode: MslMathMode.Safe);
            Time(shader, "alu loop, relaxed math", mathMode: MslMathMode.Relaxed);
        }
    }

    [Fact]
    public void GlobalRecordLoopPerDwordVersusPerRecord()
    {
        if (!Enabled)
        {
            return;
        }

        var perDword = Compile(GlobalRecords, new Gen5MslCodegenOptions(DeviceAddressSpans: false));
        var perRecord = Compile(GlobalRecords, new Gen5MslCodegenOptions(DeviceAddressSpans: true));
        Assert.DoesNotContain("sharpemu_resolve_device_span(sharpemu_ranges", perDword.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_resolve_device_span(sharpemu_ranges", perRecord.Source, StringComparison.Ordinal);
        var (tables, _) = MetalNative.CreateGuestMapping(GuestBase, new byte[GuestBytes], 0x1000);
        for (var round = 0; round < 3; round++)
        {
            Time(perDword, "global records, per dword", tables);
            Time(perRecord, "global records, per record", tables);
        }
    }

    // The fragment function stands in for a translated pixel shader that stores to a buffer
    // and ends with the exit discard every translated pixel shader carries; only the attribute
    // differs between the two.
    private static string OverdrawSource(bool earlyFragmentTests) => $$"""
        #include <metal_stdlib>
        using namespace metal;
        struct VOut { float4 position [[position]]; };
        vertex VOut bench_vs(uint vid [[vertex_id]], constant float& z [[buffer(0)]])
        {
            float2 p = float2(float((vid << 1) & 2u), float(vid & 2u));
            VOut o;
            o.position = float4(p * 2.0f - 1.0f, z, 1.0f);
            return o;
        }
        {{(earlyFragmentTests ? "[[early_fragment_tests]] " : string.Empty)}}fragment float4 bench_fs(VOut in [[stage_in]], device uint* out [[buffer(0)]])
        {
            float v = in.position.x * 0.001f;
            for (int i = 0; i < 96; i++)
            {
                v = sin(v) * 1.0001f + sqrt(abs(v) + 1.0f);
            }
            out[uint(in.position.y) * 1920u + uint(in.position.x)] = as_type<uint>(v);
            if (v < -1.0e30f)
            {
                discard_fragment();
            }
            return float4(v);
        }
        """;

    [Fact]
    public void BufferStoringFragmentOverdrawWithAndWithoutEarlyFragmentTests()
    {
        if (!Enabled)
        {
            return;
        }

        for (var round = 0; round < 3; round++)
        {
            Assert.True(MetalNative.TryBenchmarkOverdraw(OverdrawSource(false), 1920, 1080, 8, 4, out var late, out var lateError), lateError);
            Assert.True(MetalNative.TryBenchmarkOverdraw(OverdrawSource(true), 1920, 1080, 8, 4, out var early, out var earlyError), earlyError);
            output.WriteLine($"overdraw x8, tested after shader   {late,9:F3} ms GPU");
            output.WriteLine($"overdraw x8, [[early_fragment_tests]] {early,6:F3} ms GPU");
        }
    }
}
