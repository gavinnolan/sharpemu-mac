// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// An image too large to map or stage whole whose guest range crosses the edge between two
// windows. It used to resolve as one range, stage only the ring's first 64 MiB, skip the
// copy as too short and still be marked clean, so the texture stayed zero for good. It is
// now cut into window-resident runs: each at most a run's worth, starting and ending on
// cuts no subresource straddles, wholly inside the window its first byte selects, and
// together covering every byte.
public sealed partial class MetalImageTransferTests
{
    private const ulong MaxUploadRunBytes = 32UL * 1024 * 1024;

    // A fixture layout: a 16.4375 GiB backing in 4 GiB windows overlapping by a 64 MiB seam.
    private const ulong SeamAliasSize = 0x41C000000;
    private const ulong FourGib = 4UL * 1024 * 1024 * 1024;

    // Silent Hill's 4098x4096 RGBA32F render target at window offset 0xEFC50000: 260 MiB,
    // the last 0x50000 bytes of it past the end of window 0.
    [Fact]
    public void ARenderTargetAcrossAWindowEdgeUploadsInWindowRuns()
    {
        using var transfer = new MetalImageTransfer(RequireDevice() ? Device.Value : CreateHeadlessDevice());
        var description = Color2D(
            GuestPixelFormat.Bits32_32_32_32Float, GpuFormat.R32G32B32A32Sfloat, 4098, 4096, 1, 1,
            GuestTileMode.RenderTarget);
        Assert.Equal(0x10400000UL, description.Data.Size);

        AssertWindowRunsCoverTheImage(transfer, in description, 0xEFC50000, "render target RGBA32F");
    }

    // A linear surface cuts at texel rows instead of block rows, under the same layout.
    [Fact]
    public void ALinearImageAcrossAWindowEdgeUploadsInWindowRuns()
    {
        using var transfer = new MetalImageTransfer(RequireDevice() ? Device.Value : CreateHeadlessDevice());
        var description = Color2D(
            GuestPixelFormat.Bits32_32_32_32Float, GpuFormat.R32G32B32A32Sfloat, 4098, 4096, 1, 1,
            GuestTileMode.Linear);
        Assert.True(description.Data.Size > 2 * MaxUploadRunBytes);
        // Half the image on either side of the first window's end, on a page.
        var windowOffset = (FourGib - (description.Data.Size / 2)) & ~0xFFFUL;

        AssertWindowRunsCoverTheImage(transfer, in description, windowOffset, "linear RGBA32F");
    }

    // A hole in the guest mapping no window covers is staged through the ring a block row
    // at a time, never more than a run's worth, and the windows carry the rest.
    [Fact]
    public void AHoleInTheMappingIsStagedInRunsNoLargerThanTheRing()
    {
        using var transfer = new MetalImageTransfer(RequireDevice() ? Device.Value : CreateHeadlessDevice());
        var description = Color2D(
            GuestPixelFormat.Bits32_32_32_32Float, GpuFormat.R32G32B32A32Sfloat, 4098, 4096, 1, 1,
            GuestTileMode.RenderTarget);
        const ulong holeStart = 0x4000000;
        const ulong holeEnd = 0x4800000;
        var runs = new List<MetalImageCache.UploadRun>();

        var uncovered = MetalImageCache.PlanUploadRuns(
            transfer, in description, 0, description.Data.Size, MaxUploadRunBytes,
            (offset, size) => offset >= holeStart && offset < holeEnd ? 0
                : offset < holeStart ? Math.Min(size, holeStart - offset)
                : size,
            runs);

        Assert.Equal(0UL, uncovered);
        AssertContiguousOnCuts(transfer, in description, runs, "hole");
        Assert.Contains(runs, run => run.Staged);
        foreach (var run in runs)
        {
            Assert.True(run.Size <= MaxUploadRunBytes, $"run 0x{run.Offset:X}+0x{run.Size:X} is past a run's worth");
            if (run.Staged)
            {
                // Only the block rows the hole touches are staged.
                Assert.True(run.Offset < holeEnd && run.Offset + run.Size > holeStart,
                    $"staged run 0x{run.Offset:X}+0x{run.Size:X} lies outside the hole");
            }
            else
            {
                Assert.True(run.Offset + run.Size <= holeStart || run.Offset >= holeEnd,
                    $"window run 0x{run.Offset:X}+0x{run.Size:X} reaches into the hole");
            }
        }
    }

    // On the device: guest bytes behind a window edge, uploaded through the runs the plan
    // makes over real windows, read back whole equal what the guest wrote on both sides.
    [Fact]
    public void AnImageAcrossAWindowEdgeUploadsEveryByteThroughTheWindows()
    {
        if (!RequireDevice()) return;
        using var backing = new MetalGuestWindowGranuleTests.Backing();
        using var harness = new MetalTransferHarness(Device.Value);
        using var memory = new MetalGuestMemory(
            Device.Value, backing, new MetalGuestWindowGranuleTests.BackingMemory(backing),
            MetalGuestWindowGranuleTests.GranuleBytes, maxWindowBytes: MetalGuestWindowGranuleTests.GranuleBytes);
        // 128x1024 RGBA8 in 64 KiB tiles: eight 64 KiB block rows, 0x40000 bytes of them
        // on either side of window 0's end.
        var description = Color2D(
            GuestPixelFormat.Bits8_8_8_8UNorm, GpuFormat.R8G8B8A8Unorm, 128, 1024, 1, 1, GuestTileMode.Standard64KB);
        Assert.Equal(0x80000UL, description.Data.Size);
        var address = MetalGuestWindowGranuleTests.GuestBase + MetalGuestWindowGranuleTests.GranuleBytes - 0x40000;
        description.Data = new GuestSpan(address, description.Data.Size);
        // This command buffer already used the windows on both sides of the edge, so the edge
        // cannot move out of the image before the next one and the planner has to cut the
        // image where the window ends.
        memory.BeginSubmission(1, 0);
        Assert.True(memory.ResolveWritable(address, 0x100).IsValid);
        Assert.True(memory.ResolveWritable(address + 0x40000, 0x100).IsValid);
        Assert.False(memory.ResolveWritable(address, description.Data.Size).IsValid);
        var guest = BuildGuest(description, 0x6B);
        Assert.True(backing.TryWriteBacking(address, guest));

        var runs = new List<MetalImageCache.UploadRun>();
        var uncovered = MetalImageCache.PlanUploadRuns(
            harness.Transfer, in description, 0, description.Data.Size, MaxUploadRunBytes,
            (offset, size) => memory.ResolveWritablePrefix(address + offset, size).Size, runs);
        Assert.Equal(0UL, uncovered);
        Assert.True(runs.Count >= 2, $"one run carried an image that crosses a window edge ({runs.Count})");
        Assert.DoesNotContain(runs, run => run.Staged);

        var texture = harness.Texture(harness.Transfer.ShapeFor(description));
        var whole = new SubresourceRange(0, description.Resources.Levels, 0, description.TransferLayers);
        harness.Begin();
        foreach (var run in runs)
        {
            var source = memory.ResolveWritable(address + run.Offset, run.Size);
            Assert.True(source.IsValid);
            harness.Transfer.EncodeGuestToTexture(
                harness, description, whole, source, default, texture, run.Offset, run.Size);
        }

        harness.Submit();
        var target = harness.Buffer(description.Data.Size);
        harness.Begin();
        harness.Transfer.EncodeTextureToGuest(
            harness, description, whole, texture, new MetalGuestRange(target, 0, description.Data.Size, 0), default,
            0, description.Data.Size);
        harness.Submit();

        AssertSameBytes(guest, harness.Read(target, 0, description.Data.Size), "upload across a window edge");
        Assert.Equal(0UL, memory.RingFallbacks);
        output.WriteLine($"{runs.Count} runs: {string.Join(' ', runs.Select(run => $"0x{run.Offset:x}+0x{run.Size:x}"))}");
    }

    // Plans the upload of an image at an alias offset under the measured seam layout and
    // checks every run against the window its first byte selects.
    private void AssertWindowRunsCoverTheImage(
        MetalImageTransfer transfer, in ImageDescription description, ulong windowOffset, string name)
    {
        // The planner only sees where the resolver's prefix ends; the old 4 GiB layout with a
        // 64 MiB seam is a fixture of prefixes that end mid-image (a non-contiguous backing or
        // the largest-window cap ends prefixes the same way under granule windows).
        const ulong stride = FourGib - 0x4000000UL;
        var count = (int)((SeamAliasSize - FourGib + stride - 1) / stride) + 1;
        // The precondition: the image does not fit the window its first byte selects.
        var (firstBase, firstEnd) = WindowAt(windowOffset, stride, count);
        Assert.True(windowOffset >= firstBase && windowOffset + description.Data.Size > firstEnd,
            $"{name}: the image does not cross a window edge");

        var runs = new List<MetalImageCache.UploadRun>();
        var uncovered = MetalImageCache.PlanUploadRuns(
            transfer, in description, 0, description.Data.Size, MaxUploadRunBytes,
            (offset, size) =>
            {
                // What ResolveWritablePrefix finds: the window the first byte selects,
                // up to its end.
                var alias = windowOffset + offset;
                var (windowBase, windowEnd) = WindowAt(alias, stride, count);
                return alias >= windowBase && alias < windowEnd ? Math.Min(size, windowEnd - alias) : 0;
            },
            runs);

        Assert.Equal(0UL, uncovered);
        AssertContiguousOnCuts(transfer, in description, runs, name);
        var windowsUsed = new HashSet<ulong>();
        foreach (var run in runs)
        {
            Assert.False(run.Staged, $"{name}: run 0x{run.Offset:X}+0x{run.Size:X} was staged");
            Assert.True(run.Size <= MaxUploadRunBytes, $"{name}: run 0x{run.Offset:X}+0x{run.Size:X} is past a run's worth");
            var alias = windowOffset + run.Offset;
            var (windowBase, windowEnd) = WindowAt(alias, stride, count);
            Assert.True(alias + run.Size <= windowEnd,
                $"{name}: run 0x{run.Offset:X}+0x{run.Size:X} at alias 0x{alias:X} crosses the end 0x{windowEnd:X} " +
                $"of the window at 0x{windowBase:X}");
            windowsUsed.Add(windowBase);
        }

        Assert.True(windowsUsed.Count >= 2, $"{name}: every run went through one window");
        output.WriteLine($"{name}: {runs.Count} runs over 0x{description.Data.Size:x} through {windowsUsed.Count} windows");
    }

    // The runs tile [0, size) with no gap and no overlap, and each begins and ends on a cut
    // the transfer plan itself makes: a block row of a tiled surface, a texel row of a
    // linear one.
    private static void AssertContiguousOnCuts(
        MetalImageTransfer transfer, in ImageDescription description, List<MetalImageCache.UploadRun> runs, string name)
    {
        var next = 0UL;
        foreach (var run in runs)
        {
            Assert.True(run.Size != 0, $"{name}: an empty run at 0x{run.Offset:X}");
            Assert.True(run.Offset == next, $"{name}: a gap or overlap at 0x{next:X} (next run at 0x{run.Offset:X})");
            Assert.Equal(run.Offset, transfer.AlignRunStart(in description, run.Offset));
            var end = run.Offset + run.Size;
            Assert.True(end == description.Data.Size || transfer.AlignRunEnd(in description, run.Offset, end) == end,
                $"{name}: run 0x{run.Offset:X}+0x{run.Size:X} ends inside a block row");
            Assert.True(end == description.Data.Size || transfer.AlignRunStart(in description, end) == end,
                $"{name}: run 0x{run.Offset:X}+0x{run.Size:X} ends where no run may begin");
            next = end;
        }

        Assert.Equal(description.Data.Size, next);
    }

    // The window an alias offset selects, as the resolver picks it, and where it ends.
    private static (ulong Base, ulong End) WindowAt(ulong alias, ulong stride, int count)
    {
        var index = Math.Min(alias / stride, (ulong)(count - 1));
        var windowBase = index * stride;
        return (windowBase, windowBase + Math.Min(FourGib, SeamAliasSize - windowBase));
    }
}
