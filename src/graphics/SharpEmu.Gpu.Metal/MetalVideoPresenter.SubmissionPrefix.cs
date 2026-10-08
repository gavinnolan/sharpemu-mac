// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.ExceptionServices;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    [ThreadStatic] private static GuestPrefixCapture? _guestPrefixCapture;
    [ThreadStatic] private static bool _guestPrefixExecution;
    [ThreadStatic] private static long _guestPrefixProducerSequence;
    [ThreadStatic] private static GuestQueueIdentity _guestPrefixProducerQueue;

    private static long GuestPrefixProducerForAdmission(long sequence, GuestQueueIdentity queue)
    {
        if (_guestPrefixCapture is { } capture)
        {
            if (capture.Queue != queue)
                throw new InvalidOperationException("Metal captured prefix cannot enroll another logical queue.");
            return sequence;
        }
        if (_guestPrefixProducerSequence == 0) return 0;
        if (_guestPrefixProducerQueue != queue)
            throw new InvalidOperationException("Metal completed prefix cannot enroll a renderer followup on another logical queue.");
        return _guestPrefixProducerSequence;
    }

    private static void RequireGuestPrefixExecution(bool success, string failure)
    {
        if (_guestPrefixExecution && !success)
            throw new InvalidOperationException($"Metal completed submission prefix cannot certify skipped work: {failure}.");
    }

    private sealed class GuestPrefixCapture(GuestQueueIdentity queue)
    {
        internal readonly Thread Owner = Thread.CurrentThread;
        internal readonly GuestQueueIdentity Queue = queue;
        internal long LastSequence;
    }

    internal static void BeginGuestSubmissionPrefixCapture(int queueId, ulong submissionId)
    {
        if (_guestPrefixCapture is not null)
            throw new InvalidOperationException("Metal submission-prefix capture cannot nest.");
        var name = queueId == 0 ? "dcb.graphics" : $"acb.compute[{GpuCommandInterpreter.ComputeQueueBase + queueId - 1}]";
        _guestPrefixCapture = new(new(name, submissionId));
    }

    private static void RecordGuestSubmissionPrefixAdmission(long sequence, GuestQueueIdentity queue)
    {
        if (_guestPrefixCapture is not { } capture) return;
        if (!ReferenceEquals(capture.Owner, Thread.CurrentThread) || capture.Queue != queue)
            throw new InvalidOperationException("Metal submission-prefix admission lost its exact worker/queue identity.");
        capture.LastSequence = sequence;
    }

    internal static IGuestSubmissionPrefixCompletion? EndGuestSubmissionPrefixCapture(bool complete)
    {
        var capture = _guestPrefixCapture;
        _guestPrefixCapture = null; // No admission/wait/error can leave a stale TLS capture.
        if (capture is null)
        {
            if (complete) throw new InvalidOperationException("Metal completed prefix has no exact capture.");
            return null;
        }
        if (!complete) return null;
        RequireGuestCompletionHealthy();
        if (capture.LastSequence == 0) return null;
        if (!ReferenceEquals(capture.Owner, Thread.CurrentThread))
            throw new InvalidOperationException("Metal submission-prefix completion lost its original worker.");
        var completion = new GuestPrefixCompletion(capture.Queue, capture.LastSequence);
        // Append before resolving the preparation receipt, on this exact logical
        // queue and accepted prefix. Never sample a later global/per-queue tail.
        using var queue = EnterGuestQueue(capture.Queue.Name, capture.Queue.SubmissionId);
        lock (_gate)
        {
            LinkedListNode<PendingGuestWork>? exactTail = null;
            if (_pendingGuestWorkByQueue.TryGetValue(capture.Queue.Name, out var pending))
                for (var node = pending.First; node is not null; node = node.Next)
                    if (node.Value.Queue == capture.Queue &&
                        (node.Value.Sequence == capture.LastSequence || node.Value.PrefixProducerSequence == capture.LastSequence))
                        exactTail = node; // Last exact descendant, never an unrelated queue tail.
            if ((_takenGuestWorkSequence > capture.LastSequence && _takenGuestPrefixProducerSequence != capture.LastSequence) ||
                (exactTail is null && !IsGuestWorkCompletedLocked(capture.LastSequence) &&
                 _takenGuestWorkSequence != capture.LastSequence && _takenGuestPrefixProducerSequence != capture.LastSequence))
                throw new InvalidOperationException("Metal cannot place an exact prefix completion behind an unknown or already executing later owner.");
            var previousImmediate = _enqueueAsImmediateQueueFollowup;
            var previousTail = _immediateFollowupTail;
            try
            {
                _enqueueAsImmediateQueueFollowup = true;
                _immediateFollowupTail = exactTail; // null inserts before unexecuted future work.
                completion.Sequence = EnqueueGuestWorkLocked(new OrderedGuestAction(
                    static () => WaitForFlushedGuestCommands(), "completed submission prefix"));
            }
            finally
            {
                _enqueueAsImmediateQueueFollowup = previousImmediate;
                _immediateFollowupTail = previousTail;
            }
        }
        if (completion.Sequence == 0)
            ExceptionDispatchInfo.Capture(LatchGuestCompletionFailure("completed submission prefix was not admitted")).Throw();
        return completion;
    }

    private sealed class GuestPrefixCompletion(GuestQueueIdentity queue, long lastPrefixSequence) : IGuestSubmissionPrefixCompletion
    {
        // This immutable identity is logical admission, not a native handle lease.
        internal readonly GuestQueueIdentity Queue = queue;
        internal readonly long LastPrefixSequence = lastPrefixSequence;
        internal long Sequence;
        public void WaitUntilCompleted()
        {
            if (_guestWorkDrainDepth != 0 || ReferenceEquals(_renderOwner, Thread.CurrentThread))
                ExceptionDispatchInfo.Capture(LatchGuestCompletionFailure("render owner cannot wait for its own completed prefix")).Throw();
            if (Sequence <= LastPrefixSequence || !WaitForGuestWork(Sequence, 30_000))
                ExceptionDispatchInfo.Capture(LatchGuestCompletionFailure("completed submission prefix failed or timed out")).Throw();
            RequireGuestCompletionHealthy();
        }
    }
}
