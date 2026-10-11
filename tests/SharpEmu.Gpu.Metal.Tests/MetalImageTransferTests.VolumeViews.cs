// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// A volume render target is drawn one slice at a time, and the guest view of it is the
// 2D-array view Vulkan makes of a 3D image. Metal has no such view: asking for one raises
// an Objective-C assertion that takes the process down with it, which is how SILENT HILL
// The Short Message died the moment its first scene built volumetric fog. A render pass
// names the slice of a 3D attachment with its depth plane instead, so the attachment
// binds the volume itself.
public sealed partial class MetalImageTransferTests
{
    [Fact]
    public void AVolumeAttachmentBindsTheTextureRatherThanAnArrayViewOfIt()
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var shape = new MetalTextureShape(
            MtlFormat.Rgba16Float, Mtl.TextureType3D, 16, 8, 4, 1, 1, 1,
            Mtl.TextureUsageRenderTarget | Mtl.TextureUsageShaderRead | Mtl.TextureUsagePixelFormatView);
        var texture = harness.Texture(shape);
        var description = ImageDescription.Create();
        description.PixelFormat = GpuFormat.R16G16B16A16Sfloat;
        description.BytesPerBlock = 8;
        description.Pitch = 16;
        description.Type = GuestImageType.Color3D;
        description.Extent = new GpuExtent3D(16, 8, 4);
        var image = new MetalCachedImage(in description, in shape, texture);

        // One slice of the volume, as the colour-target builder describes it.
        var slice = new ImageViewDescription(
            GpuFormat.R16G16B16A16Sfloat, GpuImageViewType.Type2D, GpuImageAspect.ColorBit,
            0, 1, 2, 1, default, GpuImageUsage.ColorAttachmentBit);
        Assert.Equal(texture, image.GetOrCreateView(slice));

        // And the whole volume, as the same builder describes an exclusive slice range.
        var wholeVolume = slice with { Type = GpuImageViewType.Type2DArray, BaseLayer = 0, LayerCount = 4 };
        Assert.Equal(texture, image.GetOrCreateView(wholeVolume));
        // And the cache keeps the volume for such a request rather than dropping it:
        // the description would only build the same 3D texture again, so refusing it
        // releases a live texture once per pass for nothing.
        Assert.True(image.SupportsViewType(slice));
        Assert.True(image.SupportsViewType(wholeVolume));
        output.WriteLine("a volume attachment bound its own texture for both the slice and the whole-range view");
    }
}
