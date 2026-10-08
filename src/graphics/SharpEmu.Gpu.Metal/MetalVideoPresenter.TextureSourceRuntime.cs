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
        // Only still-queued work is unexecuted and owns its CPU payloads. A
        // completed CWT owner may point at an array already returned/rerented;
        // never use that history as authority to return it again.
        var arrays = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        var indices = new HashSet<GuestIndexBufferLease>(ReferenceEqualityComparer.Instance);
        foreach (var queue in _pendingGuestWorkByQueue.Values)
            foreach (var pending in queue)
                switch (pending.Work)
                {
                    case ComputeGuestDispatch compute:
                        foreach (var buffer in compute.GlobalMemoryBuffers)
                            if (buffer.Pooled) arrays.Add(buffer.Data);
                        break;
                    case OffscreenGuestDraw offscreen:
                        foreach (var buffer in offscreen.Draw.GlobalMemoryBuffers)
                            if (buffer.Pooled) arrays.Add(buffer.Data);
                        foreach (var buffer in offscreen.Draw.VertexBuffers)
                            if (buffer.Pooled) arrays.Add(buffer.Data);
                        if (offscreen.Draw.IndexBuffer is { Pooled: true } index)
                        {
                            if (index.Lease is { } lease) indices.Add(lease);
                            else arrays.Add(index.Data);
                        }
                        break;
                }
        // Record copies share the index lease. It owns its array return even if
        // another descriptor in the canceled batch references that same array.
        foreach (var queue in _pendingGuestWorkByQueue.Values)
            foreach (var pending in queue)
                if (pending.Work is OffscreenGuestDraw { Draw.IndexBuffer: { Pooled: true, Lease: not null } index })
                    arrays.Remove(index.Data);
        _pendingGuestWorkByQueue.Clear();
        _pendingGuestQueueSchedule.Clear();
        _lastEnqueuedGuestWorkByQueue.Clear();
        _pendingGuestQueueCursor = 0;
        _pendingGuestWorkCount = 0;
        _pendingGuestWorkBytes = 0;
        _immediateFollowupTail = null;
        // Clear queue ownership before returning arrays. Even a pool-cache
        // allocation failure must not permit retry to return a newer lease.
        Exception? failure = null;
        foreach (var index in indices)
            try { index.TryReturn(); _textureSourceAfterCanceledPayloadReturn?.Invoke(); } catch (Exception exception) { failure ??= exception; }
        foreach (var array in arrays)
            try { GuestDataPool.Shared.Return(array); _textureSourceAfterCanceledPayloadReturn?.Invoke(); } catch (Exception exception) { failure ??= exception; }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
