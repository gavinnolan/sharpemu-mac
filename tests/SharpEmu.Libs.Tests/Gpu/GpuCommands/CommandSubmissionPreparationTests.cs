// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

[Collection(CommandStreamQueueStateCollection.Name)]
public sealed class CommandSubmissionPreparationTests
{
    private static CommandSubmissionPreparation Enqueue(RecordingCommandStreamHost host, CommandStreamQueue queue,
        ulong id, params uint[][] packets)
    {
        var words = StreamRunner.Concat(packets);
        var address = StreamRunner.CommandAddress + id * 0x100;
        host.WriteWords(address, words);
        return queue.EnqueueWithPreparation(0, address, (uint)words.Length, id, null);
    }

    [Fact]
    public async Task AcceptedReceiptWaitsForItsActualWorkerPrefixWithoutHoldingQueueGate()
    {
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        host.BeforeGuestRead = _ => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(2))); };
        var receipt = Enqueue(host, queue, 11, StreamRunner.Packet(PacketOpcode.NumInstances, 7));
        var worker = new CommandStreamWorker(queue, host, static () => false, true, "managed preparation");
        worker.Start();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            var waiter = Task.Run(receipt.WaitUntilPrepared);
            Assert.False(waiter.IsCompleted);
            var later = Enqueue(host, queue, 12, StreamRunner.Packet(PacketOpcode.NumInstances, 9));
            Assert.Equal(0, receipt.QueueId); Assert.Equal(11UL, receipt.SubmissionId);
            release.Set();
            Assert.Equal(GuestSubmissionPreparationResult.PreparedPrefix, await waiter.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(GuestSubmissionPreparationResult.PreparedPrefix, later.WaitUntilPrepared());
            Assert.Equal(9u, queue.GetInterpreter(0).InstanceCount);
            Assert.Empty(host.EndOfPipeWrites); // Preparation never synthesizes an EOP.
        }
        finally { release.Set(); worker.Stop(); }
    }

    [Fact]
    public void BlockedPrefixAndFifoFollowerAreExplicitlyDeferredUntilAnotherQueueMakesProgress()
    {
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host);
        var wait = StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13u,
            StreamRunner.Low(StreamRunner.LabelAddress), StreamRunner.High(StreamRunner.LabelAddress), 1u, uint.MaxValue, 0u);
        var blocked = Enqueue(host, queue, 1, wait, StreamRunner.Packet(PacketOpcode.NumInstances, 3));
        var follower = Enqueue(host, queue, 2, StreamRunner.Packet(PacketOpcode.NumInstances, 5));
        Assert.Equal(SliceResult.BlockedWithoutProgress, queue.ProcessOne());
        Assert.Equal(GuestSubmissionPreparationResult.DeferredDependency, blocked.WaitUntilPrepared());
        Assert.Equal(GuestSubmissionPreparationResult.DeferredDependency, follower.WaitUntilPrepared());
        Assert.Equal(1u, queue.GetInterpreter(0).InstanceCount);
        var words = StreamRunner.Packet(PacketOpcode.WriteData, 0x500u,
            StreamRunner.Low(StreamRunner.LabelAddress), StreamRunner.High(StreamRunner.LabelAddress), 1u);
        host.WriteWords(StreamRunner.DataAddress, words);
        var ready = queue.EnqueueWithPreparation(0x20, StreamRunner.DataAddress, (uint)words.Length, 3, null);
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(GuestSubmissionPreparationResult.PreparedPrefix, ready.WaitUntilPrepared());
        queue.RetryBlocked();
        Assert.Equal(SliceResult.Completed, queue.ProcessOne()); Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        Assert.Equal(5u, queue.GetInterpreter(0).InstanceCount);
        // A resumed shader is outside the original deferred readiness receipt.
        Assert.Equal(GuestSubmissionPreparationResult.DeferredDependency, blocked.WaitUntilPrepared());
    }

    [Fact]
    public void WorkerCannotWaitOnItsOwnPendingReceiptAndOriginalCauseFailsAdmission()
    {
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host);
        var receipt = Enqueue(host, queue, 1, StreamRunner.Packet(PacketOpcode.NumInstances, 3));
        host.BeforeGuestRead = _ => receipt.WaitUntilPrepared();
        var original = Assert.Throws<InvalidOperationException>(() => queue.ProcessOne());
        Assert.Contains("own command worker", original.Message);
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => receipt.WaitUntilPrepared()));
        Assert.Equal(IdleOutcome.Failed, queue.Outcome);
        Assert.Throws<CommandStreamFatalException>(() => Enqueue(host, queue, 2, StreamRunner.Packet(PacketOpcode.NumInstances, 5)));
    }

    [Fact]
    public void OriginalPreparationFailureDropsFollowerAndDoesNotPublishItsLabel()
    {
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host);
        var first = Enqueue(host, queue, 1, StreamRunner.Packet(PacketOpcode.NumInstances, 3));
        var following = Enqueue(host, queue, 2, StreamRunner.Packet(PacketOpcode.WriteData, 0x500u,
            StreamRunner.Low(StreamRunner.LabelAddress), StreamRunner.High(StreamRunner.LabelAddress), 7u));
        var cause = new NotSupportedException("real preparation refusal");
        host.BeforeGuestRead = _ => throw cause;
        Assert.Same(cause, Assert.Throws<NotSupportedException>(() => queue.ProcessOne()));
        Assert.Same(cause, Assert.Throws<NotSupportedException>(() => first.WaitUntilPrepared()));
        Assert.Same(cause, Assert.Throws<NotSupportedException>(() => following.WaitUntilPrepared()));
        Assert.False(queue.HasPending); Assert.Empty(host.EndOfPipeWrites);
        Assert.Equal(0u, host.ReadDword(StreamRunner.LabelAddress));
    }

    [Fact]
    public void CancellationCleanupCannotReplaceOriginalPreparationFailureOrSkipOtherCleanup()
    {
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host);
        var receipt = Enqueue(host, queue, 1, StreamRunner.Packet(PacketOpcode.NumInstances, 3));
        var cause = new NotSupportedException("original library error");
        var cleanup = new InvalidOperationException("control cleanup error");
        var laterCancelled = false;
        Assert.True(queue.TryEnqueueControlBarrier(() => { }, () => throw cleanup));
        Assert.True(queue.TryEnqueueControlBarrier(() => { }, () => laterCancelled = true));
        host.BeforeGuestRead = _ => throw cause;
        Assert.Same(cause, Assert.Throws<NotSupportedException>(() => queue.ProcessOne()));
        Assert.Same(cause, Assert.Throws<NotSupportedException>(() => receipt.WaitUntilPrepared()));
        Assert.Same(cleanup, cause.Data["CommandCancellationCleanup"]);
        Assert.True(laterCancelled); Assert.False(queue.HasPending);
    }

    [Fact]
    public void FailedDiscardReleasesAnAcceptedUnstartedReceiptWithoutCertifyingReadiness()
    {
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host);
        var receipt = Enqueue(host, queue, 1, StreamRunner.Packet(PacketOpcode.NumInstances, 3));
        queue.DiscardAll();
        Assert.Throws<InvalidOperationException>(() => receipt.WaitUntilPrepared());
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void NormalStopStillDrainsTheAcceptedPrefixAndEmptyComputeRetainsOriginalRefusal()
    {
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host);
        var receipt = Enqueue(host, queue, 1, StreamRunner.Packet(PacketOpcode.NumInstances, 3));
        queue.StopAccepting();
        Assert.Equal(IdleOutcome.Completed, queue.DrainForShutdown(true));
        Assert.Equal(GuestSubmissionPreparationResult.PreparedPrefix, receipt.WaitUntilPrepared());
        Assert.Equal(3u, queue.GetInterpreter(0).InstanceCount);
        Assert.Throws<OperationCanceledException>(() => queue.EnqueueWithPreparation(0, 0, 0, 9, null));
        Assert.Throws<CommandStreamFatalException>(() => queue.EnqueueWithPreparation(0x20, 0, 0, 9, null));
    }
}
