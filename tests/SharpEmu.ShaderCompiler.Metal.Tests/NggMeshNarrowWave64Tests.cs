// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>Structural checks over the narrow-wave64 lowering of mesh programs.</summary>
public sealed class NggMeshNarrowWave64Tests
{
    [Fact]
    public void CompareIntoAScalarPairClearsTheHighDword()
    {
        // A wave64 v_cmp into s[0:1] defines both dwords; the upper lanes do not exist, so
        // s1 is 0 rather than whatever the guest left there, and s_bcnt1_i32_b64 counts
        // only the real survivors.
        var shader = Gen5NggFixtures.CompileOrThrow(
            Gen5NggFixtures.Wave64MaskPairProbe(), Gen5NggFixtures.Wave64StageInfo);
        Assert.True(Assert.IsType<Gen5MslMeshInfo>(shader.MeshInfo).NarrowWave64);
        Assert.Matches(@"s0 = sharpemu_ballot\([^;]*\);\s+s1 = 0u;", shader.Source);

        // The same program as wave32 keeps its one-dword mask semantics.
        var wave32 = Gen5NggFixtures.CompileOrThrow(Gen5NggFixtures.Wave64MaskPairProbe());
        Assert.DoesNotMatch(@"s0 = sharpemu_ballot\([^;]*\);\s+s1 = 0u;", wave32.Source);
    }
}
