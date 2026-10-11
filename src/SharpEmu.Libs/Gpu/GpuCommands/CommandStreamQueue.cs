// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Libs.Gpu.GpuCommands;

public enum IdleOutcome
{
    Completed,
    Cancelled,
    Failed,
}

public readonly record struct BlockedSnapshot(int Outstanding, double OldestAgeMilliseconds, ulong SampleWaitAddress, int SampleQueueId);

public enum SliceResult
{
    NoWork,
    AllBlocked,
    Progressed,
    BlockedWithoutProgress,
    Completed,
}

// Submissions of every queue; one thread runs the slices, other threads enqueue and wait.
public sealed class CommandStreamQueue
{
    private sealed record ControlBarrier(ulong CutoffOrdinal, Action Work, Action Cancel);

    public const int ComputeQueueCount = 56;
    public const int QueueCount = 1 + ComputeQueueCount;
    public const int AllBlockedRetryMilliseconds = 100;
    // How many frame boundaries the guest may be ahead of the worker. One by default;
    // explicit zero keeps the synchronous diagnostic mode, and a larger number is the
    // knob for a title that calls submitDone several times a frame and spends the frame
    // blocked in it - Poppy blocks for the whole wall clock across about seven calls a
    // frame while the worker sits idle for sixty per cent of it.
    public static readonly int DefaultBoundariesInFlight = ReadBoundariesInFlight();

    private static int ReadBoundariesInFlight()
    {
        var text = Environment.GetEnvironmentVariable("SHARPEMU_SUSPEND_POINTS_IN_FLIGHT");
        return !string.IsNullOrWhiteSpace(text) && int.TryParse(text, out var value) && value >= 0
            ? Math.Min(value, 64)
            : 1;
    }

    private readonly ICommandStreamHost _host;
    private readonly object _gate = new();
    private readonly LinkedList<CommandSubmission>[] _queues = new LinkedList<CommandSubmission>[QueueCount];
    private readonly GpuCommandInterpreter?[] _interpreters = new GpuCommandInterpreter?[QueueCount];
    private readonly Queue<ControlBarrier> _controlBarriers = new();
    private int _nextQueue;
    private int _submissionCount;
    private ulong _admissionOrdinal;
    private bool _accepting = true;
    private bool _stopping;
    private bool _processing;
    private bool _graphicsDone;
    private ulong _submitId;
    private ulong _lastCompletedGpuTick;
    private int _doneCount;
    private int _pendingBoundaries;
    private IdleOutcome _outcome = IdleOutcome.Completed;
    private Thread? _processingThread;
    private CommandSubmissionPreparation? _activePreparation;
    private Exception? _preparationFailure;

    private readonly int _boundariesInFlight;

    public CommandStreamQueue(ICommandStreamHost host) : this(host, DefaultBoundariesInFlight)
    {
    }

    public CommandStreamQueue(ICommandStreamHost host, int boundariesInFlight)
    {
        _host = host;
        _boundariesInFlight = Math.Max(0, boundariesInFlight);
        for (var index = 0; index < QueueCount; index++)
        {
            _queues[index] = new LinkedList<CommandSubmission>();
        }
    }

    public ulong SubmissionsStarted { get; private set; }

    public ulong SlicesRun { get; private set; }

    public ulong BlockedRetries { get; private set; }

    public int FrameNumber => Volatile.Read(ref _doneCount);

    public bool IsStopping
    {
        get
        {
            lock (_gate)
            {
                return _stopping;
            }
        }
    }

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _submissionCount != 0 || _controlBarriers.Count != 0;
            }
        }
    }

    public int PendingSubmissionCount
    {
        get
        {
            lock (_gate)
            {
                return _submissionCount;
            }
        }
    }

    public bool HasUnblockedPending
    {
        get
        {
            lock (_gate)
            {
                return HasRunnableWorkLocked();
            }
        }
    }

    public GpuCommandInterpreter GetInterpreter(int queueId)
    {
        if (queueId < 0 || queueId >= QueueCount)
        {
            throw _host.Fatal($"The queue id is outside the queue table: queue={queueId}.");
        }

        lock (_gate)
        {
            if (_interpreters[queueId] is { } existing)
            {
                return existing;
            }

            var created = new GpuCommandInterpreter(
                _host, queueId, queueId == 0 ? 0 : GpuCommandInterpreter.ComputeQueueBase + queueId - 1)
            {
                GuestMemoryWritten = NotifyGuestMemoryWritten,
                PendingLabelRecorded = NotePendingLabel,
                PendingLabelLookup = TryGetPendingLabel,
            };
            _interpreters[queueId] = created;
            return created;
        }
    }

    public void EnqueueGraphics(ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots)
    {
        if (dwordCount == 0)
        {
            return;
        }

        lock (_gate)
        {
            var submission = new CommandSubmission(CommandSubmissionKind.Graphics, 0, address, dwordCount, submissionId, geometrySnapshots)
            {
                ResetInterpreter = _graphicsDone,
            };
            _graphicsDone = false;
            EnqueueLocked(submission);
        }
    }

    // Compute queues are addressed 0x20 to 0x57; queue id 1 is the first of them.
    public void EnqueueCompute(uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots)
    {
        if (queue < GpuCommandInterpreter.ComputeQueueBase || queue >= GpuCommandInterpreter.ComputeQueueBase + ComputeQueueCount)
        {
            throw _host.Fatal($"The compute queue is outside the supported range: queue=0x{queue:X}.");
        }

        if (dwordCount == 0)
        {
            throw _host.Fatal($"A compute submission is empty: queue=0x{queue:X} address=0x{address:X16}.");
        }

        lock (_gate)
        {
            EnqueueLocked(new CommandSubmission(CommandSubmissionKind.Compute, (int)(queue - GpuCommandInterpreter.ComputeQueueBase) + 1, address, dwordCount, submissionId, geometrySnapshots));
        }
    }

    // Preparation is optional; old callers preserve admission/return behavior.
    public CommandSubmissionPreparation EnqueueWithPreparation(uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots)
    {
        if (queue != 0 && (queue < GpuCommandInterpreter.ComputeQueueBase || queue >= GpuCommandInterpreter.ComputeQueueBase + ComputeQueueCount))
            throw _host.Fatal($"The compute preparation queue is outside the supported range: queue=0x{queue:X}.");
        if (queue != 0 && dwordCount == 0)
            throw _host.Fatal($"A compute submission is empty: queue=0x{queue:X} address=0x{address:X16}.");
        var queueId = queue == 0 ? 0 : checked((int)(queue - GpuCommandInterpreter.ComputeQueueBase) + 1);
        lock (_gate)
        {
            var receipt = new CommandSubmissionPreparation(this, queueId, submissionId);
            if (dwordCount == 0)
            {
                if (!_accepting) throw new OperationCanceledException("Command preparation admission is closed.");
                receipt.Resolve(CommandSubmissionPreparation.State.PreparedPrefix);
                return receipt;
            }
            var submission = new CommandSubmission(queue == 0 ? CommandSubmissionKind.Graphics : CommandSubmissionKind.Compute,
                queueId, address, dwordCount, submissionId, geometrySnapshots)
            { Preparation = receipt, ResetInterpreter = queue == 0 && _graphicsDone };
            EnqueueLocked(submission);
            if (queue == 0) _graphicsDone = false;
            return receipt;
        }
    }

    internal GuestSubmissionPreparationResult WaitForPreparation(CommandSubmissionPreparation receipt)
    {
        Exception? failure = null;
        IGuestSubmissionPrefixCompletion? completion = null;
        var result = GuestSubmissionPreparationResult.DeferredDependency;
        lock (_gate)
        {
            if (_processing && ReferenceEquals(_processingThread, Thread.CurrentThread))
                failure = new InvalidOperationException("Command preparation cannot wait on its own command worker.");
            else
            {
                var deadline = Environment.TickCount64 + 30_000;
                while (receipt.Status == CommandSubmissionPreparation.State.Pending && _outcome == IdleOutcome.Completed)
                {
                    // CPU/other-queue dependencies must remain able to be submitted.
                    // Deferral certifies no shader/GPU work beyond the parsed prefix.
                    if (!_processing && _submissionCount != 0 && !HasRunnableWorkLocked())
                    { receipt.Resolve(CommandSubmissionPreparation.State.DeferredDependency); break; }
                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0)
                    { failure = new TimeoutException("Accepted command preparation did not reach a runnable-prefix boundary."); break; }
                    Monitor.Wait(_gate, (int)Math.Min(remaining, 1000));
                }
                failure ??= receipt.Failure ?? _preparationFailure;
                if (failure is null && receipt.Status == CommandSubmissionPreparation.State.Failed)
                    failure = new InvalidOperationException("Accepted command preparation failed before readiness.");
                if (failure is null && receipt.Status is CommandSubmissionPreparation.State.Pending or CommandSubmissionPreparation.State.Cancelled)
                    failure = new OperationCanceledException("Accepted command preparation was cancelled before readiness.");
            }
            if (receipt.Status == CommandSubmissionPreparation.State.PreparedPrefix)
            { result = GuestSubmissionPreparationResult.PreparedPrefix; completion = receipt.Completion; }
        }
        if (failure is not null)
        {
            if (failure is not OperationCanceledException)
                try { Fail(failure); }
                catch (Exception cleanup)
                { try { failure.Data["CommandPrefixWaitCleanup"] = cleanup; } catch { } }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        // Both AGC gates and this queue gate are free. Future queue slices stay
        // runnable while this exact caller waits for its accepted native prefix.
        try { completion?.WaitUntilCompleted(); }
        catch (Exception original)
        {
            try { Fail(original); }
            catch (Exception cleanup)
            { try { original.Data["CommandPrefixWaitCleanup"] = cleanup; } catch { } }
            throw;
        }
        return result;
    }

    // Queues a video-out export flip behind the graphics submissions accepted before it.
    public bool TryEnqueueFlipPreparation(int handle, int index, ulong requestId)
    {
        lock (_gate)
        {
            if (!_accepting)
            {
                return false;
            }

            EnqueueLocked(new CommandSubmission(CommandSubmissionKind.FlipPreparation, 0, 0, 0, 0, null)
            {
                FlipHandle = handle,
                FlipIndex = index,
                FlipRequestId = requestId,
            });
            return true;
        }
    }

    private void EnqueueLocked(CommandSubmission submission)
    {
        if (!_accepting)
        {
            if (_outcome != IdleOutcome.Failed)
            {
                throw new OperationCanceledException("The command stream has stopped accepting submissions.");
            }

            throw _host.Fatal($"The command stream no longer accepts submissions: queue={submission.QueueId} address=0x{submission.Address:X16}.");
        }

        submission.AdmissionOrdinal = ++_admissionOrdinal;
        _queues[submission.QueueId].AddLast(submission);
        _submissionCount++;
        if (submission.Kind is not (CommandSubmissionKind.FlipPreparation or CommandSubmissionKind.FrameBoundary))
            SubmissionFlowProfile.Record(SubmissionFlowProfile.EventKind.Enqueued, submission.QueueId,
                submission.SubmissionId, submission.Address, submission.DwordCount, _submissionCount);
        Monitor.PulseAll(_gate);
    }

    public bool TryEnqueueControlBarrier(Action work, Action cancel)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(cancel);
        lock (_gate)
        {
            if (!_accepting)
            {
                return false;
            }

            _controlBarriers.Enqueue(new ControlBarrier(_admissionOrdinal, work, cancel));
            Monitor.PulseAll(_gate);
            return true;
        }
    }

    public IdleOutcome Done()
    {
        if (_processingThread == Thread.CurrentThread)
        {
            lock (_gate)
            {
                _graphicsDone = true;
                _doneCount++;
            }

            return IdleOutcome.Completed;
        }

        lock (_gate)
        {
            if (_outcome != IdleOutcome.Completed)
            {
                return _outcome;
            }

            _graphicsDone = true;
            if (!_accepting)
            {
                // Shutdown: no slice will run a queued boundary, so drain as before.
                while (_outcome == IdleOutcome.Completed && (_processing || _submissionCount != 0))
                {
                    Monitor.Wait(_gate);
                }

                _doneCount++;
                return _outcome;
            }

            _pendingBoundaries++;
            EnqueueLocked(new CommandSubmission(CommandSubmissionKind.FrameBoundary, 0, 0, 0, 0, null));
            while (_outcome == IdleOutcome.Completed && _pendingBoundaries > _boundariesInFlight)
            {
                Monitor.Wait(_gate);
            }

            return _outcome;
        }
    }

    // Returns once nothing is pending; a cancelled or failed queue reports that instead.
    public IdleOutcome WaitForIdle()
    {
        lock (_gate)
        {
            while (_outcome == IdleOutcome.Completed &&
                   (_processing || _submissionCount != 0 || _controlBarriers.Count != 0))
            {
                Monitor.Wait(_gate);
            }

            return _outcome;
        }
    }

    public void Wake()
    {
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }
    }

    // Waits until a submission can run or the timeout passes; returns whether one can run.
    public bool WaitForWork(int timeoutMilliseconds)
    {
        lock (_gate)
        {
            if (HasRunnableWorkLocked() || _stopping)
            {
                return HasRunnableWorkLocked();
            }

            Monitor.Wait(_gate, timeoutMilliseconds);
            return HasRunnableWorkLocked();
        }
    }

    // Waits the whole interval unless a submission becomes runnable; stopping does not cut it short.
    public bool WaitForRetryInterval(int intervalMilliseconds)
    {
        lock (_gate)
        {
            var deadline = Environment.TickCount64 + intervalMilliseconds;
            while (!HasRunnableWorkLocked())
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    return false;
                }

                Monitor.Wait(_gate, (int)remaining);
            }

            return true;
        }
    }

    // Publishes a failure: nothing runs any more and every waiter is released with Failed.
    public void Fail(Exception? original = null)
    {
        Action[] cancellations;
        lock (_gate)
        {
            _preparationFailure ??= original;
            FailLocked();
            cancellations = CancelControlBarriersLocked();
        }

        Exception? cleanupFailure = null;
        foreach (var cancel in cancellations)
        {
            try { cancel(); }
            catch (Exception cleanup)
            {
                if (original is null) cleanupFailure ??= cleanup;
                else
                {
                    try
                    {
                        if (original.Data["CommandCancellationCleanup"] is Exception previous)
                            original.Data["CommandCancellationCleanup"] = new AggregateException(previous, cleanup);
                        else original.Data["CommandCancellationCleanup"] = cleanup;
                    }
                    catch { /* Diagnostic attachment cannot replace the first failure. */ }
                }
            }
        }
        if (cleanupFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private void FailLocked()
    {
        _outcome = IdleOutcome.Failed;
        _accepting = false;
        _stopping = true;
        DropAllLocked();
        _activePreparation?.Resolve(CommandSubmissionPreparation.State.Failed, _preparationFailure);
        _activePreparation = null;
        _processing = false;
        Monitor.PulseAll(_gate);
    }

    // A completed GPU tick can make a blocked memory wait ready. A flip slot or a
    // presented flip is not something a tick decides, so those heads stay asleep.
    // Labels a queue recorded for the GPU to write and the tick they land by, keyed by
    // address. A wait on another queue reads them as written: async compute waiting on a
    // graphics fence would otherwise see it only once the GPU had written it and the guest
    // had been told, and the guest recycles a fence slot as soon as it is told - by then
    // the waiter reads someone else's bytes and never sees the value it was waiting for.
    private readonly Dictionary<ulong, (ulong Value, int Size, ulong Tick)> _pendingLabels = new();

    private void NotePendingLabel(ulong address, int size, ulong value)
    {
        var tick = _host.PendingLabelTick;
        if (tick == 0)
        {
            return;
        }

        lock (_gate)
        {
            _pendingLabels[address] = (value, size, tick);
            WakeMatchingLocked(wait => wait.Kind == CommandWaitKind.Unknown || wait.TouchedBy(address, (ulong)size));
        }
    }

    private bool TryGetPendingLabel(ulong address, bool is64Bit, out ulong value)
    {
        lock (_gate)
        {
            if (_pendingLabels.TryGetValue(address, out var label) && label.Size >= (is64Bit ? 8 : 4))
            {
                value = is64Bit ? label.Value : (uint)label.Value;
                return true;
            }

            // A 32-bit wait on the upper half of a 64-bit label.
            if (!is64Bit && address >= 4 && _pendingLabels.TryGetValue(address - 4, out label) && label.Size == 8)
            {
                value = (uint)(label.Value >> 32);
                return true;
            }

            value = 0;
            return false;
        }
    }

    public void NotifyCompletedGpuTick(ulong completedTick)
    {
        lock (_gate)
        {
            if (completedTick <= _lastCompletedGpuTick)
            {
                return;
            }

            _lastCompletedGpuTick = completedTick;
            if (_pendingLabels.Count != 0)
            {
                // A landed label is in memory now, where the guest may already have reused it.
                List<ulong>? landed = null;
                foreach (var (address, label) in _pendingLabels)
                {
                    if (label.Tick <= completedTick)
                    {
                        (landed ??= []).Add(address);
                    }
                }

                if (landed is not null)
                {
                    foreach (var address in landed)
                    {
                        _pendingLabels.Remove(address);
                    }
                }
            }

            WakeMatchingLocked(wait => wait.Kind is CommandWaitKind.GuestMemory or
                CommandWaitKind.EngineCounter or CommandWaitKind.Unknown);
        }
    }

    /// <summary>
    /// Wakes every blocked head. This is the fallback: it is what a timer does when
    /// nothing told us which wait could have changed, and what a guest CPU write we
    /// cannot observe is eventually caught by.
    /// </summary>
    public void RetryBlocked()
    {
        lock (_gate)
        {
            foreach (var queue in _queues)
            {
                if (queue.First is { } head)
                {
                    head.Value.Blocked = false;
                }
            }

            BlockedRetries++;
            if (PacketProfile.Enabled)
            {
                PacketProfile.RecordRetryAll();
            }

            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>How many heads a targeted wake has unblocked since startup.</summary>
    public ulong TargetedWakes { get; private set; }

    /// <summary>How many blocked heads a targeted wake has left alone.</summary>
    public ulong TargetedWakesSkipped { get; private set; }

    // Every wake event, whether or not a parked head matched it. A slice running at the
    // time has not recorded what it waits for yet, so the event cannot reach it; the park
    // compares this instead and keeps the head runnable.
    private ulong _wakeGeneration;

    /// <summary>Wake events that have been delivered; a slice compares it at its park.</summary>
    public ulong WakeGeneration
    {
        get
        {
            lock (_gate)
            {
                return _wakeGeneration;
            }
        }
    }

    /// <summary>Heads kept runnable because their wake arrived while they still ran.</summary>
    public ulong WakesRacedWithSlice { get; private set; }

    // Caller holds the gate. Wakes the heads a condition predicate selects.
    private void WakeMatchingLocked(Func<CommandWaitCondition, bool> matches)
    {
        _wakeGeneration++;
        var woke = false;
        var wokeCount = 0L;
        var skippedCount = 0L;
        foreach (var queue in _queues)
        {
            if (queue.First is not { } head || !head.Value.Blocked)
            {
                continue;
            }

            if (matches(head.Value.Wait))
            {
                head.Value.Blocked = false;
                TargetedWakes++;
                wokeCount++;
                woke = true;
            }
            else
            {
                TargetedWakesSkipped++;
                skippedCount++;
            }
        }

        if (PacketProfile.Enabled)
        {
            PacketProfile.RecordWakes(wokeCount, skippedCount);
        }

        if (woke)
        {
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// These guest bytes changed, so a compare against them may pass now. Everything
    /// that writes guest memory on the command-stream thread reports through here:
    /// the interpreter's own label stores, the memory operations it executes, and the
    /// backend's end-of-pipe writes.
    /// </summary>
    public void NotifyGuestMemoryWritten(ulong address, ulong size)
    {
        if (size == 0 || _submissionCount == 0)
        {
            return;
        }

        lock (_gate)
        {
            WakeMatchingLocked(wait => wait.Kind == CommandWaitKind.Unknown || wait.TouchedBy(address, size));
        }
    }

    /// <summary>
    /// A pipeline the backend was compiling is ready, so the draws and dispatches that
    /// suspended on one can run. They re-check their own pipeline when they resume.
    /// </summary>
    public void NotifyPipelineReady()
    {
        lock (_gate)
        {
            WakeMatchingLocked(wait => wait.Kind is CommandWaitKind.Pipeline or CommandWaitKind.Unknown);
        }
    }

    /// <summary>The presenter freed a flip slot.</summary>
    public void NotifyFlipSlotAvailable()
    {
        lock (_gate)
        {
            WakeMatchingLocked(wait =>
                wait.Kind is CommandWaitKind.FlipSlot or CommandWaitKind.FlipDone or CommandWaitKind.Unknown);
        }
    }

    // Runs one slice of the next unblocked submission on the calling thread.
    public SliceResult ProcessOne()
    {
        CommandSubmission? submission = null;
        ControlBarrier? control = null;
        var sliceWakeGeneration = 0UL;
        lock (_gate)
        {
            if (_processing)
            {
                throw _host.Fatal("The command stream queue is already processing a slice.");
            }

            if (IsControlBarrierReadyLocked())
            {
                control = _controlBarriers.Dequeue();
            }
            else
            {
                var selected = SelectQueueLocked();
                if (selected < 0)
                {
                    if (_submissionCount != 0)
                        foreach (var pendingQueue in _queues)
                            foreach (var pending in pendingQueue)
                                pending.Preparation?.Resolve(CommandSubmissionPreparation.State.DeferredDependency);
                    Monitor.PulseAll(_gate);
                    return _submissionCount != 0 ? SliceResult.AllBlocked : SliceResult.NoWork;
                }

                var queue = _queues[selected];
                submission = queue.First!.Value;
                queue.RemoveFirst();
                _submissionCount--;
                _nextQueue = (selected + 1) % QueueCount;
            }

            _processing = true;
            _processingThread = Thread.CurrentThread;
            _activePreparation = submission?.Preparation;
            // What this slice is about to wait for may change while it runs, and the wake
            // for it arrives at a head that is not parked yet. The generation it starts
            // from decides, at the park below, whether it missed one.
            sliceWakeGeneration = _wakeGeneration;
            if (submission is { Started: false } &&
                submission.Kind is not (CommandSubmissionKind.FlipPreparation or CommandSubmissionKind.FrameBoundary))
                SubmissionFlowProfile.Record(SubmissionFlowProfile.EventKind.ProcessingStarted, submission.QueueId,
                    submission.SubmissionId, submission.Address, submission.DwordCount, _submissionCount);
        }

        if (control is not null)
        {
            try
            {
                control.Work();
            }
            catch (Exception exception)
            {
                try
                {
                    Console.Error.WriteLine($"[GPU][ERROR] Command stream control barrier failed. {exception}");
                }
                catch (Exception)
                {
                }

                Fail(exception);
                throw;
            }

            lock (_gate)
            {
                _processing = false;
                Monitor.PulseAll(_gate);
            }

            return SliceResult.Completed;
        }

        bool complete;
        IGuestSubmissionPrefixCompletion? prefixCompletion = null;
        ICommandSubmissionCompletionHost? completionHost;
        lock (_gate)
            completionHost = submission!.Preparation is { Status: CommandSubmissionPreparation.State.Pending }
                ? _host as ICommandSubmissionCompletionHost : null;
        var captureStarted = false;
        try
        {
            if (completionHost is not null)
            {
                completionHost.BeginSubmissionCompletionCapture(submission!.QueueId, submission.SubmissionId);
                captureStarted = true;
            }
            complete = RunSlice(submission!);
            if (captureStarted)
            {
                prefixCompletion = completionHost!.EndSubmissionCompletionCapture(complete);
                captureStarted = false;
            }
        }
        catch (Exception exception)
        {
            if (captureStarted)
                try { _ = completionHost!.EndSubmissionCompletionCapture(false); }
                catch (Exception cleanup)
                { try { exception.Data["CommandPrefixCaptureCleanup"] = cleanup; } catch { } }
            try
            {
                // Report the cause before another submitting thread observes the failed queue.
                Console.Error.WriteLine($"[GPU][ERROR] Command stream slice failed: queue={submission!.QueueId} address=0x{submission.Address:X16}. {exception}");
            }
            catch (Exception)
            {
                // An output failure must not prevent queue failure or replace the original exception.
            }

            Fail(exception);
            throw;
        }

        lock (_gate)
        {
            submission!.Preparation?.Resolve(complete ? CommandSubmissionPreparation.State.PreparedPrefix : CommandSubmissionPreparation.State.DeferredDependency,
                completion: complete ? prefixCompletion : null);
            _activePreparation = null;
            SliceResult result;
            if (!complete)
            {
                // A wake that arrived while this slice ran was delivered to a head that was
                // not parked yet, so parking now would sleep until the retry timer: the
                // slice re-runs instead and reads what changed. Polling a label the GPU is
                // about to write is exactly this race, and the timer is a hundred
                // milliseconds against a submission that completes in one.
                var raced = _wakeGeneration != sliceWakeGeneration &&
                    submission!.RacedWakeGeneration != _wakeGeneration;
                submission!.Blocked = !raced;
                if (raced)
                {
                    // One more run for this wake, and only one: a head that re-ran on
                    // every wake would never park while completions keep arriving.
                    submission.RacedWakeGeneration = _wakeGeneration;
                    WakesRacedWithSlice++;
                }

                submission.BlockedSinceTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                var packetAddress = submission.Commands.CurrentPacketAddress;
                if (submission.StalledSinceTicks == 0 || submission.BlockedPacketAddress != packetAddress)
                {
                    submission.StalledSinceTicks = submission.BlockedSinceTicks;
                    submission.StallReported = false;
                }

                submission.BlockedPacketAddress = packetAddress;
                submission.Wait = submission.Commands.SuspendCondition;
                _queues[submission.QueueId].AddFirst(submission);
                _submissionCount++;
                result = submission.Commands.MadeProgress ? SliceResult.Progressed : SliceResult.BlockedWithoutProgress;
            }
            else
            {
                if (submission.Kind == CommandSubmissionKind.FrameBoundary)
                {
                    _pendingBoundaries--;
                    _doneCount++;
                }

                // A finished submission frees a flip slot and advances the engine
                // counters of its own queue, and anything it wrote into guest memory
                // has already been reported through NotifyGuestMemoryWritten. Waking
                // every head here instead made each one re-run a slice, re-read its
                // label and suspend again.
                WakeMatchingLocked(wait => wait.Kind is not CommandWaitKind.GuestMemory);
                result = SliceResult.Completed;
            }

            _processing = false;
            Monitor.PulseAll(_gate);
            return result;
        }
    }

    private bool RunSlice(CommandSubmission submission)
    {
        if (submission.Kind == CommandSubmissionKind.FlipPreparation)
        {
            SlicesRun++;
            _host.PrepareCpuFlip(submission.FlipHandle, submission.FlipIndex, submission.FlipRequestId);
            _host.Flush();
            return true;
        }

        // A suspend point drains the graphics pipe: submit what the frame recorded.
        if (submission.Kind == CommandSubmissionKind.FrameBoundary)
        {
            SlicesRun++;
            _host.Flush();
            return true;
        }

        var firstSlice = !submission.Started;
        var processor = GetInterpreter(submission.QueueId);
        if (firstSlice && submission.ResetInterpreter)
        {
            processor.Reset();
        }

        if (firstSlice)
        {
            submission.Started = true;
            processor.SubmitId = ++_submitId;
            processor.ResetCounters();
            processor.SetFlip(default);
            SubmissionsStarted++;
        }

        SlicesRun++;
        var sliceStarted = PacketProfile.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        _host.BeginSubmission(submission.QueueId, submission.SubmissionId, submission.GeometrySnapshots);
        processor.ConstantEngineComplete = true;
        var complete = processor.Process(submission.Commands, submission.Address, submission.DwordCount) == SubmissionProgress.Complete;
        if (PacketProfile.Enabled)
        {
            PacketProfile.RecordSlice(System.Diagnostics.Stopwatch.GetTimestamp() - sliceStarted, !complete);
            PacketProfile.ReportIfDue();
        }

        submission.CommandsComplete = complete;
        if (submission.Commands.MadeProgress)
        {
            if (complete)
            {
                _host.RunGarbageCollector();
            }

            _host.Flush();
        }
        else if (complete)
        {
            _host.RunGarbageCollector();
        }

        return complete;
    }

    public void StopAccepting()
    {
        lock (_gate)
        {
            _accepting = false;
            _stopping = true;
            Monitor.PulseAll(_gate);
        }
    }

    // Processes to empty; with the flag, heads still blocked after a full retry cycle are cancelled.
    public IdleOutcome DrainForShutdown(bool cancelBlockedOnNoProgress)
    {
        for (;;)
        {
            var result = ProcessOne();
            switch (result)
            {
                case SliceResult.NoWork:
                    return Outcome;
                case SliceResult.Progressed:
                case SliceResult.Completed:
                    continue;
                case SliceResult.BlockedWithoutProgress:
                    if (HasUnblockedPending)
                    {
                        continue;
                    }

                    goto case SliceResult.AllBlocked;
                case SliceResult.AllBlocked:
                    _ = WaitForRetryInterval(AllBlockedRetryMilliseconds);
                    RetryBlocked();
                    if (!RunRetryCycle() && cancelBlockedOnNoProgress)
                    {
                        CancelBlocked();
                    }

                    continue;
            }
        }
    }

    // One pass over every head after a retry; true when any slice made progress.
    private bool RunRetryCycle()
    {
        var progressed = false;
        while (HasUnblockedPending)
        {
            var result = ProcessOne();
            if (result is SliceResult.Progressed or SliceResult.Completed)
            {
                progressed = true;
            }

            if (result is SliceResult.NoWork or SliceResult.AllBlocked)
            {
                break;
            }
        }

        return progressed;
    }

    private void CancelBlocked()
    {
        lock (_gate)
        {
            foreach (var queue in _queues)
            {
                if (queue.First is { } head && head.Value.Blocked)
                {
                    _submissionCount -= queue.Count;
                    foreach (var dropped in queue)
                    {
                        if (dropped.Kind == CommandSubmissionKind.FrameBoundary)
                        {
                            _pendingBoundaries--;
                        }
                    }

                    foreach (var dropped in queue) dropped.Preparation?.Resolve(CommandSubmissionPreparation.State.Cancelled);
                    queue.Clear();
                    _outcome = IdleOutcome.Cancelled;
                }
            }

            Monitor.PulseAll(_gate);
        }
    }

    // Drops every submission without stopping admission; the presenter uses it after device loss.
    public void DiscardAll()
    {
        Action[] cancellations;
        lock (_gate)
        {
            _outcome = IdleOutcome.Failed;
            _activePreparation?.Resolve(CommandSubmissionPreparation.State.Failed, _preparationFailure);
            DropAllLocked();
            cancellations = CancelControlBarriersLocked();
            Monitor.PulseAll(_gate);
        }

        foreach (var cancel in cancellations)
            cancel();
    }

    // Heads that have sat on the same packet for at least minimumMilliseconds and have not
    // been reported yet: each is named once per stall, so a deadlock says what it is
    // waiting for instead of looking like an idle GPU.
    public IReadOnlyList<(int QueueId, ulong SubmissionId, CommandSubmissionKind Kind, ulong PacketAddress,
        CommandWaitCondition Wait, double Milliseconds)> TakeNewStalls(double minimumMilliseconds)
    {
        List<(int, ulong, CommandSubmissionKind, ulong, CommandWaitCondition, double)>? stalls = null;
        lock (_gate)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var queueId = 0; queueId < QueueCount; queueId++)
            {
                if (_queues[queueId].First is not { } head || !head.Value.Blocked || head.Value.StallReported ||
                    head.Value.StalledSinceTicks == 0)
                {
                    continue;
                }

                var milliseconds = (now - head.Value.StalledSinceTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (milliseconds < minimumMilliseconds)
                {
                    continue;
                }

                head.Value.StallReported = true;
                (stalls ??= []).Add((queueId, head.Value.SubmissionId, head.Value.Kind,
                    head.Value.BlockedPacketAddress, head.Value.Wait, milliseconds));
            }
        }

        return (IReadOnlyList<(int, ulong, CommandSubmissionKind, ulong, CommandWaitCondition, double)>?)stalls ?? [];
    }

    // A diagnostic view of the blocked heads: their count, the oldest age and one sample.
    public BlockedSnapshot SnapshotBlocked()
    {
        lock (_gate)
        {
            var count = 0;
            var oldestTicks = 0L;
            var sampleAddress = 0UL;
            var sampleQueue = -1;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var queueId = 0; queueId < QueueCount; queueId++)
            {
                if (_queues[queueId].First is not { } head || !head.Value.Blocked)
                {
                    continue;
                }

                count++;
                var age = now - head.Value.BlockedSinceTicks;
                if (sampleQueue < 0 || age > oldestTicks)
                {
                    oldestTicks = age;
                    sampleAddress = head.Value.BlockedPacketAddress;
                    sampleQueue = queueId;
                }
            }

            return new BlockedSnapshot(count, oldestTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, sampleAddress, sampleQueue);
        }
    }

    public IdleOutcome Outcome
    {
        get
        {
            lock (_gate)
            {
                return _outcome;
            }
        }
    }

    public int BlockedQueueCount
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                foreach (var queue in _queues)
                {
                    if (queue.First is { } head && head.Value.Blocked)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    private int SelectQueueLocked()
    {
        if (_controlBarriers.TryPeek(out var control))
        {
            for (var offset = 0; offset < QueueCount; offset++)
            {
                var queueId = (_nextQueue + offset) % QueueCount;
                if (_queues[queueId].First is { } head &&
                    head.Value.AdmissionOrdinal <= control.CutoffOrdinal &&
                    !head.Value.Blocked)
                {
                    return queueId;
                }
            }
        }

        for (var offset = 0; offset < QueueCount; offset++)
        {
            var queueId = (_nextQueue + offset) % QueueCount;
            if (_queues[queueId].First is { } head && !head.Value.Blocked)
            {
                return queueId;
            }
        }

        return -1;
    }

    private bool HasRunnableWorkLocked() => IsControlBarrierReadyLocked() || SelectQueueLocked() >= 0;

    private bool IsControlBarrierReadyLocked()
    {
        if (!_controlBarriers.TryPeek(out var control))
        {
            return false;
        }

        foreach (var queue in _queues)
        {
            if (queue.First is { } head && head.Value.AdmissionOrdinal <= control.CutoffOrdinal)
            {
                return false;
            }
        }

        return true;
    }

    private Action[] CancelControlBarriersLocked()
    {
        if (_controlBarriers.Count == 0)
        {
            return [];
        }

        var cancellations = new Action[_controlBarriers.Count];
        for (var index = 0; index < cancellations.Length; index++)
        {
            cancellations[index] = _controlBarriers.Dequeue().Cancel;
        }

        return cancellations;
    }

    private void DropAllLocked()
    {
        foreach (var queue in _queues)
        {
            foreach (var dropped in queue) dropped.Preparation?.Resolve(
                _outcome == IdleOutcome.Failed ? CommandSubmissionPreparation.State.Failed : CommandSubmissionPreparation.State.Cancelled, _preparationFailure);
            queue.Clear();
        }

        _submissionCount = 0;
        _pendingBoundaries = 0;
    }
}
