// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal;

// Finite physical-view metadata, not a mapping/backing lease or a content
// freshness receipt. The normalized resolver owns the original allocation
// layout; a one-level Metal attachment addresses only the selected guest mip.
internal static class MetalColorMipPlacement
{
    internal static bool Enabled = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_METAL_COLOR_PHYSICAL_VIEW"), "1", StringComparison.Ordinal);
    internal sealed record Observation(ICpuMemory Memory, ColorTargetWords Registers,
        ulong PhysicalAddress, ulong ByteCount, ulong BackingByteCount);
    private static readonly ConditionalWeakTable<GuestRenderTarget, Observation> Targets = new();

    internal static bool TryGet(GuestRenderTarget target, out Observation observation) =>
        Targets.TryGetValue(target, out observation!);

    internal static GuestRenderTarget Project(ICpuMemory memory, ColorTargetResolution resolution,
        ColorTargetWords words, GuestRenderTarget target)
    {
        if (!Enabled || words.MaxMip == 0) return target;
        // Select the explicit GPU reader before any original observation or
        // provider capture. Receipt identity is never rewritten afterward.
        memory = CpuGpuMemoryView.Resolve(memory);
        var exact = new ColorTargetWords(words.BaseAddress, 0, 0x40730, 0,
            0x907FC1FF, 0x4DC6C000, 72, 0, 0, 0, 0);
        var description = resolution.Request.Description;
        if (description.Resources.Levels != 10 || description.Data.Size != 0x2B0000)
            throw new NotSupportedException("Metal color mip projection lacks its complete normalized backing layout.");
        // MipLevelLayouts is a fixed sixteen-slot inline array. The exact ten
        // normalized levels are checked above; a default slot fails below.
        var mip = description.MipLayout[0];
        if (words != exact || words.BaseAddress == 0 || (words.BaseAddress & 0xFFFF) != 0 ||
            resolution.BaseAddress != words.BaseAddress || resolution.BaseMipLevel != 0 ||
            resolution.BaseArrayLayer != 0 || resolution.Samples != 1 ||
            resolution.Extent.Width != 512 || resolution.Extent.Height != 512 ||
            resolution.BackingSize != 0x2B0000 || description.Data.Address != words.BaseAddress ||
            description.Data.Size != 0x2B0000 || description.Resources.Levels != 10 ||
            description.Resources.Layers != 1 || description.BytesPerBlock != 8 ||
            description.Pitch != 512 || description.Samples != 1 || description.Extent.Depth != 1 ||
            description.PixelFormat != GpuFormat.R16G16B16A16Sfloat || description.Type != GuestImageType.Color2D || description.HasMetadata || description.HasStencil ||
            description.IsVolume || description.IsBlock || mip.Offset != 0xB0000 || mip.Size != 0x200000 ||
            target.Address != words.BaseAddress || target.Width != 512 || target.Height != 512 ||
            target.Registers != words || target.MipLevels != 1 || target.TileMode != 27 ||
            target.Format != (uint)words.Layout || target.NumberType != (uint)words.NumberType || target.ComponentSwap != (uint)words.Order)
            throw new NotSupportedException("Metal multi-mip color target requires the qualified normalized 512x512 RGBA16Float mode27 ten-mip selected-zero physical view.");
        if (words.BaseAddress > ulong.MaxValue - resolution.BackingSize)
            throw new NotSupportedException("Metal color mip physical range wraps its guest allocation.");
        var projected = target with { Address = words.BaseAddress + mip.Offset };
        var observation = new Observation(memory, words, projected.Address, mip.Size, resolution.BackingSize);
        MetalVideoPresenter.CaptureNativeColorOriginal(observation);
        try { Targets.Add(projected, observation); }
        catch (Exception primary)
        {
            MetalVideoPresenter.ReleasePreparedNativeColorTarget(projected, observation, primary);
            throw;
        }
        return projected;
    }
}
