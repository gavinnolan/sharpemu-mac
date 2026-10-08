// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;

namespace SharpEmu.HLE.Host;

internal sealed partial class HostManagedWriteIntervals
{
    internal const int HistoryCapacity = 512;
    internal readonly record struct PhysicalRun(ulong Offset, ulong Count);
    [InlineArray(HostMappedCopyLedger.MaximumPublicationRuns)]
    private struct HistoryRuns { private PhysicalRun element; }
    private struct HistoryEntry
    {
        internal long State; // 0 empty, -identity filling, +identity complete
        internal int Count;
        internal bool Unknown;
        internal HistoryRuns Runs;
    }
    // Created only at ordinary enrollment, never in a write/End hot path.
    private readonly HistoryEntry[] history = new HistoryEntry[HistoryCapacity];

    private void RecordCompletedHistory(long identity, ulong offset, ulong count, bool operation, bool succeeded)
    {
        if (operation)
        {
            // Every actual destination is bracketed by the existing physical
            // helpers. A successful envelope therefore contributes no guessed
            // range; an unresolved/failed envelope remains conservatively unknown.
            RecordHistory(identity, ReadOnlySpan<PhysicalRun>.Empty, !succeeded);
            return;
        }
        Span<PhysicalRun> run = stackalloc PhysicalRun[1];
        run[0] = new(offset, count);
        RecordHistory(identity, run, !succeeded);
    }

    private void RecordHistory(long identity, ReadOnlySpan<PhysicalRun> runs, bool unknown)
    {
        ref var entry = ref history[(int)((ulong)identity % HistoryCapacity)];
        var previous = Volatile.Read(ref entry.State);
        // One bounded CAS, never spin or overwrite an in-progress/newer record.
        // A delayed completion that has fallen out of the ring loses coverage.
        if (identity <= 0 || previous < 0 || previous >= identity || runs.Length > HostMappedCopyLedger.MaximumPublicationRuns ||
            Interlocked.CompareExchange(ref entry.State, -identity, previous) != previous)
        { Interlocked.Exchange(ref coverageIncomplete, 1); return; }
        entry.Count = runs.Length; entry.Unknown = unknown;
        for (var index = 0; index < runs.Length; index++) entry.Runs[index] = runs[index];
        Volatile.Write(ref entry.State, identity);
    }

    // Called only while PublicationBit excludes all participating writers. End
    // publishes history before releasing admission; no slot/list lock is needed.
    private bool HasDisjointCompletedHistory(long originalRevision, ReadOnlySpan<PhysicalRun> requested)
    {
        var current = Volatile.Read(ref nextIdentity);
        if (current == long.MaxValue || current < originalRevision || current - originalRevision > HistoryCapacity || requested.IsEmpty)
            return false;
        foreach (var run in requested)
            if (run.Count == 0 || run.Offset > backing.Size || run.Count > backing.Size - run.Offset) return false;
        for (var identity = originalRevision + 1; identity <= current; identity++)
        {
            ref var entry = ref history[(int)((ulong)identity % HistoryCapacity)];
            if (Volatile.Read(ref entry.State) != identity || entry.Unknown) return false;
            for (var index = 0; index < entry.Count; index++)
            {
                var changed = entry.Runs[index];
                foreach (var run in requested)
                    if (changed.Offset < run.Offset + run.Count && run.Offset < changed.Offset + changed.Count) return false;
            }
            if (Volatile.Read(ref entry.State) != identity) return false;
        }
        return true;
    }
}
