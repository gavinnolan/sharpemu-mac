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
    private static bool IsQualifiedSampledR8Volume(Binding binding)
    {
        var d = binding.Descriptor; var s = binding.Shape;
        return !binding.IsStorage && !binding.IsArrayed && !binding.DynamicMip && binding.MipLevel == 0 &&
            binding.Access == GuestImageAccess.Read && s.Volume && !s.Storage && !s.Arrayed && !s.Cube &&
            !s.OneDimensional && !s.Multisampled && !s.DepthCompare && !s.Atomic && !s.R128 && !s.DynamicMip &&
            s.NumericClass == TextureNumericClass.Float && binding.ResourceDescriptor is { Count: 8 } raw &&
            TryDecodeTextureDescriptor(raw, out var actual) && actual == d &&
            d.Address != 0 && (d.Address & 4095) == 0 && d.Address <= ulong.MaxValue - 4096 &&
            d.Width == 4 && d.Height == 4 && d.Depth == 16 && d.Pitch == 4 &&
            d.Format == 1 && d.NumberType == 0 && d.Type == 10 && d.TileMode == 5 &&
            d.ResourceMipLevels == 1 && d.BaseLevel == 0 && d.LastLevel == 0 &&
            (raw[1] & 0xFFFF_FF00u) == 0xC010_0000 && raw[2] == 0x0000_C000 && raw[3] == 0xA050_0800 &&
            raw[4] == 15 && raw[5] == 0x0070_0000 && raw[6] == 0 && raw[7] == 0;
    }

    private static bool TryCreateSampledR8Volume(CpuContext context, Binding binding, out GuestDrawTexture texture)
    {
        texture = default!;
        if (!IsQualifiedSampledR8Volume(binding)) return false;
        var d = binding.Descriptor;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            MetalVideoPresenter.RejectKnownSampledSingletonVolumeGpuAlias(d.Address);
            var epoch = HostMemory.MappingGeneration;
            var read = GuestImageWriteTracker.BeginReadSnapshot(d.Address, 4096, source: "agc.sampled-r8-volume");
            var observation = MetalVideoPresenter.ObserveCpuTextureSnapshot(d.Address, 4096, epoch);
            var physical = new byte[4096];
            if (!context.Memory.TryRead(d.Address, physical))
                throw new InvalidOperationException($"Metal sampled R8 volume physical block is unreadable: addr=0x{d.Address:X} required=4096.");
            var linear = Gfx10SampledR8VolumeLayout.Decode4x4x16(physical);
            if (!GuestImageWriteTracker.IsReadSnapshotStable(read) ||
                !MetalVideoPresenter.IsCpuTextureSnapshotAdmissionStable(d.Address, observation)) continue;
            MetalVideoPresenter.RejectKnownSampledSingletonVolumeGpuAlias(d.Address);
            texture = new GuestDrawTexture(d.Address, 4, 4, 1, 0, linear, IsFallback: false,
                IsStorage: false, MipLevels: 1, MipLevel: 0, BaseMipLevel: 0, ResourceMipLevels: 1,
                Pitch: 4, TileMode: 5, DstSelect: 0x800, Sampler: ToGuestSampler(binding.SamplerDescriptor),
                WriteGeneration: read.Active ? read.Generation : -1, Type: 10, Depth: 16,
                SourceByteCount: 4096, Descriptor: binding.ResourceDescriptor!.ToArray(), Shape: binding.Shape, Access: binding.Access);
            MetalVideoPresenter.RegisterCpuTextureSnapshotProvenance(texture, observation);
            return true;
        }
        throw new InvalidOperationException("Metal sampled R8 volume changed during all three owned-copy attempts.");
    }

    private static void ValidateSampledR8VolumeUpload(GuestDrawTexture texture, Binding binding)
    {
        if (!IsQualifiedSampledR8Volume(binding) || texture.Address != binding.Descriptor.Address ||
            texture.Width != 4 || texture.Height != 4 || texture.Depth != 16 || texture.Pitch != 4 ||
            texture.Format != 1 || texture.NumberType != 0 || texture.Type != 10 || texture.TileMode != 5 ||
            texture.MipLevels != 1 || texture.BaseMipLevel != 0 || texture.ResourceMipLevels != 1 ||
            texture.DstSelect != 0x800 || texture.ArrayLayers != 1 || texture.SourceByteCount != 4096 ||
            texture.RgbaPixels.Length != 256 || texture.TiledSource is not null || texture.Detile is not null ||
            texture.MipUploads is { Length: > 0 } || texture.IsFallback)
            throw new NotSupportedException("Metal sampled R8 volume upload requires its complete owned256-byte XYZ seed and exact mode5 4x4x16 000R native view.");
        MetalVideoPresenter.RejectKnownSampledSingletonVolumeGpuAlias(texture.Address);
    }
}

internal static class Gfx10SampledR8VolumeLayout
{
    internal static byte[] Decode4x4x16(ReadOnlySpan<byte> physical)
    {
        if (physical.Length != 4096) throw new InvalidOperationException("The qualified R8 volume requires its full4096-byte physical block.");
        var linear = new byte[256];
        // PAL AddrLib eq70 (Navi10)/eq80 (Navi21), Kairo SDK eq118:
        // x0,x1,z0,y0,z1,y1,x2,z2,y2,x3,z3,y3.
        // X/Y higher bits are zero for this exact4x4x16 shape. Never stack XY slices.
        for (uint z = 0; z < 16; z++) for (uint y = 0; y < 4; y++) for (uint x = 0; x < 4; x++)
        {
            var offset = (x & 3) | (y & 1) << 3 | (y & 2) << 4 |
                (z & 1) << 2 | (z & 2) << 3 | (z & 4) << 5 | (z & 8) << 7;
            linear[(z * 4 + y) * 4 + x] = physical[(int)offset];
        }
        return linear;
    }
}
