// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// The bytes of command-processor writes the GPU has been told to make but has not made
/// yet, by guest address, so that what runs at parse time reads them as written.
/// </summary>
/// <remarks>
/// The hardware's command processor sees its own writes at once; on this host they land
/// when the GPU reaches them, in order with the labels around them. Until then a read
/// that overlaps one takes its bytes from here instead of waiting for the GPU or reading
/// what memory held before. An entry stops answering for bytes any later write covers -
/// a shader store, a fill, a copy, a label, a host write - because from then on program
/// order says those bytes are someone else's; and it is dropped once its submission has
/// completed, when memory itself holds it (and the guest may since have written over it).
/// </remarks>
internal sealed class MetalPendingWriteOverlay
{
    private const int PageShift = 12;

    // An entry is published under the lock; reads may come from a prefetch thread.
    private readonly object _gate = new();
    private readonly Dictionary<ulong, List<Entry>> _pages = new();
    private readonly Queue<Entry> _order = new();
    private ulong _sequence;
    private int _count;
    private long _version;

    // Every read the command stream and the resource walks make asks this overlay first (each
    // packet header, every word a cached materialization validates), and almost none of them
    // lands on a page a pending write is on. A count per hashed page of the entries listed on it
    // answers that without the lock: a range none of whose pages counts anything overlaps no
    // entry. The counts change under the lock with the page lists; a read that races an add
    // sees the overlay either before or after it, as it would by taking the lock first or second.
    // A collision only sends the read on to the locked walk.
    private const int FilterBits = 14;
    private const ulong FilterMaxPages = 64;
    private readonly int[]? _pageFilter;

    /// <param name="pageFilter">False takes the lock for every read, as before the page filter.</param>
    public MetalPendingWriteOverlay(bool pageFilter = true)
    {
        _pageFilter = pageFilter ? new int[1 << FilterBits] : null;
    }

    private sealed class Entry
    {
        public required ulong Address;
        public required byte[] Bytes;
        public required ulong Serial;
        public required ulong Sequence;
        public bool[]? Dead;
        public bool Removed;

        public ulong End => Address + (ulong)Bytes.Length;
    }

    /// <summary>Entries not yet dropped. Zero is the fast path every read checks first.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Changes on every add, forget and clear (a forget stands for a newer write over the
    /// range); retiring landed entries leaves it alone.
    /// </summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>Records the bytes a write recorded into <paramref name="serial"/> will land.</summary>
    public void Add(ulong address, ReadOnlySpan<byte> bytes, ulong serial)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            Interlocked.Increment(ref _version);
            var entry = new Entry { Address = address, Bytes = bytes.ToArray(), Serial = serial, Sequence = ++_sequence };
            for (var page = address >> PageShift; page <= (entry.End - 1) >> PageShift; page++)
            {
                if (!_pages.TryGetValue(page, out var list))
                {
                    _pages[page] = list = new List<Entry>(2);
                }

                list.Add(entry);
                if (_pageFilter is not null)
                {
                    Interlocked.Increment(ref _pageFilter[FilterSlot(page)]);
                }
            }

            _order.Enqueue(entry);
            _count++;
        }
    }

    /// <summary>Drops every entry whose submission has completed.</summary>
    public void Retire(ulong completedSerial)
    {
        lock (_gate)
        {
            while (_order.Count != 0 && (_order.Peek().Serial <= completedSerial || _order.Peek().Removed))
            {
                RemoveLocked(_order.Dequeue());
            }
        }
    }

    /// <summary>A later write covers this range: no pending entry speaks for it any more.</summary>
    public void Forget(ulong address, ulong size)
    {
        Interlocked.Increment(ref _version);
        if (size == 0 || Count == 0 || !MayOverlap(address, size))
        {
            return;
        }

        lock (_gate)
        {
            ForEachOverlapping(address, size, entry =>
            {
                var start = Math.Max(address, entry.Address);
                var end = Math.Min(address + size, entry.End);
                if (start <= entry.Address && end >= entry.End)
                {
                    RemoveLocked(entry);
                    return;
                }

                entry.Dead ??= new bool[entry.Bytes.Length];
                entry.Dead.AsSpan((int)(start - entry.Address), (int)(end - start)).Fill(true);
            });
        }
    }

    /// <summary>Drops everything: some write of unknown extent was recorded.</summary>
    public void Clear()
    {
        Interlocked.Increment(ref _version);
        lock (_gate)
        {
            while (_order.Count != 0)
            {
                RemoveLocked(_order.Dequeue());
            }
        }
    }

    /// <summary>
    /// How much of a range pending writes cover, after dropping those whose submission has
    /// completed. <paramref name="completedSerial"/> is asked for only when the range
    /// overlaps an entry at all, since asking the GPU is a message send.
    /// </summary>
    public MetalOverlayCoverage Coverage(ulong address, ulong size, Func<ulong> completedSerial)
    {
        if (size == 0 || Count == 0 || !MayOverlap(address, size))
        {
            return MetalOverlayCoverage.None;
        }

        lock (_gate)
        {
            if (!AnyOverlapLocked(address, size))
            {
                return MetalOverlayCoverage.None;
            }

            RetireOverlappingLocked(address, size, completedSerial());
            if (size > 64 * 1024)
            {
                return AnyOverlapLocked(address, size) ? MetalOverlayCoverage.Partial : MetalOverlayCoverage.None;
            }

            var covered = new bool[size];
            var any = false;
            ForEachOverlapping(address, size, entry =>
            {
                var start = Math.Max(address, entry.Address);
                var end = Math.Min(address + size, entry.End);
                for (var at = start; at < end; at++)
                {
                    if (entry.Dead is null || !entry.Dead[at - entry.Address])
                    {
                        covered[at - address] = true;
                        any = true;
                    }
                }
            });

            if (!any)
            {
                return MetalOverlayCoverage.None;
            }

            return Array.IndexOf(covered, false) < 0 ? MetalOverlayCoverage.Full : MetalOverlayCoverage.Partial;
        }
    }

    /// <summary>Writes the pending bytes over <paramref name="destination"/>, oldest first.</summary>
    public void Apply(ulong address, Span<byte> destination)
    {
        if (destination.IsEmpty || Count == 0 || !MayOverlap(address, (ulong)destination.Length))
        {
            return;
        }

        lock (_gate)
        {
            var size = (ulong)destination.Length;
            List<Entry>? entries = null;
            ForEachOverlapping(address, size, entry => (entries ??= new List<Entry>(2)).Add(entry));
            if (entries is null)
            {
                return;
            }

            entries.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
            foreach (var entry in entries)
            {
                var start = Math.Max(address, entry.Address);
                var end = Math.Min(address + size, entry.End);
                for (var at = start; at < end; at++)
                {
                    if (entry.Dead is null || !entry.Dead[at - entry.Address])
                    {
                        destination[(int)(at - address)] = entry.Bytes[at - entry.Address];
                    }
                }
            }
        }
    }

    // False only when no entry overlaps the range; true may be a collision or a long range.
    private bool MayOverlap(ulong address, ulong size)
    {
        if (_pageFilter is null)
        {
            return true;
        }

        var firstPage = address >> PageShift;
        var lastPage = (address + size - 1) >> PageShift;
        if (lastPage - firstPage >= FilterMaxPages)
        {
            return true;
        }

        for (var page = firstPage; page <= lastPage; page++)
        {
            if (Volatile.Read(ref _pageFilter[FilterSlot(page)]) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int FilterSlot(ulong page) => (int)((page * 0x9E3779B97F4A7C15UL) >> (64 - FilterBits));

    private bool AnyOverlapLocked(ulong address, ulong size)
    {
        var found = false;
        ForEachOverlapping(address, size, _ => found = true);
        return found;
    }

    private void RetireOverlappingLocked(ulong address, ulong size, ulong completedSerial)
    {
        List<Entry>? landed = null;
        ForEachOverlapping(address, size, entry =>
        {
            if (entry.Serial <= completedSerial)
            {
                (landed ??= new List<Entry>(2)).Add(entry);
            }
        });

        if (landed is not null)
        {
            foreach (var entry in landed)
            {
                RemoveLocked(entry);
            }
        }
    }

    // Visits each live entry overlapping the range once, in no particular order.
    private void ForEachOverlapping(ulong address, ulong size, Action<Entry> visit)
    {
        if (_pages.Count == 0)
        {
            return;
        }

        var end = address + size;
        var firstPage = address >> PageShift;
        var lastPage = (end - 1) >> PageShift;
        if (lastPage - firstPage > 64)
        {
            // A long range: walking the entries is cheaper than walking its pages.
            foreach (var entry in _order)
            {
                if (!entry.Removed && entry.Address < end && address < entry.End)
                {
                    visit(entry);
                }
            }

            return;
        }

        Entry? previous = null;
        HashSet<Entry>? seen = null;
        for (var page = firstPage; page <= lastPage; page++)
        {
            if (!_pages.TryGetValue(page, out var list))
            {
                continue;
            }

            // Backwards, so a visit that removes the entry it was given keeps the walk valid.
            for (var index = list.Count - 1; index >= 0; index--)
            {
                if (index >= list.Count)
                {
                    continue;
                }

                var entry = list[index];
                if (entry.Removed || entry.Address >= end || address >= entry.End)
                {
                    continue;
                }

                // An entry spanning pages is listed on each; visit it once.
                if (entry.End - 1 >> PageShift != entry.Address >> PageShift)
                {
                    if (entry == previous || !(seen ??= new HashSet<Entry>()).Add(entry))
                    {
                        continue;
                    }
                }

                previous = entry;
                visit(entry);
            }
        }
    }

    private void RemoveLocked(Entry entry)
    {
        if (entry.Removed)
        {
            return;
        }

        entry.Removed = true;
        _count--;
        for (var page = entry.Address >> PageShift; page <= (entry.End - 1) >> PageShift; page++)
        {
            if (_pages.TryGetValue(page, out var list))
            {
                list.Remove(entry);
                if (list.Count == 0)
                {
                    _pages.Remove(page);
                }
            }

            if (_pageFilter is not null)
            {
                Interlocked.Decrement(ref _pageFilter[FilterSlot(page)]);
            }
        }
    }
}

internal enum MetalOverlayCoverage
{
    None,
    Partial,
    Full,
}
