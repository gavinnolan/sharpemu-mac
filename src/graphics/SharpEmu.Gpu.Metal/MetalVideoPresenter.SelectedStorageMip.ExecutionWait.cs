// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private sealed record SelectedStorageMipSeedExecutionCandidate(GuestDrawTexture Texture,
        SelectedStorageMipAssociation Owner, SelectedStorageMipSeed Seed, object Work, long Sequence, long Session);
    // Allocated before optional release. Unknown release retains the exact old
    // receipt even if a later association replacement removes its CWT root.
    private sealed class SelectedSeedExecutionCleanupRoots(SelectedStorageMipSeedExecutionCandidate[] originals)
    {
        internal readonly SelectedStorageMipSeedExecutionCandidate[] Originals = originals;
        internal SelectedSeedExecutionCleanupRoots? Next;
        internal int Retained;
    }
    private static SelectedSeedExecutionCleanupRoots? _selectedSeedExecutionCleanupRoots;

    private static SelectedStorageMipSeedExecutionScope BeginSelectedStorageMipSeedExecution(ComputeGuestDispatch dispatch)
    {
        var possible = false;
        foreach (var texture in dispatch.Textures)
            if (_selectedStorageMipAssociations.TryGetValue(texture, out var owner) &&
                _selectedStorageMipSeeds.TryGetValue(owner, out var seed) && Volatile.Read(ref seed.Phase) is 1 or 2)
            { possible = true; break; }
        if (!possible) return default; // same allocation/gate-free legacy fast path
        SelectedStorageMipSeedExecutionCandidate[] candidates;
        lock (_gate)
        {
            var found = false;
            foreach (var texture in dispatch.Textures)
                if (_selectedStorageMipAssociations.TryGetValue(texture, out var owner) &&
                    _selectedStorageMipSeeds.TryGetValue(owner, out var seed) && seed.Phase == 1 && ReferenceEquals(seed.Work, dispatch))
                { found = true; break; }
            if (!found) return BeginSelectedStorageMipSeedExecutionLegacy(dispatch);
            if (_selectedStorageMipSeedExecutingWork is not null)
                throw new InvalidOperationException("Metal original selected seed cannot enter a nested dispatch scope.");
            candidates = dispatch.Textures.Select(texture =>
                _selectedStorageMipAssociations.TryGetValue(texture, out var owner) &&
                _selectedStorageMipSeeds.TryGetValue(owner, out var seed) && seed.Phase == 1 && ReferenceEquals(seed.Work, dispatch)
                    ? new SelectedStorageMipSeedExecutionCandidate(texture, owner, seed, dispatch, seed.Sequence, owner.Session) : null)
                .Where(candidate => candidate is not null).Cast<SelectedStorageMipSeedExecutionCandidate>().ToArray();
        }
        var roots = new SelectedSeedExecutionCleanupRoots(candidates);
        try
        {
            var deadline = Environment.TickCount64 + 500;
            foreach (var candidate in candidates.DistinctBy(candidate => candidate.Seed)) RequireSelectedStorageMipSeedExecutionPreflight(candidate, deadline);
            // No status result is promoted into execution authority. The exact
            // original Boolean/read/mapping/token checks still run under _gate.
            // A new writer in this final gap may conservatively refuse again.
            return BeginSelectedStorageMipSeedExecutionCore(dispatch, candidates);
        }
        catch (Exception primary)
        {
            Exception? cleanup = null;
            try
            {
                lock (_gate)
                {
                    foreach (var candidate in candidates) ConsumeSavedSelectedSeedOnFailure(candidate, dispatch, ref cleanup);
                    if (cleanup is not null) _selectedNativeOriginalCleanupFailure ??= cleanup;
                    if (Volatile.Read(ref _selectedNativeOriginalCleanupFailure) is not null) RetainSelectedSeedExecutionCleanupRoots(roots);
                }
            }
            catch (Exception unavailableCleanup)
            {
                // A failed metadata-gate acquisition/cleanup cannot replace the
                // primary or drop a removed original. No cleanup retry follows.
                cleanup ??= unavailableCleanup;
                Interlocked.CompareExchange(ref _selectedNativeOriginalCleanupFailure, cleanup, null);
                RetainSelectedSeedExecutionCleanupRoots(roots);
            }
            if (cleanup is not null)
                try { primary.Data["MetalSelectedSeedWatchCleanup"] = cleanup; } catch { }
            throw;
        }
    }

    private static void RetainSelectedSeedExecutionCleanupRoots(SelectedSeedExecutionCleanupRoots roots)
    {
        if (Interlocked.Exchange(ref roots.Retained, 1) != 0) return;
        SelectedSeedExecutionCleanupRoots? previous;
        do
        {
            previous = Volatile.Read(ref _selectedSeedExecutionCleanupRoots); roots.Next = previous;
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref _selectedSeedExecutionCleanupRoots, roots, previous), previous));
    }

    private static void ConsumeSavedSelectedSeedOnFailure(SelectedStorageMipSeedExecutionCandidate candidate,
        object dispatch, ref Exception? cleanup)
    {
        var seed = candidate.Seed;
        if (!ReferenceEquals(seed.Work, dispatch))
        {
            if (seed.Phase == 3 && seed.Work is null && seed.WriteWatchConsumed == 1) return; // known Core consumption
            cleanup ??= new InvalidOperationException("Selected original cleanup lost its exact admitted work; old receipt retained without release/retry.");
            return;
        }
        seed.Phase = 3; seed.Thread = null; seed.Work = null;
        if (!seed.NativeOwnerTransferred) TryReleaseSelectedSeedWatch(seed, ref cleanup);
    }

    private static SelectedStorageMipSeedExecutionScope BeginSelectedStorageMipSeedExecutionCore(
        ComputeGuestDispatch dispatch, SelectedStorageMipSeedExecutionCandidate[] candidates)
    {
        if (candidates.Length == 0) return BeginSelectedStorageMipSeedExecutionLegacy(dispatch);
        lock (_gate)
        {
            // No mutable-CWT shortcut precedes validation of saved originals.
            foreach (var candidate in candidates) RequireSelectedStorageMipSeedExecutionIdentityLocked(candidate);
            foreach (var texture in dispatch.Textures)
                if (_selectedStorageMipAssociations.TryGetValue(texture, out var owner) &&
                    _selectedStorageMipSeeds.TryGetValue(owner, out var extra) && extra.Phase == 1 && ReferenceEquals(extra.Work, dispatch) &&
                    !candidates.Any(candidate => ReferenceEquals(candidate.Texture, texture) && ReferenceEquals(candidate.Owner, owner) && ReferenceEquals(candidate.Seed, extra)))
                    throw new InvalidOperationException("Metal selected seed cannot adopt an added original at first execution.");
            try
            {
                foreach (var candidate in candidates.DistinctBy(candidate => candidate.Seed))
                    if (!IsSelectedStorageMipSeedCurrentWithReason(candidate.Owner, candidate.Seed, out var reason))
                        throw new InvalidOperationException($"Metal original selected seed lost its exact first execution receipt. reason={reason}; no_later_query=true.");
                var seeds = candidates.Select(candidate => candidate.Seed).Distinct().ToArray();
                foreach (var seed in seeds) { seed.Thread = Thread.CurrentThread; seed.Phase = 2; }
                _selectedStorageMipSeedExecutingWork = dispatch;
                return new(dispatch, seeds);
            }
            catch (Exception primary)
            {
                Exception? cleanup = null;
                // Only saved original receipts may be consumed, never a later
                // association found by re-enumerating the mutable CWT.
                foreach (var candidate in candidates) ConsumeSavedSelectedSeedOnFailure(candidate, dispatch, ref cleanup);
                if (cleanup is not null)
                {
                    _selectedNativeOriginalCleanupFailure ??= cleanup;
                    try { primary.Data["MetalSelectedSeedWatchCleanup"] = cleanup; } catch { }
                }
                throw;
            }
        }
    }

    private static void RequireSelectedStorageMipSeedExecutionIdentityLocked(SelectedStorageMipSeedExecutionCandidate candidate)
    {
        var seed = candidate.Seed;
        if (candidate.Work is not ComputeGuestDispatch dispatch || !dispatch.Textures.Any(texture => ReferenceEquals(texture, candidate.Texture)) ||
            !_selectedStorageMipAssociations.TryGetValue(candidate.Texture, out var owner) || !ReferenceEquals(owner, candidate.Owner) ||
            !_selectedStorageMipSeeds.TryGetValue(candidate.Owner, out var current) || !ReferenceEquals(current, seed) ||
            seed.Phase != 1 || seed.WriteWatchConsumed != 0 || seed.NativeOwnerTransferred ||
            !ReferenceEquals(seed.Work, candidate.Work) || seed.Sequence != candidate.Sequence || candidate.Sequence <= 0 ||
            candidate.Sequence != Volatile.Read(ref _executingGuestWorkSequence) || candidate.Owner.Session != candidate.Session ||
            candidate.Session != CurrentSelectedStorageMipSeedSession || _selectedStorageMipSeedExecutingWork is not null)
            throw new InvalidOperationException("Metal original selected seed lost its frozen first-execution identity/session/sequence; no recapture.");
    }

    private static void RequireSelectedStorageMipSeedExecutionPreflight(SelectedStorageMipSeedExecutionCandidate candidate, long deadline)
    {
        if (!_selectedNativeOriginalReuseEnabled || candidate.Seed.Source is not ICpuMappedReadWatchSource source ||
            !source.CanWaitForMappedReadWatchStatus) return; // unknown/legacy keeps existing strict Boolean route
        if (Monitor.IsEntered(_gate)) throw new InvalidOperationException("Selected original watch preflight requires a released presenter gate.");
        // Preserve the first receipt's read -> mapping -> watch predicate order.
        EnterOriginalWatchMetadataGate(null);
        try { RequireSelectedStorageMipSeedExecutionIdentityLocked(candidate); }
        finally { Monitor.Exit(_gate); }
        RequireSelectedStorageMipSeedExecutionReadMapping(candidate);
        var busyObserved = false;
        void NonWatchCurrent()
        {
            RequireSelectedStorageMipSeedExecutionReadMapping(candidate);
            var owner = candidate.Owner;
            if (!IsSelectedStorageMipSeedObservationContentCurrent(owner) || !IsSelectedStorageMipSeedContentCurrent(owner, out _))
                throw new InvalidOperationException("Metal original selected seed lost its original content/epoch/token while Busy.");
            EnterOriginalWatchMetadataGate(busyObserved ? deadline : null);
            try { RequireSelectedStorageMipSeedExecutionIdentityLocked(candidate); }
            finally { Monitor.Exit(_gate); }
        }
        WaitForOrdinaryStorageOriginalWatch(source, candidate.Seed.Mapping, true, deadline,
            () => { busyObserved = true; NonWatchCurrent(); });
        if (!source.CanWaitForMappedReadWatchStatus)
            throw new NotSupportedException("Selected original seed lost provider wait closure; no Boolean reclassification.");
        NonWatchCurrent(); // whole exact original, never refreshed observation
    }

    private static void RequireSelectedStorageMipSeedExecutionReadMapping(SelectedStorageMipSeedExecutionCandidate candidate)
    {
        if (!GuestImageWriteTracker.IsReadSnapshotStable(candidate.Seed.Read))
            throw new InvalidOperationException("Metal original selected seed preflight refused original-read-generation; no_later_query=true.");
        if (!candidate.Seed.Source.IsMappedCopyObservationCurrent(candidate.Seed.Mapping))
            throw new InvalidOperationException("Metal original selected seed preflight refused original-mapping-currentness; no_later_query=true.");
    }

    // Non-watch veto only, never a positive receipt or wait/currentness result.
    private static bool IsSelectedStorageMipSeedObservationContentCurrent(SelectedStorageMipAssociation owner)
    {
        var observation = owner.Observation;
        if (observation.MappingEpoch != HostMemory.MappingGeneration)
            return !TextureSourceGraphEnabled && observation.Observed &&
                IsCpuTextureSnapshotContentObservationStable(owner.Key.Resource.Address, observation);
        return IsCpuTextureSnapshotAdmissionStable(owner.Key.Resource.Address, observation);
    }
}
