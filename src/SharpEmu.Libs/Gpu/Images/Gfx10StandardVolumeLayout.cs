// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Images;

/// <summary>Bounded GFX10 standard non-XOR thick-3D layout; not an XY detiler.</summary>
public static class Gfx10StandardVolumeLayout
{
    public const int ByteCount = 8192;

    /// <summary>The independently qualified single-mip 16-bit 16-cube standard-4KiB layout.</summary>
    public static bool IsSupported(uint width, uint height, uint depth, uint bitsPerTexel, uint swizzleMode, uint mipLevels) =>
        width == 16 && height == 16 && depth == 16 && bitsPerTexel == 16 && swizzleMode == 5 && mipLevels == 1;

    public static int TexelOffset16Cube(int x, int y, int z)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)x, 16u, nameof(x));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)y, 16u, nameof(y));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)z, 16u, nameof(z));
        // AddrLib GFX10.3 ADDR_SW_4KB_S/TEX_3D/16bpp equation81.
        // X in AddrLib's equation is a BYTE coordinate: x0 is the zero byte bit.
        // Two 8x16x16 blocks are placed along X; texel X bit3 selects block1.
        return ((x & 1) << 1) | ((x & 2) << 5) | ((x & 4) << 7) | ((x & 8) << 9) |
               ((y & 1) << 3) | ((y & 2) << 4) | ((y & 4) << 6) | ((y & 8) << 8) |
               ((z & 1) << 2) | ((z & 2) << 3) | ((z & 4) << 5) | ((z & 8) << 7);
    }

    public static byte[] Decode16Cube(ReadOnlySpan<byte> source)
    {
        if (source.Length != ByteCount)
            throw new ArgumentException("The qualified volume requires the complete 8192-byte physical footprint.", nameof(source));
        var linear = new byte[ByteCount];
        for (var z = 0; z < 16; z++)
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
            source.Slice(TexelOffset16Cube(x, y, z), 2).CopyTo(linear.AsSpan(((z * 16 + y) * 16 + x) * 2, 2));
        return linear;
    }
}
