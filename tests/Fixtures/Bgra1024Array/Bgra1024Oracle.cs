// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SharpEmu.Tests.Fixtures;

public static class Bgra1024Oracle
{
    public const int Extent = 1024, ByteCount = 4194304;
    public const string OffsetSha256 = "F9547A1FC8DE057F80DFE2B8BDCD7C66A5B16D3EC6866DD114F03E5221ECA964";
    // PAL c5e8000 public Addr2 API, Navi21/GFX10.3 rev0x28, GB_ADDR_CONFIG0x344,
    // TEX2D, mode27/32bpp, one slice/mip/sample, noMetadata, pipeBankXor0.
    // Its equation X coordinate is in bytes; this helper takes texel coordinates.
    public static int Offset(int x, int y)
    {
        var xb = x << 2;
        var value = 0;
        value |= (((xb >> 0) & 1)) << 0;
        value |= (((xb >> 1) & 1)) << 1;
        value |= (((xb >> 2) & 1)) << 2;
        value |= (((xb >> 3) & 1)) << 3;
        value |= (((y >> 0) & 1)) << 4;
        value |= (((y >> 1) & 1)) << 5;
        value |= (((xb >> 4) & 1)) << 6;
        value |= (((y >> 2) & 1)) << 7;
        value |= (((y >> 4) & 1) ^ ((xb >> 9) & 1) ^ ((y >> 7) & 1)) << 8;
        value |= (((xb >> 6) & 1) ^ ((y >> 4) & 1)) << 9;
        value |= (((y >> 5) & 1) ^ ((xb >> 8) & 1)) << 10;
        value |= (((xb >> 7) & 1) ^ ((y >> 6) & 1)) << 11;
        value |= (((xb >> 5) & 1)) << 12;
        value |= (((y >> 3) & 1)) << 13;
        value |= (((xb >> 8) & 1) ^ ((y >> 7) & 1)) << 14;
        value |= (((y >> 6) & 1) ^ ((xb >> 9) & 1)) << 15;
        return ((y / 128) * 8 + x / 128) * 65536 + value;
    }
    public static byte[] Offsets()
    {
        var bytes = new byte[ByteCount];
        for (var y = 0; y < Extent; y++) for (var x = 0; x < Extent; x++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((y * Extent + x) * 4), (uint)Offset(x, y));
        if (Convert.ToHexString(SHA256.HashData(bytes)) != OffsetSha256)
            throw new InvalidOperationException("Independent PAL coordinate fixture changed.");
        return bytes;
    }
    public static byte[] LinearSeed()
    {
        var bytes = new byte[ByteCount];
        for (var i = 0; i < Extent * Extent; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), 0xA5000000u | (uint)i);
        return bytes;
    }
    public static byte[] PhysicalSeed()
    {
        _ = Offsets(); // Certify all coordinates against the retained public-API digest.
        var bytes = new byte[ByteCount];
        for (var y = 0; y < Extent; y++) for (var x = 0; x < Extent; x++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(Offset(x, y)), 0xA5000000u | (uint)(y * Extent + x));
        return bytes;
    }
}
