// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

// Ordinary participating alias writes only. Internal bookkeeping, never content
// authority: pre-enrollment/raw/native stores and copy serialization are absent.
internal sealed class HostManagedWriteIntervals
{
    internal const int Capacity = 128;
    private sealed class Slot
    {
        internal long State; // 0 empty, -identity reserved, +identity published
        internal ulong Offset, Count;
    }
    private readonly HostBackingObject backing;
    private readonly Slot[] slots = Enumerable.Range(0, Capacity).Select(_ => new Slot()).ToArray();
    private long nextIdentity, active, begun, completed, aborted;
    private int coverageIncomplete;

    internal HostManagedWriteIntervals(HostBackingObject backing) => this.backing = backing;
    internal readonly record struct Token(HostManagedWriteIntervals? Owner, int Slot, long Identity);

    internal Token Begin(ulong offset, ulong count)
    {
        if (count == 0) return default;
        if (offset > backing.Size || count > backing.Size - offset)
        { Interlocked.Exchange(ref coverageIncomplete, 1); return default; }
        if (Volatile.Read(ref coverageIncomplete) != 0) return default;
        var identity = Interlocked.Increment(ref nextIdentity);
        if (identity <= 0) { Interlocked.Exchange(ref coverageIncomplete, 1); return default; }
        var first = (int)((ulong)identity % Capacity);
        for (var visited = 0; visited < Capacity; visited++)
        {
            var index = (first + visited) % Capacity; var slot = slots[index];
            // One bounded attempt per preallocated slot; no spin, wait, query,
            // callback or allocation while the caller holds an outer gate.
            if (Interlocked.CompareExchange(ref slot.State, -identity, 0) != 0) continue;
            Interlocked.Increment(ref active);
            slot.Offset = offset; slot.Count = count;
            if (Interlocked.Increment(ref begun) <= 0) Interlocked.Exchange(ref coverageIncomplete, 1);
            Volatile.Write(ref slot.State, identity);
            return new(this, index, identity);
        }
        Interlocked.Exchange(ref coverageIncomplete, 1);
        return default; // preserve legacy write, explicitly lose bookkeeping coverage
    }

    internal bool End(Token token, bool succeeded)
    {
        if (token.Owner is null) return token.Identity == 0 && token.Slot == 0; // disabled/nonempty tracking decline
        if (!ReferenceEquals(token.Owner, this) || token.Identity <= 0 || (uint)token.Slot >= Capacity)
            return false;
        // Consume this exact operation before reuse. A copied/stale token cannot
        // release a later operation or repeat an unknown completion.
        if (Interlocked.CompareExchange(ref slots[token.Slot].State, 0, token.Identity) != token.Identity)
            return false;
        Interlocked.Decrement(ref active);
        var total = succeeded ? Interlocked.Increment(ref completed) : Interlocked.Increment(ref aborted);
        if (total <= 0) Interlocked.Exchange(ref coverageIncomplete, 1);
        return true;
    }

    // Test-only ordinary observation. Never used for publication, permission,
    // GPU/fault admission or content-currentness. Concurrent snapshot is fuzzy:
    // reservations may be unpublished and completions may be between counters.
    private (ulong Offset, ulong Count, long Identity)[] ActiveForControl()
    {
        var found = new List<(ulong, ulong, long)>();
        foreach (var slot in slots)
        {
            var identity = Volatile.Read(ref slot.State);
            if (identity <= 0) continue;
            var offset = slot.Offset; var count = slot.Count;
            if (Volatile.Read(ref slot.State) == identity) found.Add((offset, count, identity));
        }
        return found.ToArray();
    }
}
