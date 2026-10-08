// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalCommandWorkerFailureTests
{
    [Fact]
    public async Task ResumedBlockedSliceFailureWakesPresenterBeforeStopWithoutPublishingItsLabel()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host);
        var words = StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13u,
            StreamRunner.Low(StreamRunner.LabelAddress), StreamRunner.High(StreamRunner.LabelAddress), 1u, uint.MaxValue, 0u);
        host.WriteWords(StreamRunner.CommandAddress, words);
        queue.EnqueueGraphics(StreamRunner.CommandAddress, (uint)words.Length, 81, null);
        Assert.Equal(SliceResult.BlockedWithoutProgress, queue.ProcessOne());
        var labelPublished = false;
        var sequence = MetalVideoPresenter.SubmitOrderedGuestAction(() => labelPublished = true, "unexecuted EOP boundary");
        Assert.True(sequence > 0);
        var waiter = Task.Run(() => MetalVideoPresenter.WaitForGuestWork(sequence, Timeout.Infinite));
        var original = new NotSupportedException("later resumed library refusal");
        host.BeforeGuestRead = _ => throw original;
        queue.RetryBlocked();
        using var observed = new ManualResetEventSlim();
        var calls = 0;
        var worker = new CommandStreamWorker(queue, host, static () => false, true, "deferred Metal failure", failure =>
        {
            Assert.Same(original, failure);
            Assert.False(Monitor.IsEntered(Field(typeof(CommandStreamQueue), "_gate").GetValue(queue)!));
            Assert.False(Monitor.IsEntered(Field(typeof(MetalVideoPresenter), "_gate").GetValue(null)!));
            Interlocked.Increment(ref calls);
            MetalGuestGpuBackend.NotifyCommandStreamFailure(failure);
            observed.Set();
        });
        worker.Start();
        try
        {
            Assert.True(observed.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(await waiter.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Same(original, Field(typeof(MetalVideoPresenter), "_guestWorkFailure").GetValue(null));
            var refused = Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
            Assert.Same(original, refused.InnerException); // Failure precedes every native call.
            Assert.False(labelPublished); Assert.Empty(host.EndOfPipeWrites); Assert.Empty(reports);
            Assert.Equal(0L, Field(typeof(MetalVideoPresenter), "_completedGuestWorkSequence").GetValue(null));
        }
        finally { Assert.Same(original, Assert.Throws<NotSupportedException>(() => worker.Stop())); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ObserverCleanupCannotReplaceOriginalWorkerFailure(int failureSite)
    {
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host);
        var original = new NotSupportedException("original worker cause");
        var observerCleanup = new InvalidOperationException("observer cleanup");
        var cancellation = new InvalidOperationException("control cancellation");
        if (failureSite == 1) host.PendingCommands.Enqueue(() => throw original);
        else if (failureSite == 2) Assert.True(queue.TryEnqueueControlBarrier(() => throw original, () => { }));
        else
        {
            var words = StreamRunner.Packet(PacketOpcode.NumInstances, 3);
            host.WriteWords(StreamRunner.CommandAddress, words);
            queue.EnqueueGraphics(StreamRunner.CommandAddress, (uint)words.Length, 82, null);
            host.BeforeGuestRead = _ => throw original;
        }
        Assert.True(queue.TryEnqueueControlBarrier(() => { }, () => throw cancellation));
        var finalCancelled = false;
        Assert.True(queue.TryEnqueueControlBarrier(() => { }, () => finalCancelled = true));
        var count = 0;
        using var observed = new ManualResetEventSlim();
        var worker = new CommandStreamWorker(queue, host, static () => false, true, "observer failure", failure =>
        {
            Assert.Same(original, failure); Interlocked.Increment(ref count);
            observed.Set(); throw observerCleanup;
        });
        worker.Start();
        Assert.True(observed.Wait(TimeSpan.FromSeconds(2)));
        Assert.Same(original, Assert.Throws<NotSupportedException>(() => worker.Stop()));
        Assert.Equal(1, count); Assert.Same(observerCleanup, original.Data["CommandStreamWorkerCleanup"]);
        Assert.Same(cancellation, original.Data["CommandCancellationCleanup"]);
        Assert.Equal(IdleOutcome.Failed, queue.Outcome);
        Assert.True(finalCancelled);
    }

    [Fact]
    public void WorkerNotificationKeepsPriorPresenterCauseWithoutInvokingFailFast()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
        var original = new InvalidOperationException("first renderer cause");
        Assert.Same(original, MetalVideoPresenter.LatchGuestCompletionFailure("first", original));
        MetalGuestGpuBackend.NotifyCommandStreamFailure(new NotSupportedException("later worker cause"));
        Assert.Same(original, Field(typeof(MetalVideoPresenter), "_guestWorkFailure").GetValue(null));
        Assert.Empty(reports);
    }

    [Fact]
    public void SuccessfulWorkerNeverNotifiesTerminalFailureObserver()
    {
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host);
        var count = 0;
        var worker = new CommandStreamWorker(queue, host, static () => false, true, "healthy observer", _ => count++);
        worker.Start(); Assert.Equal(IdleOutcome.Completed, worker.Stop()); Assert.Equal(0, count);
    }

    private static FieldInfo Field(Type type, string name) => type.GetField(name,
        BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!;
}
