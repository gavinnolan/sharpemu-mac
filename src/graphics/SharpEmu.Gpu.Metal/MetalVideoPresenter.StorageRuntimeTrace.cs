// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private static int _finiteStorageRuntimeTraceRows;
    private static long _finiteStorageRuntimeProducers, _finiteStorageRuntimeApplied, _finiteStorageRuntimeCanonical;

    private static void TraceFiniteStorageDispatchRefusal(ComputeGuestDispatch dispatch, Exception failure)
    {
        if (!_rgbaStorageRuntimePublicationRequested || Monitor.IsEntered(_gate)) return;
        try
        {
            var texture = dispatch.Textures.FirstOrDefault(MetalBgra1024ArrayStorageAdmission.IsQualified);
            if (texture is not null) TraceFiniteStorageRuntime("dispatch-refused", texture.Address, failure.Message, StorageCpuOrigin(texture));
        }
        catch { /* Even producer lookup must preserve the original exception. */ }
    }

    // Runtime opt-in only. Scalar counters are diagnostic, never receipts. One
    // bounded reservation attempt may drop a contended row; no signal calls.
    // All owner snapshots are already captured. Tracker sampling/formatting is
    // outside the presenter/provider gates and cannot replace a primary error.
    private static void TraceFiniteStorageRuntime(string stage, ulong address, string reason,
        StorageCpuSeedOrigin? origin = null)
    {
        if (!_rgbaStorageRuntimePublicationRequested || Monitor.IsEntered(_gate)) return;
        try
        {
            if (stage == "producer-retained") Interlocked.Increment(ref _finiteStorageRuntimeProducers);
            else if (stage == "provider-applied") Interlocked.Increment(ref _finiteStorageRuntimeApplied);
            else if (stage == "canonical-completed") Interlocked.Increment(ref _finiteStorageRuntimeCanonical);
            var rows = Volatile.Read(ref _finiteStorageRuntimeTraceRows);
            if (rows >= 64 || Interlocked.CompareExchange(ref _finiteStorageRuntimeTraceRows, rows + 1, rows) != rows) return;
            var original = origin?.Observation.Token;
            var current = default(GuestImageWriteTracker.ContentToken);
            var observed = origin is not null && GuestImageWriteTracker.TryGetProtectedObservedContentToken(
                address, origin.Layout.PhysicalBytes, out current);
            Console.Error.WriteLine($"finite-storage-runtime stage={stage}; address=0x{address:X}; reason={reason[..Math.Min(reason.Length, 192)]}; " +
                $"producer_count={Volatile.Read(ref _finiteStorageRuntimeProducers)}; applied_count={Volatile.Read(ref _finiteStorageRuntimeApplied)}; canonical_count={Volatile.Read(ref _finiteStorageRuntimeCanonical)}; " +
                $"original_session={origin?.Session}; original_protected_id={original?.ProtectedIdentity}; original_protected_generation={original?.ProtectedGeneration}; " +
                $"original_watch_id={original?.WatchIdentity}; original_watch_generation={original?.WatchGeneration}; current_observed={observed}; " +
                $"current_protected_id={current.ProtectedIdentity}; current_protected_generation={current.ProtectedGeneration}; current_watch_id={current.WatchIdentity}; current_watch_generation={current.WatchGeneration}; " +
                $"original_token_equal={observed && original == current}; ordinary_observations_not_writer_authority=true");
        }
        catch { /* A diagnostic cannot change ordering, refusal or the primary. */ }
    }
}
