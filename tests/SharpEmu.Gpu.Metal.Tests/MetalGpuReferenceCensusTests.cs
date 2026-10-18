// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>The 2 MiB block census behind the "[MEM] gpu-refs" line (SHARPEMU_LOG_MEMORY=1).</summary>
public sealed class MetalGpuReferenceCensusTests
{
    private const ulong Mib = 1UL << 20;
    private const ulong Gib = 1UL << 30;
    private const ulong Base = 0x8_0000_0000;

    [Fact]
    public void ARangeCoversEveryTwoMiBBlockItTouches()
    {
        var set = new MetalBlockSet();
        set.AddRange(Base + Mib, 1);            // one byte in block 0 (1 MiB into it)
        Assert.Equal(1, set.Blocks);
        set.AddRange(Base + (2 * Mib) - 1, 2);  // straddles blocks 0 and 1
        Assert.Equal(2, set.Blocks);
        set.AddRange(Base, 4 * Mib);            // blocks 0..1 again: nothing new
        Assert.Equal(2, set.Blocks);
        set.AddRange(Base + (10 * Mib), 3 * Mib);  // 10 MiB..13 MiB: blocks 5, 6
        Assert.Equal(4, set.Blocks);
    }

    [Fact]
    public void AMultiGigabyteRangeAcrossChunkBoundariesIsCountedExactly()
    {
        var set = new MetalBlockSet();
        // Starts 4 MiB before a 128 MiB chunk edge and runs 3 GiB: 1536 blocks + the partial block.
        var start = Base + (128 * Mib) - (4 * Mib) + 1;
        set.AddRange(start, 3 * Gib);
        var firstBlock = start >> 21;
        var lastBlock = (start + (3 * Gib) - 1) >> 21;
        Assert.Equal((long)(lastBlock - firstBlock + 1), set.Blocks);
    }

    [Fact]
    public void UnionAndDeviceAddressOnlyCountsUseBlocksNotNamedByAnExactRange()
    {
        var exact = new MetalBlockSet();
        exact.AddRange(Base, 8 * Mib);                 // blocks 0..3
        var reach = new MetalBlockSet();
        reach.AddRange(Base + (4 * Mib), 8 * Mib);     // blocks 2..5: 2,3 already named exactly
        Assert.Equal(2, reach.CountNotIn(exact));
        var union = new MetalBlockSet();
        union.UnionWith(exact);
        union.UnionWith(reach);
        Assert.Equal(6, union.Blocks);
    }

    [Fact]
    public void TheReportSplitsKindsAndTracksIntervalAgainstCumulative()
    {
        var tracker = new MetalGpuReferenceTracker();
        tracker.Record(MetalRefKind.Binding, Base, 6 * Mib);                 // 3 blocks = 6 MiB
        tracker.Record(MetalRefKind.ImageUpload, Base + (4 * Mib), 4 * Mib); // blocks 2..3: block 2 overlaps the binding
        tracker.Record(MetalRefKind.Staged, Base + (100 * Mib), 100);        // 1 block
        tracker.SetDeviceAddressSpans([(Base, 12 * Mib)]);                   // blocks 0..5
        var first = tracker.TakeReport();
        // Named blocks: bind 0..2, image_up 2..3, staged 50: union 0..3 + 50 = 5 blocks = 10 MiB.
        Assert.Contains("interval_mib=10 cumulative_mib=10", first);
        Assert.Contains("bind=6/6", first);
        Assert.Contains("image_up=4/4", first);
        Assert.Contains("staged=2/2", first);
        Assert.Contains("image_wb=0/0", first);
        // The table covers blocks 0..5; blocks 4 and 5 are named by no exact range.
        Assert.Contains("da_only_mib=4/4", first);
        Assert.Contains("da_table_mib=12", first);
        Assert.Contains("marks=3", first);

        tracker.Record(MetalRefKind.Binding, Base + (8 * Mib), 2 * Mib);     // block 4
        var second = tracker.TakeReport();
        Assert.Contains("interval_mib=2 cumulative_mib=12", second);
        Assert.Contains("bind=2/8", second);
        Assert.Contains("da_only_mib=10/2", second);  // interval: 5 of the table's 6 blocks unnamed; cumulative: only block 5
        Assert.Contains("marks=1", second);
    }
}
