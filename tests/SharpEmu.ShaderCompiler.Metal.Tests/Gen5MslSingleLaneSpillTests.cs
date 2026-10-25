// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// A stage that models a single wave lane (a vertex program, sharpemu_lane = 0 in every
/// thread) spills SGPRs through VGPR lanes like any other: a writelane to lane 1 lands in no
/// thread there, so its reload has to read the per-thread copy the writelane kept, or it
/// reads lane 0's spill (Black Myth: Wukong's hand base pass placed vertices that way).
/// </summary>
public sealed class Gen5MslSingleLaneSpillTests
{
    // s2 = 0x1234 and s3 = 77 are parked in lanes 0 and 1 of v1, clobbered and read back.
    private static readonly Gen5ComputeFixture TwoLaneSpill = new(
        "single-lane-spill",
        [
            0xBE8203FFu, 0x00001234u, // s_mov_b32 s2, 0x1234
            0xBE8303FFu, 0x0000004Du, // s_mov_b32 s3, 77
            0xD7610001u, 0x00010002u, // v_writelane_b32 v1, s2, 0
            0xD7610001u, 0x00010203u, // v_writelane_b32 v1, s3, 1
            0xBE820380u,              // s_mov_b32 s2, 0
            0xBE830380u,              // s_mov_b32 s3, 0
            0xD7600000u, 0x00010101u, // v_readlane_b32 s0, v1, 0
            0xD7600001u, 0x00010301u, // v_readlane_b32 s1, v1, 1
            0xBF810000u,              // s_endpgm
        ],
        0,
        0);

    // s2 = 0x1234 parked in lane 4 of v16, a one-dword load into v15 (no TFE status word, so
    // v16 is untouched), the reload.
    private static readonly Gen5ComputeFixture SpillPastNarrowLoad = new(
        "spill-past-narrow-load",
        [
            0xBE8203FFu, 0x00001234u, // s_mov_b32 s2, 0x1234
            0xD7610010u, 0x00010802u, // v_writelane_b32 v16, s2, 4
            0xE0301000u, 0x80020F00u, // buffer_load_dword v15, v0, s[8:11], 0 offen
            0xBE820380u,              // s_mov_b32 s2, 0
            0xD7600000u, 0x00010910u, // v_readlane_b32 s0, v16, 4
            0xBF810000u,              // s_endpgm
        ],
        8,
        256);

    // Ghost of Tsushima: the copy was dropped as if the load wrote a TFE status word into
    // v16, so a pixel lane its partial simdgroup did not have rebuilt a descriptor from
    // another lane's spill.
    [Fact]
    public void ALoadWithoutAStatusWordKeepsTheSpillInTheNextRegister()
    {
        var program = Gen5ComputeFixtures.DecodeOrThrow(SpillPastNarrowLoad);
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Pixel);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);

        Assert.Contains("s0 = sharpemu_spill0;", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void AVertexProgramReloadsBothSpilledLanesFromTheirCopies()
    {
        var program = Gen5ComputeFixtures.DecodeOrThrow(TwoLaneSpill);
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Vertex);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);

        Assert.Contains("const uint sharpemu_lane = 0u;", shader.Source, StringComparison.Ordinal);
        Assert.Contains("uint sharpemu_spill0 = 0u;", shader.Source, StringComparison.Ordinal);
        Assert.Contains("uint sharpemu_spill1 = 0u;", shader.Source, StringComparison.Ordinal);
        Assert.Contains("s0 = sharpemu_spill0;", shader.Source, StringComparison.Ordinal);
        Assert.Contains("s1 = sharpemu_spill1;", shader.Source, StringComparison.Ordinal);
    }
}
