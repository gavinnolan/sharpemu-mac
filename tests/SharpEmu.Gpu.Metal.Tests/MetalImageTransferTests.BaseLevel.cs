// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// A texture held from a later guest level: the transfer reads the coarser levels at their
// full-chain guest offsets, from the bytes those levels occupy and nothing else, and puts
// guest level L in texture level L - firstLevel. The oracle is the whole-chain upload the
// other transfer tests check: every level the cut texture holds must match it bit for bit.
public sealed partial class MetalImageTransferTests
{
    public static TheoryData<string, uint> BaseLevelCases => new()
    {
        { "rgba8 64kb 9 mips", 1 },
        { "rgba8 64kb 9 mips", 3 },
        { "rgba16f 4kb 7 mips", 2 },
        { "bc1 4kb 8 mips", 2 },
        { "rgba8 linear 6 mips", 2 },
        { "rgba8 4kb 3 layers 5 mips", 1 },
    };

    private static ImageDescription BaseLevelCase(string name) => name switch
    {
        "rgba8 64kb 9 mips" => Color2D(GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm,
            256, 256, 9, 1, GuestTileMode.Standard64KB),
        "rgba16f 4kb 7 mips" => Color2D(GuestPixelFormat.Bits16_16_16_16Float, GpuFormat.R16G16B16A16Sfloat,
            96, 64, 7, 1, GuestTileMode.Standard4KB),
        "bc1 4kb 8 mips" => Color2D(GuestPixelFormat.Bc1UNorm, GpuFormat.BC1RgbaUnormBlock,
            128, 128, 8, 1, GuestTileMode.Standard4KB),
        "rgba8 linear 6 mips" => Color2D(GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm,
            64, 32, 6, 1, GuestTileMode.Linear),
        "rgba8 4kb 3 layers 5 mips" => Color2D(GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm,
            64, 64, 5, 3, GuestTileMode.Standard4KB),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(BaseLevelCases))]
    public void ATextureFromALaterLevelHoldsExactlyTheWholeChainsLevelsFromThere(string name, uint firstLevel)
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = BaseLevelCase(name);
        var guest = BuildGuest(description, 211 + firstLevel);
        var levels = description.Resources.Levels;
        var layers = description.Resources.Layers;

        // The cut shape: the level's own extent, the levels from there.
        var fullShape = harness.Transfer.ShapeFor(description);
        var cutShape = harness.Transfer.ShapeFor(description, firstLevel);
        Assert.Equal(levels - firstLevel, cutShape.MipLevels);
        Assert.Equal(Math.Max(description.Extent.Width >> (int)firstLevel, 1), cutShape.Width);
        Assert.Equal(Math.Max(description.Extent.Height >> (int)firstLevel, 1), cutShape.Height);
        Assert.Equal(fullShape.ArrayLength, cutShape.ArrayLength);

        // The run the coarser levels read: inside the image, and for one layer strictly
        // smaller than it, because level 0 is the largest level.
        var (start, end) = harness.Transfer.LevelBytes(description, firstLevel);
        Assert.Equal((0UL, description.Data.Size), harness.Transfer.LevelBytes(description, 0));
        Assert.True(end > start && end <= description.Data.Size);
        if (layers == 1)
        {
            Assert.True(end - start < description.Data.Size, $"{name}: 0x{start:x}..0x{end:x} of 0x{description.Data.Size:x}");
        }

        output.WriteLine($"{name} from level {firstLevel}: reads 0x{start:x}..0x{end:x} of 0x{description.Data.Size:x}");

        var full = harness.Texture(fullShape);
        var cut = harness.Texture(cutShape);
        harness.Begin();
        harness.Transfer.EncodeGuestToTexture(harness, description,
            new SubresourceRange(0, levels, 0, layers),
            new MetalGuestRange(harness.Buffer(guest), 0, description.Data.Size, 0), default, full, 0, description.Data.Size);
        // Only the run is mapped: a transfer that reached outside it would read past the buffer.
        harness.Transfer.EncodeGuestToTexture(harness, description,
            new SubresourceRange(firstLevel, levels - firstLevel, 0, layers),
            new MetalGuestRange(harness.Buffer(guest.AsSpan((int)start, (int)(end - start))), 0, end - start, 0),
            default, cut, start, end - start, firstLevel);
        harness.Submit();

        var (_, copies, _, element) = BuildLayout(description);
        var compared = 0UL;
        foreach (var copy in copies)
        {
            if (copy.Level < firstLevel)
            {
                continue;
            }

            var expected = ReadSubresource(harness, full, copy.Slice, copy.Level, copy.Width, copy.Height, element);
            var actual = ReadSubresource(harness, cut, copy.Slice, copy.Level - firstLevel, copy.Width, copy.Height, element);
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"{name}: level={copy.Level} slice={copy.Slice} differs");
            Assert.Contains(expected, value => value != 0);
            compared += (ulong)expected.Length;
        }

        Assert.NotEqual(0UL, compared);
        output.WriteLine($"{name}: 0x{compared:x} texel bytes of levels {firstLevel}..{levels - 1} matched the whole chain");
    }

    private static byte[] ReadSubresource(
        MetalTransferHarness harness, nint texture, uint slice, uint level, uint width, uint height, TileElementLayout element)
    {
        var rowBytes = (ulong)Math.Max((width + element.TexelWidth - 1) / element.TexelWidth, 1) * element.Bytes;
        var rows = (ulong)Math.Max((height + element.TexelHeight - 1) / element.TexelHeight, 1);
        var buffer = harness.Buffer(rowBytes * rows);
        harness.Begin();
        var blit = harness.Tiler.BeginBlit(harness);
        MetalNative.SendCopyTextureToBufferOptions(blit, SelCopyTextureToBuffer,
            texture, slice, level, default,
            new MtlSize { Width = width, Height = height, Depth = 1 },
            buffer, 0, (nuint)rowBytes, (nuint)(rowBytes * rows), (nuint)MtlBlitOption.None);
        MetalTiler.EndEncoding(blit);
        harness.Submit();
        return harness.Read(buffer, 0, rowBytes * rows);
    }
}
