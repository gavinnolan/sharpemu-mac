// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// An image whose guest bytes are not one backed span is transferred a run at a time, and
// a run ends wherever a window does rather than on a layer boundary. What arrives has to
// be what one whole transfer would have produced, byte for byte: a run that cuts a
// surface part way down is carried by whole block rows, which are the finest cut whose
// tiled bytes are still one contiguous range.
public sealed partial class MetalImageTransferTests
{
    [Theory]
    [InlineData((int)GuestTileMode.Standard64KB, 128u, 512u, 1u, 0x10000UL, "tiled 64KB")]
    [InlineData((int)GuestTileMode.Standard64KB, 128u, 512u, 1u, 0x18000UL, "tiled 64KB/odd run")]
    [InlineData((int)GuestTileMode.Standard4KB, 64u, 128u, 1u, 0x2000UL, "tiled 4KB")]
    [InlineData((int)GuestTileMode.Standard4KB, 96u, 128u, 1u, 0x3000UL, "tiled 4KB/odd run")]
    [InlineData((int)GuestTileMode.Linear, 64u, 128u, 1u, 0x2000UL, "linear")]
    [InlineData((int)GuestTileMode.Linear, 96u, 100u, 1u, 0x1000UL, "linear/odd run")]
    public void ATransferCutIntoByteRunsMatchesTheWholeTransfer(
        int tile, uint width, uint height, uint layers, ulong runBytes, string name)
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = Color2D(
            GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, width, height, 1, layers, (GuestTileMode)tile);
        var guest = BuildGuest(description, 0x51 + (uint)name.Length);
        var whole = TransferWhole(harness, description, guest);
        var runs = TransferInByteRuns(harness, description, guest, runBytes, out var pieces);

        Assert.Equal(whole, runs);
        Assert.True(pieces > 1, $"{name} was not actually cut: pieces={pieces}");
        output.WriteLine($"{name}: 0x{description.Data.Size:x} bytes in {pieces} runs of 0x{runBytes:x} matched");
    }

    [Fact]
    public void ADepthSurfaceCutIntoByteRunsMatchesTheWholeTransfer()
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = Depth(GuestDepthFormat.Z32Float, stencil: false, 64, 64, 2);
        var guest = BuildGuest(description, 0x37);
        var runs = TransferInByteRuns(harness, description, guest, description.Data.Size / 2, out var pieces);

        // Against the guest bytes themselves rather than against a whole-surface
        // transfer: a depth plane is its own oracle, and the comparison then does not
        // depend on what else the device did first.
        Assert.Equal(guest, runs);
        Assert.True(pieces > 1, $"the depth surface was not actually cut: pieces={pieces}");
        output.WriteLine($"depth: 0x{description.Data.Size:x} bytes in {pieces} runs matched");
    }

    // A run end never falls inside a subresource the transfer cannot cut, so the caller
    // can always hand a whole number of block rows over.
    [Theory]
    [InlineData((int)GuestTileMode.Standard64KB, 128u, 256u)]
    [InlineData((int)GuestTileMode.Standard4KB, 96u, 96u)]
    public void ARunEndIsPulledBackToABlockRowBoundary(int tile, uint width, uint height)
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = Color2D(
            GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, width, height, 1, 1, (GuestTileMode)tile);
        var size = description.Data.Size;
        for (var candidate = 1UL; candidate < size; candidate += 0x400)
        {
            var end = harness.Transfer.AlignRunEnd(description, 0, candidate);
            Assert.True(end <= candidate, $"a run end grew past what the window covered: 0x{end:x} > 0x{candidate:x}");
        }

        Assert.Equal(size, harness.Transfer.AlignRunEnd(description, 0, size));
    }

    // A walk stepped over a hole lands wherever the window ended, which is rarely a
    // boundary a transfer can start at. Every byte such a walk claims has to be a byte it
    // really writes, or the count of what stayed stale is a guess.
    [Theory]
    [InlineData((int)GuestTileMode.Standard64KB, 256u, 512u, 0x30000UL)]
    [InlineData((int)GuestTileMode.Standard4KB, 128u, 128u, 0x1800UL)]
    [InlineData((int)GuestTileMode.Linear, 96u, 64u, 0x500UL)]
    public void AClaimedRunWritesEveryByteItClaims(int tile, uint width, uint height, ulong holeBytes)
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = Color2D(
            GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, width, height, 1, 1, (GuestTileMode)tile);
        var size = description.Data.Size;
        var guest = BuildGuest(description, 0x6D);
        var source = harness.Buffer(guest);
        var target = harness.Buffer(size);
        var texture = harness.Texture(harness.Transfer.ShapeFor(description));
        var range = new SubresourceRange(0, description.Resources.Levels, 0, description.Resources.Layers);
        var claimed = new bool[size];
        var walked = 0;
        for (var offset = holeBytes; offset < size;)
        {
            // The hole is the first bytes of the image, so the first run starts inside a
            // row it cannot have: the start has to move off it.
            var start = harness.Transfer.AlignRunStart(description, offset);
            Assert.True(start >= offset, $"a run start moved backwards: 0x{start:x} < 0x{offset:x}");
            if (start >= size)
            {
                break;
            }

            var end = harness.Transfer.AlignRunEnd(description, start, size);
            Assert.True(end > start, $"a run from 0x{start:x} to the end of the image carried nothing");
            var pieceSize = end - start;
            harness.Begin();
            harness.Transfer.EncodeGuestToTexture(
                harness, description, range, new MetalGuestRange(source, start, pieceSize, 0), default,
                texture, start, pieceSize);
            harness.Submit();
            harness.Begin();
            harness.Transfer.EncodeTextureToGuest(
                harness, description, range, texture, new MetalGuestRange(target, start, pieceSize, 0), default,
                start, pieceSize);
            harness.Submit();
            for (var index = start; index < end; index++)
            {
                claimed[index] = true;
            }

            offset = end;
            walked++;
        }

        Assert.True(walked != 0, $"the walk claimed nothing after a hole of 0x{holeBytes:x}");
        var written = harness.Read(target, 0, size);
        for (var index = 0UL; index < size; index++)
        {
            if (claimed[index])
            {
                Assert.True(written[index] == guest[index],
                    $"a claimed byte was not written: index=0x{index:x} claimed run, " +
                    $"guest=0x{guest[index]:x2} target=0x{written[index]:x2}");
            }
        }

        output.WriteLine(
            $"tile={(GuestTileMode)tile} hole=0x{holeBytes:x}: {walked} runs claimed and wrote every claimed byte");
    }

    private static long FirstDifference(byte[] left, byte[] right)
    {
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            if (left[index] != right[index])
            {
                return index;
            }
        }

        return left.Length == right.Length ? -1 : Math.Min(left.Length, right.Length);
    }

    // Each run is handed only its own bytes, exactly as the image cache hands one
    // window-resolved run at a time, and the subresource range stays the whole image:
    // what a run carries is decided by its bytes, not by whole layers.
    private static byte[] TransferInByteRuns(
        MetalTransferHarness harness, in ImageDescription description, byte[] guest, ulong runBytes, out int pieces)
    {
        var size = description.Data.Size;
        var source = harness.Buffer(guest);
        var target = harness.Buffer(size);
        var texture = harness.Texture(harness.Transfer.ShapeFor(description));
        var range = new SubresourceRange(0, description.Resources.Levels, 0, description.Resources.Layers);
        pieces = 0;
        for (var offset = 0UL; offset < size;)
        {
            var end = harness.Transfer.AlignRunEnd(description, offset, Math.Min(offset + runBytes, size));
            Assert.True(end > offset, $"a run starting at 0x{offset:x} of 0x{runBytes:x} carried nothing");
            var pieceSize = end - offset;
            harness.Begin();
            harness.Transfer.EncodeGuestToTexture(
                harness, description, range, new MetalGuestRange(source, offset, pieceSize, 0), default,
                texture, offset, pieceSize);
            harness.Submit();
            harness.Begin();
            harness.Transfer.EncodeTextureToGuest(
                harness, description, range, texture, new MetalGuestRange(target, offset, pieceSize, 0), default,
                offset, pieceSize);
            harness.Submit();
            offset = end;
            pieces++;
        }

        return harness.Read(target, 0, size);
    }
}
