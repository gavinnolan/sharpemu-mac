// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private static void RequireQualifiedTextureSourceConsumerWork(object work)
    {
        lock (_gate)
            if (_textureSourceWorkOwners.TryGetValue(work, out var owner) && owner.Outputs.Length != 0)
                throw new NotSupportedException("Metal runtime texture-source producer encoding/post-encoder immutable copy is not qualified yet.");
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
        _pendingGuestWorkByQueue.Clear();
        _pendingGuestQueueSchedule.Clear();
        _lastEnqueuedGuestWorkByQueue.Clear();
        _pendingGuestQueueCursor = 0;
        _pendingGuestWorkCount = 0;
        _pendingGuestWorkBytes = 0;
        _immediateFollowupTail = null;
    }
}
