// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// The guest windows are named with useResource on every encoder, so their mapped total
/// is what the device has to hold resident; a command buffer that names more than the
/// device can hold fails with Insufficient Memory and nothing is drawn at all. These pin
/// the layout choice that keeps the total inside the budget.
/// </summary>
public sealed class MetalGuestWindowLayoutTests
{
    private const ulong Gib = 1024UL * 1024 * 1024;

    // The measured case: a 16.375 GiB backing, 4 GiB windows and a 24 GB M4. Half
    // overlap asks for 30.375 GiB, which the device refuses, so the windows must tile.
    [Fact]
    public void BackingLargerThanHalfTheBudgetTilesInsteadOfOverlapping()
    {
        var aliasSize = (16 * Gib) + (3 * Gib / 8);
        var (stride, count, mapped) = MetalGuestMemory.ChooseWindowLayout(aliasSize, 4 * Gib, 16 * Gib);

        Assert.Equal(4 * Gib, stride);
        Assert.Equal(5, count);
        Assert.Equal(aliasSize, mapped);
        Assert.True(mapped <= aliasSize);
    }

    // The measured Demon's Souls case: a 16.4375 GiB backing and an 18186 MiB working set.
    // Half overlap does not fit, but a seam per edge does, and the ~2 MiB binding at alias
    // offset 0xFFE60000 that used to straddle the first edge now lies inside one window.
    [Fact]
    public void BackingTooLargeForHalfOverlapStillOverlapsByTheSeam()
    {
        const ulong aliasSize = 0x41C000000;
        const ulong budget = 18186UL * 1024 * 1024;
        var seam = MetalGuestMemory.SeamOverlapBytes;
        var (stride, count, mapped) = MetalGuestMemory.ChooseWindowLayout(aliasSize, 4 * Gib, budget);

        Assert.Equal(4 * Gib - seam, stride);
        Assert.Equal(5, count);
        Assert.Equal(aliasSize + ((ulong)(count - 1) * seam), mapped);
        Assert.True(mapped <= budget);
        Assert.True(FitsOneWindow(0xFFE60000, 0x234000, aliasSize, 4 * Gib, stride, count));
    }

    // Every range up to the seam, wherever it sits around an edge, fits the window its
    // first byte selects - the selection the resolver makes.
    [Fact]
    public void EveryRangeUpToTheSeamFitsTheWindowItsFirstByteSelects()
    {
        const ulong aliasSize = 0x41C000000;
        var seam = MetalGuestMemory.SeamOverlapBytes;
        var (stride, count, _) = MetalGuestMemory.ChooseWindowLayout(aliasSize, 4 * Gib, 18186UL * 1024 * 1024);
        for (var edge = 1; edge < count; edge++)
        {
            var tiledEdge = (ulong)edge * 4 * Gib;
            foreach (var before in new ulong[] { 1, 4096, seam / 2, seam - 1, seam })
            {
                var start = tiledEdge - before;
                var size = Math.Min(seam, aliasSize - start);
                Assert.True(FitsOneWindow(start, size, aliasSize, 4 * Gib, stride, count),
                    $"edge={edge} start=0x{start:X} size=0x{size:X}");
            }
        }
    }

    // Without room even for the seam the windows tile; the seam is never forced past the budget.
    [Fact]
    public void ABudgetWithoutRoomForTheSeamTiles()
    {
        const ulong aliasSize = 0x41C000000;
        var (stride, _, mapped) = MetalGuestMemory.ChooseWindowLayout(aliasSize, 4 * Gib, aliasSize + 1);

        Assert.Equal(4 * Gib, stride);
        Assert.Equal(aliasSize, mapped);
    }

    private static bool FitsOneWindow(ulong offset, ulong size, ulong aliasSize, ulong windowBytes, ulong stride, int count)
    {
        var index = (ulong)Math.Min(offset / stride, (ulong)(count - 1));
        var windowBase = index * stride;
        var windowSize = Math.Min(windowBytes, aliasSize - windowBase);
        return offset >= windowBase && offset + size <= windowBase + windowSize;
    }

    // A device with room for it keeps the overlap, so no range up to one stride straddles.
    [Fact]
    public void BackingThatFitsTwiceKeepsTheOverlap()
    {
        var aliasSize = (16 * Gib) + (3 * Gib / 8);
        var (stride, count, mapped) = MetalGuestMemory.ChooseWindowLayout(aliasSize, 4 * Gib, 48 * Gib);

        Assert.Equal(2 * Gib, stride);
        Assert.Equal(8, count);
        Assert.True(mapped > aliasSize);
        Assert.True(mapped <= 48 * Gib);
    }

    [Fact]
    public void BackingInsideOneWindowNeedsOneWindow()
    {
        var (stride, count, mapped) = MetalGuestMemory.ChooseWindowLayout(Gib, 4 * Gib, 64 * Gib);

        Assert.Equal(2 * Gib, stride);
        Assert.Equal(1, count);
        Assert.Equal(Gib, mapped);
    }

    // However small the budget, the tiled layout is the floor: it maps each guest byte
    // once, which is the least a no-copy window set can ask for.
    [Fact]
    public void ATinyBudgetStillMapsEveryGuestByteExactlyOnce()
    {
        var aliasSize = (12 * Gib) + 4096;
        var (stride, _, mapped) = MetalGuestMemory.ChooseWindowLayout(aliasSize, 4 * Gib, 1);

        Assert.Equal(4 * Gib, stride);
        Assert.Equal(aliasSize, mapped);
    }
}
