// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// Which guest images may be textures over the guest window instead of owned copies.
/// The rule is pure, so it is pinned without a device: Apple GPUs ask a buffer-backed
/// texture for a row pitch and an offset aligned to the format, and an image whose bytes
/// do not satisfy that has to keep its own texture.
/// </summary>
public sealed class MetalLinearWindowViewTests
{
    private const ulong Address = 0x2_0010_0000;
    private const ulong Alignment = 16;

    private static (ImageDescription Description, MetalTextureShape Shape) Plane(
        uint width, uint height, GpuFormat format, GuestPixelFormat guest, uint bytes, ulong mtlFormat,
        ulong size = 0)
    {
        var pitchBytes = (ulong)width * bytes;
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(Address, size != 0 ? size : pitchBytes * height);
        description.PixelFormat = format;
        description.GuestFormat = guest;
        description.Type = GuestImageType.Color2D;
        description.Extent = new GpuExtent3D(width, height, 1);
        description.Resources = new SubresourceCount(1, 1);
        description.Pitch = width;
        description.BytesPerBlock = bytes;
        description.Samples = 1;
        description.TileMode = GuestTileMode.Linear;
        var shape = new MetalTextureShape(mtlFormat, Mtl.TextureType2D, width, height, 1, 1, 1, 1, 0);
        return (description, shape);
    }

    private static (ImageDescription Description, MetalTextureShape Shape) R8(uint width, uint height, ulong size = 0) =>
        Plane(width, height, GpuFormat.R8Unorm, GuestPixelFormat.Bits8UNorm, 1, (ulong)MtlPixelFormat.R8Unorm, size);

    private static (ImageDescription Description, MetalTextureShape Shape) R8G8(uint width, uint height) =>
        Plane(width, height, GpuFormat.R8G8Unorm, GuestPixelFormat.Bits8_8UNorm, 2, (ulong)MtlPixelFormat.Rg8Unorm);

    // Metal asserts when asked the linear alignment of these, so they are refused first.
    [Theory]
    [InlineData((ulong)MtlPixelFormat.Bc1Rgba)]
    [InlineData((ulong)MtlPixelFormat.Bc7RgbaUnorm)]
    [InlineData((ulong)MtlPixelFormat.Depth32Float)]
    [InlineData(0UL)]
    public void AFormatWithoutLinearTexturesIsRefusedBeforeTheDeviceIsAsked(ulong format) =>
        Assert.False(MetalLinearWindowView.SupportsLinearFormat(format));

    [Fact]
    public void AnOrdinaryColourFormatMayBeAskedItsLinearAlignment()
    {
        Assert.True(MetalLinearWindowView.SupportsLinearFormat((ulong)MtlPixelFormat.R8Unorm));
        Assert.True(MetalLinearWindowView.SupportsLinearFormat((ulong)MtlPixelFormat.Rg8Unorm));
    }

    [Fact]
    public void ASilentHillLumaPlaneQualifies()
    {
        var (description, shape) = R8(512, 150);

        Assert.True(MetalLinearWindowView.Qualifies(in description, in shape, 0x1_0000, Alignment, out var pitch));
        Assert.Equal(512UL, pitch);
    }

    [Fact]
    public void ASilentHillChromaPlaneQualifies()
    {
        var (description, shape) = R8G8(256, 75);

        Assert.True(MetalLinearWindowView.Qualifies(in description, in shape, 0x1_0000, Alignment, out var pitch));
        Assert.Equal(512UL, pitch);
    }

    [Fact]
    public void AnOffsetOffTheDeviceAlignmentDoesNotQualify()
    {
        var (description, shape) = R8(512, 150);

        Assert.False(MetalLinearWindowView.Qualifies(in description, in shape, 0x1_0008, Alignment, out _));
    }

    [Fact]
    public void APitchOffTheDeviceAlignmentDoesNotQualify()
    {
        var (description, shape) = R8(512, 150);

        Assert.False(MetalLinearWindowView.Qualifies(in description, in shape, 0x1_0000, 1024, out _));
    }

    [Fact]
    public void AnImageSmallerThanItsRowsDoesNotQualify()
    {
        var (description, shape) = R8(512, 150, size: 512UL * 149);

        Assert.False(MetalLinearWindowView.Qualifies(in description, in shape, 0x1_0000, Alignment, out _));
    }

    [Fact]
    public void ATiledImageDoesNotQualify()
    {
        var (description, shape) = R8(512, 150);
        description.TileMode = GuestTileMode.Standard64KB;

        Assert.False(MetalLinearWindowView.Qualifies(in description, in shape, 0x1_0000, Alignment, out _));
    }

    [Fact]
    public void AMippedOrLayeredImageDoesNotQualify()
    {
        var (mipped, shape) = R8(512, 150);
        mipped.Resources = new SubresourceCount(2, 1);
        var (layered, _) = R8(512, 150);
        layered.Resources = new SubresourceCount(1, 2);

        Assert.False(MetalLinearWindowView.Qualifies(in mipped, in shape, 0x1_0000, Alignment, out _));
        Assert.False(MetalLinearWindowView.Qualifies(in layered, in shape, 0x1_0000, Alignment, out _));
    }

    [Fact]
    public void ADepthOrCompressedImageDoesNotQualify()
    {
        var (depth, depthShape) = Plane(
            256, 256, GpuFormat.D32Sfloat, GuestPixelFormat.Bits32Float, 4, (ulong)MtlPixelFormat.Depth32Float);
        var (block, blockShape) = Plane(
            256, 256, GpuFormat.R8G8B8A8Unorm, GuestPixelFormat.Bc1UNorm, 8, (ulong)MtlPixelFormat.Bc1Rgba);

        Assert.False(MetalLinearWindowView.Qualifies(in depth, in depthShape, 0x1_0000, Alignment, out _));
        Assert.False(MetalLinearWindowView.Qualifies(in block, in blockShape, 0x1_0000, Alignment, out _));
    }

    [Fact]
    public void AnUnknownDeviceAlignmentDoesNotQualify()
    {
        var (description, shape) = R8(512, 150);

        Assert.False(MetalLinearWindowView.Qualifies(in description, in shape, 0x1_0000, 0, out _));
    }
}
