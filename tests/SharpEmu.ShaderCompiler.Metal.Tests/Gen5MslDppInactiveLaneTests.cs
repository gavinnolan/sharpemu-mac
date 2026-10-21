// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// DPP16 and V_PERMLANE16 reading an EXEC-inactive source lane: with FI (fetch inactive)
// the lane's value is read anyway; without it the lane is invalid, so BOUND_CTRL writes 0
// and otherwise the destination lane keeps its old value. A row shift past the row edge
// is invalid regardless of FI.
public sealed class Gen5MslDppInactiveLaneTests(ITestOutputHelper output)
{
    private const uint Threads = 32;
    private const uint Old = 0xDEAD;

    public enum Mode { Plain, BoundControl, FetchInactive }

    [Theory]
    [InlineData(Mode.Plain, 0xFF011101u)]
    [InlineData(Mode.BoundControl, 0xFF091101u)]
    [InlineData(Mode.FetchInactive, 0xFF051101u)]
    public void RowShiftRightReadingAnInactiveLane(Mode mode, uint dppWord)
    {
        // v_mov_b32_dpp v2, v1 row_shr:1 with only the even lanes active.
        var result = Run("dpp-row-shr-inactive", [0x7E0402FA, dppWord]);
        if (result is null) return;
        for (var lane = 0u; lane < Threads; lane++)
        {
            var expected = (lane & 1) != 0
                ? Old
                : (lane & 15) == 0
                    ? mode == Mode.BoundControl ? 0u : Old
                    : mode switch { Mode.Plain => Old, Mode.BoundControl => 0u, _ => lane - 1 };
            Assert.Equal(expected, result[lane]);
        }
    }

    [Theory]
    [InlineData(Mode.Plain, 0xD7770002u)]
    [InlineData(Mode.BoundControl, 0xD7771002u)]
    [InlineData(Mode.FetchInactive, 0xD7770802u)]
    public void Permlane16ReadingAnInactiveLane(Mode mode, uint vop3Word)
    {
        // s0/s1 select lane ^ 1 within each row: every active (even) lane reads an inactive one.
        // v_permlane16_b32 v2, v1, s0, s1 [op_sel:[0,1] = BOUND_CTRL, op_sel:[1,0] = FI].
        var result = Run(
            "permlane16-inactive",
            [0xBE8003FF, 0x67452301, 0xBE8103FF, 0xEFCDAB89, vop3Word, 0x00040101]);
        if (result is null) return;
        for (var lane = 0u; lane < Threads; lane++)
        {
            var expected = (lane & 1) != 0
                ? Old
                : mode switch { Mode.Plain => Old, Mode.BoundControl => 0u, _ => lane ^ 1 };
            Assert.Equal(expected, result[lane]);
        }
    }

    private uint[]? Run(string name, uint[] body)
    {
        uint[] words =
        [
            0x7E020300,             // v_mov_b32 v1, v0
            0x7E0402FF, Old,        // v_mov_b32 v2, 0xdead
            0xBEFE03FF, 0x55555555, // s_mov_b32 exec_lo, 0x55555555
            .. body,
            0xBEFE03C1,             // s_mov_b32 exec_lo, -1
            0xE0702000, 0x80010200, // buffer_store_dword v2, v0, s[4:7], 0 idxen
            0xBF810000,             // s_endpgm
        ];
        var program = Gen5ComputeFixtures.DecodeOrThrow(new Gen5ComputeFixture(name, words, 0, 0));
        if (SkipWithoutMetalDevice()) return null;

        var userData = new uint[16];
        userData[4] = 0x200000;
        userData[5] = 4u << 16;
        userData[6] = Threads;
        userData[7] = DescriptorConstants.IdentityDestinationSelect;
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Compute, waveLaneCount: 32, localSizeX: Threads,
            runtimeInputs: new ResourceRuntimeInputs { UserData = userData });
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.True(MetalNative.TryCompileLibrary(shader!.Source, out var library, out var compileError), compileError);
        Assert.True(
            MetalNative.TryExecuteWithArgumentBuffer(
                library,
                shader.EntryPoint,
                new byte[Threads * 4],
                new byte[Resources.PushData.ByteSize],
                shader.ArgumentLayout!,
                Threads,
                out var bytes,
                out var runError),
            runError);
        var lanes = new uint[Threads];
        for (var lane = 0; lane < Threads; lane++) lanes[lane] = BitConverter.ToUInt32(bytes, lane * 4);
        return lanes;
    }

    private bool SkipWithoutMetalDevice()
    {
        if (MetalNative.IsAvailable) return false;
        Assert.False(
            OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1",
            "The required gate cannot run the DPP inactive-lane test without a Metal device.");
        output.WriteLine("No Metal device on this host; the DPP inactive-lane test skipped.");
        return true;
    }
}
