// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.PerfAudit;

internal static class Descriptors
{
    public const uint IdentitySelect = 0xFAC;

    /// <summary>A V# for structured (idxen) loads: <paramref name="stride"/> bytes per record.</summary>
    public static uint[] Buffer(ulong address, uint stride, uint records, uint format = 77, uint oobSelect = 0) =>
    [
        (uint)address,
        ((uint)(address >> 32) & 0xFFFF) | (stride << 16),
        records,
        IdentitySelect | (format << 12) | (1u << 24) | (oobSelect << 28),
    ];

    /// <summary>
    /// A T# over a linear or tiled image. <paramref name="type"/> 9 = 2D, 13 = 2D array, 11 = cube, 10 = 3D. Word 5 MAX_MIP is
    /// the resource's mip count minus one, independent of the view's LAST_LEVEL; <paramref name="maxMip"/> defaults to LAST_LEVEL.
    /// </summary>
    public static uint[] Texture(ulong address, uint format, uint width, uint height, uint type = 9, uint tileMode = 0,
        uint baseLevel = 0, uint lastLevel = 0, uint depthOrLastLayer = 0, uint baseArray = 0, uint dstSelect = IdentitySelect,
        uint minLodFixed = 0, uint? maxMip = null)
    {
        var shifted = address >> 8;
        var lastX = width - 1;
        var lastY = height - 1;
        return
        [
            (uint)shifted,
            ((uint)(shifted >> 32) & 0xFF) | (minLodFixed << 8) | (format << 20) | ((lastX & 0x3) << 30),
            ((lastX >> 2) & 0xFFF) | (lastY << 14),
            dstSelect | (baseLevel << 12) | (lastLevel << 16) | (tileMode << 20) | (type << 28),
            depthOrLastLayer | (baseArray << 16),
            (maxMip ?? lastLevel) << 4,
            0,
            0,
        ];
    }

    /// <summary>
    /// An S#. Clamp 0 = wrap, 1 = mirror, 2 = clamp-to-last, 4 = clamp-half-border, 6 = clamp-to-border; filters 0 = point, 1 = bilinear;
    /// mip filter 0 = none, 1 = point, 2 = linear. LOD fields are unsigned 4.8 fixed point; bias is signed 6.8.
    /// </summary>
    public static uint[] Sampler(uint clampX = 2, uint clampY = 2, uint clampZ = 2, uint depthCompare = 0,
        uint magFilter = 0, uint minFilter = 0, uint mipFilter = 0, float minLod = 0, float maxLod = 15, float lodBias = 0,
        uint borderColorType = 0, uint maxAniso = 0, bool forceUnnormalized = false, uint zFilter = 0)
    {
        static uint Fixed(float value, int bits) => (uint)Math.Clamp((int)MathF.Round(value * 256f), 0, (1 << bits) - 1);
        var bias = (uint)((int)MathF.Round(lodBias * 256f) & 0x3FFF);
        return
        [
            clampX | (clampY << 3) | (clampZ << 6) | (maxAniso << 9) | ((uint)depthCompare << 12) | (forceUnnormalized ? 1u << 15 : 0u),
            Fixed(minLod, 12) | (Fixed(maxLod, 12) << 12),
            bias | (magFilter << 20) | (minFilter << 22) | (zFilter << 24) | (mipFilter << 26),
            borderColorType << 30,
        ];
    }

    public static uint Lo(ulong address) => (uint)address;

    public static uint Hi(ulong address) => (uint)(address >> 32);
}
