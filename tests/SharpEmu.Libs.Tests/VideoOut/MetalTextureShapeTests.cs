// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class MetalTextureShapeTests
{
    [Theory]
    [InlineData(9u, false, 1u, 1u, 2u, false, 0ul)]
    [InlineData(13u, true, 1u, 1u, 3u, true, 0ul)]
    [InlineData(13u, true, 3u, 1u, 3u, true, 0ul)]
    [InlineData(12u, true, 1u, 1u, 3u, true, 0ul)]
    [InlineData(10u, false, 1u, 1u, 7u, true, 64ul)]
    [InlineData(10u, false, 1u, 4u, 7u, true, 64ul)]
    [InlineData(11u, false, 6u, 1u, 3u, true, 0ul)]
    public void SingletonExtentsKeepTheDeclaredTextureKindAndUploadStride(uint type, bool arrayed,
        uint layers, uint depth, uint expectedType, bool requiresSliceUpload, ulong imageStride)
    {
        var texture = new GuestDrawTexture(0, 4, 4, 10, 0, [], false, false,
            Type: type, ArrayedView: arrayed, ArrayLayers: layers, Depth: depth);
        var shape = MetalVideoPresenter.GuestTextureShape(texture);
        Assert.Equal(expectedType, shape.TextureType);
        Assert.Equal(requiresSliceUpload, MetalVideoPresenter.RequiresSliceUpload(shape));
        Assert.Equal(imageStride, MetalVideoPresenter.TextureImageStride(shape, 64));
    }

    [Theory]
    [InlineData(14u)]
    [InlineData(15u)]
    public void MissingPerSampleDataIsRejectedBeforeNativeAllocation(uint type)
    {
        var texture = new GuestDrawTexture(0, 4, 4, 10, 0, [], false, false, Type: type);
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.CreateDrawTexture(0, 0, texture, out _));
    }
}
