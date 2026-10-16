// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

// A stencil plane bound with DB_STENCIL_READ_BASE zero and only a write base.
[Collection(SchedulingStateCollection.Name)]
public sealed class WriteOnlyStencilTargetTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong StencilWrite = Base + 0x80000;
    private const uint StencilEnable = 1u;
    private const uint StencilClearEnable = 1u << 1;
    private const uint StencilWriteDisable = 1u << 25;

    private static DepthTargetWords WriteOnly(uint depthControlBits = 0, uint renderControl = 0, uint depthView = 0)
    {
        var words = RegisterWords.Depth(Base, 64, 64, stencilBase: StencilWrite);
        return words with
        {
            StencilReadBase = 0,
            DepthControl = words.DepthControl | depthControlBits,
            RenderControl = renderControl,
            DepthView = depthView,
        };
    }

    [Fact]
    public void DepthPassWithoutStencilTestBindsTheWriteBasePlane()
    {
        using var fatal = new FatalScope();
        var resolution = ImageRequestBuilders.DepthTarget(WriteOnly(), new AcceptingFormatSupport());

        Assert.NotNull(resolution);
        var value = resolution.Value;
        Assert.True(value.HasStencil);
        Assert.Equal(StencilWrite, value.StencilAddress);
        Assert.Equal(StencilWrite, value.Request.Description.Stencil.Address);
        Assert.Equal(value.StencilSize, value.Request.Description.Stencil.Size);
        Assert.False(value.StencilClearEnabled);
        Assert.Empty(fatal.Messages);
    }

    [Fact]
    public void StencilClearBindsTheWriteBasePlane()
    {
        using var fatal = new FatalScope();
        var words = WriteOnly(StencilEnable, StencilClearEnable) with { DepthControl = StencilEnable | (7u << 8) };
        var resolution = ImageRequestBuilders.DepthTarget(words, new AcceptingFormatSupport());

        Assert.NotNull(resolution);
        Assert.Equal(StencilWrite, resolution.Value.StencilAddress);
        Assert.True(resolution.Value.StencilClearEnabled);
        Assert.Empty(fatal.Messages);
    }

    [Fact]
    public void StencilTestWithoutReadBaseBindsTheWriteBasePlane()
    {
        using var fatal = new FatalScope();
        var words = WriteOnly() with { StencilInfo = 0x20000181, DepthControl = 0x002002F3 };
        var resolution = ImageRequestBuilders.DepthTarget(words, new AcceptingFormatSupport());

        Assert.NotNull(resolution);
        Assert.Equal(StencilWrite, resolution.Value.StencilAddress);
        Assert.Empty(fatal.Messages);
    }

    [Fact]
    public void FrontAndBackEqualStencilTestWithDepthTestBindsTheWriteBasePlane()
    {
        using var fatal = new FatalScope();
        // STENCIL_ENABLE, Z_ENABLE, Z_WRITE off, ZFUNC LEQUAL, BACKFACE_ENABLE, STENCILFUNC and STENCILFUNC_BF EQUAL.
        const uint depthControl = StencilEnable | (1u << 1) | (3u << 4) | (1u << 7) | (2u << 8) | (2u << 20);
        var words = WriteOnly() with { DepthControl = depthControl };
        var resolution = ImageRequestBuilders.DepthTarget(words, new AcceptingFormatSupport());

        Assert.NotNull(resolution);
        Assert.Equal(StencilWrite, resolution.Value.StencilAddress);
        Assert.Empty(fatal.Messages);
    }

    [Fact]
    public void WriteDisabledStencilWithoutReadBaseStaysFatal()
    {
        using var fatal = new FatalScope();
        Assert.Throws<SchedulerFatalException>(() =>
            ImageRequestBuilders.DepthTarget(WriteOnly(depthView: StencilWriteDisable), new AcceptingFormatSupport()));
        Assert.Single(fatal.Messages);
    }

    [Fact]
    public void DifferingReadAndWriteBasesStayFatal()
    {
        using var fatal = new FatalScope();
        var words = RegisterWords.Depth(Base, 64, 64, stencilBase: StencilWrite) with { StencilReadBase = Base + 0x100000 };
        Assert.Throws<SchedulerFatalException>(() => ImageRequestBuilders.DepthTarget(words, new AcceptingFormatSupport()));
        Assert.Single(fatal.Messages);
    }
}
