// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

public sealed unsafe partial class HostMappedCopyLedger
{
    // Selected only by the optional local validation diagnostic overload.
    // Same predicates/order/loops as IsOriginalDisjointReadWatchCurrent. No
    // second currentness query, native effect, list scan, lock or callback.
    private bool IsOriginalDisjointReadWatchCurrentWithDiagnostic(HostBackingObject backing,
        CpuMappedCopyObservation reservationOriginal, HostManagedWriteIntervals writers,
        ReadOnlySpan<HostManagedWriteIntervals.PhysicalRun> reservedRuns,
        CpuMappedCopyObservation watchedOriginal, ref CpuMappedDisjointReadWatchDiagnostic diagnostic)
    {
        diagnostic = diagnostic with { Guard = "reservation-mapping-initial" };
        if (!IsCurrentForReadWatchDiagnostic(backing, reservationOriginal, out var mappingGuard))
        { diagnostic = diagnostic with { MappingGuard = mappingGuard }; return false; }
        diagnostic = diagnostic with { Guard = "watched-mapping-initial" };
        if (!IsCurrentForReadWatchDiagnostic(backing, watchedOriginal, out mappingGuard))
        {
            // Immutable ORIGINAL segment metadata, not a later View/currentness
            // sample. It describes the captured backing even on foreign/ABA veto.
            diagnostic = diagnostic with { MappingGuard = mappingGuard,
                WatchedBackingIdentity = watchedOriginal is not null && watchedOriginal.Segments.Count > 0
                    ? watchedOriginal.Segments[0].BackingIdentity : null };
            return false;
        }
        diagnostic = diagnostic with { Guard = "watched-capture-missing" };
        if (watchedOriginal.OwnerIdentity is not Capture capture) return false;
        diagnostic = diagnostic with { Guard = "watched-capture-ledger" };
        if (!ReferenceEquals(capture.Owner, this)) return false;
        diagnostic = diagnostic with { Guard = "watched-watch-missing" };
        if (capture.ReadWatch is not { } watch) return false;
        diagnostic = diagnostic with { Guard = "watched-watch-initial", WatchedBackingIdentity = watch.DiagnosticBackingIdentity };
        if (!watch.TryIsUnrevokedDuringPublication(writers, out var watchGuard, out var primaryAdmission))
        { diagnostic = diagnostic with { WatchGuard = watchGuard, PrimaryAdmission = primaryAdmission >= 0 ? primaryAdmission : null }; return false; }
        foreach (var watched in watch.Runs)
            foreach (var reserved in reservedRuns)
                if (Overlaps(new(watched.Offset, watched.Count), new(reserved.Offset, reserved.Count)))
                {
                    diagnostic = diagnostic with { Guard = "physical-overlap", WatchedOffset = watched.Offset,
                        WatchedCount = watched.Count, ReservedOffset = reserved.Offset, ReservedCount = reserved.Count };
                    return false;
                }
        diagnostic = diagnostic with { Guard = "watched-watch-final" };
        if (!watch.TryIsUnrevokedDuringPublication(writers, out watchGuard, out primaryAdmission))
        { diagnostic = diagnostic with { WatchGuard = watchGuard, PrimaryAdmission = primaryAdmission >= 0 ? primaryAdmission : null }; return false; }
        diagnostic = diagnostic with { Guard = "reservation-mapping-final" };
        if (!IsCurrentForReadWatchDiagnostic(backing, reservationOriginal, out mappingGuard))
        { diagnostic = diagnostic with { MappingGuard = mappingGuard }; return false; }
        diagnostic = diagnostic with { Guard = "watched-mapping-final" };
        if (!IsCurrentForReadWatchDiagnostic(backing, watchedOriginal, out mappingGuard))
        { diagnostic = diagnostic with { MappingGuard = mappingGuard }; return false; }
        diagnostic = diagnostic with { Guard = "current" };
        return true;
    }
}
