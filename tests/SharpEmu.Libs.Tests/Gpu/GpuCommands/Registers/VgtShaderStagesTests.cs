// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands.Registers;

// How VGT_SHADER_STAGES_EN reads. The executor and the pipeline cache both go through
// these, so they cannot disagree about which draws are primitive-shader draws.
public sealed class VgtShaderStagesTests
{
    [Theory]
    [InlineData(0x02002000u)]
    [InlineData(0x00002000u)]
    [InlineData(0x00002030u)]
    [InlineData(0x00002000u | VgtShaderStages.WaveSizeBits)]
    public void CompactPrimitiveMasksAreRecognized(uint stages) =>
        Assert.True(VgtShaderStages.IsPrimitiveShader(stages));

    [Theory]
    [InlineData(0u)]
    [InlineData(0x1u)]
    [InlineData(0x00402020u)] // Void Terrarium's culling draws
    public void OtherMasksAreNotTheCompactPrimitivePath(uint stages) =>
        Assert.False(VgtShaderStages.IsPrimitiveShader(stages));

    [Theory]
    [InlineData(0x00402020u)] // Void Terrarium's culling draws
    [InlineData(0x00002000u)]
    [InlineData(0x00002030u)]
    [InlineData(0x02002000u)]
    [InlineData(0x00020000u)] // PRIMGEN_EN on its own
    public void MeshAdmissionCoversEveryNggOnlyMask(uint stages) =>
        Assert.True(VgtShaderStages.SelectsPrimitiveShaderOnly(stages));

    [Theory]
    [InlineData(0u)] // the plain vertex/pixel path, not a primitive-shader one
    [InlineData(0x1u)] // LS_EN
    [InlineData(0x4u)] // HS_EN
    [InlineData(0x40u)] // VS_EN
    [InlineData(0x00402060u)] // the NGG mask plus VS_EN
    public void MeshAdmissionRefusesMasksThatSelectAnotherStage(uint stages) =>
        Assert.False(VgtShaderStages.SelectsPrimitiveShaderOnly(stages));

    [Fact]
    public void WaveSizeBitsNeverChangeTheReading()
    {
        // GS_W32_EN/VS_W32_EN only pick the wave width; graphics stages are always wave32.
        for (var bits = 0u; bits <= 3u; bits++)
        {
            var stages = 0x00402020u | (bits << 22);
            Assert.True(VgtShaderStages.SelectsPrimitiveShaderOnly(stages));
            Assert.False(VgtShaderStages.IsPrimitiveShader(stages));
        }
    }
}
