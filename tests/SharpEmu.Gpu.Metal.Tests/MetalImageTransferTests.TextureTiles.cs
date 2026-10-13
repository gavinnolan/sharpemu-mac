// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// A tiled colour transfer of whole 4, 8 or 16 byte texels moves between the guest
// surface and the texture in one pass, with no staging buffer and no blit. A round trip
// alone cannot tell a right pass from two mirrored wrong ones, so each direction is
// checked against the CPU oracle on its own: the upload against the texels a blit reads
// back out of the texture, the writeback against the guest bytes the oracle tiles.
public sealed partial class MetalImageTransferTests
{
    public static TheoryData<string> TextureTileCases => new()
    {
        "rgba8 render target",
        "rgba16f render target 2 layers",
        "r11g11b10 render target",
        "rg32 render target",
        "rgba32f 64kb 2 mips",
        "rgba16f 4kb 3 layers",
    };

    private static ImageDescription TextureTileCase(string name) => name switch
    {
        "rgba8 render target" => Color2D(GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm,
            192, 96, 1, 1, GuestTileMode.RenderTarget),
        "rgba16f render target 2 layers" => Color2D(GuestPixelFormat.Bits16_16_16_16Float, GpuFormat.R16G16B16A16Sfloat,
            160, 72, 1, 2, GuestTileMode.RenderTarget),
        "r11g11b10 render target" => Color2D(GuestPixelFormat.Bits11_11_10Float, GpuFormat.B10G11R11UfloatPack32,
            200, 120, 1, 1, GuestTileMode.RenderTarget),
        "rg32 render target" => Color2D(GuestPixelFormat.Bits32_32UInt, GpuFormat.R32G32Uint,
            96, 64, 1, 1, GuestTileMode.RenderTarget),
        "rgba32f 64kb 2 mips" => Color2D(GuestPixelFormat.Bits32_32_32_32Float, GpuFormat.R32G32B32A32Sfloat,
            256, 256, 2, 1, GuestTileMode.Standard64KB),
        "rgba16f 4kb 3 layers" => Color2D(GuestPixelFormat.Bits16_16_16_16Float, GpuFormat.R16G16B16A16Sfloat,
            96, 48, 1, 3, GuestTileMode.Standard4KB),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(TextureTileCases))]
    public void ATiledColorUploadWritesTheTextureInOnePassExactlyAsTheOracleDetiles(string name)
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = TextureTileCase(name);
        var guest = BuildGuest(description, 91, out var linear);
        var (_, copies, _, element) = BuildLayout(description);
        var texture = harness.Texture(harness.Transfer.ShapeFor(description));
        var range = new SubresourceRange(0, description.Resources.Levels, 0, description.Resources.Layers);

        harness.Begin();
        harness.Transfer.EncodeGuestToTexture(harness, description, range,
            new MetalGuestRange(harness.Buffer(guest), 0, description.Data.Size, 0), default, texture, 0, description.Data.Size);
        harness.Submit();

        // One pass: nothing was staged on the way in.
        Assert.Equal(0u, harness.Tiler.StagingBufferCount);
        var readback = harness.Buffer((ulong)linear.Length);
        harness.Begin();
        var blit = harness.Tiler.BeginBlit(harness);
        foreach (var copy in copies)
        {
            var rowBytes = RowBytesOf(copy, element);
            MetalNative.SendCopyTextureToBufferOptions(blit, SelCopyTextureToBuffer,
                texture, copy.Slice, copy.Level, default,
                new MtlSize { Width = copy.Width, Height = copy.Height, Depth = 1 },
                readback, (nuint)copy.BufferOffset, (nuint)rowBytes, (nuint)(rowBytes * copy.Height),
                (nuint)MtlBlitOption.None);
        }

        MetalTiler.EndEncoding(blit);
        harness.Submit();
        var actual = harness.Read(readback, 0, (ulong)linear.Length);
        AssertLiveTexelsEqual(copies, element, linear, actual, name);
    }

    [Theory]
    [MemberData(nameof(TextureTileCases))]
    public void ATiledColorWritebackReadsTheTextureInOnePassExactlyAsTheOracleTiles(string name)
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = TextureTileCase(name);
        var expected = BuildGuest(description, 93, out var linear);
        var (_, copies, _, element) = BuildLayout(description);
        var texture = harness.Texture(harness.Transfer.ShapeFor(description));
        var source = harness.Buffer(linear);
        harness.Begin();
        var blit = harness.Tiler.BeginBlit(harness);
        foreach (var copy in copies)
        {
            var rowBytes = RowBytesOf(copy, element);
            MetalNative.SendCopyBufferToTextureOptions(blit, SelCopyBufferToTexture,
                source, (nuint)copy.BufferOffset, (nuint)rowBytes, (nuint)(rowBytes * copy.Height),
                new MtlSize { Width = copy.Width, Height = copy.Height, Depth = 1 },
                texture, copy.Slice, copy.Level, default, (nuint)MtlBlitOption.None);
        }

        MetalTiler.EndEncoding(blit);
        harness.Submit();

        var target = harness.Buffer(description.Data.Size);
        var range = new SubresourceRange(0, description.Resources.Levels, 0, description.Resources.Layers);
        harness.Begin();
        harness.Transfer.EncodeTextureToGuest(harness, description, range, texture,
            new MetalGuestRange(target, 0, description.Data.Size, 0), default, 0, description.Data.Size);
        harness.Submit();

        Assert.Equal(0u, harness.Tiler.StagingBufferCount);
        Assert.Equal(expected, harness.Read(target, 0, description.Data.Size));
        output.WriteLine($"{name}: one-pass writeback of 0x{description.Data.Size:x} bytes matched the oracle");
    }

    // A texture the one-pass kernels cannot write - an sRGB target has no shader-write
    // usage - still lands exactly, through the staging buffer.
    [Fact]
    public void AnSrgbRenderTargetStillRoundTripsThroughStaging()
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        RoundTrip(harness, Color2D(GuestPixelFormat.Bits8_8_8_8Srgb, GpuFormat.R8G8B8A8Srgb,
            192, 96, 1, 1, GuestTileMode.RenderTarget), 95, "srgb render target");
        Assert.NotEqual(0u, harness.Tiler.StagingBufferCount);
    }

    private static ulong RowBytesOf(in MetalSubresourceCopy copy, TileElementLayout element) =>
        (ulong)(copy.BufferRowLength != 0 ? copy.BufferRowLength : copy.Width) * element.Bytes;

    private void AssertLiveTexelsEqual(
        List<MetalSubresourceCopy> copies, TileElementLayout element, byte[] expected, byte[] actual, string name)
    {
        ulong compared = 0;
        foreach (var copy in copies)
        {
            var rowBytes = RowBytesOf(copy, element);
            var live = (int)(copy.Width * element.Bytes);
            for (uint row = 0; row < copy.Height; row++)
            {
                var offset = (int)(copy.BufferOffset + (row * rowBytes));
                Assert.True(
                    expected.AsSpan(offset, live).SequenceEqual(actual.AsSpan(offset, live)),
                    $"{name}: level={copy.Level} slice={copy.Slice} row={row} differs");
                compared += (ulong)live;
            }
        }

        output.WriteLine($"{name}: one-pass upload matched the oracle over 0x{compared:x} texel bytes");
    }
}
