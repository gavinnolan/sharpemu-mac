// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// Z_EXPORT_ENABLE reaches the pixel program only when a depth aspect is bound: the DB
// drops the export otherwise, and Metal rejects a depth-writing fragment function in a
// pipeline without a depth attachment.
[Collection(SchedulingStateCollection.Name)]
public sealed class PixelDepthExportResolverTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;

    private readonly FatalScope _fatal = new();
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void DepthExportFollowsTheBoundDepthAspect(bool zExportEnable, bool depthAspectBound, bool expected)
    {
        var shaderInterface = new ShaderInterfaceRegisters
        {
            DepthShaderControl = DepthShaderControlRegisters.Decode(zExportEnable ? 1u : 0u),
        };
        var info = PixelStageInputResolver.Resolve(
            new CpuContext(_memory, Generation.Gen5),
            new RegisteredShader(MemoryBase, MemoryBase, 16, 0, 0, 0, 0, 0, 0),
            shaderInterface,
            new byte[PixelInputInfo.TargetCount],
            new ColorComponentMap[PixelInputInfo.TargetCount],
            inputCount: 0,
            depthAspectBound);

        Assert.Equal(expected, info.DepthExportEnable);
    }

    public void Dispose() => _fatal.Dispose();
}
