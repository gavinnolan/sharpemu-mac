// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using TextureDescriptor = SharpEmu.Libs.Agc.AgcExports.TextureDescriptor;
using static SharpEmu.Libs.Agc.AgcBackendServices;

namespace SharpEmu.Gpu.Metal;

// This admission is a single origin texel in layer zero, not a general array
// layout. The qualified mode24/27 classes own full 64KiB physical blocks.
// Requiring a block-aligned descriptor address excludes embedded within-block/XOR address bits; at
// X=Y=layer=0 every spatial term is zero. Still use the real detile path.
internal static class MetalSingletonArrayStorageAdmission
{
    private static bool HasQualifiedTypedFormat(TextureDescriptor descriptor, IReadOnlyList<uint> raw,
        ShaderImageShape shape) =>
        (descriptor.TileMode == 27 && descriptor.Format == 10 && descriptor.NumberType == 0 &&
            shape.NumericClass == TextureNumericClass.Float && (raw[1] & 0xFFFF_FF00u) == (56u << 20)) ||
        // Exact SH/Octopath R32Uint X001 class. Do not reinterpret integer
        // storage as float or normalize unqualified selectors/hints.
        (descriptor.TileMode == 27 && descriptor.Format == 4 && descriptor.NumberType == 4 &&
            shape.NumericClass == TextureNumericClass.Uint && (raw[1] & 0xFFFF_FF00u) == (20u << 20) &&
            descriptor.DstSelect == 0x204 && raw[5] == 0x0070_0000u) ||
        // Proactive canonical R32Float class, not additional captured game T#s.
        // Exact unified/number/shader-class agreement; no numeric conversion.
        (descriptor.TileMode == 27 && descriptor.Format == 4 && descriptor.NumberType == 7 &&
            shape.NumericClass == TextureNumericClass.Float && (raw[1] & 0xFFFF_FF00u) == (22u << 20) &&
            descriptor.DstSelect == 0x204 && raw[5] == 0x0070_0000u) ||
        // Exact SH/Octopath/Oregon RGBA16Float identity/PERF7 class.
        // Its origin texel is eight owned bytes, never a four-byte conversion.
        (descriptor.TileMode == 27 && descriptor.Format == 12 && descriptor.NumberType == 7 &&
            shape.NumericClass == TextureNumericClass.Float && (raw[1] & 0xFFFF_FF00u) == (71u << 20) &&
            descriptor.DstSelect == 0xFAC && raw[5] == 0x0070_0000u) ||
        // Exact SH/Octopath R8Uint X001/PERF7 mode24 origin class.
        // The independent 8bpp layout owns 64KiB, with one byte at origin.
        (descriptor.TileMode == 24 && descriptor.Format == 1 && descriptor.NumberType == 4 &&
            shape.NumericClass == TextureNumericClass.Uint && (raw[1] & 0xFFFF_FF00u) == (5u << 20) &&
            descriptor.DstSelect == 0x204 && raw[5] == 0x0070_0000u) ||
        // Exact Oregon RGBA8Uint identity/PERF7 class (unified0x3C).
        // This is four bytes; RGBA16Uint unified0x45 remains unqualified.
        (descriptor.TileMode == 27 && descriptor.Format == 10 && descriptor.NumberType == 4 &&
            shape.NumericClass == TextureNumericClass.Uint && (raw[1] & 0xFFFF_FF00u) == (60u << 20) &&
            descriptor.DstSelect == 0xFAC && raw[5] == 0x0070_0000u);

    internal static bool IsQualified(TextureDescriptor descriptor, IReadOnlyList<uint>? raw,
        bool arrayed, uint selectedMip, ShaderImageShape shape, GuestImageAccess access) =>
        raw is { Count: 8 } && TryDecodeTextureDescriptor(raw, out var actual) && actual == descriptor &&
        descriptor.Address != 0 && (descriptor.Address & 0xFFFFUL) == 0 &&
        descriptor.Type == 13 && descriptor.Width == 1 && descriptor.Height == 1 && descriptor.Depth == 1 &&
        HasQualifiedTypedFormat(descriptor, raw, shape) && descriptor.Pitch == 1 &&
        descriptor.BaseLevel == 0 && descriptor.LastLevel == 0 && descriptor.MaxMip == 0 && selectedMip == 0 &&
        descriptor.BaseArray == 0 && descriptor.ArrayPitch == 0 && descriptor.MinLod == 0 && descriptor.MinLodWarn == 0 &&
        descriptor.MetadataAddress == 0 && descriptor.DescriptorFlags == 0 && descriptor.BcSwizzle == 0 &&
        // Mesa GFX10 SQ_IMG_RSRC_WORD5 names bits20..22 PERF_MOD.
        // Qualify only the previously captured 0 and observed SH/Octopath 7;
        // adjacent flags/reserved bits stay rejected by exact-word matching.
        // Reserved/non-content fields are not qualified by a lossy decoder.
        raw[2] == 0 &&
        raw[3] == ((13u << 28) | (descriptor.TileMode << 20) | descriptor.DstSelect) &&
        raw[4] == 0 && (raw[5] == 0 || raw[5] == 0x0070_0000u) && raw[6] == 0 && raw[7] == 0 &&
        access != GuestImageAccess.Unknown && (access & ~(GuestImageAccess.Read | GuestImageAccess.Write)) == 0 &&
        arrayed && shape.Arrayed && shape.Storage &&
        !shape.Volume && !shape.Cube && !shape.DynamicMip && !shape.OneDimensional &&
        !shape.Multisampled && !shape.DepthCompare && !shape.R128 && !shape.Atomic;

    internal static bool IsQualified(GuestDrawTexture texture) =>
        texture.Descriptor is { Length: 8 } raw && TryDecodeTextureDescriptor(raw, out var descriptor) &&
        IsQualified(descriptor, raw, texture.ArrayedView, texture.MipLevel, texture.Shape, texture.Access) &&
        texture.IsStorage && texture.Address == descriptor.Address && texture.Width == 1 && texture.Height == 1 &&
        texture.Type == 13 && texture.Format == descriptor.Format && texture.NumberType == descriptor.NumberType && texture.TileMode == descriptor.TileMode &&
        texture.Pitch == 1 && texture.DstSelect == descriptor.DstSelect && texture.ArrayLayers == 1 && texture.Depth == 1 &&
        texture.BaseMipLevel == 0 && texture.MipLevels == 1 && texture.ResourceMipLevels == 1 &&
        texture.MipUploads is not { Length: > 0 } && texture.TiledSource is null && texture.Detile is null;
}
