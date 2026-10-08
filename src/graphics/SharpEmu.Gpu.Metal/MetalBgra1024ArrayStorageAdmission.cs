// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using TextureDescriptor = SharpEmu.Libs.Agc.AgcExports.TextureDescriptor;
using static SharpEmu.Libs.Agc.AgcBackendServices;

namespace SharpEmu.Gpu.Metal;

// Finite captured SH/Octopath/Oregon write-only view. Independent PAL0x344
// qualification covers all 1024² coordinates under the existing GnmTiling
// 16-pipe/8-packer model, not arbitrary topology, XOR, mip or array layouts.
internal static class MetalBgra1024ArrayStorageAdmission
{
    internal static bool IsQualified(TextureDescriptor descriptor, IReadOnlyList<uint>? raw,
        bool arrayed, uint selectedMip, ShaderImageShape shape, GuestImageAccess access) =>
        raw is { Count: 8 } && TryDecodeTextureDescriptor(raw, out var actual) && actual == descriptor &&
        descriptor.Address != 0 && (descriptor.Address & 0xFFFFUL) == 0 &&
        descriptor.Type == 13 && descriptor.Width == 1024 && descriptor.Height == 1024 && descriptor.Depth == 1 &&
        descriptor.Pitch == 1024 && descriptor.Format == 10 && descriptor.NumberType == 0 &&
        descriptor.TileMode == 27 && descriptor.DstSelect == 0xF2E &&
        descriptor.BaseLevel == 0 && descriptor.LastLevel == 0 && descriptor.MaxMip == 0 && selectedMip == 0 &&
        descriptor.BaseArray == 0 && descriptor.ArrayPitch == 0 && descriptor.MinLod == 0 && descriptor.MinLodWarn == 0 &&
        descriptor.MetadataAddress == 0 && descriptor.DescriptorFlags == 0 && descriptor.BcSwizzle == 0 &&
        // Includes the width's low two bits in WORD1; no lossy format-only match.
        (raw[1] & 0xFFFF_FF00u) == 0xC380_0000u && raw[2] == 0x00FF_C0FFu && raw[3] == 0xD1B0_0F2Eu &&
        raw[4] == 0 && raw[5] == 0x0070_0000u && raw[6] == 0 && raw[7] == 0 &&
        access == GuestImageAccess.Write && arrayed && shape.Arrayed && shape.Storage &&
        shape.NumericClass == TextureNumericClass.Float &&
        !shape.Volume && !shape.Cube && !shape.DynamicMip && !shape.OneDimensional &&
        !shape.Multisampled && !shape.DepthCompare && !shape.R128 && !shape.Atomic;

    internal static bool IsQualified(GuestDrawTexture texture) =>
        texture.Descriptor is { Length: 8 } raw && TryDecodeTextureDescriptor(raw, out var descriptor) &&
        IsQualified(descriptor, raw, texture.ArrayedView, texture.MipLevel, texture.Shape, texture.Access) &&
        texture.IsStorage && texture.Address == descriptor.Address && texture.Type == 13 &&
        texture.Width == 1024 && texture.Height == 1024 && texture.Pitch == 1024 &&
        texture.Format == 10 && texture.NumberType == 0 && texture.TileMode == 27 && texture.DstSelect == 0xF2E &&
        texture.ArrayLayers == 1 && texture.Depth == 1 && texture.SourceByteCount == 4194304UL && texture.BaseMipLevel == 0 &&
        texture.MipLevels == 1 && texture.ResourceMipLevels == 1 &&
        texture.MipUploads is not { Length: > 0 } && texture.TiledSource is null && texture.Detile is null;
}
