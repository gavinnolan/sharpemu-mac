// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// The run the cache refreshes or publishes for a dirty range of an image. It covers the
// whole range, widened outwards to the nearest cuts the transfer plan can make - whole
// texel rows of a linear surface, whole block rows of a tiled one - and a linear
// surface's run may begin wherever such a cut falls, so an alias over its tail or its
// middle moves its own bytes rather than every byte of the image. A tiled surface's run
// still begins at its first byte. Planning only: no device.
public sealed partial class MetalImageTransferTests
{
    // 64x128 RGBA8, linear: 0x100 bytes a texel row, 0x8000 in all.
    private static ImageDescription LinearRows() => Color2D(
        GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 64, 128, 1, 1, GuestTileMode.Linear);

    // 128x512 RGBA8 in 64 KiB tiles: four block rows of 64 KiB each.
    private static ImageDescription FourBlockRows() => Color2D(
        GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 128, 512, 1, 1, GuestTileMode.Standard64KB);

    [Fact]
    public void ALinearTailOverlapIsCoveredFromTheTexelRowItStartsIn()
    {
        using var transfer = new MetalImageTransfer(RequireDevice() ? Device.Value : CreateHeadlessDevice());
        var description = LinearRows();
        Assert.Equal(0x8000UL, description.Data.Size);

        Assert.True(MetalImageCache.TryCoveringRun(transfer, in description, 0x6010, 0x8000, out var start, out var end));

        Assert.Equal(0x6000UL, start);
        Assert.Equal(0x8000UL, end);
    }

    [Fact]
    public void ALinearMiddleOverlapIsCoveredByTheTexelRowsAroundIt()
    {
        using var transfer = new MetalImageTransfer(RequireDevice() ? Device.Value : CreateHeadlessDevice());
        var description = LinearRows();

        Assert.True(MetalImageCache.TryCoveringRun(transfer, in description, 0x1010, 0x2020, out var start, out var end));

        Assert.Equal(0x1000UL, start);
        Assert.Equal(0x2100UL, end);
    }

    // The end still stops at the first block row past the overlap rather than running
    // to the image's end, so a tiled surface's untouched tail stays out of the run.
    [Fact]
    public void ATiledMiddleOverlapIsCoveredFromTheImageStartToTheBlockRowAfterIt()
    {
        using var transfer = new MetalImageTransfer(RequireDevice() ? Device.Value : CreateHeadlessDevice());
        var description = FourBlockRows();
        Assert.Equal(0x40000UL, description.Data.Size);

        Assert.True(MetalImageCache.TryCoveringRun(transfer, in description, 0x10100, 0x2FF00, out var start, out var end));

        Assert.Equal(0UL, start);
        Assert.Equal(0x30000UL, end);
    }

    // Overlaps whose covering run is the whole image are refused, so the caller moves
    // the whole image exactly as it did before: a tiled tail, a range inside a single
    // block row that is the whole image, and a range no row boundary separates from
    // either end.
    [Theory]
    [InlineData(128u, 512u, 0x30100UL, 0x40000UL)]
    [InlineData(256u, 128u, 0x100UL, 0x200UL)]
    [InlineData(128u, 512u, 0x100UL, 0x3FF00UL)]
    public void AnOverlapWhoseRunIsTheWholeImageFallsBack(uint width, uint height, ulong dirtyStart, ulong dirtyEnd)
    {
        using var transfer = new MetalImageTransfer(RequireDevice() ? Device.Value : CreateHeadlessDevice());
        var description = Color2D(
            GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, width, height, 1, 1, GuestTileMode.Standard64KB);

        Assert.False(MetalImageCache.TryCoveringRun(transfer, in description, dirtyStart, dirtyEnd, out _, out _));
    }
}
