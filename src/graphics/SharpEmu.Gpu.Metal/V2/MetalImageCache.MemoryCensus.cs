// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SharpEmu.Gpu.Metal.V2;

internal sealed partial class MetalImageCache
{
    private static readonly nint SelAllocatedSize = MetalNative.Selector("allocatedSize");
    private static readonly string[] CensusKinds = ["depth", "color_target", "storage", "texture", "window_view"];
    private static readonly string[] CensusAges = ["<1s", "1-10s", "10-60s", ">60s"];
    private long _evictedAccountedBytes;
    private long _evictedAllocatedBytes;
    private long _releasedAccountedBytes;
    private long _releasedImages;
    private (long Evictions, long EvictedAccounted, long EvictedAllocated, long Released, long ReleasedBytes) _censusReported;
    private (long Images, long Bytes) _censusRetired;
    private long _censusRefaults;

    // Notes an image about to be dropped. Only runs with SHARPEMU_LOG_MEMORY=1.
    private void NoteReleasedForTelemetry(MetalCachedImage image, MetalImageReleaseCause cause)
    {
        if (!SharpEmu.Logging.MemoryTelemetry.Enabled)
        {
            return;
        }

        Interlocked.Increment(ref _releasedImages);
        Interlocked.Add(ref _releasedAccountedBytes, (long)image.AccountedSize);
        if (cause == MetalImageReleaseCause.Budget)
        {
            Interlocked.Add(ref _evictedAccountedBytes, (long)image.AccountedSize);
            Interlocked.Add(ref _evictedAllocatedBytes, (long)image.AllocatedBytes);
        }
    }

    // MTLTexture.allocatedSize, asked once per texture: a window view holds no memory of its own.
    private static ulong TextureAllocatedBytes(MetalCachedImage image) =>
        image.Texture == 0 || image.IsWindowLinearView ? 0 : MetalV2Native.SendValue64(image.Texture, SelAllocatedSize);

    private static int CensusKind(MetalCachedImage image)
    {
        if (image.IsWindowLinearView)
        {
            return 4;
        }

        var uses = image.Uses;
        return uses.DepthTarget ? 0 : uses.RenderTarget || uses.VideoOut ? 1 : uses.Storage ? 2 : 3;
    }

    private static int CensusAge(long idleTicks)
    {
        var seconds = (double)idleTicks / Stopwatch.Frequency;
        return seconds < 1 ? 0 : seconds < 10 ? 1 : seconds < 60 ? 2 : 3;
    }

    /// <summary>
    /// The image cache as the device holds it: images and real MTLTexture.allocatedSize by role and by
    /// seconds since last use, next to the guest bytes the cache accounts, plus eviction and release
    /// counts and bytes since the last report. Holds the cache lock for one pass over the images.
    /// </summary>
    public string DescribeMemoryCensus()
    {
        const double Mb = 1048576.0;
        var culture = CultureInfo.InvariantCulture;
        var count = new long[CensusKinds.Length, CensusAges.Length];
        var allocated = new ulong[CensusKinds.Length, CensusAges.Length];
        var accounted = 0UL;
        var now = Stopwatch.GetTimestamp();
        int images;
        MetalHostMemorySample host;
        ulong counted;
        long evaluations;
        using (var held = _lock.Hold())
        {
            images = _slots.Count;
            host = _hostSample;
            counted = _totalUsedMemory;
            evaluations = _budgetEvaluations;
            _slots.ForEach((_, image) =>
            {
                var kind = CensusKind(image);
                var age = CensusAge(now - image.LastAccessTimestamp);
                count[kind, age]++;
                allocated[kind, age] += image.AllocatedBytes;
                accounted += image.AccountedSize;
            });
        }

        var byKind = new StringBuilder();
        var byAge = new StringBuilder();
        var cells = new StringBuilder();
        var totalAllocated = 0UL;
        for (var kind = 0; kind < CensusKinds.Length; kind++)
        {
            long kindCount = 0;
            ulong kindBytes = 0;
            for (var age = 0; age < CensusAges.Length; age++)
            {
                kindCount += count[kind, age];
                kindBytes += allocated[kind, age];
                if (count[kind, age] != 0)
                {
                    cells.Append(' ').Append(CensusKinds[kind]).Append('[').Append(CensusAges[age]).Append("]=")
                        .Append(count[kind, age]).Append('/').Append((allocated[kind, age] / Mb).ToString("F0", culture));
                }
            }

            totalAllocated += kindBytes;
            byKind.Append(' ').Append(CensusKinds[kind]).Append('=').Append(kindCount).Append('/')
                .Append((kindBytes / Mb).ToString("F0", culture));
        }

        for (var age = 0; age < CensusAges.Length; age++)
        {
            long ageCount = 0;
            ulong ageBytes = 0;
            for (var kind = 0; kind < CensusKinds.Length; kind++)
            {
                ageCount += count[kind, age];
                ageBytes += allocated[kind, age];
            }

            byAge.Append(' ').Append(CensusAges[age]).Append('=').Append(ageCount).Append('/')
                .Append((ageBytes / Mb).ToString("F0", culture));
        }

        var current = (Volatile.Read(ref _evictions), Volatile.Read(ref _evictedAccountedBytes),
            Volatile.Read(ref _evictedAllocatedBytes), Volatile.Read(ref _releasedImages),
            Volatile.Read(ref _releasedAccountedBytes));
        var last = _censusReported;
        _censusReported = current;
        var retired = (Images: Volatile.Read(ref _retiredImages), Bytes: Volatile.Read(ref _retiredBytes));
        var lastRetired = _censusRetired;
        _censusRetired = retired;
        var refaults = Volatile.Read(ref _refaults);
        var lastRefaults = _censusRefaults;
        _censusRefaults = refaults;
        var policy = _budgetSettings.FixedBudget is null ? "host" : "fixed";
        return string.Create(culture,
            $"images={images} allocated_mb={totalAllocated / Mb:F0} accounted_mb={accounted / Mb:F0} " +
            $"counted_mb={counted / Mb:F0} budget_mb={MemoryBudgetBytes / Mb:F0} budget_policy={policy} " +
            $"budget_evaluations={evaluations} pressure={PressureName(host.Pressure)} " +
            $"pressure_events={MetalHostMemory.PressureEvents} host_available_mb={host.AvailableBytes / Mb:F0} " +
            $"process_footprint_mb={host.ProcessFootprintBytes / Mb:F0} " +
            $"retired={retired.Images}(+{retired.Images - lastRetired.Images}) " +
            $"retired_mb={retired.Bytes / Mb:F0}(+{(retired.Bytes - lastRetired.Bytes) / Mb:F0}) " +
            $"refaults={refaults}(+{refaults - lastRefaults}) " +
            $"(count/allocated_mb) by_kind:{byKind} by_idle:{byAge} cells:{cells} " +
            $"evictions={current.Item1}(+{current.Item1 - last.Evictions}) " +
            $"evicted_accounted_mb={current.Item2 / Mb:F0}(+{(current.Item2 - last.EvictedAccounted) / Mb:F0}) " +
            $"evicted_allocated_mb={current.Item3 / Mb:F0}(+{(current.Item3 - last.EvictedAllocated) / Mb:F0}) " +
            $"released={current.Item4}(+{current.Item4 - last.Released}) " +
            $"released_accounted_mb={current.Item5 / Mb:F0}(+{(current.Item5 - last.ReleasedBytes) / Mb:F0})");
    }
}
