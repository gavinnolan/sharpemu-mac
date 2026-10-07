// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class MetalNullResourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualFrontendNullPolicyPreservesReadOnlyFloatTypeAndAllFourZeroChannels(bool storage)
    {
        var words = new uint[8];
        var image = Image(storage);
        var texture = MetalCommandStreamHost.CreateUndecodableNullImage(words, image);
        Assert.Equal(0UL, texture.Address);
        Assert.True(texture.IsFallback);
        Assert.Equal((10u, 0u, 9u), (texture.Format, texture.NumberType, texture.Type));
        Assert.Equal(new byte[4], texture.RgbaPixels);
        Assert.Equal(GuestImageAccess.Read, texture.Access);
        Assert.Equal(storage, texture.Shape.Storage);
        Assert.Equal(TextureNumericClass.Float, texture.Shape.NumericClass);
        words[2] = uint.MaxValue;
        Assert.Equal(0u, texture.Descriptor![2]);
        var second = MetalCommandStreamHost.CreateUndecodableNullImage(new uint[8], image);
        Assert.NotSame(texture.RgbaPixels, second.RgbaPixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullStoresFailExplicitlyRatherThanPreservingWritesInDummyTexels(bool atomic)
    {
        var image = Image(true);
        image.Written = !atomic;
        image.Atomic = atomic;
        var failure = Assert.Throws<NotSupportedException>(() => MetalCommandStreamHost.CreateUndecodableNullImage(new uint[8], image));
        Assert.Contains("discarded stores", failure.Message);
    }

    [Fact]
    public void InvalidRealDescriptorCannotBecomeNullEvenIfItsBaseAddressBitsAreZero()
    {
        var words = new uint[8]; words[3] = 0x0100;
        Assert.Throws<NotSupportedException>(() => MetalCommandStreamHost.CreateUndecodableNullImage(words, Image(false)));
    }

    [Theory]
    [InlineData(ImageDimension.Unknown)]
    [InlineData((ImageDimension)255)]
    [InlineData(ImageDimension.Dim1D)]
    [InlineData(ImageDimension.Dim2DArray)]
    [InlineData(ImageDimension.Dim3D)]
    [InlineData(ImageDimension.Dim2DMsaa)]
    public void UnsupportedTypedNullViewsCannotReceiveAPlainTwoDimensionalDummy(ImageDimension dimension)
    {
        var image = Image(true); image.Dimension = dimension;
        Assert.Throws<NotSupportedException>(() => MetalCommandStreamHost.CreateUndecodableNullImage(new uint[8], image));
    }

    [Fact]
    public void UnknownOrIntegerTypedNullIntentRequiresIndependentQualification()
    {
        var image = Image(true); image.Read = false;
        Assert.Throws<NotSupportedException>(() => MetalCommandStreamHost.CreateUndecodableNullImage(new uint[8], image));
        image.Read = true; image.ResourceClass = ImageResourceClass.None;
        Assert.Throws<NotSupportedException>(() => MetalCommandStreamHost.CreateUndecodableNullImage(new uint[8], image));
        image.ResourceClass = ImageResourceClass.Storage; image.NumericClass = ImageNumericClass.Uint;
        Assert.Throws<NotSupportedException>(() => MetalCommandStreamHost.CreateUndecodableNullImage(new uint[8], image));
    }

    private static ImageResource Image(bool storage) => new()
    {
        ResourceClass = storage ? ImageResourceClass.Storage : ImageResourceClass.Sampled,
        Dimension = ImageDimension.Dim2D,
        NumericClass = ImageNumericClass.Float,
        Read = true,
    };
}
