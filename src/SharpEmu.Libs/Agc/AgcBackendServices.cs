// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using static SharpEmu.Libs.Agc.AgcExports;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.Libs.Agc;

/// <summary>Guest texture semantics and command translation consumed by graphics backends.</summary>
public static class AgcBackendServices
{
    public static AgcExports.CommandStreamTranslation CreateCommandStreamTranslation(ICpuMemory memory, ICommandStreamHost host) =>
        AgcExports.CreateCommandStreamTranslation(memory, host);

    public static ulong? ParseOptionalHexAddress(string? value) =>
        AgcExports.ParseOptionalHexAddress(value);

    public static int GetBlockCompressedBlockBytes(uint format) =>
        AgcExports.GetBlockCompressedBlockBytes(format);

    public static GuestSampler ToGuestSampler(IReadOnlyList<uint> descriptor) =>
        AgcExports.ToGuestSampler(descriptor);

    public static ulong GetTextureBytesPerTexel(uint format) =>
        AgcExports.GetTextureBytesPerTexel(format);

    public static ulong GetTextureByteCount(
        uint format,
        uint width,
        uint height,
        uint depth = 1) =>
        AgcExports.GetTextureByteCount(format, width, height, depth);

    public static uint GetTextureVolumeDepth(uint type, uint depth) =>
        AgcExports.GetTextureVolumeDepth(type, depth);

    public static bool TryDecodeTextureDescriptor(
        IReadOnlyList<uint> fields,
        out TextureDescriptor descriptor) =>
        AgcExports.TryDecodeTextureDescriptor(fields, out descriptor);

    public const uint Gen5TextureFormatR8G8B8A8Unorm = AgcExports.Gen5TextureFormatR8G8B8A8Unorm;
    public const uint Gen5TextureFormatR16G16B16A16Float = AgcExports.Gen5TextureFormatR16G16B16A16Float;
    public const uint Gen5TextureType1D = AgcExports.Gen5TextureType1D;
    public const uint Gen5TextureType2D = AgcExports.Gen5TextureType2D;
    public const uint Gen5TextureType3D = AgcExports.Gen5TextureType3D;
    public const uint Gen5TextureTypeCube = AgcExports.Gen5TextureTypeCube;
    public const uint Gen5TextureType1DArray = AgcExports.Gen5TextureType1DArray;
    public const uint Gen5TextureType2DArray = AgcExports.Gen5TextureType2DArray;
}
