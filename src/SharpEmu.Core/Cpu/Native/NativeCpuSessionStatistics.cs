// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu.Native;

internal readonly record struct NativeCpuSessionStatistics(int ImportsHit, int UniqueNidsHit);

internal interface INativeCpuSessionStatisticsProvider
{
    NativeCpuSessionStatistics LastSessionStatistics { get; }
}

/// <summary>
/// Per-session import statistics. Every guest thread crosses into the import gateway
/// hundreds of thousands of times a second, so recording must not touch shared state:
/// each thread owns a block (hit count plus a table of the NIDs it has seen) that only it writes, and
/// the blocks are folded together once, at the end of the session.
/// </summary>
internal sealed class NativeImportSessionCounters
{
    private sealed class ThreadBlock
    {
        public NativeImportSessionCounters? Owner;
        public long Hits;
        public string?[] Nids = new string?[256];
    }

    [ThreadStatic]
    private static ThreadBlock? _threadBlock;

    private readonly object _blocksLock = new();
    private readonly List<ThreadBlock> _blocks = new();

    public void Record(int importIndex, string nid)
    {
        var block = _threadBlock;
        if (block is null || !ReferenceEquals(block.Owner, this))
        {
            block = new ThreadBlock { Owner = this };
            lock (_blocksLock)
            {
                _blocks.Add(block);
            }

            _threadBlock = block;
        }

        block.Hits++;
        var nids = block.Nids;
        if ((uint)importIndex >= (uint)nids.Length)
        {
            Array.Resize(ref nids, Math.Max(importIndex + 1, nids.Length * 2));
            block.Nids = nids;
        }

        // Compare first: the steady state is a read of an already-published reference,
        // which skips the GC write barrier and keeps the cache line clean.
        if (!ReferenceEquals(nids[importIndex], nid))
        {
            nids[importIndex] = nid;
        }
    }

    /// <summary>
    /// Folds the per-thread blocks. Call once the session's threads have stopped; a
    /// racing thread can only make the figures lag by a few calls.
    /// </summary>
    public NativeCpuSessionStatistics Snapshot()
    {
        long hits = 0;
        var unique = new HashSet<string>(StringComparer.Ordinal);
        lock (_blocksLock)
        {
            foreach (var block in _blocks)
            {
                hits += Volatile.Read(ref block.Hits);
                foreach (var nid in block.Nids)
                {
                    if (nid is not null)
                    {
                        unique.Add(nid);
                    }
                }
            }
        }

        return new((int)Math.Min(hits, int.MaxValue), unique.Count);
    }
}
