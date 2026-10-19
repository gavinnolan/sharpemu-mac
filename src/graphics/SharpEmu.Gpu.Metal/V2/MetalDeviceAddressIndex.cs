// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// The granule index the device-address resolver reads after the range table. For each
/// granule of guest pages it holds the half-open window [low, high) of table entries that
/// can decide the binary search for a page inside it, so the shader searches a handful of
/// entries instead of the whole table. Layout, in dwords, right after the entries:
/// <c>shift, granuleCount, baseGranuleLow, baseGranuleHigh</c>, then per granule
/// <c>low, high</c>. The table's entry-count word carries <see cref="IndexedFlag"/> when this
/// index is present; keep this layout in step with resource_prelude.msl.
/// </summary>
internal static class MetalDeviceAddressIndex
{
    internal const uint IndexedFlag = 0x8000_0000u;
    internal const int HeaderDwords = 4;
    internal const int MinGranuleShift = 12; // 4096 pages of 16 KiB = 64 MiB
    internal const int MaxGranules = 8192;

    /// <summary>Builds the index for entries sorted by first page, or an empty array when
    /// there are none.</summary>
    internal static uint[] Build(IReadOnlyList<(ulong FirstPage, ulong PageCount, ulong Base)> entries)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var firstPage = entries[0].FirstPage;
        var lastPage = entries[^1].FirstPage;
        var shift = MinGranuleShift;
        while (((lastPage >> shift) - (firstPage >> shift) + 1) > MaxGranules)
        {
            shift++;
        }

        var baseGranule = firstPage >> shift;
        var count = (int)((lastPage >> shift) - baseGranule + 1);
        var words = new uint[HeaderDwords + count * 2];
        words[0] = (uint)shift;
        words[1] = (uint)count;
        words[2] = (uint)baseGranule;
        words[3] = (uint)(baseGranule >> 32);

        // low(g): entries with FirstPage <= start of g. high(g): entries with FirstPage < start of g + 1.
        var lowCursor = 0;
        var highCursor = 0;
        for (var granule = 0; granule < count; granule++)
        {
            var start = (baseGranule + (ulong)granule) << shift;
            var next = start + (1UL << shift);
            while (lowCursor < entries.Count && entries[lowCursor].FirstPage <= start)
            {
                lowCursor++;
            }

            while (highCursor < entries.Count && entries[highCursor].FirstPage < next)
            {
                highCursor++;
            }

            words[HeaderDwords + granule * 2] = (uint)lowCursor;
            words[HeaderDwords + granule * 2 + 1] = (uint)highCursor;
        }

        return words;
    }

    /// <summary>Reference of what the shader does: the upper bound of <paramref name="page"/>
    /// over the entries' first pages, found through the index. Used by tests.</summary>
    internal static int UpperBound(
        IReadOnlyList<(ulong FirstPage, ulong PageCount, ulong Base)> entries, uint[] index, ulong page)
    {
        var low = 0;
        var high = entries.Count;
        if (index.Length != 0)
        {
            var granule = (page >> (int)index[0]) - (((ulong)index[3] << 32) | index[2]);
            if (granule < index[1])
            {
                low = (int)index[HeaderDwords + (int)granule * 2];
                high = (int)index[HeaderDwords + (int)granule * 2 + 1];
            }
        }

        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (entries[middle].FirstPage <= page)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
