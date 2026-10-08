// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.ExceptionServices;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private sealed class InitialGuestCompletionReceipt
    {
        public readonly Thread Owner = Thread.CurrentThread;
        public readonly long Deadline = Environment.TickCount64 + _initialGuestCompletionTimeoutMilliseconds;
    }
    private static InitialGuestCompletionReceipt? _initialGuestCompletion;
    private static int _initialGuestCompletionTimeoutMilliseconds = 5000;
    private static int _initialGuestCompletionWaiters;

    // Presentation writers are not guest-queue producers. Replacement cannot
    // prove that an earlier render borrower has finished; keep scalar debt until
    // an actual ordered producer receipt exists (none in this implementation).
    private static long _unorderedPresentationWriteSequence;

    // Returns the accepted queue sequence, or zero only after the exact initial
    // callback finishes. Refusal always throws, never publishes a label/event.
    public static long SubmitOrderedGuestCompletion(Action action, string debugName)
    {
        ArgumentNullException.ThrowIfNull(action);
        InitialGuestCompletionReceipt? initial = null;
        Exception? admissionFailure = null;
        lock (_gate)
        {
            try
            {
                WaitForInitialGuestCompletionLocked(debugName);
                RequireNoUnorderedPresentationWritesLocked();
                if (IsInitialEmptyGuestCompletionLocked())
                    _initialGuestCompletion = initial = new InitialGuestCompletionReceipt();
                else if (!_closed && _guestWorkFailure is null && _thread is not null)
                {
                    var sequence = EnqueueGuestWorkLocked(new OrderedGuestAction(() =>
                    {
                        RequireGuestCompletionHealthy();
                        WaitForFlushedGuestCommands();
                        RequireGuestCompletionHealthy();
                        action();
                    }, debugName));
                    if (sequence != 0) return sequence;
                }
            }
            catch (Exception exception) { admissionFailure = exception; }
        }
        if (initial is null) throw FailGuestCompletion($"{debugName}: completion was not admitted", admissionFailure);
        try
        {
            // No callbacks under _gate. The exact receipt blocks overtaking
            // until finally, including failure; timeout never steals its owner.
            RequireGuestCompletionHealthy();
            action();
            RequireGuestCompletionHealthy();
        }
        catch (Exception exception) { throw FailGuestCompletion($"{debugName}: initial completion failed", exception); }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_initialGuestCompletion, initial)) _initialGuestCompletion = null;
                Monitor.PulseAll(_gate);
            }
        }
        return 0;
    }

    // Caller owns _gate. Monitor.Wait drops it; neither callbacks nor native
    // teardown run while the receipt is live. Self-reentry and deadline expiry
    // fail closed without clearing a callback that still owns guest memory.
    private static void WaitForInitialGuestCompletionLocked(string operation, bool allowFailedLifetime = false)
    {
        if (_initialGuestCompletion is null) return;
        var deadline = Environment.TickCount64 + _initialGuestCompletionTimeoutMilliseconds;
        _initialGuestCompletionWaiters++;
        try
        {
            while (_initialGuestCompletion is { } receipt)
            {
                var remaining = (int)Math.Max(0, Math.Min(deadline, receipt.Deadline) - Environment.TickCount64);
                if (ReferenceEquals(receipt.Owner, Thread.CurrentThread) || remaining == 0)
                {
                    var failure = _guestWorkFailure ?? new InvalidOperationException(
                        $"Metal {operation} cannot pass an in-flight initial guest completion: " +
                        (remaining == 0 ? "deadline expired; callback ownership remains live." : "self-reentry; callback ownership remains live."));
                    _guestWorkFailure ??= failure;
                    Monitor.PulseAll(_gate);
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }
                Monitor.Wait(_gate, remaining);
            }
            if (!allowFailedLifetime && _guestWorkFailure is { } previous)
                ExceptionDispatchInfo.Capture(previous).Throw();
        }
        finally { _initialGuestCompletionWaiters--; }
    }

    private static bool IsInitialEmptyGuestCompletionLocked() =>
        !_closed && _thread is null && _guestWorkFailure is null &&
        _enqueuedGuestWorkSequence == 0 && _completedGuestWorkSequence == 0 &&
        _pendingGuestWorkCount == 0 && _pendingGuestWorkBytes == 0 &&
        _pendingGuestWorkByQueue.Count == 0 && _pendingGuestQueueSchedule.Count == 0 &&
        _executingGuestWorkSequence == 0 && _device == 0 && _commandQueue == 0 &&
        !_batchOpen && _batchCommandBuffer == 0 && _lastFlushedCommandBuffer == 0;

    private static bool PresentationHasUnorderedGuestWrites(Presentation presentation) =>
        presentation.TranslatedDraw is { } draw &&
        (draw.GlobalMemoryBuffers.Any(buffer => buffer.Writable && buffer.WriteBackToGuest && buffer.Length != 0) ||
         draw.Textures.Any(texture => (texture.Access & (GuestImageAccess.Write | GuestImageAccess.Atomic)) != 0));

    private static void RequireNoUnorderedPresentationWritesLocked()
    {
        if (_unorderedPresentationWriteSequence != 0 ||
            _latestPresentation is { } presentation && PresentationHasUnorderedGuestWrites(presentation))
            throw new NotSupportedException($"Metal guest completion cannot certify an unordered writable presentation: presentation_sequence={_unorderedPresentationWriteSequence}; ordered presentation producer/writeback receipt is unavailable.");
    }

    internal static void RequireGuestCompletionHealthy()
    {
        lock (_gate)
        {
            if (_guestWorkFailure is { } previous) ExceptionDispatchInfo.Capture(previous).Throw();
            if (_closed) throw new InvalidOperationException("Metal guest completion cannot publish after presenter close.");
            RequireNoUnorderedPresentationWritesLocked();
        }
    }

    public static void RequireEmptyGuestCompletion(string reason)
    {
        Exception? failure = null;
        lock (_gate)
        {
            try
            {
                WaitForInitialGuestCompletionLocked(reason);
                RequireNoUnorderedPresentationWritesLocked();
                if (IsInitialEmptyGuestCompletionLocked()) return;
            }
            catch (Exception exception) { failure = exception; }
        }
        throw FailGuestCompletion(reason, failure);
    }

    public static Exception FailGuestCompletion(string reason, Exception? failure = null)
    {
        var cause = LatchGuestCompletionFailure(reason, failure);
        return GpuFatal.Fatal($"Metal guest completion failed: {reason}.", cause);
    }

    // Worker failure must wake/refuse presenter work promptly without invoking
    // FailFast before the render lifetime can unwind its owned native resources.
    internal static Exception LatchGuestCompletionFailure(string reason, Exception? failure = null)
    {
        Exception cause;
        lock (_gate)
        {
            cause = _guestWorkFailure ?? failure ?? new InvalidOperationException(reason);
            if (_guestWorkFailure is null)
            {
                if (_textureSourceGraphEnabled) LatchTextureSourceDrainFailureLocked(cause);
                else { _guestWorkFailure = cause; Monitor.PulseAll(_gate); }
            }
        }
        return cause;
    }
}
