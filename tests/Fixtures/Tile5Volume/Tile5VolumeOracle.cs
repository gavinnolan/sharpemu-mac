// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;

namespace SharpEmu.Tests.Fixtures;

internal static class Tile5VolumeOracle
{
    internal const ulong Address = 0x7AC109C000;
    internal static uint[] Raw => [0x7AC109C0, 0xC0B00000, 0x0003C003, 0xA0500004, 0x0000000F, 0x00700000, 0, 0];
    internal static int[] Offsets()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Tile5Volume.offsets.csv")!;
        using var reader = new StreamReader(stream);
        var rows = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var offsets = new int[4096];
        foreach (var row in rows)
        {
            var fields = row.Split(',').Select(int.Parse).ToArray();
            offsets[(fields[2] * 16 + fields[1]) * 16 + fields[0]] = fields[3];
        }
        if (rows.Length != offsets.Length) throw new InvalidDataException("Incomplete primary oracle");
        return offsets;
    }
    internal static (byte[] Tiled, byte[] Linear) Seed()
    {
        var tiled = new byte[8192]; var linear = new byte[8192];
        var offsets = Offsets();
        for (var i = 0; i < offsets.Length; i++)
        {
            var value = (ushort)(i * 13 + 1001); // unique complete16-bit values
            BinaryPrimitives.WriteUInt16LittleEndian(tiled.AsSpan(offsets[i]), value);
            BinaryPrimitives.WriteUInt16LittleEndian(linear.AsSpan(i * 2), value);
        }
        return (tiled, linear);
    }
}
