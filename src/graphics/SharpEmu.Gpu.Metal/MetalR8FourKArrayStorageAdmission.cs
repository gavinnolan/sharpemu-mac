// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using TextureDescriptor = SharpEmu.Libs.Agc.AgcExports.TextureDescriptor;
using static SharpEmu.Libs.Agc.AgcBackendServices;

namespace SharpEmu.Gpu.Metal;

// Finite captured SH/Octopath write-only R8Uint view. PAL0x344 agrees
// with all 8,294,400 coordinates under the existing Gnm model; the last
// physical block row owns 552,960 padding bytes. Other topology is unqualified.
internal static class MetalR8FourKArrayStorageAdmission
{
    internal static bool IsQualified(TextureDescriptor descriptor, IReadOnlyList<uint>? raw,
        bool arrayed, uint selectedMip, ShaderImageShape shape, GuestImageAccess access) =>
        raw is { Count: 8 } && TryDecodeTextureDescriptor(raw, out var actual) && actual == descriptor &&
        descriptor.Address != 0 && (descriptor.Address & 0xFFFFUL) == 0 &&
        descriptor.Type == 13 && descriptor.Width == 3840 && descriptor.Height == 2160 && descriptor.Depth == 1 &&
        descriptor.Pitch == 3840 && descriptor.Format == 1 && descriptor.NumberType == 4 &&
        descriptor.TileMode == 24 && descriptor.DstSelect == 0x204 &&
        descriptor.BaseLevel == 0 && descriptor.LastLevel == 0 && descriptor.MaxMip == 0 && selectedMip == 0 &&
        descriptor.BaseArray == 0 && descriptor.ArrayPitch == 0 && descriptor.MinLod == 0 && descriptor.MinLodWarn == 0 &&
        descriptor.MetadataAddress == 0 && descriptor.DescriptorFlags == 0 && descriptor.BcSwizzle == 0 &&
        // Includes the width's low two bits in WORD1; no lossy format-only match.
        (raw[1] & 0xFFFF_FF00u) == 0xC050_0000u && raw[2] == 0x021B_C3BFu && raw[3] == 0xD180_0204u &&
        raw[4] == 0 && raw[5] == 0x0070_0000u && raw[6] == 0 && raw[7] == 0 &&
        access == GuestImageAccess.Write && arrayed && shape.Arrayed && shape.Storage &&
        shape.NumericClass == TextureNumericClass.Uint &&
        !shape.Volume && !shape.Cube && !shape.DynamicMip && !shape.OneDimensional &&
        !shape.Multisampled && !shape.DepthCompare && !shape.R128 && !shape.Atomic;

    internal static bool IsQualified(GuestDrawTexture texture) =>
        texture.Descriptor is { Length: 8 } raw && TryDecodeTextureDescriptor(raw, out var descriptor) &&
        IsQualified(descriptor, raw, texture.ArrayedView, texture.MipLevel, texture.Shape, texture.Access) &&
        texture.IsStorage && texture.Address == descriptor.Address && texture.Type == 13 &&
        texture.Width == 3840 && texture.Height == 2160 && texture.Pitch == 3840 &&
        texture.Format == 1 && texture.NumberType == 4 && texture.TileMode == 24 && texture.DstSelect == 0x204 &&
        texture.ArrayLayers == 1 && texture.Depth == 1 && texture.SourceByteCount == 8847360UL && texture.BaseMipLevel == 0 &&
        texture.MipLevels == 1 && texture.ResourceMipLevels == 1 &&
        texture.MipUploads is not { Length: > 0 } && texture.TiledSource is null && texture.Detile is null;
}
