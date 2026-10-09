// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

internal sealed partial class HostManagedWriteIntervals
{
    private ReadWatch?[]? readWatches; // Allocated only by the optional cold watched-copy path.
    private int activeReadWatches;
    internal sealed class ReadWatch(HostManagedWriteIntervals owner, PhysicalRun[] runs, long enrollmentRevision)
    {
        internal readonly PhysicalRun[] Runs = runs;
        // Original exclusive enrollment, before copied bytes. Never resampled
        // after a copy or substituted for an Applied publication's own revision.
        internal readonly long EnrollmentRevision = enrollmentRevision;
        internal int Slot = -1, Revoked, Released;
        internal void TraceDecline(string reason, long originalRevision, long primaryAdmission = -1,
            ulong originalSourceAddress = 0, ulong originalSourceBytes = 0)
        {
            try
            {
                HostPublicationDiagnostics.Declined(primaryAdmission >= 0 && (primaryAdmission & PublicationBit) != 0
                    ? "read-watch-held-publication" : "read-watch", reason,
                    originalRevision, Volatile.Read(ref owner.nextIdentity),
                    Volatile.Read(ref owner.admission), Volatile.Read(ref owner.coverageIncomplete),
                    Volatile.Read(ref Released), Volatile.Read(ref Revoked), primaryAdmission,
                    originalSourceAddress, originalSourceBytes);
            }
            catch (Exception) { } // Even formatting must preserve the refusal.
        }
        internal bool IsCurrent => TryIsCurrent(out _, out _);
        internal CpuMappedReadWatchStatus Status
        {
            get
            {
                if (TryIsCurrent(out var reason, out var primaryAdmission)) return CpuMappedReadWatchStatus.Current;
                // Only a pristine ORIGINAL watch refused by ordinary writer
                // admission may defer. Publication/unknown/retired states never
                // gain authority from this classification.
                if (reason != "admission-nonzero" || primaryAdmission <= 0 || primaryAdmission >= PublicationBit ||
                    Volatile.Read(ref Released) != 0 || Volatile.Read(ref Revoked) != 0 ||
                    Volatile.Read(ref owner.coverageIncomplete) != 0) return CpuMappedReadWatchStatus.Stale;
                return CpuMappedReadWatchStatus.Busy;
            }
        }
        internal bool TryIsCurrent(out string reason, out long primaryAdmission)
        {
            // Exactly the original reads, short-circuit order and final checks.
            // The primary admission is the actual guard value, never a later sample.
            primaryAdmission = -1;
            reason = "released"; if (Volatile.Read(ref Released) != 0) return false;
            reason = "revoked"; if (Volatile.Read(ref Revoked) != 0) return false;
            reason = "coverage-incomplete"; if (Volatile.Read(ref owner.coverageIncomplete) != 0) return false;
            primaryAdmission = Volatile.Read(ref owner.admission);
            reason = "admission-nonzero"; if (primaryAdmission != 0) return false;
            reason = "revoked-final"; if (Volatile.Read(ref Revoked) != 0) return false;
            reason = "released-final"; if (Volatile.Read(ref Released) != 0) return false;
            reason = "current"; return true;
        }
        internal CpuMappedReadWatchStatus GetStatusWithDiagnostic(out CpuMappedReadWatchDiagnostic diagnostic)
        {
            var current = TryIsCurrent(out var reason, out var primaryAdmission);
            diagnostic = new() { WatchGuard = reason, PrimaryAdmission = primaryAdmission >= 0 ? primaryAdmission : null };
            if (current) { diagnostic = diagnostic with { WatchStatus = CpuMappedReadWatchStatus.Current, ProviderStatus = CpuMappedReadWatchStatus.Current }; return CpuMappedReadWatchStatus.Current; }
            // Same checks/order as Status. The label refers to the actual
            // reached classification guard, never a later diagnostic sample.
            var classification = reason != "admission-nonzero" ? "initial-watch-refusal" :
                primaryAdmission <= 0 ? "nonpositive-admission" :
                primaryAdmission >= PublicationBit ? "publication-admission" :
                Volatile.Read(ref Released) != 0 ? "released-classification" :
                Volatile.Read(ref Revoked) != 0 ? "revoked-classification" :
                Volatile.Read(ref owner.coverageIncomplete) != 0 ? "coverage-classification" : null;
            var status = classification is null ? CpuMappedReadWatchStatus.Busy : CpuMappedReadWatchStatus.Stale;
            diagnostic = diagnostic with { ClassificationGuard = classification, WatchStatus = status, ProviderStatus = status };
            return status;
        }
        // Only the exact prepared provider ticket may use this scalar predicate.
        // Ordinary reads and new-watch admission still require admission == 0.
        internal bool IsUnrevokedDuringPublication(HostManagedWriteIntervals expected) =>
            ReferenceEquals(owner, expected) && Volatile.Read(ref Released) == 0 && Volatile.Read(ref Revoked) == 0 &&
            Volatile.Read(ref owner.coverageIncomplete) == 0 && Volatile.Read(ref owner.admission) == PublicationBit &&
            Volatile.Read(ref Revoked) == 0 && Volatile.Read(ref Released) == 0;
        internal bool CoversDuringPublication(HostManagedWriteIntervals expected, ReadOnlySpan<PhysicalRun> requested)
        {
            if (!IsUnrevokedDuringPublication(expected) || requested.IsEmpty) return false;
            foreach (var run in requested)
            {
                if (run.Count == 0 || run.Offset > owner.backing.Size || run.Count > owner.backing.Size - run.Offset) return false;
                var covered = false;
                foreach (var original in Runs)
                    if (run.Offset >= original.Offset && run.Offset - original.Offset <= original.Count &&
                        run.Count <= original.Count - (run.Offset - original.Offset)) { covered = true; break; }
                if (!covered) return false;
            }
            return IsUnrevokedDuringPublication(expected);
        }
        internal void Release()
        {
            if (Interlocked.Exchange(ref Released, 1) != 0) return;
            if (Slot >= 0 && Interlocked.CompareExchange(ref owner.readWatches![Slot], null, this) == this)
                Interlocked.Decrement(ref owner.activeReadWatches);
        }
    }

    internal ReadWatch? TryCreateReadWatch(PhysicalRun[] runs)
        => TryCreateReadWatch(runs, out _, out _);

    // First actual rejection scalars only. This overload changes neither
    // enrollment policy nor its original reads, reservation or finite bounds.
    internal ReadWatch? TryCreateReadWatch(PhysicalRun[] runs, out string? rejection, out long originalAdmission)
    {
        rejection = null; originalAdmission = -1;
        if (runs.Length is 0 or > CpuMappedCopyObservation.MaximumSegments)
        { rejection = "watch-enrollment-run-bound"; return null; }
        var installed = Volatile.Read(ref readWatches);
        if (installed is null)
        {
            var candidate = new ReadWatch?[Capacity];
            installed = Interlocked.CompareExchange(ref readWatches, candidate, null) ?? candidate;
        }
        if (Volatile.Read(ref coverageIncomplete) != 0)
        { rejection = "watch-enrollment-coverage-incomplete"; return null; }
        originalAdmission = Interlocked.CompareExchange(ref admission, PublicationBit, 0);
        if (originalAdmission != 0)
        { rejection = "watch-enrollment-admission-busy"; return null; }
        try
        {
            if (Volatile.Read(ref coverageIncomplete) != 0)
            { rejection = "watch-enrollment-coverage-incomplete-final"; return null; }
            var revision = Volatile.Read(ref nextIdentity);
            if (revision < 0 || revision == long.MaxValue)
            { rejection = "watch-enrollment-invalid-revision"; return null; }
            var watch = new ReadWatch(this, runs, revision); // Exact exclusive enrollment, cold path only.
            for (var index = 0; index < installed.Length; index++)
            {
                watch.Slot = index;
                if (Interlocked.CompareExchange(ref installed[index], watch, null) is null)
                { Interlocked.Increment(ref activeReadWatches); return watch; }
            }
            rejection = "watch-enrollment-slot-capacity";
            return null; // Finite watch capacity; never synthesize a current receipt.
        }
        finally { ReleasePublication(); }
    }

    // Cold allocation under the exact exclusive reservation. This successor
    // remains private until the transaction fully applies its own bytes.
    internal ReadWatch? TryCreatePublicationSuccessor(ReadWatch original)
    {
        if (!original.IsUnrevokedDuringPublication(this)) return null;
        var installed = Volatile.Read(ref readWatches);
        if (installed is null) return null;
        var revision = Volatile.Read(ref nextIdentity);
        if (revision < 0 || revision == long.MaxValue) return null;
        var successor = new ReadWatch(this, original.Runs, revision);
        for (var index = 0; index < installed.Length; index++)
        {
            successor.Slot = index;
            if (Interlocked.CompareExchange(ref installed[index], successor, null) is null)
            { Interlocked.Increment(ref activeReadWatches); return successor; }
        }
        return null;
    }

    private void InvalidateReadWatches(ulong offset, ulong count, bool unknown = false, ReadWatch? unpublishedSuccessor = null)
    {
        if (Volatile.Read(ref activeReadWatches) == 0) return;
        var installed = Volatile.Read(ref readWatches)!;
        for (var index = 0; index < installed.Length; index++)
        {
            var watch = Volatile.Read(ref installed[index]);
            if (watch is null || Volatile.Read(ref watch.Released) != 0 || ReferenceEquals(watch, unpublishedSuccessor)) continue;
            foreach (var run in watch.Runs)
                if (unknown || offset < run.Offset + run.Count && run.Offset < offset + count)
                { Volatile.Write(ref watch.Revoked, 1); break; }
        }
    }
}
