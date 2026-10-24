// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.HLE.GuestMemory;

/// <summary>
/// Every guest view of another process's backing, replicated in this process at the guest address
/// plus one constant, <see cref="Base"/>. The two-process split's GPU process cannot map guest
/// memory at the guest addresses (macOS 27 guards 0xFC0000000-0x70_0000_0000, and the process has a
/// layout of its own), so the GPU backend's "host pointer = guest address" becomes "host pointer =
/// guest address + Base": no lookup, and a Metal no-copy window over a guest range is exactly as
/// contiguous as it is in the owner.
///
/// <para>
/// It attaches to a mirror of the backing (<see cref="IHostGuestMirrorMemory.TryMapMirror"/>) and to
/// the owner's <see cref="GuestViewTable"/>, reserves its window of guest addresses + Base, and maps
/// what the table holds. <see cref="Sync"/> then replays the table's view log up to a given record,
/// at the split's sync points: a map becomes a remap of the mirror's range at guest address + Base,
/// an unmap puts the reservation back. Protection records are skipped and the replica stays
/// read-write: a protection here would guard only this process's own writes, and the owner's page
/// watches change protections thousands of times a second. A replica a whole ring behind starts
/// again from the table's entries, which say which record they are current to; attaching is the same.
/// </para>
///
/// <para>
/// Only backing views exist here. Memory private to the owner (module images, stacks, HLE
/// allocations) is not in the backing, and its guest address + Base stays an inaccessible
/// reservation (docs/macos/split-shared-memory.md). The replica owns its window, not the mirror or
/// the table mapping, which must outlive it.
/// </para>
/// </summary>
public sealed unsafe class GuestReplica : IDisposable
{
    /// <summary>16 TiB: clear of the guest range and of the macOS 27 guard, inside an arm64 process's 47-bit VA.</summary>
    public const ulong DefaultBase = 0x1000_0000_0000;

    /// <summary>The guest addresses a default replica covers: all of the guest range (it ends at 0xFC_0000_0000).</summary>
    public const ulong DefaultWindowStart = 0;

    public const ulong DefaultWindowEnd = 0x100_0000_0000;

    private readonly IHostGuestMirrorMemory _host;
    private readonly HostBackingObject _mirror;
    private readonly GuestViewTableHeader* _header;
    private readonly GuestViewTableEntry* _entries;
    private readonly int _capacity;
    private readonly GuestViewLogRecord* _log;
    private readonly int _logCapacity;
    private readonly object _gate = new();
    private ulong _replayed;
    private long _failures;
    private long _resyncs;
    private bool _startedFromIncompleteTable;
    private bool _disposed;

    private GuestReplica(IHostGuestMirrorMemory host, HostBackingObject mirror, HostSharedRegion viewTable, ulong replicaBase,
        ulong windowStart, ulong windowEnd)
    {
        _host = host;
        _mirror = mirror;
        GuestViewTableReader.Open(viewTable.Address, viewTable.Size, out _header, out _entries, out _capacity, out _log, out _logCapacity);
        Base = replicaBase;
        WindowStart = windowStart;
        WindowEnd = windowEnd;
    }

    /// <summary>
    /// Reserves guest [<paramref name="windowStart"/>, <paramref name="windowEnd"/>) + <paramref name="replicaBase"/>
    /// and maps every view the table holds into it; null when any of that address range is taken.
    /// </summary>
    public static GuestReplica? TryAttach(IHostGuestMirrorMemory host, HostBackingObject mirror, HostSharedRegion viewTable,
        ulong replicaBase = DefaultBase, ulong windowStart = DefaultWindowStart, ulong windowEnd = DefaultWindowEnd)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(viewTable);
        if (windowStart >= windowEnd || replicaBase > ulong.MaxValue - windowEnd)
            throw new ArgumentOutOfRangeException(nameof(windowEnd), "The replica window is empty or past the end of the address space.");
        var replica = new GuestReplica(host, mirror, viewTable, replicaBase, windowStart, windowEnd);
        if (!host.TryReserveReplica(replicaBase + windowStart, windowEnd - windowStart)) return null;
        lock (replica._gate) replica.ResyncLocked();
        return replica;
    }

    /// <summary>The constant every guest address is replicated at: host address = guest address + Base.</summary>
    public ulong Base { get; }

    public ulong WindowStart { get; }

    public ulong WindowEnd { get; }

    /// <summary>Log records replayed so far: every view change the owner made before record number this.</summary>
    public ulong ReplayedSequence => Volatile.Read(ref _replayed);

    /// <summary>Log records the owner has published.</summary>
    public ulong PublishedSequence => Volatile.Read(ref _header->LogSequence);

    /// <summary>Times the replica started again from the table's entries (attaching included).</summary>
    public long Resyncs => Volatile.Read(ref _resyncs);

    /// <summary>Views since the last start that could not be replicated (outside the window, or refused by the host).</summary>
    public long Failures => Volatile.Read(ref _failures);

    /// <summary>
    /// False when a view since the last start could not be replicated, or the last start read a
    /// table the owner could not keep complete: then some guest address + Base is not what it is in the owner.
    /// </summary>
    public bool IsComplete
    {
        get { lock (_gate) return _failures == 0 && !_startedFromIncompleteTable; }
    }

    /// <summary>Where guest <paramref name="guestAddress"/> is in this process.</summary>
    public ulong HostAddress(ulong guestAddress) => Base + guestAddress;

    /// <summary>Whether all of guest [address, address + size) is inside the replica's window.</summary>
    public bool Covers(ulong guestAddress, ulong size) =>
        size != 0 && guestAddress >= WindowStart && guestAddress < WindowEnd && size <= WindowEnd - guestAddress;

    /// <summary>
    /// Replays the owner's view log up to record <paramref name="upTo"/> (all it has published, by
    /// default) and returns the record number replayed to, which is at least that far, or further
    /// when the replica had to start again from the table.
    /// </summary>
    public ulong Sync(ulong upTo = ulong.MaxValue)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var target = Math.Min(upTo, Volatile.Read(ref _header->LogSequence));
            if (target <= _replayed) return _replayed;
            if (target - _replayed > (ulong)_logCapacity)
            {
                ResyncLocked();
                return _replayed;
            }

            while (_replayed < target)
            {
                if (!GuestViewTableReader.TryReadLog(_log, _logCapacity, _replayed, out var record))
                {
                    // Overwritten before it was read: a whole ring behind after all.
                    ResyncLocked();
                    break;
                }

                ApplyLocked(record);
                Volatile.Write(ref _replayed, _replayed + 1);
            }

            return _replayed;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_host.TryReleaseReplica(Base + WindowStart, WindowEnd - WindowStart))
                throw new InvalidOperationException($"The guest replica at 0x{Base + WindowStart:X} could not be released.");
        }
    }

    private void ApplyLocked(GuestViewLogEntry record)
    {
        switch (record.Kind)
        {
            case GuestViewLogKind.Map:
                if (!Covers(record.Address, record.Size) ||
                    !_host.TryMapReplicaView(_mirror, Base + record.Address, record.Offset, record.Size))
                    _failures++;
                break;
            case GuestViewLogKind.Unmap:
                // Nothing outside the window was ever mapped here.
                if (Covers(record.Address, record.Size) && !_host.TryUnmapReplicaView(Base + record.Address, record.Size))
                    _failures++;
                break;
            // GuestViewLogKind.Protect: the replica stays read-write (see the class summary).
        }
    }

    // The whole window back to the reservation, then one mapping per run of the table: entries
    // whose addresses and offsets both continue, whatever their protection, so a run always starts
    // and ends on view edges (guest pages), never on a page watch's host page.
    private void ResyncLocked()
    {
        var entries = GuestViewTableReader.Snapshot(_header, _entries, _capacity, out var logSequence);
        _startedFromIncompleteTable = (Volatile.Read(ref _header->Flags) & GuestViewTable.IncompleteFlag) != 0;
        _failures = 0;
        if (!_host.TryUnmapReplicaView(Base + WindowStart, WindowEnd - WindowStart)) _failures++;
        for (var index = 0; index < entries.Length;)
        {
            var run = entries[index++];
            var size = run.Size;
            while (index < entries.Length && entries[index].Address == run.Address + size && entries[index].Offset == run.Offset + size)
                size += entries[index++].Size;
            ApplyLocked(new GuestViewLogEntry(logSequence, GuestViewLogKind.Map, run.Address, size, run.Offset, run.Protection));
        }

        Volatile.Write(ref _replayed, logSequence);
        _resyncs++;
    }
}
