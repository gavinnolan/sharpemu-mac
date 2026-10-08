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
        catch (Exception cleanupFailure) { _textureSourceCleanupFailure = cleanupFailure; }
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
    private static void CompletePresenterShutdown()
    {
        lock (_gate)
        {
            _closed = true;
            _thread = null;
            Monitor.PulseAll(_gate);
        }
        if (_textureSourceGraphEnabled)
        {
            ShutdownTextureSourceNativeOwners();
            _batchOpen = false;
            _batchCommandBuffer = 0;
            _executingTextureSourceWork = null;
            _touchedUploadPages.Clear();
            _uploadTouchStamp++;
            lock (_gate) DropCanceledGuestQueueReferencesLocked();
        }
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
