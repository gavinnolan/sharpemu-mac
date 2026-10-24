// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.HLE.GuestMemory;

/// <summary>
/// Turns guest virtual addresses into pointers into an offset mirror of the guest backing, through
/// a <see cref="GuestViewTable"/> another process owns: the two-process split's GPU process cannot
/// map guest memory at the guest addresses (macOS 27 guards 0xFC0000000-0x70_0000_0000, and the
/// process has a layout of its own), so it maps the backing wherever the host puts it
/// (<see cref="IHostGuestMirrorMemory.TryMapMirror"/>) and finds each guest address here, without
/// a call. (<see cref="GuestReplica"/> is the other way: every view mapped again at the guest address
/// plus a constant, so the GPU backend needs no lookup at all.)
///
/// <para>
/// A hit costs one load of the table's mapping generation and one cached range check. Each of the
/// <see cref="CacheSlots"/> slots holds one immutable range, so threads share the cache without a
/// lock; a slot is good while the mapping generation it was read under is current, and protection
/// changes do not move that generation. A miss reads the table under its sequence and caches the
/// whole run of entries around the address whose addresses and offsets both continue.
/// </para>
///
/// <para>
/// A translation is as current as the table was when it was read: an unmap the owner makes right
/// after it is not seen by the caller, exactly as a host pointer taken before an unmap in one
/// process. The split orders mapping changes against GPU work at its own sync points.
/// Addresses the backing does not hold (module images, stacks, HLE allocations: private memory of
/// the owner) are never in the table.
/// </para>
/// </summary>
public sealed unsafe class GuestMirrorTranslator
{
    public const int CacheSlots = 64;

    // Slots are picked by the 64 KiB block of the address: the guest's own mapping granule.
    private const int CacheShift = 16;

    private readonly GuestViewTableHeader* _header;
    private readonly GuestViewTableEntry* _entries;
    private readonly int _capacity;
    private readonly CachedRange?[] _cache = new CachedRange?[CacheSlots];
    private long _hits;
    private long _misses;

    private sealed class CachedRange(ulong start, ulong end, ulong offset, ulong generation)
    {
        public readonly ulong Start = start;
        public readonly ulong End = end;
        public readonly ulong Offset = offset;
        public readonly ulong Generation = generation;
    }

    /// <summary>
    /// A translator over a mapped view table (often read-only) and a mirror of the backing it
    /// describes. Both mappings must outlive the translator.
    /// </summary>
    public GuestMirrorTranslator(ulong tableAddress, ulong tableBytes, ulong mirrorBase, ulong mirrorBytes)
    {
        GuestViewTableReader.Open(tableAddress, tableBytes, out _header, out _entries, out _capacity, out _, out _);
        MirrorBase = mirrorBase;
        MirrorBytes = Math.Min(mirrorBytes, _header->BackingSize);
    }

    public GuestMirrorTranslator(HostSharedRegion table, HostBackingObject mirror)
        : this(table.Address, table.Size, mirror.AliasBase, mirror.Size)
    {
    }

    /// <summary>Host address of backing offset 0 in this process.</summary>
    public ulong MirrorBase { get; }

    /// <summary>Backing bytes the mirror covers; no translation reaches past them.</summary>
    public ulong MirrorBytes { get; }

    /// <summary>False once the owner could not fit a change: translations must then come from the owner.</summary>
    public bool IsComplete => (Volatile.Read(ref _header->Flags) & GuestViewTable.IncompleteFlag) == 0;

    public ulong MappingGeneration => Volatile.Read(ref _header->MappingGeneration);

    // Diagnostics only: plain increments, so counts from racing threads may be lost.
    public long CacheHits => Volatile.Read(ref _hits);

    public long CacheMisses => Volatile.Read(ref _misses);

    /// <summary>
    /// The mirror address of guest [address, address + size), when one run of the table holds all
    /// of it; false when any of it is unmapped, private to the owner, or split over runs whose
    /// offsets do not continue (the caller then translates piece by piece).
    /// </summary>
    public bool TryTranslate(ulong address, ulong size, out ulong mirrorAddress)
    {
        mirrorAddress = 0;
        if (size == 0 || size > ulong.MaxValue - address || !TryGetRange(address, out var range) || address + size > range.End)
            return false;
        mirrorAddress = MirrorBase + range.Offset + (address - range.Start);
        return true;
    }

    /// <summary>
    /// The backing offset of guest <paramref name="address"/> and how many bytes from there continue
    /// in the backing (to the end of its run).
    /// </summary>
    public bool TryGetOffset(ulong address, out ulong offset, out ulong contiguousBytes)
    {
        offset = contiguousBytes = 0;
        if (!TryGetRange(address, out var range)) return false;
        offset = range.Offset + (address - range.Start);
        contiguousBytes = range.End - address;
        return true;
    }

    /// <summary>
    /// The table entry holding <paramref name="address"/>, never cached. Its protection is the view's
    /// mapping protection unless the table tracks protection changes (<see cref="GuestViewTable.TracksProtection"/>).
    /// </summary>
    public bool TryGetEntry(ulong address, out GuestViewEntry entry) =>
        GuestViewTableReader.TryFind(_header, _entries, _capacity, address, joinRun: false, out entry, out _);

    /// <summary>Every entry, in address order, as of one consistent moment.</summary>
    public GuestViewEntry[] Snapshot() => GuestViewTableReader.Snapshot(_header, _entries, _capacity);

    private bool TryGetRange(ulong address, out CachedRange range)
    {
        var generation = Volatile.Read(ref _header->MappingGeneration);
        ref var slot = ref _cache[(int)((address >> CacheShift) & (CacheSlots - 1))];
        var cached = Volatile.Read(ref slot);
        if (cached is not null && cached.Generation == generation && address >= cached.Start && address < cached.End)
        {
            _hits++;
            range = cached;
            return true;
        }

        _misses++;
        range = null!;
        if (!GuestViewTableReader.TryFind(_header, _entries, _capacity, address, joinRun: true, out var entry, out var readGeneration))
            return false;
        // A run reaching past the mirror is cut at its end; an address past it has no translation.
        if (entry.Offset >= MirrorBytes) return false;
        var size = Math.Min(entry.Size, MirrorBytes - entry.Offset);
        if (address - entry.Address >= size) return false;
        range = new CachedRange(entry.Address, entry.Address + size, entry.Offset, readGeneration);
        Volatile.Write(ref slot, range);
        return true;
    }
}
