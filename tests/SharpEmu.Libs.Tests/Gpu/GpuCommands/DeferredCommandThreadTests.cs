// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class DeferredCommandThreadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProducerIdentityAndOrderedConsumerWorkSurviveThreadSelection(bool useHostQos)
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host);
        var producer = new DeferredCommandThread(queue, host.Memory, _ => { }, null, () => { }, useHostQos);
        using var entered = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        bool producerIdentity = false, consumerIdentity = true;
        var values = new List<int>();
        Assert.True(queue.TryEnqueueControlBarrier(() =>
        {
            producerIdentity = producer.IsProducerThread;
            producer.Post(() => values.Add(1));
            entered.Set();
            producer.Run(() => { consumerIdentity = producer.IsProducerThread; values.Add(2); });
            completed.Set();
        }, () => { }));
        producer.Start();
        try
        {
            Assert.False(producer.IsProducerThread);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(producer.IsAlive);
            Assert.Throws<ThreadStateException>(producer.Start);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                producer.RunBatch(long.MaxValue, 64, () => { });
                return completed.IsSet;
            }, TimeSpan.FromSeconds(10)));
            producer.ThrowIfFailed();
            Assert.True(producerIdentity);
            Assert.False(consumerIdentity);
            Assert.Equal(new[] { 1, 2 }, values);
        }
        finally
        {
            producer.Stop(() => producer.RunBatch(long.MaxValue, 64, () => { }));
        }
        Assert.False(producer.IsAlive);
    }

    // A barrier or pass end only orders GPU commands: it runs in stream order like any other
    // item, but the draws after it do not wait for it to resolve their programs.
    [Fact]
    public void AnOrderingItemRunsInOrderWithoutHoldingTheGate()
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host);
        var producer = new DeferredCommandThread(queue, host.Memory, _ => { }, null, () => { }, false);
        using var completed = new ManualResetEventSlim();
        var values = new List<int>();
        long gateAfterPost = -1, gateAfterOrdering = -1;
        Assert.True(queue.TryEnqueueControlBarrier(() =>
        {
            producer.Post(() => values.Add(1));
            gateAfterPost = producer.Gate;
            producer.PostOrdering(() => values.Add(2));
            gateAfterOrdering = producer.Gate;
            producer.Run(() => values.Add(3));
            completed.Set();
        }, () => { }));
        producer.Start();
        try
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                producer.RunBatch(long.MaxValue, 64, () => { });
                return completed.IsSet;
            }, TimeSpan.FromSeconds(10)));
            producer.ThrowIfFailed();
            Assert.Equal(new[] { 1, 2, 3 }, values);
            Assert.Equal(gateAfterPost, gateAfterOrdering);
            Assert.Equal(gateAfterPost + 1, producer.Gate);
        }
        finally
        {
            producer.Stop(() => producer.RunBatch(long.MaxValue, 64, () => { }));
        }
    }

    [Fact]
    public void AnUngatedWriteHoldsOnlyItsRangeForTheResolutionsAfterIt()
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host);
        var producer = new DeferredCommandThread(queue, host.Memory, _ => { }, null, () => { }, false);
        using var queued = new ManualResetEventSlim();
        long gateBefore = -1, gateAfter = -1;
        bool heldInside = false, heldOutside = true, heldEarlierJob = true, heldGated = true;
        Assert.True(queue.TryEnqueueControlBarrier(() =>
        {
            gateBefore = producer.Gate;
            producer.PostRange(() => { }, 0x1000, 0x100, ungated: true);
            gateAfter = producer.Gate;
            producer.PostRange(() => { }, 0x8000, 0x100);
            heldInside = producer.PendingRangeHolds(0, 0x10F0, 0x20);
            heldOutside = producer.PendingRangeHolds(0, 0x1100, 0x20);
            heldEarlierJob = producer.PendingRangeHolds(-1, 0x1000, 0x10);
            heldGated = producer.PendingRangeHolds(0, 0x8000, 0x10);
            queued.Set();
        }, () => { }));
        producer.Start();
        try
        {
            Assert.True(queued.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(gateBefore, gateAfter);
            Assert.True(heldInside);
            Assert.False(heldOutside);
            Assert.False(heldEarlierJob);
            Assert.False(heldGated);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                producer.RunBatch(long.MaxValue, 64, () => { });
                return !producer.PendingRangeHolds(0, 0x1000, 0x100);
            }, TimeSpan.FromSeconds(10)));
            producer.ThrowIfFailed();
        }
        finally
        {
            producer.Stop(() => producer.RunBatch(long.MaxValue, 64, () => { }));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProducerFailureIsReportedAndThreadCanBeStopped(bool useHostQos)
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host);
        using var failed = new ManualResetEventSlim();
        var expected = new InvalidOperationException("producer failure");
        var producer = new DeferredCommandThread(queue, host.Memory, _ => { }, null, failed.Set, useHostQos);
        Assert.True(queue.TryEnqueueControlBarrier(() => throw expected, () => { }));
        producer.Start();
        try
        {
            Assert.True(failed.Wait(TimeSpan.FromSeconds(10)));
            Assert.Same(expected, Assert.Throws<InvalidOperationException>(producer.ThrowIfFailed).InnerException);
        }
        finally
        {
            producer.Stop(() => { });
        }
        Assert.False(producer.IsAlive);
    }
}
