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
    private long historyRecordLosses;
    internal long HistoryRecordLosses => Volatile.Read(ref historyRecordLosses);

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
        if (identity <= 0 || runs.Length > HostMappedCopyLedger.MaximumPublicationRuns)
        { LoseCoverage(CoverageLoss.InvalidHistoryRecord); return; }
        ref var entry = ref history[(int)((ulong)identity % HistoryCapacity)];
        var previous = Volatile.Read(ref entry.State);
        // One bounded CAS, never spin or overwrite an in-progress/newer record.
        // A delayed completion may outlive its bounded history slot, including
        // a successful operation envelope around many precisely tracked chunks.
        // Missing history still rejects unwatched publication. Physical watches
        // were revoked BEFORE overlapping bytes (or ALL on unknown completion)
        // and admission stays held until End; their coverage is not lost here.
        if (previous < 0 || previous >= identity ||
            Interlocked.CompareExchange(ref entry.State, -identity, previous) != previous)
        {
            // Failure-only diagnostic, never a revision or admission certificate.
            // Saturate with one bounded CAS; an approximate concurrent count is enough.
            var losses = Volatile.Read(ref historyRecordLosses);
            if (losses < long.MaxValue) Interlocked.CompareExchange(ref historyRecordLosses, losses + 1, losses);
            return;
        }
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
        {
            TracePublicationDecline("history-window-or-empty-runs", originalRevision);
            return false;
        }
        foreach (var run in requested)
            if (run.Count == 0 || run.Offset > backing.Size || run.Count > backing.Size - run.Offset)
            { TracePublicationDecline("history-requested-run-invalid", originalRevision); return false; }
        for (var identity = originalRevision + 1; identity <= current; identity++)
        {
            ref var entry = ref history[(int)((ulong)identity % HistoryCapacity)];
            if (Volatile.Read(ref entry.State) != identity || entry.Unknown)
            { TracePublicationDecline("history-record-missing-or-unknown", originalRevision); return false; }
            for (var index = 0; index < entry.Count; index++)
            {
                var changed = entry.Runs[index];
                foreach (var run in requested)
                    if (changed.Offset < run.Offset + run.Count && run.Offset < changed.Offset + changed.Count)
                    { TracePublicationDecline("history-overlapping-completed-write", originalRevision); return false; }
            }
            if (Volatile.Read(ref entry.State) != identity)
            { TracePublicationDecline("history-record-changed", originalRevision); return false; }
        }
        return true;
    }
}
