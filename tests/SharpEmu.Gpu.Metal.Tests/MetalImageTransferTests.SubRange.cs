// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// Refreshing only the bytes that changed has to leave the texture exactly as refreshing
// all of them would have: the touched region carries the new guest bytes, and every byte
// outside it still carries what it carried before. A partial refresh that loses a row is
// not a slow path that catches up later - the texture and guest memory disagree for good.
public sealed partial class MetalImageTransferTests
{
    // The bytes outside the run are the ones a partial refresh is most likely to lose, so
    // they get an assertion of their own rather than being folded into the comparison.
    [Fact]
    public void ARefreshedRunDoesNotDisturbTheBytesAroundIt()
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = Color2D(
            GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 128, 512, 1, 1, GuestTileMode.Standard64KB);
        var first = BuildGuest(description, 0x11);
        var second = BuildGuest(description, 0xC4);
        Assert.True(TryCoveringRun(
            harness, in description, description.Data.Size / 3, description.Data.Size / 2, out var start, out var end));
        Assert.Equal(0UL, start);

        var actual = TransferWholeThenRun(harness, description, first, second, start, end);

        // Before the run and after it, the texture still holds what the first upload put
        // there; inside it, what the second one did.
        Assert.Equal(first.AsSpan(0, (int)start).ToArray(), actual.AsSpan(0, (int)start).ToArray());
        Assert.Equal(
            second.AsSpan((int)start, (int)(end - start)).ToArray(),
            actual.AsSpan((int)start, (int)(end - start)).ToArray());
        Assert.Equal(
            first.AsSpan((int)end).ToArray(),
            actual.AsSpan((int)end).ToArray());
        output.WriteLine($"run 0x{start:x}..0x{end:x} left 0x{start:x} + 0x{description.Data.Size - end:x} bytes alone");
    }

    // Writing back only a run has the same obligation in the other direction: guest
    // memory takes the run's bytes and keeps whatever it held outside it.
    [Fact]
    public void WritingBackOnlyARunLeavesTheRestOfGuestMemoryAlone()
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = Color2D(
            GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 128, 256, 1, 1, GuestTileMode.Standard64KB);
        var guest = BuildGuest(description, 0x4D);
        Assert.True(TryCoveringRun(
            harness, in description, description.Data.Size / 4, description.Data.Size / 2, out var start, out var end));

        var texture = harness.Texture(harness.Transfer.ShapeFor(description));
        var source = harness.Buffer(guest);
        var whole = new SubresourceRange(0, description.Resources.Levels, 0, description.TransferLayers);
        harness.Begin();
        harness.Transfer.EncodeGuestToTexture(
            harness, description, whole, new MetalGuestRange(source, 0, description.Data.Size, 0), default,
            texture, 0, description.Data.Size);
        harness.Submit();

        // A destination pre-filled with a sentinel, so anything the run writes outside
        // itself shows up as a changed sentinel byte.
        var sentinel = new byte[description.Data.Size];
        Array.Fill(sentinel, (byte)0xA5);
        var target = harness.Buffer(sentinel);
        harness.Begin();
        harness.Transfer.EncodeTextureToGuest(
            harness, description, whole, texture,
            new MetalGuestRange(target, start, end - start, 0), default, start, end - start);
        harness.Submit();

        var written = harness.Read(target, 0, description.Data.Size);
        Assert.Equal(
            guest.AsSpan((int)start, (int)(end - start)).ToArray(),
            written.AsSpan((int)start, (int)(end - start)).ToArray());
        Assert.Equal(sentinel.AsSpan(0, (int)start).ToArray(), written.AsSpan(0, (int)start).ToArray());
        Assert.Equal(sentinel.AsSpan((int)end).ToArray(), written.AsSpan((int)end).ToArray());
        output.WriteLine($"writeback of 0x{start:x}..0x{end:x} left the sentinel intact either side");
    }

    // A linear surface's run may begin past its first byte: in the middle of it, for an
    // alias over the middle, or at its tail. Refreshing it from new guest bytes and
    // writing it back both leave every byte outside it as it was.
    [Theory]
    [InlineData(0x2010UL, 0x4020UL, "middle")]
    [InlineData(0x6010UL, 0x8000UL, "tail")]
    public void ALinearRunPastTheFirstByteMovesOnlyItsOwnBytes(ulong dirtyStart, ulong dirtyEnd, string name)
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = Color2D(
            GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 64, 128, 1, 1, GuestTileMode.Linear);
        Assert.True(TryCoveringRun(harness, in description, dirtyStart, dirtyEnd, out var start, out var end));
        Assert.True(start > 0 && start <= dirtyStart && end >= dirtyEnd);
        var first = BuildGuest(description, 0x29);
        var second = BuildGuest(description, 0x9E);

        var refreshed = TransferWholeThenRun(harness, description, first, second, start, end);
        Assert.Equal(first.AsSpan(0, (int)start).ToArray(), refreshed.AsSpan(0, (int)start).ToArray());
        Assert.Equal(
            second.AsSpan((int)start, (int)(end - start)).ToArray(),
            refreshed.AsSpan((int)start, (int)(end - start)).ToArray());
        Assert.Equal(first.AsSpan((int)end).ToArray(), refreshed.AsSpan((int)end).ToArray());

        var texture = harness.Texture(harness.Transfer.ShapeFor(description));
        var whole = new SubresourceRange(0, description.Resources.Levels, 0, description.TransferLayers);
        var source = harness.Buffer(first);
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
        var written = harness.Read(target, 0, description.Data.Size);
        Assert.Equal(
            first.AsSpan((int)start, (int)(end - start)).ToArray(),
            written.AsSpan((int)start, (int)(end - start)).ToArray());
        Assert.Equal(sentinel.AsSpan(0, (int)start).ToArray(), written.AsSpan(0, (int)start).ToArray());
        Assert.Equal(sentinel.AsSpan((int)end).ToArray(), written.AsSpan((int)end).ToArray());
        output.WriteLine($"{name}: run 0x{start:x}..0x{end:x} refreshed and written back alone");
    }

    // A piece has to name the same bytes whether or not the other pieces travel with it.
    // Uploading everything and reading one piece back must give that piece's guest bytes:
    // if a piece is only self-consistent - correct when written and read through the same
    // piece parameters, and misplaced otherwise - then no caller can refresh part of an
    // image, because the part it refreshes is not the part it asked for.
    [Theory]
    [InlineData((int)GuestTileMode.Linear, 64u, 128u, "linear")]
    [InlineData((int)GuestTileMode.Standard64KB, 128u, 512u, "tiled 64KB")]
    [InlineData((int)GuestTileMode.Standard4KB, 96u, 128u, "tiled 4KB")]
    public void APieceReadBackAfterAWholeUploadCarriesThatPiecesBytes(
        int tile, uint width, uint height, string name)
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = Color2D(
            GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, width, height, 1, 1, (GuestTileMode)tile);
        var guest = BuildGuest(description, 0x63 + (uint)name.Length);
        var texture = harness.Texture(harness.Transfer.ShapeFor(description));
        var whole = new SubresourceRange(0, description.Resources.Levels, 0, description.TransferLayers);

        var source = harness.Buffer(guest);
        harness.Begin();
        harness.Transfer.EncodeGuestToTexture(
            harness, description, whole, new MetalGuestRange(source, 0, description.Data.Size, 0), default,
            texture, 0, description.Data.Size);
        harness.Submit();

        var start = harness.Transfer.AlignRunEnd(in description, 0, description.Data.Size / 2);
        var end = harness.Transfer.AlignRunEnd(in description, start, description.Data.Size);
        Assert.True(end > start, $"{name}: no second piece");

        var target = harness.Buffer(description.Data.Size);
        harness.Begin();
        harness.Transfer.EncodeTextureToGuest(
            harness, description, whole, texture,
            new MetalGuestRange(target, start, end - start, 0), default, start, end - start);
        harness.Submit();

        var written = harness.Read(target, start, end - start);
        Assert.Equal(guest.AsSpan((int)start, (int)(end - start)).ToArray(), written);
        output.WriteLine($"{name}: piece 0x{start:x}..0x{end:x} read back its own bytes after a whole upload");
    }

    // The other half of absolute placement: a piece *upload* has to land where the piece
    // says, not merely where a matching piece read would look for it.
    [Theory]
    [InlineData((int)GuestTileMode.Linear, 64u, 128u, "linear")]
    [InlineData((int)GuestTileMode.Standard64KB, 128u, 512u, "tiled 64KB")]
    [InlineData((int)GuestTileMode.Standard4KB, 96u, 128u, "tiled 4KB")]
    public void APieceUploadLandsWhereThePieceSays(int tile, uint width, uint height, string name) =>
        CheckPieceUpload(tile, width, height, name, toImageEnd: true);

    // The same, for a run with image on both sides of it: that is the one a cache asks
    // for when only the middle of a surface changed, and the one a piece walk never
    // tests on its own, because a walk reads each piece back through the same parameters
    // it wrote it with and a consistent misplacement cancels out.
    [Theory]
    [InlineData((int)GuestTileMode.Linear, 64u, 128u, "linear")]
    [InlineData((int)GuestTileMode.Standard64KB, 128u, 512u, "tiled 64KB")]
    [InlineData((int)GuestTileMode.Standard4KB, 96u, 128u, "tiled 4KB")]
    public void APieceUploadBoundedAtBothEndsLandsWhereThePieceSays(
        int tile, uint width, uint height, string name) =>
        CheckPieceUpload(tile, width, height, name, toImageEnd: false);

    private void CheckPieceUpload(int tile, uint width, uint height, string name, bool toImageEnd)
    {
        if (!RequireDevice()) return;
        using var harness = new MetalTransferHarness(Device.Value);
        var description = Color2D(
            GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, width, height, 1, 1, (GuestTileMode)tile);
        var first = BuildGuest(description, 0x15 + (uint)name.Length);
        var second = BuildGuest(description, 0xB2 + (uint)name.Length);
        var texture = harness.Texture(harness.Transfer.ShapeFor(description));
        var whole = new SubresourceRange(0, description.Resources.Levels, 0, description.TransferLayers);

        var firstBuffer = harness.Buffer(first);
        harness.Begin();
        harness.Transfer.EncodeGuestToTexture(
            harness, description, whole, new MetalGuestRange(firstBuffer, 0, description.Data.Size, 0), default,
            texture, 0, description.Data.Size);
        harness.Submit();

        var start = harness.Transfer.AlignRunEnd(in description, 0, description.Data.Size / 4);
        var end = harness.Transfer.AlignRunEnd(
            in description, start, toImageEnd ? description.Data.Size : description.Data.Size / 4 * 3);
        Assert.True(end > start);
        Assert.True(toImageEnd ? end == description.Data.Size : end < description.Data.Size);

        var secondBuffer = harness.Buffer(second);
        harness.Begin();
        harness.Transfer.EncodeGuestToTexture(
            harness, description, whole, new MetalGuestRange(secondBuffer, start, end - start, 0), default,
            texture, start, end - start);
        harness.Submit();

        // Read the same piece back: if the upload landed where it said, this is second's.
        var target = harness.Buffer(description.Data.Size);
        harness.Begin();
        harness.Transfer.EncodeTextureToGuest(
            harness, description, whole, texture,
            new MetalGuestRange(target, start, end - start, 0), default, start, end - start);
        harness.Submit();

        Assert.Equal(
            second.AsSpan((int)start, (int)(end - start)).ToArray(),
            harness.Read(target, start, end - start));
        output.WriteLine($"{name}: piece upload 0x{start:x}..0x{end:x} landed where the piece said");
    }

    // The cache's own rule, so the tests measure what it will really ask for.
    private static bool TryCoveringRun(
        MetalTransferHarness harness, in ImageDescription description,
        ulong dirtyStart, ulong dirtyEnd, out ulong start, out ulong end) =>
        MetalImageCache.TryCoveringRun(harness.Transfer, in description, dirtyStart, dirtyEnd, out start, out end);

    // Uploads first, then refreshes just the run from a second set of guest bytes.
    private static byte[] TransferWholeThenRun(
        MetalTransferHarness harness, in ImageDescription description,
        byte[] first, byte[] second, ulong start, ulong end)
    {
        var texture = harness.Texture(harness.Transfer.ShapeFor(description));
        var whole = new SubresourceRange(0, description.Resources.Levels, 0, description.TransferLayers);
        var firstBuffer = harness.Buffer(first);
        harness.Begin();
        harness.Transfer.EncodeGuestToTexture(
            harness, description, whole, new MetalGuestRange(firstBuffer, 0, description.Data.Size, 0), default,
            texture, 0, description.Data.Size);
        harness.Submit();

        var secondBuffer = harness.Buffer(second);
        harness.Begin();
        harness.Transfer.EncodeGuestToTexture(
            harness, description, whole, new MetalGuestRange(secondBuffer, start, end - start, 0), default,
            texture, start, end - start);
        harness.Submit();

        return ReadBack(harness, description, texture);
    }

    private static byte[] ReadBack(MetalTransferHarness harness, in ImageDescription description, nint texture)
    {
        var target = harness.Buffer(description.Data.Size);
        var whole = new SubresourceRange(0, description.Resources.Levels, 0, description.TransferLayers);
        harness.Begin();
        harness.Transfer.EncodeTextureToGuest(
            harness, description, whole, texture,
            new MetalGuestRange(target, 0, description.Data.Size, 0), default, 0, description.Data.Size);
        harness.Submit();
        return harness.Read(target, 0, description.Data.Size);
    }
}
