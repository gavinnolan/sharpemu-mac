// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using ImageResourceClass = SharpEmu.ShaderCompiler.Resources.ImageResourceClass;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

public sealed partial class MetalRenderHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualStagePreparationNormalizesAllZeroReadonlyResourceWithoutGuestMemoryReads(bool storage)
    {
        var image = new ImageResource
        {
            ResourceClass = storage ? ImageResourceClass.Storage : ImageResourceClass.Sampled,
            Dimension = ImageDimension.Dim2D, NumericClass = ImageNumericClass.Float, Read = true,
        };
        var program = Program(ShaderStageKind.Compute, 131, new ShaderResourceInfo { Images = [image] });
        var words = new uint[8];
        using var scope = ((IRenderHost)_host).BeginPreparation();
        var prepared = ((IRenderHost)_host).PrepareBindings(new ShaderStageResources(program,
            new ResourceSnapshot { Images = [words] }));
        var textures = (GuestDrawTexture[])prepared.GetType().GetProperty("Textures")!.GetValue(prepared)!;
        var texture = Assert.Single(textures);
        Assert.Equal(0UL, texture.Address);
        Assert.Equal((10u, 0u, 9u), (texture.Format, texture.NumberType, texture.Type));
        Assert.Equal(new byte[4], texture.RgbaPixels);
        Assert.Equal(storage, texture.Shape.Storage);
        Assert.Equal(GuestImageAccess.Read, texture.Access);
        words[3] = uint.MaxValue;
        Assert.Equal(0u, texture.Descriptor![3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualStagePreparationRejectsNullStoresOrInvalidZeroAddressDescriptor(bool nonzeroInvalidWord)
    {
        var image = new ImageResource
        {
            ResourceClass = ImageResourceClass.Storage,
            Dimension = ImageDimension.Dim2D, NumericClass = ImageNumericClass.Float, Written = true,
        };
        var program = Program(ShaderStageKind.Compute, 132, new ShaderResourceInfo { Images = [image] });
        using var scope = ((IRenderHost)_host).BeginPreparation();
        var words = new uint[8];
        if (nonzeroInvalidWord) words[3] = 9u << 28;
        var failure = Assert.Throws<NotSupportedException>(() => ((IRenderHost)_host).PrepareBindings(
            new ShaderStageResources(program, new ResourceSnapshot { Images = [words] })));
        Assert.Contains(nonzeroInvalidWord ? "not a documented all-zero null" : "discarded stores", failure.Message);
        Assert.Empty(_backend.Dispatches);
    }
}
