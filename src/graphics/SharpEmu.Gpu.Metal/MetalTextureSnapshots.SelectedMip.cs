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
    // Deliberately bounded to the DS descriptor class. This is not a generic
    // tiled mip-chain implementation or an allocation-wide alias resolver.
    private static bool TryCreateSelectedStorageMip(CpuContext context, Binding binding, out GuestDrawTexture texture)
    {
        texture = default!;
        if (!binding.IsStorage || binding.MipLevel == 0) return false;
        var d = binding.Descriptor;
        if (binding.ResourceDescriptor is not { Count: >= 8 } raw ||
            !TryDecodeTextureDescriptor(raw, out var actual) || actual != d ||
            d.Address == 0 || (d.Address & 0xFFFF) != 0 || d.Type != 9 ||
            d.Width != 512 || d.Height != 256 || d.Pitch != 512 ||
            d.Format != 4 || d.NumberType != 7 || d.TileMode != 9 ||
            d.Depth != 1 || d.BaseArray != 0 || d.ArrayPitch != 0 ||
            d.MetadataAddress != 0 || d.DescriptorFlags != 0 || d.BcSwizzle != 0 || d.MinLod != 0 || d.MinLodWarn != 0 ||
            d.ResourceMipLevels != 9 || d.BaseLevel != 1 || d.LastLevel != 1 || binding.MipLevel != 1 ||
            binding.IsArrayed || binding.DynamicMip || binding.Shape.Volume || binding.Shape.Arrayed ||
            binding.Shape.Cube || binding.Shape.OneDimensional || binding.Shape.Multisampled ||
            binding.Shape.DepthCompare || binding.Shape.R128 || binding.Shape.Atomic || binding.Shape.DynamicMip ||
            !binding.Shape.Storage || binding.Shape.NumericClass != TextureNumericClass.Float ||
            binding.Access == GuestImageAccess.Unknown || (binding.Access & ~(GuestImageAccess.Read | GuestImageAccess.Write)) != 0)
            throw new NotSupportedException($"Metal selected storage mip requires the qualified complete static R32Float 512x256/mip1 mode9 resource: addr=0x{d.Address:X} selected={binding.MipLevel} raw=[{string.Join(",", binding.ResourceDescriptor?.Select(static w => $"{w:X8}") ?? [])}].");

        if (!GnmTiling.TryGetMipChainPlacement(d.TileMode, (int)d.Width, (int)d.Height, 4,
            d.ResourceMipLevels, out var placements, out var chainBytes))
            throw new NotSupportedException("Metal cannot place selected storage mip in its original tiled resource.");
        var selected = placements[binding.MipLevel];
        var width = Math.Max(d.Width >> (int)binding.MipLevel, 1);
        var height = Math.Max(d.Height >> (int)binding.MipLevel, 1);
        var end = checked(selected.ByteOffset + selected.ByteCount);
        if (selected.InMipTail || selected.ElementsWide != width || selected.ElementsHigh != height ||
            end > chainBytes || selected.ByteCount > MaximumSnapshotBytes || d.Address > ulong.MaxValue - end)
            throw new NotSupportedException("Metal selected storage mip placement is outside the qualified non-tail resource range.");

        texture = SelectedMipTexture(binding, width, height, end, []);
        if (MetalVideoPresenter.TryAttachLiveSelectedStorageMip(context.Memory, d, texture)) return true;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var epoch = HostMemory.MappingGeneration;
            var read = GuestImageWriteTracker.BeginReadSnapshot(d.Address, end, source: "agc.selected-storage-mip");
            var observation = MetalVideoPresenter.ObserveCpuTextureSnapshot(d.Address, end, epoch);
            var hasToken = GuestImageWriteTracker.TryGetContentToken(d.Address, out var token);
            var source = new byte[checked((int)selected.ByteCount)];
            if (!context.Memory.TryRead(d.Address + selected.ByteOffset, source))
                throw new InvalidOperationException($"Metal selected storage mip is unreadable: addr=0x{d.Address:X} mip={binding.MipLevel} offset={selected.ByteOffset} bytes={selected.ByteCount}.");
            var pixels = new byte[checked((int)(width * height * 4))];
            if (!GnmTiling.TryDetile(source, pixels, d.TileMode, (int)width, (int)height, 4))
                throw new NotSupportedException("Metal selected storage mip cannot reinterpret failed detiling as linear bytes.");
            if (!GuestImageWriteTracker.IsReadSnapshotStable(read) ||
                !MetalVideoPresenter.IsCpuTextureSnapshotAdmissionStable(d.Address, observation) ||
                (hasToken && (!GuestImageWriteTracker.TryGetContentToken(d.Address, out var after) || after != token))) continue;
            texture = SelectedMipTexture(binding, width, height, end, pixels) with
            { WriteGeneration = read.Active ? read.Generation : -1 };
            MetalVideoPresenter.RegisterCpuTextureSnapshotProvenance(texture, observation);
            MetalVideoPresenter.RegisterSelectedStorageMip(context.Memory, d, texture, epoch, token, hasToken, observation);
            return true;
        }
        throw new InvalidOperationException("Metal selected storage mip changed during all three owned-copy attempts.");
    }
    private static GuestDrawTexture SelectedMipTexture(Binding binding, uint width, uint height, ulong end, byte[] pixels)
    {
        var d = binding.Descriptor;
        return new GuestDrawTexture(d.Address, width, height, d.Format, d.NumberType, pixels,
            IsFallback: false, IsStorage: true, MipLevels: 1, MipLevel: binding.MipLevel,
            BaseMipLevel: d.ViewBaseLevel, ResourceMipLevels: d.ResourceMipLevels,
            Pitch: width, TileMode: d.TileMode, DstSelect: d.DstSelect, Type: d.Type, Depth: 1,
            SourceByteCount: end, Descriptor: binding.ResourceDescriptor!.ToArray(), Shape: binding.Shape, Access: binding.Access);
    }
}
