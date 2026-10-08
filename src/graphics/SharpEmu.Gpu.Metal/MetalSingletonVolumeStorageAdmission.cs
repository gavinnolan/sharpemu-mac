// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using TextureDescriptor = SharpEmu.Libs.Agc.AgcExports.TextureDescriptor;
using static SharpEmu.Libs.Agc.AgcBackendServices;

namespace SharpEmu.Gpu.Metal;

// Actual Octopath seq24: one RGBA8Uint origin voxel in a complete mode27 block.
// No general XYZ equation, neighboring voxel, mip, or format is admitted here.
internal static class MetalSingletonVolumeStorageAdmission
{
    internal const ulong PhysicalBytes = 65536;
    internal static bool IsCandidate(TextureDescriptor descriptor) =>
        descriptor.Type == 10 && descriptor.TileMode == 27 && descriptor.Width == 1 &&
        descriptor.Height == 1 && descriptor.Depth == 1 && descriptor.Format == 10 && descriptor.NumberType == 4;
    internal static bool IsQualified(TextureDescriptor descriptor, IReadOnlyList<uint>? raw,
        bool arrayed, uint selectedMip, ShaderImageShape shape, GuestImageAccess access) =>
        raw is { Count: 8 } && TryDecodeTextureDescriptor(raw, out var actual) && actual == descriptor &&
        descriptor.Address != 0 && (descriptor.Address & 0xFFFFUL) == 0 &&
        descriptor.Width == 1 && descriptor.Height == 1 && descriptor.Depth == 1 && descriptor.Pitch == 1 &&
        descriptor.Format == 10 && descriptor.NumberType == 4 && descriptor.Type == 10 && descriptor.TileMode == 27 &&
        descriptor.DstSelect == 0xFAC && descriptor.BaseLevel == 0 && descriptor.LastLevel == 0 && descriptor.MaxMip == 0 &&
        descriptor.BaseArray == 0 && descriptor.ArrayPitch == 0 && descriptor.MinLod == 0 && descriptor.MinLodWarn == 0 &&
        descriptor.MetadataAddress == 0 && descriptor.DescriptorFlags == 0 && descriptor.BcSwizzle == 0 &&
        (raw[1] & 0xFFFFFF00U) == 0x03C00000U && raw[2] == 0 && raw[3] == 0xA1B00FACU &&
        raw[4] == 0 && raw[5] == 0x00700000U && raw[6] == 0 && raw[7] == 0 && selectedMip == 0 &&
        !arrayed && shape.Volume && shape.Storage && shape.NumericClass == TextureNumericClass.Uint &&
        !shape.Arrayed && !shape.Cube && !shape.DynamicMip && !shape.OneDimensional && !shape.Multisampled &&
        !shape.DepthCompare && !shape.R128 && !shape.Atomic && access != GuestImageAccess.Unknown &&
        (access & ~(GuestImageAccess.Read | GuestImageAccess.Write)) == 0;

    internal static bool IsQualified(GuestDrawTexture texture) =>
        texture.Descriptor is { Length: 8 } raw && TryDecodeTextureDescriptor(raw, out var descriptor) &&
        IsQualified(descriptor, raw, texture.ArrayedView, texture.MipLevel, texture.Shape, texture.Access) &&
        texture.IsStorage && !texture.IsFallback && texture.Address == descriptor.Address &&
        texture.Width == 1 && texture.Height == 1 && texture.Depth == 1 && texture.ArrayLayers == 1 &&
        texture.Type == 10 && texture.Format == 10 && texture.NumberType == 4 && texture.TileMode == 27 &&
        texture.Pitch == 1 && texture.DstSelect == 0xFAC && texture.BaseMipLevel == 0 &&
        texture.MipLevels == 1 && texture.ResourceMipLevels == 1 &&
        texture.MipUploads is not { Length: > 0 } && texture.TiledSource is null && texture.Detile is null;
}
