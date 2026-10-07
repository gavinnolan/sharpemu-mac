// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class MetalTextureSourceMetadataTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SnapshotOwnsDescriptorAndPreservesActualShapeAndInstructionIntent(bool storage, bool written)
    {
        var image = new ImageResource
        {
            ResourceClass = storage ? ImageResourceClass.Storage : ImageResourceClass.Sampled,
            Dimension = ImageDimension.Dim2D,
            NumericClass = ImageNumericClass.Float,
            Read = true,
            Written = written,
        };
        var words = new uint[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        var shape = MetalCommandStreamHost.ImageShapeOf(image);
        var access = MetalCommandStreamHost.ImageAccessOf(image);
        var descriptor = new AgcExports.TextureDescriptor(0, 1, 1, 10, 0, 0, 9, 0, 0, 1, 0xFAC);
        var binding = new AgcExports.TranslatedImageBinding(descriptor, storage, 0, [],
            ResourceDescriptor: words, Shape: shape, Access: access);
        var texture = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(new NeverReadMemory(), Generation.Gen5), [binding], out _));
        words[3] = uint.MaxValue;
        Assert.Equal(3u, texture.Descriptor![3]);
        Assert.Equal(TextureNumericClass.Float, texture.Shape.NumericClass);
        Assert.Equal(storage, texture.Shape.Storage);
        Assert.False(texture.Shape.Arrayed);
        Assert.False(texture.Shape.DynamicMip);
        Assert.Equal(access, texture.Access);
        if (storage && !written) Assert.Equal(GuestImageAccess.Read, texture.Access);
    }

    [Fact]
    public void CompilerShapePreservesTypedDimensionsAndExceptionalViewSemantics()
    {
        var image = new ImageResource
        {
            ResourceClass = ImageResourceClass.Storage,
            Dimension = ImageDimension.Dim2DMsaaArray,
            NumericClass = ImageNumericClass.Uint,
            Atomic = true,
            R128 = true,
            DepthCompare = true,
            Cube = true,
            MipMode = ImageMipMode.DynamicStorage,
        };
        var shape = MetalCommandStreamHost.ImageShapeOf(image);
        Assert.True(shape.Arrayed);
        Assert.True(shape.Multisampled);
        Assert.True(shape.Atomic);
        Assert.True(shape.R128);
        Assert.True(shape.DepthCompare);
        Assert.True(shape.Cube);
        Assert.True(shape.DynamicMip);
        Assert.Equal(TextureNumericClass.Uint, shape.NumericClass);
        Assert.False(shape.Volume);
        Assert.False(shape.OneDimensional);
    }

    [Fact]
    public void StandaloneCubeAndAtomicIntentDoNotDependOnArrayOrWrittenFlags()
    {
        var image = new ImageResource
        {
            Dimension = ImageDimension.Dim2D,
            Cube = true,
            Atomic = true,
        };
        Assert.True(MetalCommandStreamHost.ImageShapeOf(image).Arrayed);
        Assert.True(MetalCommandStreamHost.ImageShapeOf(image).Cube);
        Assert.Equal(GuestImageAccess.Read | GuestImageAccess.Write | GuestImageAccess.Atomic,
            MetalCommandStreamHost.ImageAccessOf(image));
    }

    [Theory]
    [InlineData(true, false, false, GuestImageAccess.Read)]
    [InlineData(false, true, false, GuestImageAccess.Write)]
    [InlineData(false, false, true, GuestImageAccess.Read | GuestImageAccess.Write | GuestImageAccess.Atomic)]
    [InlineData(false, false, false, GuestImageAccess.Unknown)]
    public void ActualCompilerAccessDerivationDistinguishesReadWriteAtomicAndUnknown(
        bool read, bool written, bool atomic, GuestImageAccess expected)
    {
        var image = new ImageResource { Read = read, Written = written, Atomic = atomic };
        Assert.Equal(expected, MetalCommandStreamHost.ImageAccessOf(image));
    }

    private sealed class NeverReadMemory : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination) =>
            throw new InvalidOperationException("An explicitly null resource must not read guest memory.");
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
