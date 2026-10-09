// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Binding = SharpEmu.Libs.Agc.AgcExports.TranslatedImageBinding;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalTextureSnapshots
{
    private static bool IsR32SampledMipZeroCandidate(Binding binding)
    {
        var d = binding.Descriptor;
        return !binding.IsStorage && d.Type == 9 && d.Format == 4 && d.NumberType == 7 &&
            d.TileMode == 9 && d.Width == 512 && d.Height == 256 && d.ResourceMipLevels == 9 &&
            d.BaseLevel == 0 && d.LastLevel == 0;
    }

    private static void RequireR32SampledMipZero(Binding binding)
    {
        var d = binding.Descriptor;
        if (!IsR32SampledMipZeroCandidate(binding) || d.Address == 0 || (d.Address & 0xFFFF) != 0 ||
            d.Address > ulong.MaxValue - 0xC0000 || d.Pitch != 512 || d.Depth != 1 || d.DstSelect != 0x924 ||
            binding.IsArrayed || binding.DynamicMip || binding.MipLevel != 0 || binding.Access != GuestImageAccess.Read ||
            binding.Shape != new ShaderImageShape(false, false, false, false, TextureNumericClass.Float) ||
            binding.ResourceDescriptor is not { Count: 8 } raw ||
            raw[0] != (uint)(d.Address >> 8) || raw[1] != (0xC1600000u | (uint)(d.Address >> 40)) ||
            raw[2] != 0x003FC07F || raw[3] != 0x90900924 || raw[4] != 0 || raw[5] != 0x00700080 || raw[6] != 0 || raw[7] != 0 ||
            !AgcBackendServices.TryDecodeTextureDescriptor(raw, out var decoded) || decoded != d ||
            !GnmTiling.TryGetMipChainPlacement(9, 512, 256, 4, 9, out var placements, out var chainBytes) ||
            chainBytes != 0xC0000 || placements[0].InMipTail || placements[0].ByteOffset != 0x40000 ||
            placements[0].ByteCount != 0x80000 || placements[0].ElementsWide != 512 || placements[0].ElementsHigh != 256)
            throw new NotSupportedException("Metal sampled R32 mip zero requires the complete static readonly 512x256 mode9 nine-mip RRRR descriptor and original physical placement.");
    }

    private static GuestDrawTexture CreateR32SampledMipZero(CpuContext context, Binding binding)
    {
        RequireR32SampledMipZero(binding);
        if (MetalVideoPresenter.TextureSourceGraphEnabled || context.Memory is not ICpuMappedReadWatchSource source)
            throw new NotSupportedException("Metal sampled R32 mip zero needs the actual original mapped read-watch provider; no legacy read or graph fallback is qualified.");
        var session = MetalVideoPresenter.CurrentSelectedStorageMipSeedSession;
        MetalVideoPresenter.RequireR32SampledMipZeroCopyPreflight(context.Memory, binding.Descriptor, session);
        var d = binding.Descriptor; var address = d.Address + 0x40000;
        var read = GuestImageWriteTracker.BeginReadSnapshot(address, 0x80000, source: "agc.r32-sampled-mip-zero");
        var observation = MetalVideoPresenter.ObserveCpuTextureSnapshot(address, 0x80000, HostMemory.MappingGeneration);
        if (!read.Active || !observation.Observed || observation.ByteCount != 0x80000)
            throw new InvalidOperationException("Metal sampled R32 mip zero lacks its original full selected-span read/content observation.");
        var tiled = new byte[0x80000]; CpuMappedCopyObservation? mapping = null;
        Exception? primary = null;
        try
        {
            var result = source.TryReadMappedCopyWithWriteWatch(d.Address, 0xC0000, address, tiled, out mapping);
            if (result != CpuMappedCopyResult.Copied || mapping is null || mapping.FootprintAddress != d.Address ||
                mapping.FootprintByteCount != 0xC0000 || mapping.SourceAddress != address || mapping.SourceByteCount != 0x80000 ||
                !source.IsMappedCopyObservationCurrent(mapping) || !source.IsMappedCopyWriteWatchCurrent(mapping))
                throw new InvalidOperationException("Metal sampled R32 mip zero original mapped copy was refused; no base-address recopy is qualified.");
            var pixels = new byte[0x80000];
            if (!GnmTiling.TryDetile(tiled, pixels, 9, 512, 256, 4))
                throw new NotSupportedException("Metal sampled R32 mip zero failed its original mode9 detile.");
            if (!GuestImageWriteTracker.IsReadSnapshotStable(read) ||
                !MetalVideoPresenter.IsCpuTextureSnapshotContentObservationStable(address, observation) ||
                !source.IsMappedCopyObservationCurrent(mapping) || !source.IsMappedCopyWriteWatchCurrent(mapping) ||
                session != MetalVideoPresenter.CurrentSelectedStorageMipSeedSession)
                throw new InvalidOperationException("Metal sampled R32 mip zero changed during its original owned copy.");
            var texture = new GuestDrawTexture(d.Address, 512, 256, 4, 7, pixels, false, false,
                ResourceMipLevels: 9, Pitch: 512, TileMode: 9, DstSelect: 0x924,
                Sampler: ToGuestSampler(binding.SamplerDescriptor), SourceByteCount: 0xC0000,
                Descriptor: binding.ResourceDescriptor!.ToArray(), Shape: binding.Shape, Access: binding.Access);
            MetalVideoPresenter.RegisterOwnedR32SampledMipZero(context.Memory, source, mapping, texture, session);
            return texture;
        }
        catch (Exception failure) { primary = failure; throw; }
        finally { MetalVideoPresenter.ReleaseOriginalSelectedWriteWatch(source, mapping, primary); }
    }
}
