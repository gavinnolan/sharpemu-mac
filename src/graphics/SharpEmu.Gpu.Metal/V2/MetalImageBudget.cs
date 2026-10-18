// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Gpu.Metal.V2;

// The kernel's memory-pressure level, as kern.memorystatus_vm_pressure_level and the
// DISPATCH_SOURCE_TYPE_MEMORYPRESSURE source report it (DISPATCH_MEMORYPRESSURE_NORMAL,
// _WARN, _CRITICAL).
internal enum MetalMemoryPressure
{
    Unknown = 0,
    Normal = 1,
    Warn = 2,
    Critical = 4,
}

/// <summary>
/// One reading of host memory. <see cref="AvailableBytes"/> is what the kernel can hand out
/// without compressing or swapping anything: free pages (speculative included) plus pageable
/// file-backed pages, the sum XNU's memorystatus itself compares against its pressure
/// thresholds (vm_page_free_count + vm_page_pageable_external_count; purgeable pages are left
/// out when the compressor is on). <see cref="ProcessFootprintBytes"/> is the process's
/// phys_footprint, the number the kernel charges this process with: guest memory it touched,
/// the image cache's textures, every other Metal allocation and the .NET heap.
/// </summary>
internal readonly record struct MetalHostMemorySample(
    ulong PhysicalBytes,
    ulong AvailableBytes,
    ulong ProcessFootprintBytes,
    MetalMemoryPressure Pressure);

/// <summary>
/// The budget policy for the detiled image copies. On a PS5 the GPU samples a title's tiled
/// bytes in place, so the guest's own memory is the only copy the console has; the detiled
/// MTLTextures are purely an emulator cost on top of it. The title fills its own budget
/// (Demon's Souls streams textures until ~300 MiB of its direct memory is free), so the image
/// cache has to live in whatever the host has left, not in a fixed fraction of RAM:
///
///   resident = physical - (footprint - cache) - headroom     what the host can give the cache once
///                                                           the rest of this process and macOS are in
///   grow     = cache + available - reserve                  how far the cache may grow before the
///                                                           kernel starts compressing (shrinks when
///                                                           available is below the reserve)
///   budget   = clamp(min(resident, grow), floor, ceiling)
///   pressure WARN: budget = min(budget, cache / 2); CRITICAL: budget = floor
///
/// Eviction keeps its working-set guards whatever the budget says (see MetalImageCache), so a
/// budget below the working set runs over rather than thrashing.
/// </summary>
internal sealed record MetalImageBudgetSettings
{
    private const ulong MiB = 1024 * 1024;
    private const ulong GiB = 1024 * MiB;

    /// <summary>SHARPEMU_METAL_V2_IMAGE_BUDGET_MB: a fixed budget, no host readings.</summary>
    public ulong? FixedBudget { get; init; }

    /// <summary>The budget never drops below this: images used within seconds stay.</summary>
    public ulong Floor { get; init; }

    /// <summary>The budget never exceeds this, however much memory is free.</summary>
    public ulong Ceiling { get; init; }

    /// <summary>What macOS and other processes are assumed to need on top of this process.</summary>
    public ulong Headroom { get; init; }

    /// <summary>The available memory the cache leaves alone; below it the cache shrinks.</summary>
    public ulong Reserve { get; init; }

    /// <summary>How often the budget is re-read from the host, in Stopwatch ticks.</summary>
    public long EvaluationInterval { get; init; } = Stopwatch.Frequency;

    /// <summary>
    /// Defaults for a machine of this much physical memory. Floor 1 GiB (a 1080p frame's
    /// render targets plus a few hundred MB of textures). Ceiling a quarter of RAM up to 8 GiB,
    /// the old fixed budget's upper end. Headroom a quarter of RAM, at least 3 GiB, for macOS
    /// (kernel, WindowServer, wired memory) and whatever else runs. Reserve a sixteenth of RAM,
    /// at least 1 GiB: the free plus file-backed pages left before the compressor would start
    /// on anonymous memory (guest memory included).
    /// </summary>
    public static MetalImageBudgetSettings Defaults(ulong physicalBytes)
    {
        var floor = 1 * GiB;
        return new MetalImageBudgetSettings
        {
            Floor = floor,
            Ceiling = Math.Clamp(physicalBytes / 4, floor, 8 * GiB),
            Headroom = Math.Max(physicalBytes / 4, 3 * GiB),
            Reserve = Math.Max(physicalBytes / 16, 1 * GiB),
        };
    }

    /// <summary>The defaults, with SHARPEMU_METAL_V2_IMAGE_BUDGET_MB (at least 256) as a fixed budget.</summary>
    public static MetalImageBudgetSettings FromEnvironment(ulong physicalBytes)
    {
        var settings = Defaults(physicalBytes);
        var configured = Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_IMAGE_BUDGET_MB");
        return ulong.TryParse(configured, out var megabytes) && megabytes >= 256
            ? settings with { FixedBudget = megabytes * MiB }
            : settings;
    }

    /// <summary>The budget for a cache holding <paramref name="cacheBytes"/> of textures, given this host reading.</summary>
    public ulong Compute(in MetalHostMemorySample sample, ulong cacheBytes)
    {
        if (FixedBudget is { } fixedBudget)
        {
            return fixedBudget;
        }

        var ceiling = Math.Max(Ceiling, Floor);
        if (sample.PhysicalBytes == 0)
        {
            // No reading at all: nothing to size against but the ceiling.
            return ceiling;
        }

        var rest = sample.ProcessFootprintBytes > cacheBytes ? sample.ProcessFootprintBytes - cacheBytes : 0;
        var claimed = rest + Headroom;
        var resident = sample.PhysicalBytes > claimed ? sample.PhysicalBytes - claimed : 0;

        ulong grow;
        if (sample.AvailableBytes >= Reserve)
        {
            var spare = sample.AvailableBytes - Reserve;
            grow = cacheBytes > ulong.MaxValue - spare ? ulong.MaxValue : cacheBytes + spare;
        }
        else
        {
            var shortfall = Reserve - sample.AvailableBytes;
            grow = cacheBytes > shortfall ? cacheBytes - shortfall : 0;
        }

        var budget = Math.Min(resident, grow);
        budget = sample.Pressure switch
        {
            MetalMemoryPressure.Critical => 0,
            MetalMemoryPressure.Warn => Math.Min(budget, cacheBytes / 2),
            _ => budget,
        };

        return Math.Clamp(budget, Floor, ceiling);
    }
}

/// <summary>
/// Age-based retirement settings: an image the title has not used for <see cref="Frames"/>
/// presented frames and <see cref="Seconds"/> seconds is released whatever the budget says,
/// so a long session cannot keep the detiled copies of areas, menus and cinematics it has
/// left. Both conditions hold at once: frames alone would retire a whole loading screen's
/// worth of images after a few seconds at a high frame rate, seconds alone would retire the
/// working set of a title that presents once every few seconds. Budget eviction separately
/// requires <see cref="BudgetMinimumIdleSeconds"/> of idleness (except under critical
/// pressure), so an image used every few frames, a shadow cascade or a probe updated
/// round-robin, is not dropped and re-uploaded on each pass.
/// </summary>
internal sealed record MetalImageRetirementSettings
{
    public uint Frames { get; init; } = 600;

    public double Seconds { get; init; } = 20;

    public double BudgetMinimumIdleSeconds { get; init; } = 2;

    public bool Enabled => Frames != 0 && Seconds > 0;

    /// <summary>
    /// SHARPEMU_METAL_V2_IMAGE_RETIRE_FRAMES and SHARPEMU_METAL_V2_IMAGE_RETIRE_SECONDS (either 0
    /// turns retirement off), SHARPEMU_METAL_V2_IMAGE_EVICT_MIN_IDLE_SECONDS.
    /// </summary>
    public static MetalImageRetirementSettings FromEnvironment()
    {
        var settings = new MetalImageRetirementSettings();
        if (uint.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_IMAGE_RETIRE_FRAMES"), out var frames))
        {
            settings = settings with { Frames = frames };
        }

        if (double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_IMAGE_RETIRE_SECONDS"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) &&
            seconds >= 0)
        {
            settings = settings with { Seconds = seconds };
        }

        if (double.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_IMAGE_EVICT_MIN_IDLE_SECONDS"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var idle) &&
            idle >= 0)
        {
            settings = settings with { BudgetMinimumIdleSeconds = idle };
        }

        return settings;
    }
}

/// <summary>
/// Maps "N presented frames ago" and "T seconds ago" onto collection ticks, the clock every
/// cached image is stamped with on use. An image whose last-use tick is below the tick this
/// returns was last used before that point. Frame starts are kept for the last
/// <c>frames</c> frames; time samples are taken at most once per <c>sampleInterval</c> and
/// kept for <c>seconds</c> plus a margin. Both answers err late: an image is never called
/// idle before it is.
/// </summary>
internal sealed class MetalImageIdleClock
{
    private readonly ulong[] _frameStarts;
    private long _frames;
    private readonly (long Timestamp, ulong Tick)[] _samples;
    private int _sampleHead;
    private int _sampleCount;
    private readonly long _sampleInterval;

    public MetalImageIdleClock(uint frames, double seconds, long sampleInterval)
    {
        _frameStarts = new ulong[Math.Max(frames, 1u) + 1];
        _sampleInterval = Math.Max(sampleInterval, 1);
        var span = Math.Max(seconds, 0) * Stopwatch.Frequency;
        var capacity = (int)Math.Clamp(span / _sampleInterval + 4, 8, 1 << 16);
        _samples = new (long, ulong)[capacity];
    }

    public long Frames => _frames;

    /// <summary>A presented frame began at this tick.</summary>
    public void MarkFrame(ulong tick)
    {
        _frameStarts[(int)(_frames % _frameStarts.Length)] = tick;
        _frames++;
    }

    /// <summary>Records that the cache was at this tick at this time; samples closer together than the interval are skipped.</summary>
    public void Sample(long timestamp, ulong tick)
    {
        if (_sampleCount != 0)
        {
            var newest = _samples[(_sampleHead + _sampleCount - 1) % _samples.Length];
            if (timestamp - newest.Timestamp < _sampleInterval)
            {
                return;
            }
        }

        if (_sampleCount == _samples.Length)
        {
            _sampleHead = (_sampleHead + 1) % _samples.Length;
            _sampleCount--;
        }

        _samples[(_sampleHead + _sampleCount) % _samples.Length] = (timestamp, tick);
        _sampleCount++;
    }

    /// <summary>The tick the frame <paramref name="framesAgo"/> frames back began at; zero when fewer frames were seen.</summary>
    public ulong TickFramesAgo(uint framesAgo)
    {
        if (framesAgo >= _frameStarts.Length || (ulong)_frames < (ulong)framesAgo + 1)
        {
            return 0;
        }

        return _frameStarts[(int)((_frames - 1 - framesAgo) % _frameStarts.Length)];
    }

    /// <summary>The newest sampled tick at or before <paramref name="timestamp"/>; zero when no sample is that old.</summary>
    public ulong TickAtOrBefore(long timestamp)
    {
        var found = 0UL;
        for (var index = 0; index < _sampleCount; index++)
        {
            var sample = _samples[(_sampleHead + index) % _samples.Length];
            if (sample.Timestamp > timestamp)
            {
                break;
            }

            found = sample.Tick;
        }

        return found;
    }
}
