// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using static SharpEmu.Libs.Agc.AgcBackendServices;
using Binding = SharpEmu.Libs.Agc.AgcExports.TranslatedImageBinding;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalTextureSnapshots
{
    private static bool IsQualifiedSampledSingletonVolume(Binding binding)
    {
        var d = binding.Descriptor;
        var s = binding.Shape;
        return !binding.IsStorage && !binding.IsArrayed && !binding.DynamicMip && binding.MipLevel == 0 &&
            binding.Access == GuestImageAccess.Read && s.Volume && !s.Storage && !s.Arrayed && !s.Cube &&
            !s.OneDimensional && !s.Multisampled && !s.DepthCompare && !s.Atomic && !s.R128 && !s.DynamicMip &&
            s.NumericClass == TextureNumericClass.Float && binding.ResourceDescriptor is { Count: >= 8 } raw &&
            AgcBackendServices.TryDecodeTextureDescriptor(raw, out var actual) && actual == d &&
            d.Address != 0 && (d.Address & 4095) == 0 && d.Address <= ulong.MaxValue - 4096 &&
            d.Width == 1 && d.Height == 1 && d.Depth == 1 && d.Pitch == 1 &&
            d.Format == 10 && d.NumberType == 9 && d.Type == 10 && d.TileMode == 5 &&
            d.ResourceMipLevels == 1 && d.BaseLevel == 0 && d.LastLevel == 0 &&
            (raw[1] & 0xFFFF_FF00u) == 0x0820_0000 && raw[2] == 0 && raw[3] == 0xA050_0FAC &&
            raw[4] == 0 && raw[5] == 0x0070_0000 && raw[6] == 0 && raw[7] == 0;
    }

    private static void RequireSampledSingletonVolume(Binding binding)
    {
        if (!IsQualifiedSampledSingletonVolume(binding))
            throw new NotSupportedException(
                $"Metal sampled tiled volume requires the complete static mode5 RGBA8sRGB 1x1x1 origin descriptor and Float read-only 3D shape: descriptor={binding.Descriptor} shape={binding.Shape} access={binding.Access} selected={binding.MipLevel} raw=[{string.Join(",", binding.ResourceDescriptor?.Select(static word => $"{word:X8}") ?? [])}].");
    }

    internal static void ValidateSampledSingletonVolumeUpload(GuestDrawTexture texture)
    {
        if (texture.Descriptor is not { Length: >= 8 } raw ||
            !AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor))
            throw new NotSupportedException("Metal sampled tiled volume upload requires complete original raw descriptor words.");
        RequireSampledSingletonVolume(new Binding(descriptor, false, texture.MipLevel, [],
            IsArrayed: texture.ArrayedView, ResourceDescriptor: raw, Shape: texture.Shape, Access: texture.Access));
        if (texture.Address != descriptor.Address || texture.Width != 1 || texture.Height != 1 || texture.Depth != 1 ||
            texture.Pitch != 1 || texture.Format != 10 || texture.NumberType != 9 || texture.Type != 10 || texture.TileMode != 5 ||
            texture.MipLevels != 1 || texture.BaseMipLevel != 0 || texture.ResourceMipLevels != 1 ||
            texture.DstSelect != 0xFAC || texture.ArrayLayers != 1 || texture.SourceByteCount != 4 ||
            texture.RgbaPixels.Length != 4 || texture.TiledSource is not null || texture.Detile is not null ||
            texture.MipUploads is { Length: > 0 } || texture.IsFallback)
            throw new NotSupportedException("Metal sampled singleton volume upload requires its four owned origin bytes and exact native 3D view; deferred or empty source recovery is unsupported.");
        MetalVideoPresenter.RequireCpuOnlySampledSingletonVolume(texture.Address);
    }

    private static bool TryCreateSampledSingletonVolume(CpuContext context, Binding binding, out GuestDrawTexture texture)
    {
        texture = default!;
        if (binding.IsStorage || binding.Descriptor.Type != Gen5TextureType3D || binding.Descriptor.TileMode == 0) return false;
        RequireSampledSingletonVolume(binding);
        var d = binding.Descriptor;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            MetalVideoPresenter.RequireCpuOnlySampledSingletonVolume(d.Address);
            var epoch = HostMemory.MappingGeneration;
            var read = GuestImageWriteTracker.BeginReadSnapshot(d.Address, 4, source: "agc.sampled-singleton-volume");
            var observation = MetalVideoPresenter.ObserveCpuTextureSnapshot(d.Address, 4, epoch);
            // Non-XOR mode5, zero mip and x=y=z=0: all address-equation bits
            // are zero. Only this voxel's four bytes are needed, never an XY
            // detile or a raw tiled-volume upload. Remaining block padding is
            // neither sampled nor promoted as initialized image content.
            var pixels = new byte[4];
            if (!context.Memory.TryRead(d.Address, pixels))
                throw new InvalidOperationException($"Metal sampled singleton volume origin is unreadable: addr=0x{d.Address:X} required=4.");
            if (!GuestImageWriteTracker.IsReadSnapshotStable(read) ||
                !MetalVideoPresenter.IsCpuTextureSnapshotAdmissionStable(d.Address, observation)) continue;
            MetalVideoPresenter.RequireCpuOnlySampledSingletonVolume(d.Address);
            texture = new GuestDrawTexture(d.Address, 1, 1, 10, 9, pixels, IsFallback: false,
                IsStorage: false, MipLevels: 1, MipLevel: 0, BaseMipLevel: 0, ResourceMipLevels: 1,
                Pitch: 1, TileMode: 5, DstSelect: d.DstSelect, Sampler: ToGuestSampler(binding.SamplerDescriptor),
                WriteGeneration: read.Active ? read.Generation : -1, Type: 10, Depth: 1,
                SourceByteCount: 4, Descriptor: binding.ResourceDescriptor!.ToArray(), Shape: binding.Shape, Access: binding.Access);
            MetalVideoPresenter.RegisterCpuTextureSnapshotProvenance(texture, observation);
            return true;
        }
        throw new InvalidOperationException("Metal sampled singleton volume changed during all three owned-copy attempts.");
    }
}

internal static partial class MetalVideoPresenter
{
    // The new capability is CPU-origin only. Advertised or materialized GPU
    // aliases cannot become stale RAM snapshots merely because the origin has
    // four bytes. Check before copying and again at materialization.
    internal static void RequireCpuOnlySampledSingletonVolume(ulong address)
    {
        lock (_gate)
        {
            bool SameBlock(ulong other) => (other & ~4095UL) == address;
            bool Overlaps(ulong other, ulong bytes) => (UInt128)other < (UInt128)address + 4096 &&
                (UInt128)address < (UInt128)other + bytes;
            if (_availableGuestImages.Keys.Any(SameBlock) || _guestImages.Keys.Any(SameBlock) ||
                _guestDepthImages.Keys.Any(SameBlock) || _guestDepthReadAliases.Keys.Any(SameBlock) ||
                _latestTextureSources.Keys.Any(SameBlock) || _gpuTextureSourceAddresses.Any(SameBlock) ||
                _guestImageExtents.Any(pair => Overlaps(pair.Key, pair.Value.ByteCount)) ||
                _latestTextureSources.Values.Any(source => Overlaps(source.View.Address,
                    Math.Max(source.ByteCount, source.ObservedGuestByteCount))))
                throw new NotSupportedException($"Metal sampled singleton volume at 0x{address:X} has a GPU alias in its physical block; ordered native volume alias ownership is unsupported.");
        }
    }
}
