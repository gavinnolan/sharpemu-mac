// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

// Ordinary participating alias writes only. Internal bookkeeping, never content
// authority: pre-enrollment/raw/native stores and copy serialization are absent.
internal sealed partial class HostManagedWriteIntervals
{
    internal const int Capacity = 128;
    private sealed class Slot
    {
        internal long State; // 0 empty, -identity reserved, +identity published
        internal ulong Offset, Count;
        internal bool Operation;
    }
    private readonly HostBackingObject backing;
    private readonly Slot[] slots = Enumerable.Range(0, Capacity).Select(_ => new Slot()).ToArray();
    private const long PublicationBit = 1L << 32;
    private long admission;
    private long nextIdentity, active, begun, completed, aborted;
    private int coverageIncomplete;

    internal HostManagedWriteIntervals(HostBackingObject backing) => this.backing = backing;
    internal readonly record struct Token(HostManagedWriteIntervals? Owner, int Slot, long Identity);

    internal Token Begin(ulong offset, ulong count) => BeginCore(offset, count, false);
    internal Token BeginOperation() => BeginCore(0, backing.Size, true);
    private Token BeginCore(ulong offset, ulong count, bool operation)
    {
        if (count == 0) return default;
        var admitted = Interlocked.Increment(ref admission);
        if ((admitted & PublicationBit) != 0 || admitted >= PublicationBit)
        { Interlocked.Decrement(ref admission); return new(this, -1, 0); }
        if (offset > backing.Size || count > backing.Size - offset)
        { Interlocked.Exchange(ref coverageIncomplete, 1); Interlocked.Decrement(ref admission); return default; }
        if (Volatile.Read(ref coverageIncomplete) != 0) { Interlocked.Decrement(ref admission); return default; }
        var identity = Interlocked.Increment(ref nextIdentity);
        if (identity <= 0 || identity == long.MaxValue) { Interlocked.Exchange(ref coverageIncomplete, 1); Interlocked.Decrement(ref admission); return default; }
        var first = (int)((ulong)identity % Capacity);
        for (var visited = 0; visited < Capacity; visited++)
        {
            var index = (first + visited) % Capacity; var slot = slots[index];
            // One bounded attempt per preallocated slot; no spin, wait, query,
            // callback or allocation while the caller holds an outer gate.
            if (Interlocked.CompareExchange(ref slot.State, -identity, 0) != 0) continue;
            slot.Offset = offset; slot.Count = count; slot.Operation = operation;
            if (!operation)
            {
                Interlocked.Increment(ref active);
                if (Interlocked.Increment(ref begun) <= 0) Interlocked.Exchange(ref coverageIncomplete, 1);
            }
            Volatile.Write(ref slot.State, identity);
            return new(this, index, identity);
        }
        Interlocked.Exchange(ref coverageIncomplete, 1);
        Interlocked.Decrement(ref admission);
        return default; // preserve legacy write, explicitly lose bookkeeping coverage
    }

    internal bool End(Token token, bool succeeded)
    {
        if (token.Owner is null) return token.Identity == 0 && token.Slot == 0; // disabled/nonempty tracking decline
        if (!ReferenceEquals(token.Owner, this) || token.Identity <= 0 || (uint)token.Slot >= Capacity)
            return false;
        // Consume this exact operation before reuse. A copied/stale token cannot
        // release a later operation or repeat an unknown completion.
        var operation = slots[token.Slot].Operation;
        var offset = slots[token.Slot].Offset;
        var count = slots[token.Slot].Count;
        if (Interlocked.CompareExchange(ref slots[token.Slot].State, 0, token.Identity) != token.Identity)
            return false;
        // Copy the owned slot's fields before consuming it: a later operation
        // may immediately reuse the slot. Admission stays held through history.
        RecordCompletedHistory(token.Identity, offset, count, operation, succeeded);
        if (!operation)
        {
            Interlocked.Decrement(ref active);
            var total = succeeded ? Interlocked.Increment(ref completed) : Interlocked.Increment(ref aborted);
            if (total <= 0) Interlocked.Exchange(ref coverageIncomplete, 1);
        }
        Interlocked.Decrement(ref admission);
        return true;
    }


    internal static bool AllowsBytes(Token token) => token.Owner is null || token.Slot >= 0;

    // Participating writers only, never a certificate for native/JIT/raw stores.
    // The count is acquired before allocating an interval identity; late losing
    // writers may transiently increment under PublicationBit but cannot use bytes.
    internal bool TryCaptureRevision(out long revision)
    {
        revision = -1;
        if (Volatile.Read(ref coverageIncomplete) != 0 || Volatile.Read(ref admission) != 0) return false;
        var found = Volatile.Read(ref nextIdentity);
        if (found < 0 || Volatile.Read(ref admission) != 0 || Volatile.Read(ref coverageIncomplete) != 0) return false;
        revision = found; return true;
    }
    internal bool TryReservePublication(long originalRevision, ReadOnlySpan<PhysicalRun> runs)
    {
        if (originalRevision < 0 || originalRevision == long.MaxValue || Volatile.Read(ref coverageIncomplete) != 0 ||
            Interlocked.CompareExchange(ref admission, PublicationBit, 0) != 0) return false;
        if (HasDisjointCompletedHistory(originalRevision, runs) && Volatile.Read(ref coverageIncomplete) == 0) return true;
        ReleasePublication(); return false;
    }
    internal long RecordPublicationWrite(ReadOnlySpan<PhysicalRun> runs)
    {
        var revision = Interlocked.Increment(ref nextIdentity);
        if (revision <= 0 || revision == long.MaxValue) Interlocked.Exchange(ref coverageIncomplete, 1);
        else RecordHistory(revision, runs, false);
        return revision;
    }
    internal void ReleasePublication() => Interlocked.Add(ref admission, -PublicationBit);
    internal void QuarantinePublication() => Interlocked.Exchange(ref coverageIncomplete, 1);

    // Test-only ordinary observation. Never used for publication, permission,
    // GPU/fault admission or content-currentness. Concurrent snapshot is fuzzy:
    // reservations may be unpublished and completions may be between counters.
    private (ulong Offset, ulong Count, long Identity)[] ActiveForControl()
    {
        var found = new List<(ulong, ulong, long)>();
        foreach (var slot in slots)
        {
            var identity = Volatile.Read(ref slot.State);
            if (identity <= 0 || slot.Operation) continue;
            var offset = slot.Offset; var count = slot.Count;
            if (Volatile.Read(ref slot.State) == identity) found.Add((offset, count, identity));
        }
        return found.ToArray();
    }
}
