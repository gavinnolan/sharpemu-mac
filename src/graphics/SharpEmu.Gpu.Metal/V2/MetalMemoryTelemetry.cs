// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.Logging;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// SHARPEMU_LOG_MEMORY=1: the Metal side of the "[MEM]" lines. Counters of command buffers
/// created and committed and of encoders created (to set against malloc growth: every one
/// leaves ObjC objects behind when nothing drains an autorelease pool), and the guest windows
/// each command buffer named. Naming a window with useResource makes the whole window resident
/// (wired and faulted in), so the bytes named per command buffer are what the GPU path costs.
/// The windows do not overlap (MetalWindowPartition), so the bytes are the sum of the windows
/// named; windows_* counts windows named, distinct_granules/distinct_granule_mb the granules
/// the named windows overlapped over the interval, window_mb what the window buffers alive map
/// (named or not), windows_created/released the churn of buffers over the interval,
/// window_moves the cuts moved for a range that crossed one, window_conflicts the ranges that
/// had no window for one command buffer because their cut could not move inside it, and
/// windows_cut the windows the backing is cut into now.
/// </summary>
internal static class MetalMemoryTelemetry
{
    public static readonly MemoryCounter CommandBuffers = MemoryTelemetry.Counter("metal.command_buffers");
    public static readonly MemoryCounter Commits = MemoryTelemetry.Counter("metal.commits");
    public static readonly MemoryCounter RenderEncoders = MemoryTelemetry.Counter("metal.encoders_render");
    public static readonly MemoryCounter ComputeEncoders = MemoryTelemetry.Counter("metal.encoders_compute");
    public static readonly MemoryCounter BlitEncoders = MemoryTelemetry.Counter("metal.encoders_blit");

    private static readonly object Gate = new();
    private static long _buffers;
    private static ulong _sumBytes;
    private static ulong _maxBytes;
    private static ulong _sumDeviceAddressOnlyBytes;
    private static ulong _maxDeviceAddressOnlyBytes;
    private static long _sumWindows;
    private static int _maxWindows;
    private static long _buffersWithNone;
    private static long _createdBefore;
    private static long _releasedBefore;
    private static long _movesBefore;
    private static long _conflictsBefore;
    private static readonly MetalWindowSet Distinct = new();
    private static readonly MetalWindowSet Named = new();
    private static readonly MetalWindowSet ReachOnly = new();
    private static readonly Dictionary<int, ulong> NamedBytesByGib = new();

    /// <summary>
    /// One command buffer is being committed: the windows its bindings resolved in are the
    /// guest memory's touched set, <paramref name="deviceAddressNamed"/> the windows a
    /// device-address encoder named for reach.
    /// </summary>
    public static void NoteCommit(MetalGuestMemory memory, MetalWindowSet deviceAddressNamed)
    {
        if (!MemoryTelemetry.Enabled)
        {
            return;
        }

        lock (Gate)
        {
            Named.CopyFrom(memory.TouchedWindows);
            Named.UnionWith(deviceAddressNamed);
            ReachOnly.SetDifference(deviceAddressNamed, memory.TouchedWindows);
            var bytes = memory.WindowSetBytes(Named);
            var reachOnlyBytes = memory.WindowSetBytes(ReachOnly);
            var count = Named.Count;
            _buffers++;
            _sumBytes += bytes;
            _maxBytes = Math.Max(_maxBytes, bytes);
            _sumDeviceAddressOnlyBytes += reachOnlyBytes;
            _maxDeviceAddressOnlyBytes = Math.Max(_maxDeviceAddressOnlyBytes, reachOnlyBytes);
            _sumWindows += count;
            _maxWindows = Math.Max(_maxWindows, count);
            if (count == 0)
            {
                _buffersWithNone++;
            }

            memory.AddCoveredGranules(Named, Distinct);
            memory.ForEachWindow(Named, static (offset, size) =>
            {
                var gib = (int)(offset >> 30);
                NamedBytesByGib[gib] = NamedBytesByGib.GetValueOrDefault(gib) + size;
            });
        }
    }

    /// <summary>The windows named per command buffer over the interval, and resets it.</summary>
    public static string TakeWindowReport(MetalGuestMemory memory)
    {
        const double Mb = 1048576.0;
        var culture = CultureInfo.InvariantCulture;
        var (alive, created, released, mappedBytes) = memory.WindowCounts;
        var (moves, conflicts, cut) = memory.WindowLayoutCounts;
        lock (Gate)
        {
            var buffers = (ulong)Math.Max(_buffers, 1L);
            // named_mb_by_gib: per 1 GiB of the backing, the MiB of it named per command buffer on average.
            var perGib = string.Join(',', NamedBytesByGib.OrderBy(pair => pair.Key)
                .Select(pair => string.Create(culture, $"{pair.Key}:{pair.Value / buffers / Mb:F0}")));
            var text = string.Create(culture,
                $"command_buffers={_buffers} none_named={_buffersWithNone} " +
                $"named_mb_avg={_sumBytes / buffers / Mb:F0} named_mb_max={_maxBytes / Mb:F0} " +
                $"windows_avg={_sumWindows / (double)Math.Max(_buffers, 1L):F1} windows_max={_maxWindows} " +
                $"distinct_granules={Distinct.Count} distinct_granule_mb={memory.GranuleSetBytes(Distinct) / Mb:F0} " +
                $"reach_only_mb_avg={_sumDeviceAddressOnlyBytes / buffers / Mb:F0} reach_only_mb_max={_maxDeviceAddressOnlyBytes / Mb:F0} " +
                $"granule_mb={memory.GranuleBytes / Mb:F0} alias_mb={memory.AliasBytes / Mb:F0} " +
                $"windows_alive={alive} windows_created={created - _createdBefore} windows_released={released - _releasedBefore} " +
                $"window_mb={mappedBytes / Mb:F0} window_moves={moves - _movesBefore} " +
                $"window_conflicts={conflicts - _conflictsBefore} windows_cut={cut} named_mb_by_gib={{{perGib}}}");
            _buffers = 0;
            _sumBytes = _maxBytes = _sumDeviceAddressOnlyBytes = _maxDeviceAddressOnlyBytes = 0;
            _sumWindows = 0;
            _maxWindows = 0;
            _buffersWithNone = 0;
            _createdBefore = created;
            _releasedBefore = released;
            _movesBefore = moves;
            _conflictsBefore = conflicts;
            Distinct.Clear();
            NamedBytesByGib.Clear();
            return text;
        }
    }
}
