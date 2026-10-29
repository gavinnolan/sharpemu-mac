// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// sharpemu_load_dword and sharpemu_load_dwords read through a clamped index and a select
// (Gen5MslCodegenOptions.BranchlessDwordLoads) rather than a branch around the load. Either
// form has to answer what a per-dword bounds check answers, for every address and size,
// including a run that crosses the end, an address that wraps and an empty binding.
public sealed class Gen5MslBranchlessLoadsTests
{
    private const int DataWords = 32;
    private const int CaseBase = 32;
    private const int OutputBase = 256;
    private const int OutputStride = 8;

    private const string Kernel = """

        kernel void branchless_loads_test(device uint* b [[buffer(0)]], uint t [[thread_position_in_threadgroup]])
        {
            uint addr = b[32u + t * 2u];
            uint bytes = b[33u + t * 2u];
            uint o = 256u + t * 8u;
            uint4 four = sharpemu_load_dwords<4>(b, bytes, addr);
            uint4 two = sharpemu_load_dwords<2>(b, bytes, addr);
            for (uint i = 0u; i < 4u; i++)
            {
                b[o + i] = four[i];
            }
            b[o + 4u] = two[0];
            b[o + 5u] = two[1];
            b[o + 6u] = sharpemu_load_dword(b, bytes, addr);
        }
        """;

    // addr, bytes: inside, interior, the last dwords, past the end, an odd bias, an empty binding,
    // a binding shorter than a dword, runs that cross the end, and addresses that wrap.
    public static TheoryData<bool> Forms => new() { true, false };

    private static readonly (uint Address, uint Bytes)[] Cases =
    [
        (0u, 128u), (16u, 128u), (112u, 128u), (116u, 128u), (120u, 128u), (124u, 128u), (128u, 128u),
        (2u, 128u), (1u, 128u), (7u, 128u), (0u, 0u), (0u, 3u), (4u, 8u), (8u, 16u), (12u, 16u), (16u, 16u),
        (10u, 16u), (1u, 3u), (3u, 7u), (0xFFFF_FFFCu, 128u), (0xFFFF_FFF8u, 128u), (0xFFFF_FFFFu, 128u),
        (0u, 132u), (0u, 5u), (4u, 4u),
    ];

    [Theory]
    [MemberData(nameof(Forms))]
    public void TheSwitchPicksTheBranchingOrTheBranchlessHelpers(bool branchless)
    {
        var source = Gen5MslTranslator.RenderModulePreludes(
            Gen5MslPreludes.ScalarConstants, new Gen5MslCodegenOptions { BranchlessDwordLoads = branchless });
        Assert.Equal(branchless, source.Contains("max(words, 1u) - 1u", StringComparison.Ordinal));
        Assert.Equal(!branchless, source.Contains("(uint)N <= words - index", StringComparison.Ordinal));
        // The constant-space copy follows the same form.
        Assert.Contains("static inline uint4 sharpemu_load_dwords(constant uint* b", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentSwitchDefaultsOn()
    {
        Assert.Equal(
            Environment.GetEnvironmentVariable("SHARPEMU_MSL_BRANCHLESS_LOADS") != "0",
            Gen5MslCodegenOptions.FromEnvironment.BranchlessDwordLoads);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void EveryCaseMatchesAPerDwordBoundsCheck(bool branchless)
    {
        if (!MetalNative.IsAvailable)
        {
            Assert.False(
                OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1",
                "The required gate cannot run the branchless-loads test without a Metal device.");
            return;
        }

        var source = Gen5MslTranslator.RenderModulePreludes(
            Gen5MslPreludes.None, new Gen5MslCodegenOptions { BranchlessDwordLoads = branchless }) + Kernel;
        Assert.True(MetalNative.TryCompileLibrary(source, out var library, out var compileError), compileError);

        var words = new uint[OutputBase + Cases.Length * OutputStride];
        for (var index = 0; index < DataWords; index++)
        {
            words[index] = 0x01010101u * (uint)(index + 1) ^ 0xA5A5_0000u;
        }

        for (var index = 0; index < Cases.Length; index++)
        {
            words[CaseBase + index * 2] = Cases[index].Address;
            words[CaseBase + index * 2 + 1] = Cases[index].Bytes;
        }

        var contents = new byte[words.Length * sizeof(uint)];
        Buffer.BlockCopy(words, 0, contents, 0, contents.Length);
        Assert.True(
            MetalNative.TryExecuteOnBuffer(library, "branchless_loads_test", contents, (uint)Cases.Length, out var result, out var error),
            error);
        var output = new uint[words.Length];
        Buffer.BlockCopy(result, 0, output, 0, result.Length);

        uint Expected(uint address, uint bytes, int dword)
        {
            var index = unchecked(address + (uint)(4 * dword)) >> 2;
            return index < (bytes >> 2) ? words[index] : 0u;
        }

        for (var index = 0; index < Cases.Length; index++)
        {
            var (address, bytes) = Cases[index];
            var at = OutputBase + index * OutputStride;
            for (var dword = 0; dword < 4; dword++)
            {
                Assert.True(Expected(address, bytes, dword) == output[at + dword], $"four-dword run, case {index} ({address:X}, {bytes}), dword {dword}");
            }

            Assert.True(Expected(address, bytes, 0) == output[at + 4], $"two-dword run, case {index}, dword 0");
            Assert.True(Expected(address, bytes, 1) == output[at + 5], $"two-dword run, case {index}, dword 1");
            Assert.True(Expected(address, bytes, 0) == output[at + 6], $"single dword, case {index}");
        }
    }
}
