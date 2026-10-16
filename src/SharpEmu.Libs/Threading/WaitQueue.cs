// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Threading;

/// <summary>How a wait ended. <see cref="Pending"/> only while the waiter is still queued.</summary>
public enum GuestWaitStatus
{
    Pending = 0,
    Satisfied = 1,
    TimedOut = 2,
    Canceled = 3,
    Deleted = 4,
    Interrupted = 5,
}

/// <summary>Order in which a <see cref="WaitQueue"/> hands waiters to its policy.</summary>
public enum WaitQueueOrder
{
    /// <summary>Arrival order.</summary>
    Fifo,

    /// <summary>Guest priority (lower value first), arrival order within a priority.</summary>
    Priority,
}

/// <summary>Policy hook that re-evaluates a queue after a waiter left it without being satisfied.</summary>
public delegate void WaitQueueReevaluate(WaitQueue queue, ref WakeBatch batch);

/// <summary>
/// The wait/wake core every guest synchronisation object is built on: an intrusive list of
/// <see cref="GuestWaiter"/>s under one lock (<see cref="Gate"/>, which the owning object
/// also uses for its own state), an epoch, a deleted flag and per-waiter 64-bit deadlines
/// served by <see cref="GuestTimerService"/>.
///
/// <para>Protocol. A policy layer (mutex, semaphore, event flag, ...) takes <see cref="Gate"/>,
/// checks its condition, and if it must wait enqueues a waiter and arms its deadline while
/// still holding the lock. Every completion — satisfy, timeout, cancel, delete — is decided
/// under the same lock by <see cref="GuestWaiter.TryCompleteLocked"/>, which removes the
/// waiter from the queue. The completer collects completed waiters in a
/// <see cref="WakeBatch"/> and wakes them after releasing the lock: a cooperative guest
/// thread through a direct scheduler wake (no global scan, no string keys, no predicate run
/// under the scheduler gate), a host thread through its own event. The waiter then reads
/// its <see cref="GuestWaiter.Status"/> and the policy's result fields.</para>
/// </summary>
public sealed class WaitQueue
{
    private GuestWaiter? _head;
    private GuestWaiter? _tail;
    private int _count;

    public WaitQueue(WaitQueueOrder order = WaitQueueOrder.Fifo)
    {
        Order = order;
    }

    /// <summary>The lock guarding the queue and its owner's state.</summary>
    public Lock Gate { get; } = new();

    public WaitQueueOrder Order { get; set; }

    /// <summary>The sync object this queue belongs to (for <see cref="Reevaluate"/> hooks).</summary>
    public object? Owner { get; set; }

    /// <summary>Bumped by the owner on cancel/reset; lets a waiter tell which generation it joined.</summary>
    public long Epoch { get; set; }

    /// <summary>Set once by the owner's delete; no new waiter may join afterwards.</summary>
    public bool Deleted { get; set; }

    /// <summary>Waiters currently queued.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>The head waiter (caller holds <see cref="Gate"/>).</summary>
    public GuestWaiter? First => _head;

    /// <summary>
    /// Called under <see cref="Gate"/> after a waiter left without being satisfied (timeout,
    /// interrupt), so a policy whose head blocked others (a large semaphore request, a
    /// SINGLE event flag) can satisfy the waiters behind it.
    /// </summary>
    public WaitQueueReevaluate? Reevaluate { get; set; }

    /// <summary>Appends <paramref name="waiter"/> in queue order (caller holds <see cref="Gate"/>).</summary>
    public void EnqueueLocked(GuestWaiter waiter)
    {
        if (waiter.Queue is not null)
        {
            throw new InvalidOperationException("The waiter is already queued.");
        }

        waiter.Queue = this;
        waiter.QueueEpoch = Epoch;
        if (Order == WaitQueueOrder.Priority)
        {
            for (var cursor = _head; cursor is not null; cursor = cursor.Next)
            {
                if (waiter.Priority < cursor.Priority)
                {
                    waiter.Prev = cursor.Prev;
                    waiter.Next = cursor;
                    if (cursor.Prev is null)
                    {
                        _head = waiter;
                    }
                    else
                    {
                        cursor.Prev.Next = waiter;
                    }

                    cursor.Prev = waiter;
                    _count++;
                    return;
                }
            }
        }

        waiter.Prev = _tail;
        waiter.Next = null;
        if (_tail is null)
        {
            _head = waiter;
        }
        else
        {
            _tail.Next = waiter;
        }

        _tail = waiter;
        _count++;
    }

    /// <summary>Unlinks <paramref name="waiter"/> without completing it (caller holds <see cref="Gate"/>).</summary>
    public bool RemoveLocked(GuestWaiter waiter)
    {
        if (!ReferenceEquals(waiter.Queue, this))
        {
            return false;
        }

        if (waiter.Prev is null)
        {
            _head = waiter.Next;
        }
        else
        {
            waiter.Prev.Next = waiter.Next;
        }

        if (waiter.Next is null)
        {
            _tail = waiter.Prev;
        }
        else
        {
            waiter.Next.Prev = waiter.Prev;
        }

        waiter.Prev = null;
        waiter.Next = null;
        waiter.Queue = null;
        _count--;
        return true;
    }

    /// <summary>Completes every queued waiter with <paramref name="status"/> (cancel, delete).</summary>
    public int CompleteAllLocked(GuestWaitStatus status, ref WakeBatch batch)
    {
        var completed = 0;
        while (_head is { } waiter)
        {
            if (waiter.TryCompleteLocked(status))
            {
                batch.Add(waiter);
                completed++;
            }
            else
            {
                RemoveLocked(waiter);
            }
        }

        return completed;
    }

    /// <summary>
    /// Completes the head waiter with <see cref="GuestWaitStatus.Satisfied"/>. Returns it,
    /// or null for an empty queue. The caller wakes it after releasing <see cref="Gate"/>.
    /// </summary>
    public GuestWaiter? SatisfyFirstLocked()
    {
        var head = _head;
        if (head is null)
        {
            return null;
        }

        return head.TryCompleteLocked(GuestWaitStatus.Satisfied) ? head : null;
    }

    internal void RunReevaluateLocked(ref WakeBatch batch) => Reevaluate?.Invoke(this, ref batch);
}

/// <summary>
/// A small on-stack collection of completed waiters to wake once the queue lock is released.
/// </summary>
public struct WakeBatch
{
    private GuestWaiter? _w0;
    private GuestWaiter? _w1;
    private GuestWaiter? _w2;
    private GuestWaiter? _w3;
    private List<GuestWaiter>? _more;

    public int Count { get; private set; }

    public void Add(GuestWaiter waiter)
    {
        switch (Count)
        {
            case 0: _w0 = waiter; break;
            case 1: _w1 = waiter; break;
            case 2: _w2 = waiter; break;
            case 3: _w3 = waiter; break;
            default: (_more ??= new List<GuestWaiter>()).Add(waiter); break;
        }

        Count++;
    }

    /// <summary>Wakes every collected waiter; call with no queue lock held.</summary>
    public void WakeAll()
    {
        if (Count == 0)
        {
            return;
        }

        _w0?.Wake();
        _w1?.Wake();
        _w2?.Wake();
        _w3?.Wake();
        if (_more is not null)
        {
            foreach (var waiter in _more)
            {
                waiter.Wake();
            }
        }

        this = default;
    }
}

/// <summary>
/// One blocked wait. It is the scheduler's <see cref="IGuestThreadBlockWaiter"/> for a
/// cooperative guest thread (TryWake only reports "completed", Resume runs the policy's
/// <see cref="ResumeHandler"/>) and owns the event a host thread sleeps on otherwise.
/// Policy layers derive from it to carry their own per-wait data.
/// </summary>
public class GuestWaiter : IGuestThreadBlockWaiter
{
    private int _status;
    private ManualResetEventSlim? _hostEvent;
    private GuestTimer? _timer;
    private long _completedTimestamp;
    private string? _wakeKey;

    // Guarded by Queue.Gate.
    internal WaitQueue? Queue;
    internal GuestWaiter? Prev;
    internal GuestWaiter? Next;
    internal long QueueEpoch;

    /// <summary>A waiter for the calling thread: cooperative when the thread can park in the guest scheduler.</summary>
    public GuestWaiter(int priority = 0)
        : this(GuestWait.CanBlockCooperatively, GuestThreadExecution.CurrentGuestThreadHandle, priority)
    {
    }

    public GuestWaiter(bool cooperative, ulong schedulerThreadHandle, int priority = 0)
    {
        Cooperative = cooperative && schedulerThreadHandle != 0;
        SchedulerThreadHandle = schedulerThreadHandle;
        Priority = priority;
        if (!Cooperative)
        {
            _hostEvent = new ManualResetEventSlim(false, spinCount: 0);
        }
    }

    /// <summary>True when the thread parks in the guest scheduler rather than on a host event.</summary>
    public bool Cooperative { get; }

    /// <summary>The guest scheduler's handle of the waiting thread (0 for a host thread).</summary>
    public ulong SchedulerThreadHandle { get; }

    /// <summary>Queue priority (lower runs first) for <see cref="WaitQueueOrder.Priority"/> queues.</summary>
    public int Priority { get; }

    public GuestWaitStatus Status => (GuestWaitStatus)Volatile.Read(ref _status);

    /// <summary>The absolute deadline (Stopwatch timestamp), or 0 for none.</summary>
    public long DeadlineTimestamp { get; private set; }

    /// <summary>The queue epoch the waiter joined in.</summary>
    public long JoinedEpoch => QueueEpoch;

    /// <summary>True while linked into a queue (caller holds that queue's gate).</summary>
    public bool IsQueuedLocked => Queue is not null;

    /// <summary>The waiter after this one in its queue (caller holds the queue's gate).</summary>
    public GuestWaiter? NextInQueue => Next;

    /// <summary>The queue the waiter is linked into, or null (read under that queue's gate).</summary>
    public WaitQueue? CurrentQueue => Volatile.Read(ref Queue);

    /// <summary>Computes the guest return value when a cooperative wait resumes.</summary>
    public Func<GuestWaiter, int>? ResumeHandler { get; set; }

    /// <summary>Policy result slot (e.g. the event-flag pattern, an errno), written before completion.</summary>
    public ulong ResultValue { get; set; }

    /// <summary>Microseconds left until the deadline when the wait completed (0 without a deadline).</summary>
    public long RemainingMicroseconds
    {
        get
        {
            if (DeadlineTimestamp == 0)
            {
                return 0;
            }

            var completed = Volatile.Read(ref _completedTimestamp);
            if (completed == 0)
            {
                completed = Stopwatch.GetTimestamp();
            }

            return SharpEmu.HLE.Host.HostPreciseClock.TicksToMicroseconds(DeadlineTimestamp - completed);
        }
    }

    /// <summary>
    /// Marks the wait finished with <paramref name="status"/> and unlinks it (caller holds
    /// the gate of the queue it is in, if any). False when it was already complete.
    /// </summary>
    public bool TryCompleteLocked(GuestWaitStatus status)
    {
        Debug.Assert(status != GuestWaitStatus.Pending);
        if (Interlocked.CompareExchange(ref _status, (int)status, (int)GuestWaitStatus.Pending) != (int)GuestWaitStatus.Pending)
        {
            return false;
        }

        Volatile.Write(ref _completedTimestamp, Stopwatch.GetTimestamp());
        Queue?.RemoveLocked(this);
        if (_timer is { } timer)
        {
            _ = timer.Owner.Cancel(timer);
            _timer = null;
        }

        return true;
    }

    /// <summary>
    /// Arms the deadline (caller holds the gate of the queue the waiter is in). A deadline
    /// of 0 means none.
    /// </summary>
    public void ArmDeadlineLocked(long deadlineTimestamp, GuestTimerService? service = null)
    {
        DeadlineTimestamp = deadlineTimestamp;
        if (deadlineTimestamp == 0 || deadlineTimestamp == long.MaxValue)
        {
            return;
        }

        _timer = (service ?? GuestTimerService.Shared).Schedule(deadlineTimestamp, static state => ((GuestWaiter)state!).OnDeadline(), this);
    }

    /// <summary>Records the deadline for the remaining-time computation without arming a timer.</summary>
    public void SetDeadlineWithoutTimer(long deadlineTimestamp) => DeadlineTimestamp = deadlineTimestamp;

    /// <summary>
    /// Runs when the deadline passes, with the queue gate held. The default completes the
    /// waiter as timed out and lets the queue re-evaluate. A policy that moves waiters
    /// between queues (condition variable → mutex) overrides it.
    /// </summary>
    protected internal virtual void OnDeadlineLocked(WaitQueue queue, ref WakeBatch batch)
    {
        if (TryCompleteLocked(GuestWaitStatus.TimedOut))
        {
            batch.Add(this);
            queue.RunReevaluateLocked(ref batch);
        }
    }

    private void OnDeadline() => Expire();

    /// <summary>
    /// Applies the deadline outcome now (<see cref="OnDeadlineLocked"/> under the queue
    /// gate) and wakes whatever it completed. Also used when a scheduler resumes a waiter
    /// that never completed, which is then treated as expired.
    /// </summary>
    public void Expire()
    {
        var batch = default(WakeBatch);
        while (true)
        {
            var queue = Volatile.Read(ref Queue);
            if (queue is null)
            {
                // Not queued: completed already, or an unqueued wait (sleep) that only
                // needs its completion.
                if (TryCompleteLocked(GuestWaitStatus.TimedOut))
                {
                    batch.Add(this);
                }

                break;
            }

            lock (queue.Gate)
            {
                if (!ReferenceEquals(Queue, queue))
                {
                    continue;
                }

                _timer = null;
                OnDeadlineLocked(queue, ref batch);
            }

            break;
        }

        batch.WakeAll();
    }

    /// <summary>Wakes the waiting thread after completion; call with no queue lock held.</summary>
    public void Wake()
    {
        if (Cooperative)
        {
            _ = GuestThreadExecution.Scheduler?.WakeBlockedWaiter(SchedulerThreadHandle, this, _wakeKey ?? "guest_wait");
            return;
        }

        _hostEvent?.Set();
    }

    /// <summary>
    /// Waits for completion after the waiter was queued (and its deadline armed) and the
    /// queue lock released. <paramref name="complete"/> turns the finished wait into the
    /// import's return value (it also stores RAX and writes guest out-parameters, on the
    /// waiting thread, with no lock held). A cooperative thread parks in the scheduler and
    /// <paramref name="complete"/> runs when it resumes; the value returned now is a
    /// placeholder the resume overwrites. A host thread sleeps on its own event.
    /// </summary>
    public int Block(CpuContext ctx, string reason, Func<GuestWaiter, int> complete)
    {
        ResumeHandler = complete;
        if (Cooperative && RequestCooperativeBlock(ctx, reason))
        {
            return 0;
        }

        _ = WaitOnHost(ctx);
        return complete(this);
    }

    /// <summary>
    /// Parks the calling guest thread in the scheduler until the waiter completes. The
    /// import must return right after; the resumed call's result is
    /// <see cref="ResumeHandler"/>'s value.
    /// </summary>
    public bool RequestCooperativeBlock(CpuContext ctx, string reason)
    {
        Debug.Assert(Cooperative);
        _wakeKey = reason;
        return GuestThreadExecution.RequestCurrentThreadBlock(ctx, reason, reason, this);
    }

    /// <summary>
    /// Blocks the calling host thread until the waiter completes. Guest exceptions queued
    /// for the thread are delivered while it waits (<see cref="GuestWait.InterruptHostWaiters"/>
    /// wakes it for that), then the wait resumes.
    /// </summary>
    public GuestWaitStatus WaitOnHost(CpuContext? ctx)
    {
        var hostEvent = _hostEvent ??= new ManualResetEventSlim(false, spinCount: 0);
        GuestWait.RegisterHostWaiter(this);
        try
        {
            while (Status == GuestWaitStatus.Pending)
            {
                _ = hostEvent.Wait(GuestWait.HostWaitSafetySliceMilliseconds);

                // Reset before the completion check: a Set after it stays latched.
                hostEvent.Reset();
                if (Status != GuestWaitStatus.Pending)
                {
                    break;
                }

                // Interrupted (or the safety slice passed): deliver a queued guest
                // exception at this safe point, then let the policy re-check its condition.
                if (ctx is not null)
                {
                    GuestThreadExecution.Scheduler?.DeliverPendingGuestExceptionIfReady(ctx);
                    AfterHostInterrupt(ctx);
                }
            }
        }
        finally
        {
            GuestWait.UnregisterHostWaiter(this);
        }

        return Status;
    }

    /// <summary>
    /// Runs on a host waiter after a guest exception could have been delivered (a guest
    /// signal handler may have changed the waited-on state). Value-compare waits override
    /// it to end the wait the way an interrupted kernel sleep would.
    /// </summary>
    protected virtual void AfterHostInterrupt(CpuContext ctx)
    {
    }

    /// <summary>Completes a queued waiter from outside its queue's lock (takes it) and wakes it.</summary>
    public bool CompleteAndWake(GuestWaitStatus status)
    {
        bool completed;
        while (true)
        {
            var queue = Volatile.Read(ref Queue);
            if (queue is null)
            {
                completed = TryCompleteLocked(status);
                break;
            }

            lock (queue.Gate)
            {
                if (!ReferenceEquals(Queue, queue))
                {
                    continue;
                }

                completed = TryCompleteLocked(status);
            }

            break;
        }

        if (completed)
        {
            Wake();
        }

        return completed;
    }

    internal void InterruptHostWait() => _hostEvent?.Set();

    bool IGuestThreadBlockWaiter.TryWake() => Status != GuestWaitStatus.Pending;

    int IGuestThreadBlockWaiter.Resume()
    {
        if (Status == GuestWaitStatus.Pending)
        {
            // The scheduler resumed a wait that never completed (a scheduler-side
            // deadline): settle it as expired before reporting.
            Expire();
        }

        return ResumeHandler?.Invoke(this) ?? 0;
    }
}

/// <summary>Shared helpers of the wait core.</summary>
public static class GuestWait
{
    /// <summary>
    /// Host waits are woken by their completer or by <see cref="InterruptHostWaiters"/>;
    /// this slice only bounds how long a missed interrupt could delay exception delivery.
    /// </summary>
    public const int HostWaitSafetySliceMilliseconds = 250;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<GuestWaiter, byte> HostWaiters = new();

    /// <summary>
    /// True when the calling thread is a scheduler-owned guest thread inside an import call,
    /// so it can park cooperatively and resume from its continuation (the same condition
    /// under which <see cref="GuestThreadExecution.RequestCurrentThreadBlock(CpuContext?, string, string?, IGuestThreadBlockWaiter?, long)"/>
    /// captures a continuation).
    /// </summary>
    public static bool CanBlockCooperatively =>
        GuestThreadExecution.IsGuestThread &&
        GuestThreadExecution.TryGetCurrentImportCallFrame(out _);

    /// <summary>
    /// Wakes every host-thread waiter so it re-checks for a guest exception queued for it,
    /// then keeps waiting. The scheduler calls this when it queues an exception.
    /// </summary>
    public static void InterruptHostWaiters()
    {
        foreach (var waiter in HostWaiters.Keys)
        {
            waiter.InterruptHostWait();
        }
    }

    internal static void RegisterHostWaiter(GuestWaiter waiter) => HostWaiters[waiter] = 0;

    internal static void UnregisterHostWaiter(GuestWaiter waiter) => HostWaiters.TryRemove(waiter, out _);

    /// <summary>Host waiters currently parked (diagnostics, tests).</summary>
    public static int HostWaiterCount => HostWaiters.Count;
}
