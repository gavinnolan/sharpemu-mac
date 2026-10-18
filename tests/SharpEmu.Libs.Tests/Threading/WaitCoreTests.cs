// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Threading;
using Xunit;

namespace SharpEmu.Libs.Tests.Threading;

public sealed class WaitCoreTests
{
    [Fact]
    public void TimerServiceFiresInDeadlineOrderAndNotBeforeTheDeadline()
    {
        using var service = new GuestTimerService("test-timer-order");
        var fired = new List<(int Index, long At)>();
        using var done = new CountdownEvent(3);
        // The gaps leave room for the scheduling loop to be descheduled or JIT-compiled on a
        // loaded runner: a deadline passed before its timer is armed fires out of order by design.
        var start = Stopwatch.GetTimestamp();
        var deadlines = new[]
        {
            start + HostPreciseClock.MicrosecondsToTicks(300_000),
            start + HostPreciseClock.MicrosecondsToTicks(100_000),
            start + HostPreciseClock.MicrosecondsToTicks(200_000),
        };
        for (var i = 0; i < deadlines.Length; i++)
        {
            var index = i;
            service.Schedule(deadlines[i], _ =>
            {
                lock (fired)
                {
                    fired.Add((index, Stopwatch.GetTimestamp()));
                }

                done.Signal();
            }, null);
        }

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { 1, 2, 0 }, fired.Select(item => item.Index));
        foreach (var (index, at) in fired)
        {
            Assert.True(at >= deadlines[index], $"timer {index} fired early");
        }
    }

    [Fact]
    public void TimerServiceCancelPreventsTheCallbackAndRescheduleMovesIt()
    {
        using var service = new GuestTimerService("test-timer-cancel");
        var canceledRan = 0;
        var canceled = service.ScheduleAfterMicroseconds(5_000, _ => Interlocked.Increment(ref canceledRan), null);
        Assert.True(service.Cancel(canceled));
        Assert.False(service.Cancel(canceled));

        using var moved = new ManualResetEventSlim();
        var timer = service.ScheduleAfterMicroseconds(10_000_000, _ => moved.Set(), null);
        Assert.True(service.Reschedule(timer, HostPreciseClock.DeadlineAfterMicroseconds(1_000)));
        Assert.True(moved.Wait(TimeSpan.FromSeconds(10)));
        Thread.Sleep(20);
        Assert.Equal(0, Volatile.Read(ref canceledRan));
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public void TimerServiceIsPreciseForShortDeadlines()
    {
        using var service = new GuestTimerService("test-timer-precision");
        double MedianLatenessMicroseconds()
        {
            var lateness = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                using var fired = new ManualResetEventSlim();
                var deadline = HostPreciseClock.DeadlineAfterMicroseconds(500);
                long firedAt = 0;
                service.Schedule(deadline, _ =>
                {
                    firedAt = Stopwatch.GetTimestamp();
                    fired.Set();
                }, null);
                Assert.True(fired.Wait(TimeSpan.FromSeconds(10)));
                Assert.True(firedAt >= deadline);
                lateness.Add(HostPreciseClock.TicksToMicroseconds(firedAt - deadline));
            }

            lateness.Sort();
            return lateness[lateness.Count / 2];
        }

        // Median well under the old 1 ms polling granularity. A host with every core busy can
        // delay a whole round by several ms (the timer thread is simply not scheduled), so the
        // best of a few rounds has to meet the bound.
        var best = double.MaxValue;
        for (var round = 0; round < 5 && best >= 2_000; round++)
        {
            best = Math.Min(best, MedianLatenessMicroseconds());
        }

        Assert.True(best < 2_000, $"median lateness {best} µs");
    }

    [Fact]
    public void FifoQueueCompletesInArrivalOrderAndPriorityQueueByPriority()
    {
        var fifo = new WaitQueue();
        var a = new GuestWaiter(cooperative: false, 0, priority: 5);
        var b = new GuestWaiter(cooperative: false, 0, priority: 1);
        var c = new GuestWaiter(cooperative: false, 0, priority: 3);
        lock (fifo.Gate)
        {
            fifo.EnqueueLocked(a);
            fifo.EnqueueLocked(b);
            fifo.EnqueueLocked(c);
            Assert.Same(a, fifo.SatisfyFirstLocked());
            Assert.Same(b, fifo.SatisfyFirstLocked());
            Assert.Same(c, fifo.SatisfyFirstLocked());
            Assert.Null(fifo.SatisfyFirstLocked());
        }

        var prio = new WaitQueue(WaitQueueOrder.Priority);
        var d = new GuestWaiter(cooperative: false, 0, priority: 5);
        var e = new GuestWaiter(cooperative: false, 0, priority: 1);
        var f = new GuestWaiter(cooperative: false, 0, priority: 1);
        lock (prio.Gate)
        {
            prio.EnqueueLocked(d);
            prio.EnqueueLocked(e);
            prio.EnqueueLocked(f);
            Assert.Same(e, prio.SatisfyFirstLocked());
            Assert.Same(f, prio.SatisfyFirstLocked());
            Assert.Same(d, prio.SatisfyFirstLocked());
        }
    }

    [Fact]
    public void HostWaiterTimesOutAtItsDeadlineAndReportsNoRemainingTime()
    {
        var queue = new WaitQueue();
        var waiter = new GuestWaiter(cooperative: false, 0);
        var started = Stopwatch.GetTimestamp();
        lock (queue.Gate)
        {
            queue.EnqueueLocked(waiter);
            waiter.ArmDeadlineLocked(HostPreciseClock.DeadlineAfterMicroseconds(5_000));
        }

        Assert.Equal(GuestWaitStatus.TimedOut, waiter.WaitOnHost(null));
        Assert.True(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(5));
        Assert.Equal(0, waiter.RemainingMicroseconds);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void CompletingAWaiterReportsRemainingTimeAndCancelsItsTimer()
    {
        var queue = new WaitQueue();
        var waiter = new GuestWaiter(cooperative: false, 0);
        lock (queue.Gate)
        {
            queue.EnqueueLocked(waiter);
            waiter.ArmDeadlineLocked(HostPreciseClock.DeadlineAfterMicroseconds(10_000_000));
        }

        var batch = default(WakeBatch);
        lock (queue.Gate)
        {
            Assert.Equal(1, queue.CompleteAllLocked(GuestWaitStatus.Canceled, ref batch));
        }

        batch.WakeAll();
        Assert.Equal(GuestWaitStatus.Canceled, waiter.WaitOnHost(null));
        Assert.InRange(waiter.RemainingMicroseconds, 9_000_000, 10_000_000);
        Assert.False(waiter.TryCompleteLocked(GuestWaitStatus.Satisfied));
    }

    [Fact]
    public void ErrnoAdapterMapsBothConventions()
    {
        Assert.Equal(0, GuestErrno.ToSce(0));
        Assert.Equal(unchecked((int)0x80020055), GuestErrno.ToSce(GuestErrno.ECANCELED));
        Assert.Equal(unchecked((int)0x8002000D), GuestErrno.ToSce(GuestErrno.EACCES));
        Assert.Equal(unchecked((int)0x8002003C), GuestErrno.ToSce(GuestErrno.ETIMEDOUT));
        Assert.Equal(unchecked((int)0x80020001), GuestErrno.ToSce(GuestErrno.EPERM));
        Assert.Equal(unchecked((int)0x80020054), GuestErrno.ToSce(GuestErrno.EOVERFLOW));
        foreach (var errno in new[] { GuestErrno.EPERM, GuestErrno.EDEADLK, GuestErrno.EBUSY, GuestErrno.EAGAIN, GuestErrno.ETIMEDOUT, GuestErrno.ECANCELED, GuestErrno.EACCES, GuestErrno.EOVERFLOW })
        {
            Assert.Equal(errno, GuestErrno.FromSce(GuestErrno.ToSce(errno)));
        }
    }
}
