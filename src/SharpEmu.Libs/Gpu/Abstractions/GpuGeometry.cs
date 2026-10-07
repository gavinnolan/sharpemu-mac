// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Abstractions;

// A two-dimensional size in texels; the neutral stand-in for Vulkan's Extent2D.
public record struct GpuExtent2D(uint Width, uint Height);

// A three-dimensional size in texels; the neutral stand-in for Vulkan's Extent3D.
public record struct GpuExtent3D(uint Width, uint Height, uint Depth);

// An opaque handle to a backend image view the host handed the engine. The engine only
// compares it against zero and prints it, so the backend chooses what the bits mean: the
// Vulkan host stores the VkImageView handle, the Metal host stores the image slot index.
public readonly record struct GpuImageHandle(ulong Handle);

// What the device can do with one image format, usage and tiling combination; the neutral
// stand-in for the subset of Vulkan's ImageFormatProperties the engine reads.
public record struct GpuImageFormatProperties
{
    public GpuExtent3D MaxExtent;
    public uint MaxMipLevels;
    public uint MaxArrayLayers;
    public GpuSampleCount SampleCounts;
    public ulong MaxResourceSize;
}

// How a view routes the image's channels into the shader's R, G, B and A; the neutral
// stand-in for Vulkan's ComponentMapping.
public record struct GpuComponentMapping(
    GpuComponentSwizzle R,
    GpuComponentSwizzle G,
    GpuComponentSwizzle B,
    GpuComponentSwizzle A);
