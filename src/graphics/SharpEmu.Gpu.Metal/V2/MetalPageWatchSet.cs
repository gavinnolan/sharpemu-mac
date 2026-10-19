// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;

namespace SharpEmu.Gpu.Metal.V2;

// Ranges that may overlap and repeat, holding one page-guard write watch per page between
// them: a page is watched while any range covers it, and the page guard counts it once. The
// guard keeps 7 bits of count per page, so a holder taking one watch per range runs out when
// the ranges pile up on a page. Callers serialize.
internal sealed class MetalPageWatchSet(PageGuard pages)
{
    private const ulong PageBytes = TrackerLayout.PageBytes;
    private const ulong BlockBytes = TrackerLayout.BlockBytes;

    private sealed class Block
    {
        public readonly int[] Ranges = new int[TrackerLayout.PagesPerBlock];
        public int WatchedPages;
    }

    private readonly Dictionary<ulong, Block> _blocks = [];

    /// <summary>Pages watched for at least one range.</summary>
    public long WatchedPages { get; private set; }

    public void Add(ulong address, ulong size) => Update(address, size, add: true);

    // Takes no allocation: a guest store's fault path removes ranges.
    public void Remove(ulong address, ulong size) => Update(address, size, add: false);

    /// <summary>Drops every range and its watches.</summary>
    public void Clear()
    {
        foreach (var (index, block) in _blocks)
        {
            var blockBase = index * BlockBytes;
            var runStart = 0UL;
            var runEnd = 0UL;
            for (var page = 0; page < block.Ranges.Length; page++)
            {
                if (block.Ranges[page] == 0)
                {
                    continue;
                }

                block.Ranges[page] = 0;
                var address = blockBase + (ulong)page * PageBytes;
                if (address != runEnd)
                {
                    if (runEnd != runStart) pages.RemoveWatch(runStart, runEnd - runStart, blockReads: false);
                    runStart = address;
                }

                runEnd = address + PageBytes;
            }

            if (runEnd != runStart) pages.RemoveWatch(runStart, runEnd - runStart, blockReads: false);
        }

        _blocks.Clear();
        WatchedPages = 0;
    }

    private void Update(ulong address, ulong size, bool add)
    {
        if (size == 0)
        {
            return;
        }

        var begin = address & ~(PageBytes - 1);
        var end = ((address + size - 1) & ~(PageBytes - 1)) + PageBytes;
        // The pages whose watch this changes, as one run handed to the guard at a time.
        var runStart = 0UL;
        var runEnd = 0UL;
        for (var chunk = begin; chunk < end;)
        {
            var index = chunk / BlockBytes;
            var chunkEnd = Math.Min(end, (index + 1) * BlockBytes);
            if (!_blocks.TryGetValue(index, out var block))
            {
                if (!add)
                {
                    throw new InvalidOperationException($"No watched range covers 0x{chunk:X16}.");
                }

                block = new Block();
                _blocks.Add(index, block);
            }

            for (var page = chunk; page < chunkEnd; page += PageBytes)
            {
                ref var ranges = ref block.Ranges[(int)(page % BlockBytes / PageBytes)];
                bool edge;
                if (add)
                {
                    edge = ranges++ == 0;
                }
                else
                {
                    if (ranges == 0)
                    {
                        throw new InvalidOperationException($"No watched range covers 0x{page:X16}.");
                    }

                    edge = --ranges == 0;
                }

                if (!edge)
                {
                    continue;
                }

                block.WatchedPages += add ? 1 : -1;
                WatchedPages += add ? 1 : -1;
                if (page != runEnd)
                {
                    Apply(runStart, runEnd, add);
                    runStart = page;
                }

                runEnd = page + PageBytes;
            }

            if (block.WatchedPages == 0)
            {
                _blocks.Remove(index);
            }

            chunk = chunkEnd;
        }

        Apply(runStart, runEnd, add);
    }

    private void Apply(ulong begin, ulong end, bool add)
    {
        if (end == begin)
        {
            return;
        }

        if (add)
        {
            pages.AddWatch(begin, end - begin, blockReads: false);
        }
        else
        {
            pages.RemoveWatch(begin, end - begin, blockReads: false);
        }
    }
}
