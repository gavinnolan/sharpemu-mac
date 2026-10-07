// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Gpu.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.ArchitectureRules;

// The backend-neutral GPU vocabulary in src/SharpEmu.Libs/Gpu/Abstractions is converted to
// Vulkan with a plain cast, so every neutral enum member must carry the numeric value of the
// identically named Silk.NET.Vulkan member. These tests hold that contract: without them a
// renamed or reordered Vulkan enumerant would silently change what the Vulkan backend records.
public sealed class NeutralGpuVocabularyParityTests
{
    public static TheoryData<Type, Type> EnumPairs => new()
    {
        { typeof(GpuFormat), typeof(Silk.NET.Vulkan.Format) },
        { typeof(GpuImageType), typeof(Silk.NET.Vulkan.ImageType) },
        { typeof(GpuImageTiling), typeof(Silk.NET.Vulkan.ImageTiling) },
        { typeof(GpuImageUsage), typeof(Silk.NET.Vulkan.ImageUsageFlags) },
        { typeof(GpuImageCreateFlags), typeof(Silk.NET.Vulkan.ImageCreateFlags) },
        { typeof(GpuSampleCount), typeof(Silk.NET.Vulkan.SampleCountFlags) },
        { typeof(GpuIndexType), typeof(Silk.NET.Vulkan.IndexType) },
        { typeof(GpuImageLayout), typeof(Silk.NET.Vulkan.ImageLayout) },
        { typeof(GpuImageAspect), typeof(Silk.NET.Vulkan.ImageAspectFlags) },
        { typeof(GpuPipelineStage), typeof(Silk.NET.Vulkan.PipelineStageFlags) },
        { typeof(GpuBindPoint), typeof(Silk.NET.Vulkan.PipelineBindPoint) },
        { typeof(GpuPrimitiveTopology), typeof(Silk.NET.Vulkan.PrimitiveTopology) },
        { typeof(GpuCompareOp), typeof(Silk.NET.Vulkan.CompareOp) },
        { typeof(GpuStencilOp), typeof(Silk.NET.Vulkan.StencilOp) },
        { typeof(GpuImageViewType), typeof(Silk.NET.Vulkan.ImageViewType) },
        { typeof(GpuComponentSwizzle), typeof(Silk.NET.Vulkan.ComponentSwizzle) },
    };

    [Theory]
    [MemberData(nameof(EnumPairs))]
    public void EveryNeutralEnumMemberMatchesItsVulkanCounterpart(Type neutral, Type vulkan)
    {
        var vulkanValues = Enum.GetNames(vulkan)
            .ToDictionary(name => name, name => Convert.ToInt64(Enum.Parse(vulkan, name)));

        foreach (var name in Enum.GetNames(neutral))
        {
            Assert.True(
                vulkanValues.TryGetValue(name, out var expected),
                $"{neutral.Name}.{name} has no {vulkan.Name} member of the same name.");
            Assert.Equal(expected, Convert.ToInt64(Enum.Parse(neutral, name)));
        }
    }

    [Fact]
    public void TheClearColourUnionMatchesTheVulkanLayout()
    {
        Assert.Equal(Marshal.SizeOf<Silk.NET.Vulkan.ClearColorValue>(), Marshal.SizeOf<GpuClearColor>());

        var neutral = default(GpuClearColor);
        neutral.Float32_0 = 0.25f;
        neutral.Uint32_1 = 0xdeadbeef;
        neutral.Int32_2 = -7;
        neutral.Float32_3 = 1.0f;

        var converted = VulkanTypeConversions.ToVk(neutral);
        Assert.Equal(neutral.Float32_0, converted.Float32_0);
        Assert.Equal(neutral.Uint32_1, converted.Uint32_1);
        Assert.Equal(neutral.Int32_2, converted.Int32_2);
        Assert.Equal(neutral.Float32_3, converted.Float32_3);

        var roundTripped = VulkanTypeConversions.ToNeutral(converted);
        Assert.Equal(neutral.Uint32_0, roundTripped.Uint32_0);
        Assert.Equal(neutral.Uint32_1, roundTripped.Uint32_1);
        Assert.Equal(neutral.Uint32_2, roundTripped.Uint32_2);
        Assert.Equal(neutral.Uint32_3, roundTripped.Uint32_3);
    }

    [Fact]
    public void TheExtentConversionsRoundTrip()
    {
        var extent3 = new GpuExtent3D(3, 5, 7);
        Assert.Equal(extent3, VulkanTypeConversions.ToVk(extent3).ToNeutral());

        var extent2 = new GpuExtent2D(11, 13);
        Assert.Equal(extent2, VulkanTypeConversions.ToVk(extent2).ToNeutral());

        var handle = new GpuImageHandle(0x1234_5678_9abc_def0);
        Assert.Equal(handle, VulkanTypeConversions.ToVk(handle).ToNeutral());
    }
}
