// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// Sampled textures held from their T# BASE_LEVEL, through the real request builder and the
/// real cache: what is allocated and counted, which texture level a binding samples, and how
/// the texture follows BASE_LEVEL going down (grown at once) and up (kept through a view while
/// a binding in the last window of presented frames needed the finer level, then shrunk), that
/// descriptors with different BASE_LEVELs do not make it trade reallocations, and that a write
/// gets the whole chain.
/// </summary>
public sealed unsafe partial class MetalImageBudgetTests
{
    private const uint MipSide = 256;
    private const uint MipLevels = 9;
    private const ulong CutAddress = GuestBase + 0x100000;
    private const ulong FullAddress = GuestBase + 0x200000;

    private static readonly nint SelCopyLevelToBuffer = MetalNative.Selector(
        "copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:" +
        "toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:options:");

    // ---- Policy ------------------------------------------------------------------------

    [Fact]
    public void OnlyAPlainMippedColourTextureStartsAtItsBaseLevel()
    {
        var texture = Request(CutAddress, baseLevel: 3).Description;
        Assert.Equal(3u, MetalImageCache.DesiredFirstLevel(in texture, 3));
        Assert.Equal(0u, MetalImageCache.DesiredFirstLevel(in texture, 0));
        // A BASE_LEVEL past the chain keeps the last level.
        Assert.Equal(MipLevels - 1, MetalImageCache.DesiredFirstLevel(in texture, 15));

        var single = texture;
        single.Resources = new SubresourceCount(1, 1);
        Assert.Equal(0u, MetalImageCache.DesiredFirstLevel(in single, 3));

        var volume = texture;
        volume.Type = GuestImageType.Color3D;
        Assert.Equal(0u, MetalImageCache.DesiredFirstLevel(in volume, 3));

        var dcc = texture;
        dcc.Metadata.Kind = MetadataKind.Dcc;
        Assert.Equal(0u, MetalImageCache.DesiredFirstLevel(in dcc, 3));

        var multisample = texture;
        multisample.Samples = 4;
        Assert.Equal(0u, MetalImageCache.DesiredFirstLevel(in multisample, 3));

        // A 128x128 BC1 chain: level 6 is 2x2, so the texture starts at level 5, 4x4, its
        // last level of whole blocks.
        var block = texture;
        block.GuestFormat = GuestPixelFormat.Bc1UNorm;
        block.PixelFormat = GpuFormat.BC1RgbaUnormBlock;
        block.BytesPerBlock = 8;
        block.Extent = new GpuExtent3D(128, 128, 1);
        block.Resources = new SubresourceCount(8, 1);
        Assert.Equal(5u, MetalImageCache.DesiredFirstLevel(in block, 6));
        Assert.Equal(4u, MetalImageCache.DesiredFirstLevel(in block, 4));
    }

    // ---- On the device -----------------------------------------------------------------

    // Allocated from BASE_LEVEL, counted at its real size, and sampled at the right level:
    // the view's level 0 is guest level BASE_LEVEL whatever the texture starts at.
    [Fact]
    public void ATextureStartsAtItsBaseLevelIsCountedSmallerAndSamplesTheSameTexels()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var (cutId, cutView) = AcquireMipped(rig, CutAddress, baseLevel: 2, seed: 0x31);
        var (fullId, _) = AcquireMipped(rig, FullAddress, baseLevel: 0, seed: 0x31);
        var cut = rig.Cache.GetImage(cutId);
        var full = rig.Cache.GetImage(fullId);

        Assert.Equal(2u, cut.FirstLevel);
        Assert.Equal(MipLevels - 2, cut.Shape.MipLevels);
        Assert.Equal(MipSide >> 2, cut.Shape.Width);
        Assert.Equal(0u, full.FirstLevel);
        Assert.Equal(MipLevels, full.Shape.MipLevels);
        Assert.Equal(1, rig.Cache.BaseLevelCounters.Created);

        // Levels 0 and 1 are 15/16 of the chain; whatever Metal pads, the cut texture is
        // well under half, and the budget counts each texture's own allocation.
        output.WriteLine($"cut={cut.AllocatedBytes} full={full.AllocatedBytes} guest=0x{full.Description.Data.Size:x}");
        Assert.True(cut.AllocatedBytes * 2 < full.AllocatedBytes);
        Assert.Equal(cut.AllocatedBytes + full.AllocatedBytes, rig.Cache.TotalUsedMemory);

        // The upload read only the coarser levels' bytes, and the watch covers only them.
        Assert.True(cut.ResidentEnd - cut.ResidentStart < cut.Description.Data.Size);
        Assert.Equal(CutAddress + cut.ResidentStart, cut.WatchBegin);
        Assert.Equal(CutAddress + cut.ResidentEnd, cut.WatchEnd);

        // LOD remapping: view level k is guest level 2 + k, its level count LAST - BASE + 1.
        Assert.Equal(MipLevels - 2, (uint)MetalV2Native.SendValue(cutView, MetalNative.Selector("mipmapLevelCount")));
        Assert.Equal(MipSide >> 2, (uint)MetalV2Native.SendValue(cutView, MetalNative.Selector("width")));
        for (uint level = 0; level < 3; level++)
        {
            Assert.Equal(ReadLevel(rig, full.Texture, level + 2), ReadLevel(rig, cutView, level));
        }
    }

    // BASE_LEVEL going down grows the texture in place at once; going up keeps it through a
    // view while the finer level is still in the working set, then shrinks it.
    [Fact]
    public void TheTextureFollowsBaseLevelDownAtOnceAndUpOnceTheFinerLevelIsIdle()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var (fullId, _) = AcquireMipped(rig, FullAddress, baseLevel: 0, seed: 0x47);
        var reference = rig.Cache.GetImage(fullId);
        var expected = new byte[MipLevels][];
        for (uint level = 0; level < 4; level++)
        {
            expected[level] = ReadLevel(rig, reference.Texture, level);
        }

        // Down: the image a coarser descriptor made is grown for a finer one, same slot.
        var (cutId, _) = AcquireMipped(rig, CutAddress, baseLevel: 3, seed: 0x47);
        var image = rig.Cache.GetImage(cutId);
        var smallBytes = image.AllocatedBytes;
        var (grownId, grownView) = AcquireMipped(rig, CutAddress, baseLevel: 1, seed: 0x47);
        Assert.Equal(cutId, grownId);
        Assert.Equal(1u, image.FirstLevel);
        Assert.Equal(1, rig.Cache.BaseLevelCounters.Grows);
        Assert.True(image.AllocatedBytes > smallBytes);
        Assert.Equal(image.AllocatedBytes + reference.AllocatedBytes, rig.Cache.TotalUsedMemory);
        Assert.Equal(expected[1], ReadLevel(rig, grownView, 0));
        Assert.Equal(expected[3], ReadLevel(rig, grownView, 2));

        // Up, while level 1 is still in use: the same texture through a view from level 3.
        var (_, coarseView) = AcquireMipped(rig, CutAddress, baseLevel: 3, seed: 0x47);
        Assert.Equal(1u, image.FirstLevel);
        Assert.Equal(2u, (uint)MetalV2Native.SendValue(coarseView, MetalNative.Selector("parentRelativeLevel")));
        Assert.Equal(MipSide >> 3, (uint)MetalV2Native.SendValue(coarseView, MetalNative.Selector("width")));
        Assert.Equal(expected[3], ReadLevel(rig, coarseView, 0));

        // A whole-chain image asked for from level 2 keeps its levels while level 0 is in use...
        var (sameId, _) = AcquireMipped(rig, FullAddress, baseLevel: 2, seed: 0x47);
        Assert.Equal(fullId, sameId);
        Assert.Equal(0u, reference.FirstLevel);
        var fullBytes = reference.AllocatedBytes;

        // ...and gives levels 0 and 1 back once no binding needed them for a whole window of
        // eight presented frames. Level 0 was bound in frame 0's window: frames 8-15 still
        // see it in the window before, frame 16 sees two windows without it.
        for (var frame = 1; frame < 16; frame++)
        {
            rig.Frame(seconds: 0.1, submit: false);
            AcquireMipped(rig, FullAddress, baseLevel: 2, seed: 0x47, write: false);
            Assert.Equal(0u, reference.FirstLevel);
        }

        rig.Frame(seconds: 0.1, submit: false);
        var (shrunkId, shrunkView) = AcquireMipped(rig, FullAddress, baseLevel: 2, seed: 0x47, write: false);
        Assert.Equal(fullId, shrunkId);
        Assert.Equal(2u, reference.FirstLevel);
        Assert.Equal(1, rig.Cache.BaseLevelCounters.Shrinks);
        Assert.True(reference.AllocatedBytes * 2 < fullBytes);
        Assert.Equal(image.AllocatedBytes + reference.AllocatedBytes, rig.Cache.TotalUsedMemory);
        Assert.Equal(expected[2], ReadLevel(rig, shrunkView, 0));
        Assert.Equal(expected[3], ReadLevel(rig, shrunkView, 1));
    }

    // The streamer's cycle: a texture made cut, streamed finer one level at a time (each a grow),
    // then coarsened past where it started. The texture follows down and gives the memory back.
    [Fact]
    public void ATextureTheTitleCoarsensShrinksAndReleasesItsFinerLevels()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var (id, _) = AcquireMipped(rig, CutAddress, baseLevel: 2, seed: 0x6D);
        var image = rig.Cache.GetImage(id);
        rig.Frame(seconds: 0.5, submit: false);
        AcquireMipped(rig, CutAddress, baseLevel: 1, seed: 0x6D, write: false);
        rig.Frame(seconds: 0.5, submit: false);
        AcquireMipped(rig, CutAddress, baseLevel: 0, seed: 0x6D, write: false);
        Assert.Equal(0u, image.FirstLevel);
        Assert.Equal(2, rig.Cache.BaseLevelCounters.Grows);
        var wholeBytes = rig.Cache.TotalUsedMemory;

        for (var frame = 0; frame < 16 && image.FirstLevel == 0; frame++)
        {
            rig.Frame(seconds: 0.5, submit: false);
            AcquireMipped(rig, CutAddress, baseLevel: 3, seed: 0x6D, write: false);
        }

        Assert.Equal(3u, image.FirstLevel);
        Assert.Equal(1, rig.Cache.BaseLevelCounters.Shrinks);
        Assert.True(rig.Cache.TotalUsedMemory * 8 < wholeBytes);
    }

    // Two descriptors of one texture with different BASE_LEVELs bound in alternate frames keep
    // it at the finer one and never reallocate it. One that comes back only every 20 frames,
    // past the eight-frame window, reverses the first shrink or two; each reversal doubles
    // the image's window, and once that spans the period the texture stays.
    [Fact]
    public void AlternatingBaseLevelsDoNotTradeReallocations()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var (id, _) = AcquireMipped(rig, FullAddress, baseLevel: 0, seed: 0x71);
        var image = rig.Cache.GetImage(id);
        var texture = image.Texture;
        for (var frame = 1; frame <= 48; frame++)
        {
            rig.Frame(seconds: 0.1, submit: false);
            AcquireMipped(rig, FullAddress, baseLevel: frame % 2 == 0 ? 0u : 2u, seed: 0x71, write: false);
        }

        Assert.Equal(0u, image.FirstLevel);
        Assert.Equal(texture, image.Texture);
        Assert.Equal(0, rig.Cache.BaseLevelCounters.Shrinks);
        Assert.Equal(0, rig.Cache.BaseLevelCounters.Grows);

        long settled = 0;
        for (var frame = 0; frame < 240; frame++)
        {
            rig.Frame(seconds: 0.1, submit: false);
            AcquireMipped(rig, FullAddress, baseLevel: frame % 20 == 0 ? 0u : 2u, seed: 0x71, write: false);
            if (frame == 120)
            {
                settled = rig.Cache.BaseLevelCounters.Shrinks;
            }
        }

        var counters = rig.Cache.BaseLevelCounters;
        output.WriteLine($"shrinks={counters.Shrinks} grows={counters.Grows}");
        Assert.InRange(settled, 1, 3);
        Assert.Equal(settled, counters.Shrinks);
        Assert.Equal(counters.Shrinks, counters.Grows);
    }

    // Anything that writes the image gets the whole chain first, and a written image stays
    // whole: the write-back retiles every level, as before.
    [Fact]
    public void AWriteGivesTheTextureTheWholeChainAndKeepsIt()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var (cutId, _) = AcquireMipped(rig, CutAddress, baseLevel: 2, seed: 0x5B);
        var image = rig.Cache.GetImage(cutId);
        Assert.Equal(2u, image.FirstLevel);

        rig.Cache.MarkGpuWritten(cutId);
        Assert.Equal(0u, image.FirstLevel);
        Assert.Equal(MipLevels, image.Shape.MipLevels);
        Assert.Equal(0UL, image.ResidentStart);
        Assert.Equal(image.Description.Data.Size, image.ResidentEnd);
        Assert.True(image.IsGpuModified);
        Assert.Equal(1, rig.Cache.BaseLevelCounters.Promotions);

        // More frames than the shrink test needs, so only the write keeps it whole.
        for (var frame = 0; frame < 20; frame++)
        {
            rig.Frame(seconds: 0.1, submit: false);
            AcquireMipped(rig, CutAddress, baseLevel: 2, seed: 0x5B, write: false);
        }

        Assert.Equal(0u, image.FirstLevel);
        Assert.Equal(0, rig.Cache.BaseLevelCounters.Shrinks);
    }

    // ---- Helpers -----------------------------------------------------------------------

    // The T# of a 256x256 RGBA8 64 KiB-swizzled texture with a nine-level chain.
    private static ImageRequest Request(ulong address, uint baseLevel)
    {
        var words = new uint[8];
        var encoded = address >> 8;
        words[0] = (uint)encoded;
        words[1] = (uint)((encoded >> 32) & 0xFF) | ((uint)GuestPixelFormat.Bits8_8_8_8UNorm << 20) |
            (((MipSide - 1) & 0x3) << 30);
        words[2] = ((MipSide - 1) >> 2) | ((MipSide - 1) << 14);
        words[3] = 0xFAC | (baseLevel << 12) | ((MipLevels - 1) << 16) |
            ((uint)GuestTileMode.Standard64KB << 20) | ((uint)GuestImageType.Color2D << 28);
        words[5] = (MipLevels - 1) << 4;
        var resolution = ImageRequestBuilders.Texture(
            words, new ShaderImageShape(false, false, false, false, TextureNumericClass.Float));
        Assert.Equal(baseLevel, resolution.Request.View.BaseLevel);
        Assert.Equal(MipLevels, resolution.Request.Description.Resources.Levels);
        return resolution.Request;
    }

    // Binds the texture for sampling as a draw would, with the guest bytes a pattern of the
    // seed, and submits so the upload has landed.
    private static (ResourceSlotIdentifier Image, nint View) AcquireMipped(
        Rig rig, ulong address, uint baseLevel, uint seed, bool write = true)
    {
        var request = Request(address, baseLevel);
        if (write)
        {
            rig.WriteGuest(address, MetalTileOracle.Pattern((int)request.Description.Data.Size, seed));
        }

        var identifier = rig.Cache.FindImage(ref request);
        var view = rig.Cache.AcquireTextureView(identifier, request);
        rig.Harness.Submit();
        rig.Harness.Begin();
        return (identifier, view);
    }

    private static byte[] ReadLevel(Rig rig, nint texture, uint level)
    {
        var width = Math.Max((uint)MetalV2Native.SendValue(texture, MetalNative.Selector("width")) >> (int)level, 1);
        var height = Math.Max((uint)MetalV2Native.SendValue(texture, MetalNative.Selector("height")) >> (int)level, 1);
        var rowBytes = (ulong)width * 4;
        var buffer = rig.Harness.Buffer(rowBytes * height);
        var blit = rig.Harness.Tiler.BeginBlit(rig.Harness);
        MetalNative.SendCopyTextureToBufferOptions(blit, SelCopyLevelToBuffer,
            texture, 0, level, default, new MtlSize { Width = width, Height = height, Depth = 1 },
            buffer, 0, (nuint)rowBytes, (nuint)(rowBytes * height), (nuint)MtlBlitOption.None);
        MetalTiler.EndEncoding(blit);
        rig.Harness.Submit();
        rig.Harness.Begin();
        return rig.Harness.Read(buffer, 0, rowBytes * height);
    }
}
