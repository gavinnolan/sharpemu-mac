// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Fatal-only secondary diagnostic. Cleanup cannot replace the GPU cause or
    // keep blocked admission waiters asleep; no healthy continuation is allowed.
    private static Exception? _textureSourceCleanupFailure;
    private static Action? _textureSourceAfterCanceledPayloadReturn;

    private static void LatchTextureSourceDrainFailureLocked(Exception failure)
    {
        _guestWorkFailure = failure;
        try { AbortQueuedTextureSourceWorkLocked(); }
        catch (Exception cleanupFailure) { _textureSourceCleanupFailure ??= cleanupFailure; }
        finally
        {
            _batchOpen = false;
            _batchCommandBuffer = 0;
            Monitor.PulseAll(_gate);
        }
    }

    private static void RequireQualifiedTextureSourceConsumerWork(object work)
    {
        if (work is OffscreenGuestDraw color)
        {
            RequireQualifiedColorTextureSourceWork(color);
            return;
        }
        lock (_gate)
            if (_textureSourceWorkOwners.TryGetValue(work, out var owner) && owner.Outputs.Length != 0)
                throw new NotSupportedException("Metal runtime texture-source storage producer encoding is not qualified yet.");
    }

    // Called under the presenter gate. Never waits or discards committed work.
    private static void AbortQueuedTextureSourceWorkLocked()
    {
        if (!_textureSourceGraphEnabled) return;
        InvalidateTextureSourceVersionsLocked();
        foreach (var work in _textureSourceWorkOwners.Select(static pair => pair.Key).ToArray())
            ReleaseTextureSourceWorkOwnerLocked(work);
        foreach (var batch in _textureSourceNativeBatches.Values.Where(static batch => !batch.Committed).ToArray())
            AbortTextureSourceNativeBatch(batch.CommandBuffer);
        foreach (var source in _textureSourceVersions) source.Retired = true;
        _latestTextureSources.Clear();
        // GPU address tombstones survive fatal abort; no stale CPU read is allowed.
        PruneTextureSourceNativeVersionsLocked();
        _executingTextureSourceWork = null;
        _touchedUploadPages.Clear();
        _uploadTouchStamp++;
        DropCanceledGuestQueueReferencesLocked();
    }

    // Shared with Run's actual exit path; native waits occur after releasing gate.
    private sealed class GuestPresentationShutdownAttempt
    {
        public readonly int ThreadId = Environment.CurrentManagedThreadId;
        public bool Finished, Completed;
    }
    private static GuestPresentationShutdownAttempt? _guestPresentationShutdownAttempt;
    private static int _guestPresentationShutdownJoinWaiters;
    private static bool _guestPresentationNativeOwnersDrained;

    private static void CompletePresenterShutdown()
    {
        GuestPresentationShutdownAttempt? attempt = null;
        lock (_gate)
        {
            WaitForInitialGuestCompletionLocked("presenter shutdown", allowFailedLifetime: true);
            // A successful receipt belongs to its closed presenter lifetime;
            // it cannot authorize teardown of a newly opened lifetime.
            if (!_closed)
            {
                _guestPresentationShutdownAttempt = null;
                _guestPresentationNativeOwnersDrained = false;
            }
            _closed = true;
            _thread = null;
            Monitor.PulseAll(_gate);
            if (_textureSourceGraphEnabled)
            {
                if (_guestPresentationShutdownAttempt is { Finished: false } running)
                {
                    JoinGuestPresentationShutdownLocked(running);
                    return; // This caller never owns native teardown.
                }
                if (_guestPresentationShutdownAttempt is { Completed: true }) return;
                attempt = new GuestPresentationShutdownAttempt();
                _guestPresentationShutdownAttempt = attempt;
            }
        }
        if (attempt is null) return; // Existing graph-disabled behavior.
        var completed = false;
        var cleanupFailed = false;
        try
        {
            // Revoke publication and cancellable CPU owners first. Active
            // render readers own independent pins, never force-returned here.
            lock (_gate)
                try { DropCanceledGuestQueueReferencesLocked(); }
                catch (Exception cleanupFailure)
                {
                    cleanupFailed = true;
                    _textureSourceCleanupFailure ??= cleanupFailure;
                    Console.Error.WriteLine($"[LOADER][ERROR] Metal shutdown CPU payload cleanup failed: {cleanupFailure}");
                }
            if (!WaitForGuestPresentationPayloadBorrows()) return;
            if (!_guestPresentationNativeOwnersDrained)
            {
                ShutdownTextureSourceNativeOwners();
                _guestPresentationNativeOwnersDrained = true;
            }
            _batchOpen = false;
            _batchCommandBuffer = 0;
            _executingTextureSourceWork = null;
            _touchedUploadPages.Clear();
            _uploadTouchStamp++;
            lock (_gate) DropCanceledGuestQueueReferencesLocked();
            completed = !cleanupFailed && _guestPayloadUnconfirmedReturnCount == 0;
        }
        finally
        {
            lock (_gate)
            {
                attempt.Completed = completed;
                attempt.Finished = true;
                Monitor.PulseAll(_gate);
            }
        }
    }

    // Only the accepted attempt can mutate its completion receipt. Joiners
    // wait outside the gate's critical section and never duplicate teardown.
    private static void JoinGuestPresentationShutdownLocked(GuestPresentationShutdownAttempt attempt)
    {
        var selfWait = attempt.ThreadId == Environment.CurrentManagedThreadId ||
            _activeGuestPresentationPayloadBorrows.Any(borrow => borrow.ThreadId == Environment.CurrentManagedThreadId);
        var deadline = Environment.TickCount64 + _guestPresentationBorrowShutdownTimeoutMilliseconds;
        _guestPresentationShutdownJoinWaiters++;
        try
        {
            while (!attempt.Finished)
            {
                var remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
                if (selfWait || remaining == 0)
                {
                    _textureSourceCleanupFailure ??= new InvalidOperationException(selfWait
                        ? "Metal concurrent shutdown cannot wait on its own active close/CPU borrow; accepted teardown remains in progress."
                        : "Metal concurrent shutdown timed out joining the accepted close; completion is still unconfirmed.");
                    Console.Error.WriteLine($"[LOADER][ERROR] {_textureSourceCleanupFailure.Message}");
                    return;
                }
                Monitor.Wait(_gate, remaining);
            }
        }
        finally { _guestPresentationShutdownJoinWaiters--; }
    }

    private static void DropCanceledGuestQueueReferencesLocked()
    {
        // Clear queue/presentation publication before returning accepted live
        // receipts. Taken work is included without consulting completed history.
        _latestPresentation = null;
        _pendingGuestWorkByQueue.Clear();
        _pendingGuestQueueSchedule.Clear();
        _lastEnqueuedGuestWorkByQueue.Clear();
        _pendingGuestQueueCursor = 0;
        _pendingGuestWorkCount = 0;
        _pendingGuestWorkBytes = 0;
        _immediateFollowupTail = null;
        ReleaseAllGuestPayloadOwnersLocked();
    }
}
