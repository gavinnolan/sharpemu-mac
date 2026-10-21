// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;

namespace SharpEmu.Gpu.Metal.Tests;

internal static class R16SelectedMipOracle
{
    internal const int ChainBytes = 0xAB0000;
    internal static readonly int[] Offsets = [0x2B0000, 0xB0000, 0x30000, 0x10000];
    private static readonly byte[] Block = Load("block-offsets.bin", 65536,
        "9A618F4312DD6C904F38DB9798AA2BBE42176F9AA453A6DAFCF012384CC5BF6C");
    private static readonly byte[] Origins = Load("block-origins.bin", 680,
        "888284D426E295E4811EB4A64F0A04791D66021F5BC63EC5304604EB9FF6DC2C");
    private static readonly int[] OriginStarts = [0, 128, 160, 168];
    private static byte[] Load(string name, int length, string digest)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("R16SelectedMip." + name)!;
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        var data = bytes.ToArray();
        if (data.Length != length || Convert.ToHexString(SHA256.HashData(data)) != digest)
            throw new InvalidOperationException("Independent R16 selected-mip block fixture is corrupted.");
        return data;
    }
    internal static (byte[] Guest, byte[][] Linear) Seed()
    {
        var guest = new byte[ChainBytes]; guest.AsSpan().Fill(0xCD);
        var all = new byte[4][];
        for (var mip = 0; mip < 4; mip++)
        {
            var width = 2048 >> mip; var linear = new byte[width * width * 2]; all[mip] = linear;
            for (var y = 0; y < width; y++) for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                // Lossless encoding of the retained primary all-coordinate
                // transcript, verified by compress-oracle.py. Macro origins
                // include coordinate-dependent low XOR bits: repeating the
                // first block unchanged is NOT the independently proven map.
                var originIndex = OriginStarts[mip] + (y / 128) * (width / 256) + x / 256;
                var origin = BinaryPrimitives.ReadUInt32LittleEndian(Origins.AsSpan(originIndex * 4, 4));
                var local = BinaryPrimitives.ReadUInt16LittleEndian(Block.AsSpan(((y % 128) * 256 + x % 256) * 2, 2));
                var byteOffset = checked((int)(origin ^ local));
                var bits = BitConverter.HalfToUInt16Bits((Half)(((i * 137 + mip * 331) % 8191 - 4095) / 128.0f));
                BinaryPrimitives.WriteUInt16LittleEndian(guest.AsSpan(byteOffset, 2), bits);
                BinaryPrimitives.WriteUInt16LittleEndian(linear.AsSpan(i * 2, 2), bits);
            }
        }
        return (guest, all);
    }
}
