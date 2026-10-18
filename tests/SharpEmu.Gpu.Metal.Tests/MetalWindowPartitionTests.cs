// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// The cut of the backing into disjoint windows, without a device: a range across a cut
/// moves the cut, packed straddlers do not chain into one window, a move waits while a
/// window it would replace is pinned, and idle windows are cut back to granules.
/// </summary>
public sealed class MetalWindowPartitionTests
{
    private const ulong Mib = 1024UL * 1024;
    private const ulong Page = 0x4000;
    private static readonly MetalWindowSet None = new();

    private static MetalWindowPartition Partition(ulong granules = 8, ulong maxWindowBytes = 4 * Mib) =>
        new(granules * Mib, Mib, maxWindowBytes, Page);

    private static (ulong Start, ulong End)[] Layout(MetalWindowPartition partition) =>
        partition.Windows.Select(window => (window.Start, window.End)).ToArray();

    [Fact]
    public void WindowsStartOnePerGranule()
    {
        var partition = Partition(granules: 4);
        Assert.Equal([(0, Mib), (Mib, 2 * Mib), (2 * Mib, 3 * Mib), (3 * Mib, 4 * Mib)], Layout(partition));
        Assert.Equal(1 * Mib, partition.At(Mib).Start);
        Assert.Equal(0UL, partition.At(Mib - 1).Start);
    }

    // The cut inside the range moves right to its end (rounded up to a page): the window
    // before grows by what the range takes from the one after, and nothing else changes.
    [Fact]
    public void ARangeAcrossACutMovesTheCutToItsEnd()
    {
        var partition = Partition();
        var retired = new List<MetalWindowPartition.Window>();
        var version = partition.Version;

        Assert.Equal(
            MetalWindowPartition.Placement.Moved,
            partition.Place(Mib - 0x8000, Mib + 0x7001, None, retired, out var window));
        Assert.Equal((0UL, Mib + 0x8000), (window.Start, window.End));
        Assert.Equal((Mib + 0x8000, 2 * Mib), (partition.At(Mib + 0x8000).Start, partition.At(Mib + 0x8000).End));
        Assert.Equal(8, partition.Windows.Count);
        Assert.Equal(2, retired.Count);
        Assert.NotEqual(version, partition.Version);

        // The same range, or any part of it, now lies in that window.
        Assert.Equal(MetalWindowPartition.Placement.Fits, partition.Place(Mib - 0x8000, Mib + 0x7001, None, retired, out var again));
        Assert.Same(window, again);
        Assert.Same(window, partition.At(Mib + 0x1000));
    }

    // A heap packed with allocations has one straddling each granule edge, and every two of
    // them share the granule between their edges. Merging the windows they touch would make
    // one window of the whole heap; moving the cuts keeps every window about a granule.
    [Fact]
    public void StraddlersAtEveryEdgeDoNotChainIntoOneWindow()
    {
        var partition = Partition();
        var retired = new List<MetalWindowPartition.Window>();
        var ranges = Enumerable.Range(1, 7)
            .Select(edge => ((ulong)edge * Mib - 0x10000, (ulong)edge * Mib + 0x10000))
            .ToArray();
        foreach (var (start, end) in ranges)
        {
            Assert.Equal(MetalWindowPartition.Placement.Moved, partition.Place(start, end, None, retired, out _));
        }

        Assert.Equal(8, partition.Windows.Count);
        Assert.All(partition.Windows, window => Assert.True(window.Size <= Mib + 0x10000, $"{window.Start:X}+{window.Size:X}"));
        foreach (var (start, end) in ranges)
        {
            Assert.Equal(MetalWindowPartition.Placement.Fits, partition.Place(start, end, None, retired, out _));
        }
    }

    // Ranges that overlap each other across a cut have to share a window, and do.
    [Fact]
    public void OverlappingRangesEndUpInOneWindow()
    {
        var partition = Partition();
        var retired = new List<MetalWindowPartition.Window>();
        partition.Place(Mib - 0x40000, Mib + 0x40000, None, retired, out _);
        partition.Place(Mib + 0x20000, 2 * Mib + 0x20000, None, retired, out var window);
        Assert.Equal(MetalWindowPartition.Placement.Fits, partition.Place(Mib - 0x40000, Mib + 0x40000, None, retired, out var first));
        Assert.Same(window, first);
        Assert.True(window.Start <= Mib - 0x40000 && window.End >= 2 * Mib + 0x20000);
    }

    // When moving right would grow the window past the cap, the cut moves left to the range's
    // start, and around it on both sides when that is still too large.
    [Fact]
    public void AMoveThatWouldPassTheCapGoesLeftOrAroundTheRange()
    {
        var partition = Partition(maxWindowBytes: Mib + 0x80000);
        var retired = new List<MetalWindowPartition.Window>();

        Assert.Equal(MetalWindowPartition.Placement.Moved,
            partition.Place(Mib - 0x10000, Mib + 0x90000, None, retired, out var left));
        Assert.Equal((Mib - 0x10000, 2 * Mib), (left.Start, left.End));
        Assert.Equal((0UL, Mib - 0x10000), (partition.At(0).Start, partition.At(0).End));

        Assert.Equal(MetalWindowPartition.Placement.Moved,
            partition.Place(3 * Mib - 0x30000, 4 * Mib + 0x30000, None, retired, out var around));
        Assert.Equal((3 * Mib - 0x30000, 4 * Mib + 0x30000), (around.Start, around.End));

        Assert.Equal(MetalWindowPartition.Placement.TooLarge,
            partition.Place(5 * Mib, 6 * Mib + 0x80001, None, retired, out _));
    }

    // A move replaces windows with new buffers at new GPU addresses, so it may not touch a
    // window the command buffer being recorded has used: it waits, and nothing changes.
    [Fact]
    public void AMoveThatWouldReplaceAPinnedWindowWaits()
    {
        var partition = Partition();
        var retired = new List<MetalWindowPartition.Window>();
        var pinned = new MetalWindowSet();
        pinned.Add(partition.At(Mib).Id);
        var before = Layout(partition);
        var version = partition.Version;

        Assert.Equal(MetalWindowPartition.Placement.Pinned,
            partition.Place(Mib - 0x8000, Mib + 0x8000, pinned, retired, out _));
        Assert.Equal(before, Layout(partition));
        Assert.Equal(version, partition.Version);
        Assert.Empty(retired);

        Assert.Equal(MetalWindowPartition.Placement.Moved,
            partition.Place(Mib - 0x8000, Mib + 0x8000, None, retired, out _));
    }

    // Idle windows are cut back to one per granule; a window used since the idle serial,
    // and the edges it has with its idle neighbours, stay.
    [Fact]
    public void IdleWindowsAreCutBackToGranules()
    {
        var partition = Partition();
        var retired = new List<MetalWindowPartition.Window>();
        partition.Place(Mib - 0x8000, Mib + 0x8000, None, retired, out var busy);
        partition.Place(5 * Mib - 0x8000, 5 * Mib + 0x8000, None, retired, out var idle);
        busy.LastUsedSerial = 100;
        idle.LastUsedSerial = 10;
        retired.Clear();

        Assert.True(partition.Normalize(idleSerial: 50, None, retired));
        Assert.Contains(busy, partition.Windows);
        Assert.DoesNotContain(idle, partition.Windows);
        Assert.Equal(
            [(0, Mib + 0x8000), (Mib + 0x8000, 2 * Mib), (2 * Mib, 3 * Mib), (3 * Mib, 4 * Mib),
             (4 * Mib, 5 * Mib), (5 * Mib, 6 * Mib), (6 * Mib, 7 * Mib), (7 * Mib, 8 * Mib)],
            Layout(partition));
        Assert.False(partition.Normalize(idleSerial: 50, None, retired));

        var pinned = new MetalWindowSet();
        pinned.Add(busy.Id);
        Assert.False(partition.Normalize(idleSerial: 200, pinned, retired));
        Assert.True(partition.Normalize(idleSerial: 200, None, retired));
        Assert.Equal(Enumerable.Range(0, 8).Select(index => ((ulong)index * Mib, (ulong)(index + 1) * Mib)).ToArray(),
            Layout(partition));
    }
}
