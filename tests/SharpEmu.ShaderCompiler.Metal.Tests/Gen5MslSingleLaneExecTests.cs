// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// A single-lane stage (the vertex stage, or a pixel stage with graphics subgroups off)
// runs one guest lane per Metal thread, so EXEC has exactly one real
// bit. SILENT HILL's lighting pixel shader starts with s_wqm_b64 exec, exec, which turned
// EXEC 1 into 0xF; once its own lane finished a per-light loop, bits 1..3 - lanes that do
// not exist - stayed set, the S_CBRANCH_EXECZ exit never fired, and every pixel ran to the
// dispatcher step cap with the GPU wedged for minutes.
public sealed class Gen5MslSingleLaneExecTests
{
    private static readonly uint[] WholeQuadThenExecBranchWords =
    [
        0xBEFE0A7E,             // s_wqm_b64 exec, exec
        0xBF880000,             // s_cbranch_execz +0
        0xBEFE04C1,             // s_mov_b64 exec, -1
        0xBF880000,             // s_cbranch_execz +0
        .. Gen5ComputeFixtures.PixelWords,
    ];

    [Fact]
    public void WholeQuadModeAddsNoLanesToASingleLaneStage()
    {
        var program = Gen5ComputeFixtures.DecodeOrThrow(
            new Gen5ComputeFixture("pixel-wqm", WholeQuadThenExecBranchWords, 0, 0));
        Assert.Equal("SWqmB64", program.Instructions[0].Opcode);
        Assert.Equal("SCbranchExecz", program.Instructions[1].Opcode);
        Assert.Equal("SMovB64", program.Instructions[2].Opcode);

        var source = Compile(program);

        // No quad expansion: the only lane stays the only lane.
        Assert.DoesNotContain("0x1111111111111111ul", source, StringComparison.Ordinal);

        // Every EXEC write keeps bit 0 alone, so all-ones cannot leave phantom lanes.
        Assert.Matches(@"\) & 1u;", source);

        // The branch tests the lane's own bit, never the scalar pair.
        Assert.DoesNotContain("(s126 | s127) == 0u", source, StringComparison.Ordinal);
        Assert.DoesNotContain("(s[126] | s[127]) == 0u", source, StringComparison.Ordinal);
    }

    private static string Compile(Gen5ShaderProgram program)
    {
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Pixel,
            pixelOutputs: [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
            enableGraphicsSubgroupOperations: false);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Source;
    }
}
