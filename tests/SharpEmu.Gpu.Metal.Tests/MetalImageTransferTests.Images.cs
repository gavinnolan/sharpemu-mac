// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// Whole-image transfers: the description decides the layout, TileGeometry decides the
// bytes, and a guest-to-texture-to-guest round trip has to come back byte for byte.
public sealed partial class MetalImageTransferTests
{
    private const ulong GuestAddress = 0x1_0000_0000;
    private const ulong StencilAddress = 0x1_4000_0000;

    private static ImageDescription Color2D(
        GuestPixelFormat guestFormat,
        GpuFormat hostFormat,
        uint width,
        uint height,
        uint levels,
        uint layers,
        GuestTileMode tile)
    {
        var transferFormat = tile is GuestTileMode.RenderTarget or GuestTileMode.Depth
            ? ImageDescription.RenderTargetTransferFormat(GuestPixelFormats.BytesPerElement(guestFormat))
            : guestFormat;
        var size = TileGeometry.TextureTotalSize(transferFormat, width, height, layers, levels, tile, volume: false);
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(GuestAddress, size.Size);
        description.GuestFormat = guestFormat;
        description.PixelFormat = hostFormat;
        description.Type = layers > 1 ? GuestImageType.Color2D : GuestImageType.Color2D;
        description.Extent = new GpuExtent3D(width, height, 1);
        description.Resources = new SubresourceCount(levels, layers);
        description.Pitch = TileGeometry.TexturePitch(transferFormat, width, tile);
        description.BytesPerBlock = GuestPixelFormats.BlockCompressedBytes(guestFormat) != 0
            ? GuestPixelFormats.BlockCompressedBytes(guestFormat)
            : GuestPixelFormats.BytesPerElement(guestFormat);
        description.TileMode = tile;
        return description;
    }

    private static ImageDescription Volume(GuestPixelFormat guestFormat, GpuFormat hostFormat, uint width, uint height, uint depth, uint levels)
    {
        var size = TileGeometry.TextureTotalSize(guestFormat, width, height, depth, levels, GuestTileMode.Standard64KB, volume: true);
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(GuestAddress, size.Size);
        description.GuestFormat = guestFormat;
        description.PixelFormat = hostFormat;
        description.Type = GuestImageType.Color3D;
        description.Extent = new GpuExtent3D(width, height, depth);
        description.Resources = new SubresourceCount(levels, 1);
        description.Pitch = TileGeometry.TexturePitch(guestFormat, width, GuestTileMode.Standard64KB);
        description.BytesPerBlock = GuestPixelFormats.BytesPerElement(guestFormat);
        description.TileMode = GuestTileMode.Standard64KB;
        return description;
    }

    private static ImageDescription Depth(GuestDepthFormat depthFormat, bool stencil, uint width, uint height, uint layers)
    {
        var rule = DepthFormatRule.Find(depthFormat)!;
        var stencilFormat = stencil ? GuestStencilFormat.Stencil8UInt : GuestStencilFormat.Invalid;
        Assert.True(TileGeometry.TryGetDepthSize(width, height, 0, depthFormat, stencilFormat, false,
            out var stencilSize, out _, out var depthSize));
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(GuestAddress, (ulong)depthSize.Size * layers);
        description.Stencil = stencil ? new GuestSpan(StencilAddress, (ulong)stencilSize.Size * layers) : GuestSpan.Empty;
        description.GuestFormat = rule.GuestFormat;
        description.PixelFormat = rule.AttachmentFormat(stencil);
        description.Type = GuestImageType.Color2D;
        description.Extent = new GpuExtent3D(width, height, 1);
        description.Resources = new SubresourceCount(1, layers);
        description.Pitch = TileGeometry.DepthPitch(width, rule.BytesPerElement);
        description.BytesPerBlock = rule.BytesPerElement;
        description.TileMode = GuestTileMode.Depth;
        return description;
    }

    // The transfer's own layout, rebuilt here from the shared code so the expected bytes
    // never come out of MetalImageTransfer.
    private static (MetalTextureLayout Layout, List<MetalSubresourceCopy> Copies, List<MetalTileTransfer> Tiles, TileElementLayout Element)
        BuildLayout(in ImageDescription description)
    {
        var transferFormat = description.TileMode is GuestTileMode.RenderTarget or GuestTileMode.Depth
            ? ImageDescription.RenderTargetTransferFormat(description.BytesPerBlock)
            : description.GuestFormat;
        var layout = MetalTextureLayout.Compute(transferFormat, description.Extent.Width, description.Extent.Height,
            description.Resources.Levels, description.TransferLayers, description.TileMode, description.Data.Size,
            allowDepthTile: true, volume: description.IsVolume, owner: "test");
        var copies = new List<MetalSubresourceCopy>();
        layout.BuildCopies(copies);
        var tiles = new List<MetalTileTransfer>();
        if (description.IsTiled)
        {
            Assert.True(layout.TryBuildTileTransfers(description.Data.Size, copies, description.Resources.Levels, tiles));
        }

        Assert.True(TileGeometry.TryGetElementLayout(transferFormat, out var element));
        if (description.IsTiled)
        {
            var texture = layout.Surface.Texture;
            element = new TileElementLayout(texture.Block.BytesPerElement, texture.TexelWidth, texture.TexelHeight);
        }

        return (layout, copies, tiles, element);
    }

    // Guest bytes a transfer can reproduce exactly: a tiled surface is filled through the
    // CPU twin, so the bytes no element maps to stay zero; a linear surface keeps its
    // pattern only where a copy region reads it.
    private static byte[] BuildGuest(in ImageDescription description, uint seed) => BuildGuest(description, seed, out _);

    private static byte[] BuildGuest(in ImageDescription description, uint seed, out byte[] linearReference)
    {
        var (_, copies, tiles, element) = BuildLayout(description);
        var guest = new byte[description.Data.Size];
        if (description.IsTiled)
        {
            ulong linearSize = 0;
            foreach (var transfer in tiles)
            {
                linearSize = Math.Max(linearSize, transfer.LinearOffset + transfer.LinearSize);
            }

            linearReference = MetalTileOracle.Pattern((int)linearSize, seed);
            foreach (var transfer in tiles)
            {
                MetalTileOracle.Transfer(linearReference, guest, transfer, toTiled: true);
            }

            return guest;
        }

        var pattern = MetalTileOracle.Pattern((int)description.Data.Size, seed);
        linearReference = pattern;
        foreach (var copy in copies)
        {
            var rowTexels = copy.BufferRowLength != 0 ? copy.BufferRowLength : copy.Width;
            var guestRow = (ulong)Math.Max((rowTexels + element.TexelWidth - 1) / element.TexelWidth, 1) * element.Bytes;
            var liveRow = (ulong)Math.Max((copy.Width + element.TexelWidth - 1) / element.TexelWidth, 1) * element.Bytes;
            var rows = Math.Max((copy.Height + element.TexelHeight - 1) / element.TexelHeight, 1);
            for (uint row = 0; row < rows; row++)
            {
                var offset = (int)(copy.BufferOffset + row * guestRow);
                Array.Copy(pattern, offset, guest, offset, (int)liveRow);
            }
        }

        return guest;
    }

    private void RoundTrip(MetalTransferHarness harness, in ImageDescription description, uint seed, string name)
    {
        var guest = BuildGuest(description, seed);
        var stencil = description.HasStencil ? MetalTileOracle.Pattern((int)description.Stencil.Size, seed + 7) : [];
        if (description.HasStencil && description.IsTiled)
        {
            // The stencil plane is tiled too, so only the bytes its own transfer covers
            // can survive a round trip.
            var tiled = new byte[description.Stencil.Size];
            var pitch = TileGeometry.DepthPitch(description.Extent.Width, 1);
            var slice = description.Stencil.Size / description.Resources.Layers;
            for (uint layer = 0; layer < description.Resources.Layers; layer++)
            {
                MetalTileOracle.Transfer(stencil, tiled, new MetalTileTransfer
                {
                    Kind = TileBlockKind.Depth64KB,
                    BytesPerElement = 1,
                    LinearOffset = slice * layer,
                    LinearSize = slice,
                    TiledOffset = slice * layer,
                    TiledSize = slice,
                    Width = description.Extent.Width,
                    Height = description.Extent.Height,
                    Depth = 1,
                    Pitch = pitch,
                    SurfaceZ = layer,
                }, toTiled: true);
            }

            stencil = tiled;
        }

        var source = harness.Buffer(guest);
        var target = harness.Buffer(description.Data.Size);
        var stencilSource = stencil.Length != 0 ? harness.Buffer(stencil) : 0;
        var stencilTarget = stencil.Length != 0 ? harness.Buffer(description.Stencil.Size) : 0;
        var texture = harness.Texture(harness.Transfer.ShapeFor(description));
        var range = new SubresourceRange(0, description.Resources.Levels, 0, description.Resources.Layers);
        var sourceRange = new MetalGuestRange(source, 0, description.Data.Size, 0);
        var targetRange = new MetalGuestRange(target, 0, description.Data.Size, 0);
        var stencilIn = stencilSource != 0 ? new MetalGuestRange(stencilSource, 0, description.Stencil.Size, 0) : default;
        var stencilOut = stencilTarget != 0 ? new MetalGuestRange(stencilTarget, 0, description.Stencil.Size, 0) : default;

        harness.Begin();
        harness.Transfer.EncodeGuestToTexture(harness, description, range, sourceRange, stencilIn, texture);
        harness.Submit();
        harness.Begin();
        harness.Transfer.EncodeTextureToGuest(harness, description, range, texture, targetRange, stencilOut);
        harness.Submit();

        Assert.Equal(guest, harness.Read(target, 0, description.Data.Size));
        if (stencilTarget != 0)
        {
            Assert.Equal(stencil, harness.Read(stencilTarget, 0, description.Stencil.Size));
        }

        output.WriteLine($"{name}: round trip of 0x{description.Data.Size:x} bytes matched");
    }

    [Fact]
    public void TiledColorImagesRoundTripThroughTheirTexture()
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        RoundTrip(harness, Color2D(GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 256, 128, 4, 1, GuestTileMode.Standard64KB), 11, "2d/4 mips");
        RoundTrip(harness, Color2D(GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 64, 64, 3, 6, GuestTileMode.Standard64KB), 12, "cube as array");
        RoundTrip(harness, Color2D(GuestPixelFormat.Bits16_16_16_16Float, GpuFormat.R16G16B16A16Sfloat, 96, 48, 2, 2, GuestTileMode.Standard4KB), 13, "4kb/rgba16f");
        RoundTrip(harness, Color2D(GuestPixelFormat.Bits8UNorm, GpuFormat.R8Unorm, 128, 64, 1, 1, GuestTileMode.Standard256B), 14, "256b/r8");
        RoundTrip(harness, Color2D(GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 192, 96, 1, 1, GuestTileMode.RenderTarget), 15, "render target");
    }

    [Fact]
    public void VolumeAndCompressedImagesRoundTripThroughTheirTexture()
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        RoundTrip(harness, Volume(GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 32, 32, 16, 2), 21, "volume/2 mips");
        RoundTrip(harness, Color2D(GuestPixelFormat.Bc3UNorm, GpuFormat.BC3UnormBlock, 64, 64, 3, 1, GuestTileMode.Standard64KB), 22, "bc3/3 mips");
        RoundTrip(harness, Color2D(GuestPixelFormat.Bc1UNorm, GpuFormat.BC1RgbaUnormBlock, 128, 64, 2, 1, GuestTileMode.Standard64KB), 23, "bc1/2 mips");
        RoundTrip(harness, Color2D(GuestPixelFormat.Bc7UNorm, GpuFormat.BC7UnormBlock, 64, 32, 1, 1, GuestTileMode.Standard64KB), 24, "bc7");
    }

    [Fact]
    public void DepthAndStencilPlanesRoundTripThroughTheirTexture()
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        RoundTrip(harness, Depth(GuestDepthFormat.Z32Float, stencil: false, 128, 64, 1), 31, "z32");
        RoundTrip(harness, Depth(GuestDepthFormat.Z32Float, stencil: true, 128, 64, 1), 32, "z32+s8");
        // Apple has no 16-bit combined format, so Z16 with stencil widens on the way in
        // and narrows on the way out.
        RoundTrip(harness, Depth(GuestDepthFormat.Z16, stencil: true, 128, 64, 1), 33, "z16+s8 widened");
    }

    [Fact]
    public void LinearImagesBlitWithoutACompensatingPass()
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var before = harness.Tiler.StagingBufferCount;
        RoundTrip(harness, Color2D(GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 64, 32, 1, 1, GuestTileMode.Linear), 41, "linear/1 mip");
        RoundTrip(harness, Color2D(GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 128, 64, 3, 1, GuestTileMode.Linear), 42, "linear/3 mips");
        Assert.Equal(before, harness.Tiler.StagingBufferCount);
    }

    [Fact]
    public void SubresourceRangesNarrowTheTransferToTheirLevels()
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = Color2D(GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 128, 128, 3, 2, GuestTileMode.Standard64KB);
        var guest = BuildGuest(description, 51, out var linear);
        var (_, _, tiles, _) = BuildLayout(description);
        var source = harness.Buffer(guest);
        var target = harness.Buffer(description.Data.Size);
        var texture = harness.Texture(harness.Transfer.ShapeFor(description));
        var range = new SubresourceRange(1, 1, 0, 2);
        var sourceRange = new MetalGuestRange(source, 0, description.Data.Size, 0);
        var targetRange = new MetalGuestRange(target, 0, description.Data.Size, 0);
        harness.Begin();
        harness.Transfer.EncodeGuestToTexture(harness, description, range, sourceRange, default, texture);
        harness.Submit();
        harness.Begin();
        harness.Transfer.EncodeTextureToGuest(harness, description, range, texture, targetRange, default);
        harness.Submit();

        // Only the elements of level 1 came back; level 0 and the rest of the mip tail's
        // shared block were never read, so they are still zero.
        var expected = new byte[description.Data.Size];
        foreach (var transfer in tiles)
        {
            if (transfer.Level == 1)
            {
                MetalTileOracle.Transfer(linear, expected, transfer, toTiled: true);
            }
        }

        Assert.NotEqual(expected, new byte[description.Data.Size]);
        Assert.Equal(expected, harness.Read(target, 0, description.Data.Size));
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("GPU image transfer execution requires the exclusive device gate.");
            return false;
        }

        Assert.True(OperatingSystem.IsMacOS());
        var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
        var actual = RuntimeInformation.ProcessArchitecture.ToString();
        if (!string.IsNullOrEmpty(expected))
        {
            Assert.Equal(expected, actual);
        }

        output.WriteLine($"image-transfer architecture={actual} framework={RuntimeInformation.FrameworkDescription}");
        Assert.NotEqual(0, Device.Value);
        return true;
    }

    // ShapeFor and the plan builders touch no Metal object, so they can be exercised with
    // a handle that is merely non-null when the device gate is closed.
    private static nint CreateHeadlessDevice() => 1;

    private static readonly Lazy<nint> Device = new(MetalNative.MTLCreateSystemDefaultDevice);
}
