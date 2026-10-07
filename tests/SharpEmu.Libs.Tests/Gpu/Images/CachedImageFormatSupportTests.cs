// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Images;

public sealed class CachedImageFormatSupportTests
{
    private const GpuImageCreateFlags OriginalFlags = GpuImageCreateFlags.CreateMutableFormatBit |
                                                   GpuImageCreateFlags.CreateExtendedUsageBit |
                                                   GpuImageCreateFlags.CreateBlockTexelViewCompatibleBit;
    private const GpuImageUsage SampledUsage = GpuImageUsage.TransferSrcBit | GpuImageUsage.TransferDstBit | GpuImageUsage.SampledBit;

    private sealed class TestImageFormatSupport(bool supportsBlockViews, bool supportsSampling = true, GpuSampleCount supportedSampleCounts = GpuSampleCount.Count1Bit, bool supportsCompressedStorage = true) : IImageFormatSupport
    {
        public int FormatQueryCount { get; private set; }

        public bool TryGetImageFormatProperties(GpuFormat format, GpuImageType type, GpuImageTiling tiling, GpuImageUsage usage, GpuImageCreateFlags flags, out GpuImageFormatProperties properties)
        {
            FormatQueryCount++;
            properties = new GpuImageFormatProperties { SampleCounts = supportedSampleCounts };
            return (flags & GpuImageCreateFlags.CreateBlockTexelViewCompatibleBit) != 0
                ? supportsBlockViews && (supportsCompressedStorage || (usage & GpuImageUsage.StorageBit) == 0)
                : supportsSampling && (usage & GpuImageUsage.StorageBit) == 0;
        }
    }

    private static ImageCreateInfo CreateCompressedImageConfiguration(GpuFormat format) => new()
    {
        SType = StructureType.ImageCreateInfo,
        Format = format.ToVk(),
        ImageType = ImageType.Type2D,
        Tiling = ImageTiling.Optimal,
        Extent = new Extent3D(512, 256, 1),
        MipLevels = 10,
        ArrayLayers = 1,
        Samples = SampleCountFlags.Count1Bit,
        Usage = (SampledUsage | GpuImageUsage.StorageBit).ToVk(),
        Flags = OriginalFlags.ToVk(),
    };

    [Theory]
    [InlineData(GpuFormat.BC1RgbaUnormBlock)]
    [InlineData(GpuFormat.BC7SrgbBlock)]
    public void MacOsFallbackPreservesCompressedImageSampling(GpuFormat format)
    {
        var device = new TestImageFormatSupport(supportsBlockViews: false);
        var configuration = CreateCompressedImageConfiguration(format);

        Assert.True(CachedImage.TrySelectSupportedImageConfiguration(device, ref configuration, allowCompressedImageFallback: true));

        Assert.Equal(3, device.FormatQueryCount);
        Assert.Equal(OriginalFlags & ~GpuImageCreateFlags.CreateBlockTexelViewCompatibleBit, configuration.Flags.ToNeutral());
        Assert.Equal(SampledUsage, configuration.Usage.ToNeutral());
        Assert.Equal(format, configuration.Format.ToNeutral());
        Assert.Equal(512u, configuration.Extent.Width);
        Assert.Equal(256u, configuration.Extent.Height);
        Assert.Equal(10u, configuration.MipLevels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupportedImageConfigurationRemainsUnchanged(bool allowCompressedImageFallback)
    {
        var device = new TestImageFormatSupport(supportsBlockViews: true);
        var configuration = CreateCompressedImageConfiguration(GpuFormat.BC1RgbaUnormBlock);

        Assert.True(CachedImage.TrySelectSupportedImageConfiguration(device, ref configuration, allowCompressedImageFallback));
        Assert.Equal(1, device.FormatQueryCount);
        Assert.Equal(OriginalFlags, configuration.Flags.ToNeutral());
        Assert.Equal(SampledUsage | GpuImageUsage.StorageBit, configuration.Usage.ToNeutral());
    }

    [Fact]
    public void DisabledFallbackDoesNotRetryUnsupportedImage()
    {
        var device = new TestImageFormatSupport(supportsBlockViews: false);
        var configuration = CreateCompressedImageConfiguration(GpuFormat.BC1RgbaUnormBlock);

        Assert.False(CachedImage.TrySelectSupportedImageConfiguration(device, ref configuration, allowCompressedImageFallback: false));
        Assert.Equal(2, device.FormatQueryCount);
        Assert.Equal(OriginalFlags, configuration.Flags.ToNeutral());
        Assert.Equal(SampledUsage | GpuImageUsage.StorageBit, configuration.Usage.ToNeutral());
    }

    [Theory]
    [InlineData(false, GpuSampleCount.Count1Bit)]
    [InlineData(true, GpuSampleCount.Count4Bit)]
    public void UnsupportedFallbackKeepsOriginalImageConfiguration(bool supportsSampling, GpuSampleCount supportedSampleCounts)
    {
        var device = new TestImageFormatSupport(supportsBlockViews: false, supportsSampling, supportedSampleCounts);
        var configuration = CreateCompressedImageConfiguration(GpuFormat.BC7SrgbBlock);

        Assert.False(CachedImage.TrySelectSupportedImageConfiguration(device, ref configuration, allowCompressedImageFallback: true));
        Assert.Equal(OriginalFlags, configuration.Flags.ToNeutral());
        Assert.Equal(SampledUsage | GpuImageUsage.StorageBit, configuration.Usage.ToNeutral());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompressedImageDropsOnlyStorageWhenDriverRefusesIt(bool allowCompressedImageFallback)
    {
        var device = new TestImageFormatSupport(supportsBlockViews: true, supportsCompressedStorage: false);
        var configuration = CreateCompressedImageConfiguration(GpuFormat.BC5UnormBlock);

        Assert.True(CachedImage.TrySelectSupportedImageConfiguration(device, ref configuration, allowCompressedImageFallback));
        Assert.Equal(2, device.FormatQueryCount);
        Assert.Equal(OriginalFlags, configuration.Flags.ToNeutral());
        Assert.Equal(SampledUsage, configuration.Usage.ToNeutral());
    }

    [Fact]
    public void UncompressedImageDoesNotUseCompressedFallback()
    {
        var device = new TestImageFormatSupport(supportsBlockViews: false);
        var configuration = CreateCompressedImageConfiguration(GpuFormat.R8G8B8A8Unorm);
        configuration.Flags &= ~ImageCreateFlags.CreateBlockTexelViewCompatibleBit;

        Assert.False(CachedImage.TrySelectSupportedImageConfiguration(device, ref configuration, allowCompressedImageFallback: true));
        Assert.Equal(1, device.FormatQueryCount);
        Assert.Equal(SampledUsage | GpuImageUsage.StorageBit, configuration.Usage.ToNeutral());
    }

    [Theory]
    [InlineData(GpuFormat.BC1RgbaUnormBlock, GpuFormat.BC1RgbaSrgbBlock, true)]
    [InlineData(GpuFormat.BC7SrgbBlock, GpuFormat.BC7UnormBlock, true)]
    [InlineData(GpuFormat.BC1RgbaUnormBlock, GpuFormat.R32G32Uint, false)]
    [InlineData(GpuFormat.BC7SrgbBlock, GpuFormat.R32G32B32A32Uint, false)]
    public void FallbackAllowsCompressedViewsAndRejectsUncompressedBlockViews(GpuFormat image, GpuFormat view, bool allowedWithoutBlockViews)
    {
        Assert.True(ViewFormatRules.AreImageViewFormatsCompatible(image, view, OriginalFlags));
        Assert.Equal(allowedWithoutBlockViews, ViewFormatRules.AreImageViewFormatsCompatible(image, view, OriginalFlags & ~GpuImageCreateFlags.CreateBlockTexelViewCompatibleBit));
    }
}
