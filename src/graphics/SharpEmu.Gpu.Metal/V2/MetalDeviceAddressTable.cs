// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>Where the device-address table finds the window bytes of a guest run.</summary>
internal interface IMetalGranuleRuns
{
    /// <summary>
    /// The longest run from <paramref name="gpuVa"/>, in whole pages and at most
    /// <paramref name="size"/>, that is contiguous in the backing and inside one granule,
    /// with the backing offset of its first byte; false when its first page is not backed.
    /// </summary>
    bool TryResolveGranuleRun(ulong gpuVa, ulong size, ulong page, out ulong backingOffset, out ulong length, out int granule);

    /// <summary>Changes whenever the windows are cut differently.</summary>
    long PartitionVersion { get; }

    /// <summary>
    /// The window holding a backing offset: its id, the offset it ends at, and the GPU
    /// address of the offset in it.
    /// </summary>
    (int Id, ulong End, ulong GpuAddress) WindowAt(ulong backingOffset);
}

/// <summary>
/// The sorted range table a device-address program binary-searches
/// (sharpemu_resolve_device_address in resource_prelude.msl): one entry per run of a mapped
/// guest span that is contiguous in the backing and lies in one granule and one window,
/// giving the GPU address its first page lives at. The shader computes base + (page -
/// firstPage) * pageSize + pageOffset, which is exact because the run is contiguous in the
/// backing as well as in guest space.
///
/// <para>
/// The mapped spans are resolved into backing runs cut at granule edges when the spans
/// change (<see cref="Rebuild"/>), and those are cut again at window edges into the entries
/// whenever the windows are cut differently (<see cref="Recut"/>), which needs no lookup of
/// the guest mappings. An entry in one window is named by naming that window, and an entry
/// in one granule is flagged and learned by the reach set (MetalRenderHost.DeviceAddressReach.cs)
/// one granule at a time. The cuts cost one entry per granule or window edge a run crosses:
/// Demon's Souls in-world maps ~11.2 GiB in ~1750 spans, which made ~2550 entries over 1 GiB
/// windows and makes ~2900 over 32 MiB granules (one more per 32 MiB of span; a window edge
/// moved off a granule edge adds at most one more), against the 16384 the shader's search
/// covers. The per-invocation range cache still hits for every access that stays inside one
/// run, which a granule of 32 MiB leaves almost all of.
/// </para>
///
/// <para>
/// An entry's page count carries the reach flag in its upper half: bit 63 marks a run in a
/// window the submission did not name and bits 32..62 its granule, so the shader can report
/// it; bits 0..31 are the page count (a run is at most one granule).
/// </para>
/// </summary>
internal sealed class MetalDeviceAddressTable
{
    internal const int EntryBytes = 24;
    internal const ulong UnnamedFlag = 1UL << 63;
    internal const int GranuleShift = 32;
    internal const ulong PageCountMask = 0xFFFF_FFFFUL;
    internal const ulong MaxGranule = 0x7FFF_FFFFUL;

    private readonly List<(ulong FirstPage, ulong PageCount, ulong Offset, int Granule)> _runs = new();
    private readonly List<(ulong FirstPage, ulong PageCount, ulong Base)> _entries = new();
    private int[] _entryGranules = [];
    private int[] _entryWindows = [];
    private uint[] _index = [];
    private int _maxEntries = (int)Gen5MslTranslator.MaxAddressRangeCount;

    /// <summary>The entries in table order.</summary>
    public IReadOnlyList<(ulong FirstPage, ulong PageCount, ulong Base)> Entries => _entries;

    /// <summary>The granule of each entry, in table order.</summary>
    public ReadOnlySpan<int> EntryGranules => _entryGranules.AsSpan(0, _entries.Count);

    /// <summary>The window of each entry, in table order.</summary>
    public ReadOnlySpan<int> EntryWindows => _entryWindows.AsSpan(0, _entries.Count);

    /// <summary>Every granule some entry lies in.</summary>
    public MetalWindowSet Granules { get; } = new();

    /// <summary>Every window some entry lies in.</summary>
    public MetalWindowSet Windows { get; } = new();

    /// <summary>The window cut the entries were made for.</summary>
    public long PartitionVersion { get; private set; } = long.MinValue;

    public int SpanCount { get; private set; }

    /// <summary>Bytes the entries cover.</summary>
    public ulong SpanBytes { get; private set; }

    public int Count => _entries.Count;

    /// <summary>Bytes <see cref="Write"/> needs: the entries, then the granule index.</summary>
    public int ByteSize => Math.Max((_entries.Count * EntryBytes) + (_index.Length * sizeof(uint)), EntryBytes);

    /// <summary>
    /// Rebuilds the entries from the mapped guest spans. Pages with no window are left out,
    /// never copied, because a shader store through a copied page would not reach guest
    /// memory.
    /// </summary>
    public void Rebuild(IMetalGranuleRuns memory, Action<Action<ulong, ulong>> forEachSpan, int maxEntries)
    {
        _runs.Clear();
        Granules.Clear();
        SpanBytes = 0;
        _maxEntries = maxEntries;
        var spanCount = 0;
        forEachSpan((address, size) =>
        {
            spanCount++;
            var page = DeviceAddressPaging.PageSize;
            var start = (address + page - 1) & ~(page - 1);
            var end = (address + size) & ~(page - 1);
            while (start < end)
            {
                if (!memory.TryResolveGranuleRun(start, end - start, page, out var offset, out var length, out var granule))
                {
                    start += page;
                    continue;
                }

                if (_runs.Count >= maxEntries)
                {
                    throw Full(maxEntries, spanCount, start >> DeviceAddressPaging.PageBits);
                }

                _runs.Add((start >> DeviceAddressPaging.PageBits, length >> DeviceAddressPaging.PageBits, offset, granule));
                Granules.Add(granule);
                SpanBytes += length;
                start += length;
            }
        });

        SpanCount = spanCount;
        _runs.Sort(static (left, right) => left.FirstPage.CompareTo(right.FirstPage));
        Recut(memory);
    }

    /// <summary>
    /// Cuts the runs into the entries at the window edges as they are now, with the GPU
    /// address of each in its window.
    /// </summary>
    public void Recut(IMetalGranuleRuns memory)
    {
        _entries.Clear();
        Windows.Clear();
        PartitionVersion = memory.PartitionVersion;
        var pageBits = DeviceAddressPaging.PageBits;
        foreach (var (firstPage, pageCount, offset, granule) in _runs)
        {
            var page = firstPage;
            var left = pageCount;
            var at = offset;
            while (left != 0)
            {
                var (window, end, gpuAddress) = memory.WindowAt(at);
                // Window edges are page-aligned; a run that is not keeps its pages whole.
                var pages = Math.Max(1UL, Math.Min(left, (end - at) >> pageBits));
                if (_entries.Count >= _maxEntries)
                {
                    throw Full(_maxEntries, SpanCount, page);
                }

                if (_entryGranules.Length <= _entries.Count)
                {
                    var length = Math.Max(_entries.Count + 1, _entryGranules.Length * 2);
                    Array.Resize(ref _entryGranules, length);
                    Array.Resize(ref _entryWindows, length);
                }

                _entryGranules[_entries.Count] = granule;
                _entryWindows[_entries.Count] = window;
                _entries.Add((page, pages, gpuAddress));
                Windows.Add(window);
                page += pages;
                at += pages << pageBits;
                left -= pages;
            }
        }

        _index = MetalDeviceAddressIndex.Build(_entries);
    }

    /// <summary>Adds the windows of the entries that lie in <paramref name="granules"/> to <paramref name="windows"/>.</summary>
    public void AddWindowsOfGranules(MetalWindowSet granules, MetalWindowSet windows)
    {
        for (var index = 0; index < _entries.Count; index++)
        {
            if (granules.Contains(_entryGranules[index]))
            {
                windows.Add(_entryWindows[index]);
            }
        }
    }

    /// <summary>
    /// Writes the table and its granule index. An entry in a window outside
    /// <paramref name="namedWindows"/> is flagged with its granule; null flags none.
    /// </summary>
    public void Write(Span<byte> span, MetalWindowSet? namedWindows)
    {
        span.Clear();
        for (var index = 0; index < _entries.Count; index++)
        {
            var (firstPage, pageCount, address) = _entries[index];
            var entry = span[(index * EntryBytes)..];
            var named = namedWindows is null || namedWindows.Contains(_entryWindows[index]);
            BinaryPrimitives.WriteUInt64LittleEndian(entry, firstPage);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], named ? pageCount : Flagged(pageCount, _entryGranules[index]));
            BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], address);
        }

        System.Runtime.InteropServices.MemoryMarshal.AsBytes(_index.AsSpan()).CopyTo(span[(_entries.Count * EntryBytes)..]);
    }

    /// <summary>The page count an entry carries: flagged with its granule when that is not named.</summary>
    internal static ulong FlaggedPageCount(ulong pageCount, int granule, MetalWindowSet? named) =>
        named is null || granule < 0 || named.Contains(granule) ? pageCount : Flagged(pageCount, granule);

    private static ulong Flagged(ulong pageCount, int granule) =>
        (pageCount & PageCountMask) | UnnamedFlag | (((ulong)granule & MaxGranule) << GranuleShift);

    /// <summary>
    /// Adds one run to a table. A span the table has no room for resolves to null in every
    /// shader, so its loads read zero and its stores are dropped without a word; a full
    /// table therefore ends the run instead of truncating.
    /// </summary>
    internal static void AddEntry(
        List<(ulong FirstPage, ulong PageCount, ulong Base)> entries,
        (ulong FirstPage, ulong PageCount, ulong Base) entry,
        int spanCount,
        int maxEntries = (int)Gen5MslTranslator.MaxAddressRangeCount)
    {
        if (entries.Count >= maxEntries)
        {
            throw Full(maxEntries, spanCount, entry.FirstPage);
        }

        entries.Add(entry);
    }

    private static Exception Full(int maxEntries, int spanCount, ulong firstPage) =>
        GpuFatal.Fatal(
            $"The device-address table is full and later guest spans are not addressable: " +
            $"limit={maxEntries} spans_so_far={spanCount} page=0x{firstPage:X}.");
}
