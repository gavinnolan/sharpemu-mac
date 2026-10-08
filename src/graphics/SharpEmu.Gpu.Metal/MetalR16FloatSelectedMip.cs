// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Descriptor = SharpEmu.Libs.Agc.AgcExports.TextureDescriptor;

namespace SharpEmu.Gpu.Metal;

// Finite captured SH/Octo class, conditional on the existing modeled GFX10
// topology. PAL c5e/0x344 independently matches all selected coordinates.
// Neither mip-tail addressing nor sampled/allocation-wide coherence is admitted.
internal static class MetalR16FloatSelectedMip
{
    internal const ulong ChainBytes = 0xAB0000;
    internal readonly record struct Placement(uint Width, ulong Offset, ulong Bytes)
    {
        internal ulong End => Offset + Bytes;
    }

    internal static bool IsCandidate(Descriptor resource) =>
        RequiresPhysicalSelectedView(resource.Width, resource.Height, resource.Format,
            resource.NumberType, resource.TileMode, resource.ResourceMipLevels);

    internal static bool RequiresPhysicalSelectedView(uint width, uint height, uint format,
        uint numberType, uint tileMode, uint resourceMipLevels) =>
        resourceMipLevels > 1 &&
        // Linear mip0 already has a qualified base-address seed path. Only
        // tiled R16 chains need this physical selected-mip interception; keep
        // the captured geometry gate so mutated raw classes still fail closed.
        (tileMode != 0 && format == 2 && numberType == 7 ||
         width == 2048 && height == 2048 && resourceMipLevels == 11);

    internal static bool TryPlacement(Descriptor resource, uint selected, out Placement placement)
    {
        placement = default;
        if (selected > 3 || resource.Address == 0 || (resource.Address & 0xFFFF) != 0 ||
            resource.Address > ulong.MaxValue - ChainBytes || resource.Width != 2048 || resource.Height != 2048 ||
            resource.Pitch != 2048 || resource.Format != 2 || resource.NumberType != 7 || resource.TileMode != 27 ||
            resource.Type != 9 || resource.BaseLevel != selected || resource.LastLevel != selected ||
            resource.Depth != 1 || resource.BaseArray != 0 || resource.ArrayPitch != 0 || resource.MaxMip != 10 ||
            resource.ResourceMipLevels != 11 || resource.MinLod != 0 || resource.MinLodWarn != 0 ||
            resource.BcSwizzle != 0 || resource.MetadataAddress != 0 || resource.DescriptorFlags != 0 ||
            !resource.HasExtendedDescriptor || resource.DstSelect != 0x204) return false;
        placement = selected switch
        {
            0 => new(2048, 0x2B0000, 0x800000),
            1 => new(1024, 0xB0000, 0x200000),
            2 => new(512, 0x30000, 0x80000),
            _ => new(256, 0x10000, 0x20000),
        };
        return true;
    }

    internal static bool TryQualified(Descriptor resource, IReadOnlyList<uint>? raw, uint selected,
        ShaderImageShape shape, GuestImageAccess access, out Placement placement)
    {
        if (!TryPlacement(resource, selected, out placement) || raw is not { Count: 8 } ||
            !AgcBackendServices.TryDecodeTextureDescriptor(raw, out var actual) || actual != resource ||
            raw[0] != (uint)(resource.Address >> 8) || raw[1] != (0xC0D00000u | (uint)(resource.Address >> 40)) ||
            raw[2] != 0x01FFC1FF || raw[3] != (0x91B00204u | selected << 12 | selected << 16) ||
            raw[4] != 0 || raw[5] != 0x007000A0 || raw[6] != 0 || raw[7] != 0 ||
            !shape.Storage || shape.NumericClass != TextureNumericClass.Float || shape.Volume || shape.Arrayed ||
            shape.Cube || shape.OneDimensional || shape.Multisampled || shape.DepthCompare || shape.R128 ||
            shape.Atomic || shape.DynamicMip || access != GuestImageAccess.Write) return false;
        return true;
    }

    internal static bool IsQualifiedUpload(GuestDrawTexture texture, Descriptor resource)
    {
        return TryQualified(resource, texture.Descriptor, texture.MipLevel, texture.Shape, texture.Access, out var selected) &&
            texture.Address == resource.Address && texture.Width == selected.Width && texture.Height == selected.Width &&
            texture.Pitch == selected.Width && texture.Type == 9 && texture.Depth == 1 && !texture.ArrayedView &&
            texture.ArrayLayers == 1 && texture.IsStorage && texture.Format == 2 && texture.NumberType == 7 &&
            texture.TileMode == 27 && texture.DstSelect == 0x204 && texture.ResourceMipLevels == 11 &&
            texture.MipLevels == 1 && texture.BaseMipLevel == texture.MipLevel && texture.SourceByteCount == selected.End &&
            texture.MipUploads is not { Length: > 0 } && texture.TiledSource is null && texture.Detile is null;
    }

    internal static bool SameChainDisjoint(Descriptor a, uint aMip, Descriptor b, uint bMip) =>
        TryPlacement(a, aMip, out var x) && TryPlacement(b, bMip, out var y) &&
        (a with { BaseLevel = 0, LastLevel = 0 }) == (b with { BaseLevel = 0, LastLevel = 0 }) &&
        (x.End <= y.Offset || y.End <= x.Offset);
}
