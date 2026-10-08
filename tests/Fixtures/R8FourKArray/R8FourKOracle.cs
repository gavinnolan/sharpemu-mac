// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
namespace SharpEmu.Tests.Fixtures;
public static class R8FourKOracle
{
    public const int Width = 3840, Height = 2160, LogicalBytes = 8294400, PhysicalBytes = 8847360;
    public const string OffsetSha256 = "B285D9309BEB0FA82956EA8ECA786EFB54F82C167FD06609DF4354810DD7A8BF";
    // Test-only transcript of pinned public PAL0x344/mode24/8bpp equation50.
    // Hash every coordinate against the independently executed Addr2 API.
    public static int Offset(int x, int y)
    {
        var value = 0;
        value |= (((x >> 0) & 1)) << 0;
        value |= (((y >> 0) & 1)) << 1;
        value |= (((x >> 1) & 1)) << 2;
        value |= (((y >> 1) & 1)) << 3;
        value |= (((x >> 2) & 1)) << 4;
        value |= (((y >> 2) & 1)) << 5;
        value |= (((x >> 3) & 1)) << 6;
        value |= (((y >> 3) & 1)) << 7;
        value |= (((y >> 4) & 1) ^ ((x >> 7) & 1) ^ ((y >> 7) & 1)) << 8;
        value |= (((x >> 4) & 1) ^ ((y >> 4) & 1)) << 9;
        value |= (((y >> 5) & 1) ^ ((x >> 6) & 1)) << 10;
        value |= (((x >> 5) & 1) ^ ((y >> 6) & 1)) << 11;
        value |= (((x >> 6) & 1)) << 12;
        value |= (((y >> 6) & 1)) << 13;
        value |= (((x >> 7) & 1) ^ ((y >> 8) & 1)) << 14;
        value |= (((y >> 7) & 1) ^ ((x >> 8) & 1)) << 15;
        return ((y / 256) * 15 + x / 256) * 65536 + value;
    }
    public static byte[] Offsets()
    {
        var bytes = new byte[LogicalBytes * 4];
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((y * Width + x) * 4), (uint)Offset(x, y));
        if (Convert.ToHexString(SHA256.HashData(bytes)) != OffsetSha256) throw new InvalidOperationException("Independent PAL coordinate digest changed.");
        return bytes;
    }
    public static byte[] LinearSeed()
    {
        var bytes = new byte[LogicalBytes];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i * 37) ^ (i >> 7) ^ (i >> 15) ^ 0xA5);
        return bytes;
    }
    public static byte[] PhysicalSeed()
    {
        _ = Offsets(); var linear = LinearSeed(); var bytes = new byte[PhysicalBytes]; bytes.AsSpan().Fill(0xDB);
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++) bytes[Offset(x, y)] = linear[y * Width + x];
        return bytes;
    }
}
