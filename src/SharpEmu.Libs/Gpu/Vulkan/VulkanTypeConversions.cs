// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

// Converts between the backend-neutral GPU vocabulary the shared engine speaks and the
// Silk.NET.Vulkan types the Vulkan backend records with. Every neutral enum carries the
// numeric value of its Vulkan counterpart, so each conversion is a cast and the recorded
// Vulkan stream is unchanged; the parity tests in tests/SharpEmu.Libs.Tests keep that true.
//
// This file is the only place the two vocabularies meet, so it is the seam that moves to
// SharpEmu.Gpu.Vulkan when the backends become separate assemblies.
public static class VulkanTypeConversions
{
    public static Format ToVk(this GpuFormat value) => (Format)value;

    public static GpuFormat ToNeutral(this Format value) => (GpuFormat)value;

    public static ImageType ToVk(this GpuImageType value) => (ImageType)value;

    public static GpuImageType ToNeutral(this ImageType value) => (GpuImageType)value;

    public static ImageTiling ToVk(this GpuImageTiling value) => (ImageTiling)value;

    public static GpuImageTiling ToNeutral(this ImageTiling value) => (GpuImageTiling)value;

    public static ImageUsageFlags ToVk(this GpuImageUsage value) => (ImageUsageFlags)value;

    public static GpuImageUsage ToNeutral(this ImageUsageFlags value) => (GpuImageUsage)value;

    public static ImageCreateFlags ToVk(this GpuImageCreateFlags value) => (ImageCreateFlags)value;

    public static GpuImageCreateFlags ToNeutral(this ImageCreateFlags value) => (GpuImageCreateFlags)value;

    public static SampleCountFlags ToVk(this GpuSampleCount value) => (SampleCountFlags)value;

    public static GpuSampleCount ToNeutral(this SampleCountFlags value) => (GpuSampleCount)value;

    public static IndexType ToVk(this GpuIndexType value) => (IndexType)value;

    public static GpuIndexType ToNeutral(this IndexType value) => (GpuIndexType)value;

    public static ImageLayout ToVk(this GpuImageLayout value) => (ImageLayout)value;

    public static GpuImageLayout ToNeutral(this ImageLayout value) => (GpuImageLayout)value;

    public static ImageAspectFlags ToVk(this GpuImageAspect value) => (ImageAspectFlags)value;

    public static GpuImageAspect ToNeutral(this ImageAspectFlags value) => (GpuImageAspect)value;

    public static PipelineStageFlags ToVk(this GpuPipelineStage value) => (PipelineStageFlags)value;

    public static GpuPipelineStage ToNeutral(this PipelineStageFlags value) => (GpuPipelineStage)value;

    public static PipelineBindPoint ToVk(this GpuBindPoint value) => (PipelineBindPoint)value;

    public static GpuBindPoint ToNeutral(this PipelineBindPoint value) => (GpuBindPoint)value;

    public static PrimitiveTopology ToVk(this GpuPrimitiveTopology value) => (PrimitiveTopology)value;

    public static GpuPrimitiveTopology ToNeutral(this PrimitiveTopology value) => (GpuPrimitiveTopology)value;

    public static CompareOp ToVk(this GpuCompareOp value) => (CompareOp)value;

    public static GpuCompareOp ToNeutral(this CompareOp value) => (GpuCompareOp)value;

    public static StencilOp ToVk(this GpuStencilOp value) => (StencilOp)value;

    public static GpuStencilOp ToNeutral(this StencilOp value) => (GpuStencilOp)value;

    public static ImageViewType ToVk(this GpuImageViewType value) => (ImageViewType)value;

    public static GpuImageViewType ToNeutral(this ImageViewType value) => (GpuImageViewType)value;

    public static ComponentSwizzle ToVk(this GpuComponentSwizzle value) => (ComponentSwizzle)value;

    public static GpuComponentSwizzle ToNeutral(this ComponentSwizzle value) => (GpuComponentSwizzle)value;

    public static Extent2D ToVk(this GpuExtent2D value) => new(value.Width, value.Height);

    public static GpuExtent2D ToNeutral(this Extent2D value) => new(value.Width, value.Height);

    public static Extent3D ToVk(this GpuExtent3D value) => new(value.Width, value.Height, value.Depth);

    public static GpuExtent3D ToNeutral(this Extent3D value) => new(value.Width, value.Height, value.Depth);

    public static ImageView ToVk(this GpuImageHandle value) => new(value.Handle);

    public static GpuImageHandle ToNeutral(this ImageView value) => new(value.Handle);

    // The three readings of both unions sit at the same offsets, so copying the four
    // unsigned words moves every bit of the clear colour across unchanged.
    public static ClearColorValue ToVk(this GpuClearColor value) => new()
    {
        Uint32_0 = value.Uint32_0,
        Uint32_1 = value.Uint32_1,
        Uint32_2 = value.Uint32_2,
        Uint32_3 = value.Uint32_3,
    };

    public static GpuClearColor ToNeutral(this ClearColorValue value) => new()
    {
        Uint32_0 = value.Uint32_0,
        Uint32_1 = value.Uint32_1,
        Uint32_2 = value.Uint32_2,
        Uint32_3 = value.Uint32_3,
    };

    public static GpuImageFormatProperties ToNeutral(this ImageFormatProperties value) => new()
    {
        MaxExtent = value.MaxExtent.ToNeutral(),
        MaxMipLevels = value.MaxMipLevels,
        MaxArrayLayers = value.MaxArrayLayers,
        SampleCounts = value.SampleCounts.ToNeutral(),
        MaxResourceSize = value.MaxResourceSize,
    };

    public static ImageFormatProperties ToVk(this GpuImageFormatProperties value) => new()
    {
        MaxExtent = value.MaxExtent.ToVk(),
        MaxMipLevels = value.MaxMipLevels,
        MaxArrayLayers = value.MaxArrayLayers,
        SampleCounts = value.SampleCounts.ToVk(),
        MaxResourceSize = value.MaxResourceSize,
    };
}
