// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

[Collection(CommandStreamQueueStateCollection.Name)]
public sealed class CommandPrefixCompletionTests
{
    internal sealed class Completion(Action wait) : IGuestSubmissionPrefixCompletion
    { public void WaitUntilCompleted() => wait(); }

    [Fact]
    public async Task CallerCompletionWaitLeavesQueueGateAndActualWorkerFreeForAnotherQueue()
    {
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var caller = Environment.CurrentManagedThreadId;
        host.EndPrefixCapture = complete => complete ? new Completion(() =>
        {
            Assert.False(Monitor.IsEntered(typeof(CommandStreamQueue).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue)!));
            Assert.NotEqual(caller, Environment.CurrentManagedThreadId);
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        }) : null;
        var words = StreamRunner.Packet(PacketOpcode.NumInstances, 3); host.WriteWords(StreamRunner.CommandAddress, words);
        var first = queue.EnqueueWithPreparation(0, StreamRunner.CommandAddress, (uint)words.Length, 91, null);
        var worker = new CommandStreamWorker(queue, host, static () => false, true, "prefix progress"); worker.Start();
        try
        {
            var wait = Task.Run(first.WaitUntilPrepared); Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var other = StreamRunner.Packet(PacketOpcode.NumInstances, 7); host.WriteWords(StreamRunner.DataAddress, other);
            queue.EnqueueCompute(0x20, StreamRunner.DataAddress, (uint)other.Length, 92, null);
            Assert.Equal(IdleOutcome.Completed, queue.WaitForIdle()); Assert.Equal(7u, queue.GetInterpreter(1).InstanceCount);
            Assert.False(wait.IsCompleted); release.Set();
            Assert.Equal(GuestSubmissionPreparationResult.PreparedPrefix, await wait.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { release.Set(); worker.Stop(); }
    }

    [Fact]
    public void BlockedPrefixNeverCapturesOrWaitsForCompletedFuturePackets()
    {
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host); var ends = new List<bool>();
        host.EndPrefixCapture = complete => { ends.Add(complete); return complete ? new Completion(() => throw new Exception("must not wait")) : null; };
        var words = StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13u,
            StreamRunner.Low(StreamRunner.LabelAddress), StreamRunner.High(StreamRunner.LabelAddress), 1u, uint.MaxValue, 0u);
        host.WriteWords(StreamRunner.CommandAddress, words);
        var receipt = queue.EnqueueWithPreparation(0, StreamRunner.CommandAddress, (uint)words.Length, 93, null);
        Assert.Equal(SliceResult.BlockedWithoutProgress, queue.ProcessOne());
        Assert.Equal(GuestSubmissionPreparationResult.DeferredDependency, receipt.WaitUntilPrepared()); Assert.Equal(new[] { false }, ends);
    }

    [Fact]
    public void OriginalCaptureFailureFailsReceiptAndClearsCaptureWithoutLosingCause()
    {
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host);
        var cause = new NotSupportedException("prefix capture failure"); var cleanup = new InvalidOperationException("capture cleanup");
        host.EndPrefixCapture = complete => throw (complete ? cause : cleanup);
        var words = StreamRunner.Packet(PacketOpcode.NumInstances, 3); host.WriteWords(StreamRunner.CommandAddress, words);
        var receipt = queue.EnqueueWithPreparation(0, StreamRunner.CommandAddress, (uint)words.Length, 94, null);
        Assert.Same(cause, Assert.Throws<NotSupportedException>(() => queue.ProcessOne()));
        Assert.Same(cause, Assert.Throws<NotSupportedException>(() => receipt.WaitUntilPrepared()));
        Assert.Same(cleanup, cause.Data["CommandPrefixCaptureCleanup"]); Assert.Equal(IdleOutcome.Failed, queue.Outcome);
    }

    [Fact]
    public void CompletionWaitFailureDoesNotCertifyPreparedReturnOrFollowingWork()
    {
        var host = new RecordingCommandStreamHost(); var queue = new CommandStreamQueue(host);
        var cause = new NotSupportedException("prefix native completion boundary refused");
        host.EndPrefixCapture = _ => new Completion(() => throw cause);
        var words = StreamRunner.Packet(PacketOpcode.NumInstances, 3); host.WriteWords(StreamRunner.CommandAddress, words);
        var receipt = queue.EnqueueWithPreparation(0, StreamRunner.CommandAddress, (uint)words.Length, 95, null);
        Assert.Equal(SliceResult.Completed, queue.ProcessOne());
        queue.EnqueueGraphics(StreamRunner.CommandAddress, (uint)words.Length, 96, null);
        Assert.Same(cause, Assert.Throws<NotSupportedException>(() => receipt.WaitUntilPrepared()));
        Assert.Equal(IdleOutcome.Failed, queue.Outcome); Assert.False(queue.HasPending);
    }
}
