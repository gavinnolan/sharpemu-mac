// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// The overlay's lock-free page counts only decide that a read overlaps nothing; every answer
// has to be the one the locked walk gives, with and without them.
public sealed class MetalPendingWriteOverlayTests
{
    private const ulong Page = 0x1000;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AReadOnAnotherPageOverlapsNothing(bool pageFilter)
    {
        var overlay = new MetalPendingWriteOverlay(pageFilter);
        overlay.Add(0x7000_0010, [1, 2, 3, 4], serial: 5);

        Assert.Equal(MetalOverlayCoverage.None, overlay.Coverage(0x7000_0010 + Page, 4, () => 0));
        Assert.Equal(MetalOverlayCoverage.None, overlay.Coverage(0x7000_0020, 4, () => 0));
        Span<byte> bytes = [9, 9, 9, 9];
        overlay.Apply(0x7000_0010 + Page, bytes);
        Assert.Equal([9, 9, 9, 9], bytes.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APendingWriteAnswersForItsBytes(bool pageFilter)
    {
        var overlay = new MetalPendingWriteOverlay(pageFilter);
        overlay.Add(0x7000_0010, [1, 2, 3, 4], serial: 5);

        Assert.Equal(MetalOverlayCoverage.Full, overlay.Coverage(0x7000_0010, 4, () => 0));
        Assert.Equal(MetalOverlayCoverage.Partial, overlay.Coverage(0x7000_000E, 4, () => 0));
        Span<byte> bytes = [9, 9, 9, 9];
        overlay.Apply(0x7000_000E, bytes);
        Assert.Equal([9, 9, 1, 2], bytes.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AWriteAcrossAPageBoundaryIsSeenFromBothPages(bool pageFilter)
    {
        var overlay = new MetalPendingWriteOverlay(pageFilter);
        overlay.Add(0x7000_0FFE, [1, 2, 3, 4], serial: 5);

        Assert.Equal(MetalOverlayCoverage.Partial, overlay.Coverage(0x7000_0FFC, 4, () => 0));
        Assert.Equal(MetalOverlayCoverage.Partial, overlay.Coverage(0x7000_1000, 4, () => 0));
        Assert.Equal(MetalOverlayCoverage.Full, overlay.Coverage(0x7000_0FFE, 4, () => 0));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALandedWriteStopsAnsweringOnceRetired(bool pageFilter)
    {
        var overlay = new MetalPendingWriteOverlay(pageFilter);
        overlay.Add(0x7000_0010, [1, 2, 3, 4], serial: 5);
        overlay.Add(0x7000_2010, [5, 6, 7, 8], serial: 6);

        overlay.Retire(5);

        Assert.Equal(1, overlay.Count);
        Assert.Equal(MetalOverlayCoverage.None, overlay.Coverage(0x7000_0010, 4, () => 5));
        Assert.Equal(MetalOverlayCoverage.Full, overlay.Coverage(0x7000_2010, 4, () => 5));

        overlay.Retire(6);
        overlay.Add(0x7000_0010, [1, 2, 3, 4], serial: 7);
        Assert.Equal(MetalOverlayCoverage.Full, overlay.Coverage(0x7000_0010, 4, () => 6));
        Assert.Equal(MetalOverlayCoverage.None, overlay.Coverage(0x7000_2010, 4, () => 6));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALaterWriteOverTheRangeIsForgotten(bool pageFilter)
    {
        var overlay = new MetalPendingWriteOverlay(pageFilter);
        overlay.Add(0x7000_0010, [1, 2, 3, 4], serial: 5);

        overlay.Forget(0x7000_0010 + Page, 4);
        Assert.Equal(MetalOverlayCoverage.Full, overlay.Coverage(0x7000_0010, 4, () => 0));

        overlay.Forget(0x7000_0012, 2);
        Assert.Equal(MetalOverlayCoverage.Partial, overlay.Coverage(0x7000_0010, 4, () => 0));

        overlay.Forget(0x7000_0000, 0x100);
        Assert.Equal(0, overlay.Count);
        Assert.Equal(MetalOverlayCoverage.None, overlay.Coverage(0x7000_0010, 4, () => 0));
    }

    [Fact]
    public void PagesThatShareACountStillAnswerOnlyForTheirOwnWrites()
    {
        // Far more pages than counts, so many of them share one: a shared count may send a read
        // to the locked walk, which must still find nothing on a page no write is on.
        var overlay = new MetalPendingWriteOverlay();
        for (ulong index = 0; index < 40_000; index += 2)
        {
            overlay.Add(0x7000_0000 + index * Page, [1, 2, 3, 4], serial: 5);
        }

        for (ulong index = 0; index < 40_000; index++)
        {
            var expected = index % 2 == 0 ? MetalOverlayCoverage.Full : MetalOverlayCoverage.None;
            Assert.Equal(expected, overlay.Coverage(0x7000_0000 + index * Page, 4, () => 0));
        }

        overlay.Clear();
        for (ulong index = 0; index < 40_000; index += 2)
        {
            Assert.Equal(MetalOverlayCoverage.None, overlay.Coverage(0x7000_0000 + index * Page, 4, () => 0));
        }
    }

    [Fact]
    public void ALongRangeIsWalkedRatherThanCounted()
    {
        var overlay = new MetalPendingWriteOverlay();
        overlay.Add(0x7010_0000, [1, 2, 3, 4], serial: 5);

        Assert.Equal(MetalOverlayCoverage.Partial, overlay.Coverage(0x7000_0000, 0x20_0000, () => 0));
        Assert.Equal(MetalOverlayCoverage.None, overlay.Coverage(0x7020_0000, 0x20_0000, () => 0));
    }
}
