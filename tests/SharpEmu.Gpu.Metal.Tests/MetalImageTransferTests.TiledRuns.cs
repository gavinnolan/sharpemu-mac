// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// A run of a tiled surface with image on both sides of it, checked against the whole
// image: the run is uploaded or written back on its own, and the result is compared with
// what the CPU twin says the whole surface holds. A piece read back through the same
// parameters it was written with cannot tell a misplaced run from a correct one; a whole
// readback can.
public sealed partial class MetalImageTransferTests
{
    public static TheoryData<int, int, int, uint, uint, uint, uint, string> TiledRunShapes => new()
    {
        { (int)GuestPixelFormat.Bits8_8_8_8UNorm, (int)GpuFormat.R8G8B8A8Unorm, (int)GuestTileMode.Standard64KB, 128, 512, 1, 1, "64KB one layer" },
        { (int)GuestPixelFormat.Bits8_8_8_8UNorm, (int)GpuFormat.R8G8B8A8Unorm, (int)GuestTileMode.Standard4KB, 96, 128, 1, 1, "4KB one layer" },
        { (int)GuestPixelFormat.Bits16_16_16_16Float, (int)GpuFormat.R16G16B16A16Sfloat, (int)GuestTileMode.RenderTarget, 320, 200, 1, 1, "render target RGBA16F" },
        { (int)GuestPixelFormat.Bits8_8_8_8UNorm, (int)GpuFormat.R8G8B8A8Unorm, (int)GuestTileMode.RenderTarget, 200, 300, 1, 1, "render target ragged" },
        { (int)GuestPixelFormat.Bits8_8_8_8UNorm, (int)GpuFormat.R8G8B8A8Unorm, (int)GuestTileMode.Standard64KB, 200, 300, 1, 3, "64KB layers" },
        { (int)GuestPixelFormat.Bits8_8_8_8UNorm, (int)GpuFormat.R8G8B8A8Unorm, (int)GuestTileMode.Standard4KB, 100, 70, 1, 4, "4KB layers" },
        { (int)GuestPixelFormat.Bits8_8_8_8UNorm, (int)GpuFormat.R8G8B8A8Unorm, (int)GuestTileMode.Standard4KB, 256, 256, 4, 1, "4KB mips" },
    };

    // An upload of a run in the middle leaves the run carrying the new bytes and every
    // other byte the old ones, as the whole surface reads back.
    [Theory]
    [MemberData(nameof(TiledRunShapes))]
    public void ATiledRunInTheMiddleUploadsToItsOwnTexels(
        int guestFormat, int hostFormat, int tile, uint width, uint height, uint levels, uint layers, string name)
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = Color2D(
            (GuestPixelFormat)guestFormat, (GpuFormat)hostFormat, width, height, levels, layers, (GuestTileMode)tile);
        var (start, end) = MiddleRun(harness, in description, name);
        var first = BuildGuest(description, 0x31 + (uint)name.Length);
        var second = BuildGuest(description, 0xD7 + (uint)name.Length);

        var actual = TransferWholeThenRun(harness, description, first, second, start, end);

        var expected = (byte[])first.Clone();
        Array.Copy(second, (long)start, expected, (long)start, (long)(end - start));
        AssertSameBytes(expected, actual, $"{name}: upload of 0x{start:x}..0x{end:x}");
        output.WriteLine($"{name}: run 0x{start:x}..0x{end:x} of 0x{description.Data.Size:x} uploaded in place");
    }

    // A writeback of a run in the middle stores exactly that run's tiled bytes, as the
    // CPU twin lays the whole surface out, and leaves the rest of guest memory alone.
    [Theory]
    [MemberData(nameof(TiledRunShapes))]
    public void ATiledRunInTheMiddleWritesBackItsOwnBytes(
        int guestFormat, int hostFormat, int tile, uint width, uint height, uint levels, uint layers, string name)
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = Color2D(
            (GuestPixelFormat)guestFormat, (GpuFormat)hostFormat, width, height, levels, layers, (GuestTileMode)tile);
        var (start, end) = MiddleRun(harness, in description, name);
        var guest = BuildGuest(description, 0x5B + (uint)name.Length);
        var texture = harness.Texture(harness.Transfer.ShapeFor(description));
        var whole = new SubresourceRange(0, description.Resources.Levels, 0, description.TransferLayers);
        var source = harness.Buffer(guest);
        harness.Begin();
        harness.Transfer.EncodeGuestToTexture(
            harness, description, whole, new MetalGuestRange(source, 0, description.Data.Size, 0), default,
            texture, 0, description.Data.Size);
        harness.Submit();

        var sentinel = new byte[description.Data.Size];
        Array.Fill(sentinel, (byte)0xA5);
        var target = harness.Buffer(sentinel);
        harness.Begin();
        harness.Transfer.EncodeTextureToGuest(
            harness, description, whole, texture,
            new MetalGuestRange(target, start, end - start, 0), default, start, end - start);
        harness.Submit();

        // Padding no element maps to is never written, so it keeps the sentinel too.
        var expected = (byte[])sentinel.Clone();
        var mapped = MappedBytes(in description);
        for (var index = (int)start; index < (int)end; index++)
        {
            expected[index] = mapped[index] ? guest[index] : sentinel[index];
        }

        AssertSameBytes(expected, harness.Read(target, 0, description.Data.Size), $"{name}: writeback of 0x{start:x}..0x{end:x}");
        output.WriteLine($"{name}: run 0x{start:x}..0x{end:x} of 0x{description.Data.Size:x} written back in place");
    }

    // A run between roughly a third and two thirds of the surface, on the cuts the plan
    // itself can make, with image on both sides.
    private static (ulong Start, ulong End) MiddleRun(MetalTransferHarness harness, in ImageDescription description, string name)
    {
        var size = description.Data.Size;
        var start = harness.Transfer.AlignRunEnd(in description, 0, size / 3);
        Assert.Equal(start, harness.Transfer.AlignRunStart(in description, start));
        var end = harness.Transfer.AlignRunEnd(in description, start, size / 3 * 2);
        Assert.True(start > 0 && end > start && end < size, $"{name}: no middle run (0x{start:x}..0x{end:x} of 0x{size:x})");
        return (start, end);
    }

    // The tiled bytes some element of the surface lands on, as the CPU twin places them.
    private static bool[] MappedBytes(in ImageDescription description)
    {
        var (_, _, tiles, _) = BuildLayout(description);
        ulong linearSize = 0;
        foreach (var transfer in tiles)
        {
            linearSize = Math.Max(linearSize, transfer.LinearOffset + transfer.LinearSize);
        }

        var linear = new byte[linearSize];
        Array.Fill(linear, (byte)0xFF);
        var tiled = new byte[description.Data.Size];
        foreach (var transfer in tiles)
        {
            MetalTileOracle.Transfer(linear, tiled, transfer, toTiled: true);
        }

        return Array.ConvertAll(tiled, value => value != 0);
    }

    // Names the first differing byte rather than dumping both surfaces.
    private static void AssertSameBytes(byte[] expected, byte[] actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        var first = -1;
        var count = 0;
        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index] != actual[index])
            {
                first = first < 0 ? index : first;
                count++;
            }
        }

        Assert.True(first < 0,
            $"{what}: {count} bytes differ, the first at 0x{first:x} (expected 0x{(first < 0 ? 0 : expected[first]):x2}, " +
            $"got 0x{(first < 0 ? 0 : actual[first]):x2}).");
    }
}
