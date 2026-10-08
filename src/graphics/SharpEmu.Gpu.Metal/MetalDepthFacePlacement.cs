// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Kernel;
using CpuTextureSnapshotObservation = SharpEmu.Gpu.Metal.MetalVideoPresenter.CpuTextureSnapshotObservation;

namespace SharpEmu.Gpu.Metal;

// Original normalized DB placement. This is neither a backing lease nor a
// successful GPU completion receipt. The 8-byte probe observes the COMPLETE
// original backing, and is discarded; it never supplies sampled depth bytes.
internal static class MetalDepthFacePlacement
{
    internal static bool Enabled = string.Equals(Environment.GetEnvironmentVariable(
        "SHARPEMU_METAL_NATIVE_DEPTH_CUBE"), "1", StringComparison.Ordinal);
    internal const ulong FaceBytes = 0x20000;
    internal sealed record Observation(ICpuMemory Memory, ICpuMappedCopySource Source,
        CpuMappedCopyObservation Mapping, CpuTextureSnapshotObservation Content,
        long Session, DepthTargetWords Registers, ulong BackingAddress,
        ulong BackingBytes, ulong Address, uint Layer);
    private static readonly ConditionalWeakTable<GuestDepthTarget, Observation> Targets = new();
    internal sealed record DepthSpan(ICpuMemory Memory, ulong Address, ulong Bytes);
    private static readonly ConditionalWeakTable<GuestDepthTarget, DepthSpan> Depths = new();
    internal static bool TryGetDepthSpan(GuestDepthTarget target, out DepthSpan span)
    {
        span = null!; return Enabled && Depths.TryGetValue(target, out span!);
    }
    internal sealed record ColorSpan(ICpuMemory Memory, ulong Address, ulong Bytes);
    private static readonly ConditionalWeakTable<GuestRenderTarget, ColorSpan> Colors = new();
    internal static bool TryGet(GuestDepthTarget target, out Observation observation)
    {
        observation = null!; return Enabled && Targets.TryGetValue(target, out observation!);
    }
    internal static bool TryGetColorSpan(GuestRenderTarget target, out ColorSpan span)
    {
        span = null!; return Enabled && Colors.TryGetValue(target, out span!);
    }
    internal static GuestRenderTarget RecordColorSpan(ICpuMemory memory, ColorTargetResolution resolution, GuestRenderTarget target)
    {
        if (Enabled)
        {
            var span = resolution.Request.Description.Data;
            if (!span.IsValid || span.Size == 0)
                throw new NotSupportedException("Native depth cube sibling color writer lacks its complete normalized span.");
            Colors.Add(target, new(memory, span.Address, span.Size));
        }
        return target;
    }

    internal static ulong SelectedAddress(DepthTargetResolution resolution, DepthTargetWords words)
    {
        var d = resolution.Request.Description;
        var view = resolution.Request.View;
        if (resolution.Width != 256 || resolution.Height != 256 || resolution.Samples != 1 ||
            resolution.HasStencil || words.DepthFormat != GuestDepthFormat.Z16 ||
            ((words.ZInfo >> 4) & 31) != 24 || words.MaxMip != 0 || words.ViewMipLevel != 0 ||
            words.SliceStart != words.SliceMax || words.SliceStart >= 6 ||
            words.ZReadBase == 0 || words.ZReadBase != words.ZWriteBase || (words.ZReadBase & 0xFFFF) != 0 ||
            d.Data.Address != words.ZReadBase || d.Data.Size != FaceBytes * (words.SliceMax + 1UL) ||
            resolution.DepthAddress != d.Data.Address || resolution.DepthSize != d.Data.Size ||
            d.BytesPerBlock != 2 || d.Pitch != 256 || d.Resources.Levels != 1 || d.Resources.Layers != words.SliceMax + 1 ||
            d.PixelFormat != GpuFormat.D16Unorm || d.Extent.Width != 256 || d.Extent.Height != 256 || d.Extent.Depth != 1 ||
            d.MipLayout[0].Offset != 0 || d.MipLayout[0].Size != d.Data.Size ||
            view.BaseLayer != words.SliceStart || view.LayerCount != 1 || view.BaseLevel != 0 || view.LevelCount != 1 ||
            words.ZReadBase > ulong.MaxValue - d.Data.Size)
            throw new NotSupportedException("Metal native depth cube face requires the original normalized 256-square Z16 mode24 single selected DB layer and complete backing footprint.");
        return checked(d.Data.Address + words.SliceStart * FaceBytes);
    }

    internal static GuestDepthTarget Project(ICpuMemory memory, DepthTargetResolution resolution,
        DepthTargetWords words, GuestDepthTarget target)
    {
        if (!Enabled) return target;
        if (resolution.Width != 256 || resolution.Height != 256 || words.DepthFormat != GuestDepthFormat.Z16 || ((words.ZInfo >> 4) & 31) != 24)
        {
            if (resolution.DepthAddress == 0 || resolution.DepthSize == 0 || resolution.DepthAddress > ulong.MaxValue - resolution.DepthSize)
                throw new NotSupportedException("Native cube sibling depth writer lacks its original normalized full span.");
            Depths.Add(target, new(memory, resolution.DepthAddress, resolution.DepthSize));
            return target;
        }
        var address = SelectedAddress(resolution, words);
        if (memory is not ICpuMappedCopySource source)
            throw new NotSupportedException("Metal native depth cube producer requires an original full-footprint mapping observation.");
        var originalSession = MetalVideoPresenter.CurrentStorageCpuSeedSession;
        _ = GuestImageWriteTracker.BeginReadSnapshot(resolution.DepthAddress, resolution.DepthSize, source: "metal.depth-face.original");
        var epoch = HostMemory.MappingGeneration;
        var content = MetalVideoPresenter.ObserveCpuTextureSnapshot(resolution.DepthAddress, resolution.DepthSize, epoch);
        var probe = new byte[8];
        if (source.TryReadMappedCopy(resolution.DepthAddress, resolution.DepthSize, resolution.DepthAddress, probe, out var mapping) != CpuMappedCopyResult.Copied ||
            mapping is null || mapping.FootprintAddress != resolution.DepthAddress || mapping.FootprintByteCount != resolution.DepthSize ||
            mapping.SourceAddress != resolution.DepthAddress || mapping.SourceByteCount != 8 || !content.Observed ||
            originalSession != MetalVideoPresenter.CurrentStorageCpuSeedSession || !source.IsMappedCopyObservationCurrent(mapping) ||
            !MetalVideoPresenter.IsCpuTextureSnapshotObservationStable(resolution.DepthAddress, content))
            throw new NotSupportedException("Metal native depth face original mapping/session/content changed before admission.");
        var projected = target with { ReadAddress = address, WriteAddress = address };
        Targets.Add(projected, new(memory, source, mapping, content, originalSession, words,
            resolution.DepthAddress, resolution.DepthSize, address, words.SliceStart));
        Depths.Add(projected, new(memory, address, FaceBytes));
        return projected;
    }

    internal static void RequireOriginalCurrent(Observation original)
    {
        if (!original.Content.Observed || original.Session != MetalVideoPresenter.CurrentStorageCpuSeedSession ||
            !original.Source.IsMappedCopyObservationCurrent(original.Mapping) ||
            !MetalVideoPresenter.IsCpuTextureSnapshotObservationStable(original.BackingAddress, original.Content))
            throw new NotSupportedException("Metal native depth face lost its ORIGINAL mapping/session/content observation.");
    }
}
