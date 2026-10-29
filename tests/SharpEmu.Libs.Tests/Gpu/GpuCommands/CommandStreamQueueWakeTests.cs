// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

// The gate's waiters (the worker for work, a frame boundary or a drain for completion) share one
// monitor; the queue wakes none when nobody waits and one with a single pulse when one does. A
// state change must still reach every kind of waiter, whichever of them are parked.
public sealed class CommandStreamQueueWakeTests
{
    private const ulong Graphics = StreamRunner.CommandAddress;

    private static uint[] Instances(uint count) => StreamRunner.Packet(PacketOpcode.NumInstances, count);

    private static void Enqueue(RecordingCommandStreamHost host, CommandStreamQueue queue, ulong submissionId)
    {
        var words = Instances(3);
        host.WriteWords(Graphics, words);
        queue.EnqueueGraphics(Graphics, (uint)words.Length, submissionId, null);
    }

    // Waits until the thread has parked inside the queue's wait (it cannot be observed, so it is
    // given a moment after it announces itself; a wake that came too early is still found by the
    // thread's own check, which is the case the repeated rounds cover).
    private static void LetItPark(ManualResetEventSlim announced)
    {
        Assert.True(announced.Wait(TimeSpan.FromSeconds(30)));
        Thread.Sleep(2);
    }

    [Fact]
    public void AnEnqueueWakesAParkedWorkerEveryTime()
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host);
        for (var round = 0; round < 300; round++)
        {
            using var announced = new ManualResetEventSlim();
            var worker = Task.Run(() =>
            {
                announced.Set();
                // A lost wakeup shows as the timeout passing with nothing runnable.
                return queue.WaitForWork(20_000);
            });
            LetItPark(announced);

            Enqueue(host, queue, (ulong)round + 1);

            Assert.True(worker.Wait(TimeSpan.FromSeconds(60)));
            Assert.True(worker.Result, $"round {round}: the worker slept through an enqueue");
            Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        }
    }

    [Fact]
    public void AnEnqueueWakesTheWorkerWhileAFrameBoundaryWaitsToo()
    {
        for (var round = 0; round < 25; round++)
        {
            var host = new RecordingCommandStreamHost();
            var queue = new CommandStreamQueue(host, boundariesInFlight: 0);
            // A head that parks with no label to watch: the frame boundary queued behind it cannot run.
            var blocker = StreamRunner.Packet(PacketOpcode.WaitOnDeCounterDiff, 0);
            host.WriteWords(Graphics, blocker);
            queue.EnqueueGraphics(Graphics, (uint)blocker.Length, 1, null);
            Assert.Equal(SliceResult.BlockedWithoutProgress, queue.ProcessOne());

            // The boundary's guest thread now waits on the gate for the worker to run the boundary.
            var guest = Task.Run(queue.Done);
            Assert.True(SpinWait.SpinUntil(() => queue.PendingSubmissionCount == 2, TimeSpan.FromSeconds(30)));

            using var announced = new ManualResetEventSlim();
            var worker = Task.Run(() =>
            {
                announced.Set();
                return queue.WaitForWork(20_000);
            });
            LetItPark(announced);

            // Two kinds of waiter are parked: a compute submission wakes the worker, not just the boundary.
            var compute = Instances(3);
            host.WriteWords(StreamRunner.DataAddress, compute);
            queue.EnqueueCompute(GpuCommandInterpreter.ComputeQueueBase, StreamRunner.DataAddress, (uint)compute.Length, 2, null);
            Assert.True(worker.Wait(TimeSpan.FromSeconds(60)));
            Assert.True(worker.Result, $"round {round}: the worker slept through an enqueue");

            // Ending the queue reaches the boundary's waiter as well.
            queue.Fail();
            Assert.Equal(IdleOutcome.Failed, guest.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult());
        }
    }

    [Fact]
    public void ARunSliceWakesADrainWaitingForIdleEveryTime()
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host);
        for (var round = 0; round < 200; round++)
        {
            Enqueue(host, queue, (ulong)round + 1);
            var drain = Task.Run(queue.WaitForIdle);
            Thread.Sleep(1);

            while (queue.HasPending)
            {
                Assert.NotEqual(SliceResult.NoWork, queue.ProcessOne());
            }

            Assert.Equal(IdleOutcome.Completed, drain.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult());
        }
    }

    [Fact]
    public void WakeReleasesAParkedWorkerWhoseStateChangedOutsideTheQueue()
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host);
        using var announced = new ManualResetEventSlim();
        var worker = Task.Run(() =>
        {
            announced.Set();
            // Nothing is runnable and no head is watched: only a timeout or a pulse ends the wait.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            queue.WaitForWork(20_000);
            return stopwatch.Elapsed;
        });
        LetItPark(announced);

        queue.Wake();

        Assert.True(worker.Wait(TimeSpan.FromSeconds(60)));
        Assert.True(worker.Result < TimeSpan.FromSeconds(15), $"the worker waited {worker.Result.TotalSeconds:F1} s for a wake");
    }
}
