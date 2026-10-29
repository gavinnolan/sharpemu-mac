// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

[Collection(CommandStreamQueueStateCollection.Name)]
public sealed class DeferredCommandItemReuseTests
{
    private sealed record Draw(int Index) : AgcExports.DeferredWork;
    private static readonly Action Nothing = static () => { };

    private static DeferredCommandThread NewThread(RecordingCommandStreamHost host, bool reuse,
        Action<AgcExports.DeferredWork>? draw = null) =>
        new(new CommandStreamQueue(host), host.Memory, draw ?? (_ => { }), null, Nothing,
            recycleAsyncItems: reuse);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedMixedKindsKeepOrderingAndPendingWriteOverlay(bool reuse)
    {
        var host = new RecordingCommandStreamHost();
        var seen = new List<int>();
        var thread = NewThread(host, reuse, work => seen.Add(((Draw)work).Index));
        const ulong address = RecordingCommandStreamHost.MemoryBase;
        Span<byte> actual = stackalloc byte[4];
        for (var iteration = 0; iteration < 512; iteration++)
        {
            var start = iteration * 4;
            thread.EnqueueDeferred(new Draw(start), isDraw: true);
            thread.Post(() => seen.Add(start + 1));
            thread.EnqueueDeferred(new Draw(start + 2), isDraw: false);
            var bytes = BitConverter.GetBytes(iteration + 1);
            thread.PostWrite(address, bytes);
            // The queued write is visible before it executes and owns a copy of the payload.
            bytes.AsSpan().Clear();
            Assert.True(thread.ReadOverlay(address, actual));
            Assert.Equal(iteration + 1, BinaryPrimitives.ReadInt32LittleEndian(actual));
            thread.PostRange(() => seen.Add(start + 3), address, 4);
            Assert.Equal(5, thread.RunBatch(long.MaxValue, 64, Nothing));
            Assert.False(thread.HasWork);
            // A stale pending range would enqueue a synchronous item and block this read.
            thread.WaitForPendingRange(address, 4);
            Assert.True(thread.ReadOverlay(address, actual));
            Assert.Equal(iteration + 1, BinaryPrimitives.ReadInt32LittleEndian(actual));
        }
        Assert.Equal(Enumerable.Range(0, 2048), seen);
        Assert.Equal(512 * 4, thread.Gate);
        AssertCacheContainsNoPayloads(thread, reuse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoThreadsWrapTheReturnRingAndPreserveSynchronousFailureResults(bool reuse)
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host);
        using var wake = new AutoResetEvent(false);
        using var completed = new ManualResetEventSlim();
        var observed = new List<int>();
        var thread = new DeferredCommandThread(queue, host.Memory,
            work => observed.Add(((Draw)work).Index), null, () => wake.Set(), recycleAsyncItems: reuse);
        Exception? failure = null;
        var syncFailures = 0;
        const int count = 20000;
        Assert.True(queue.TryEnqueueControlBarrier(() =>
        {
            try
            {
                for (var i = 0; i < count; i++)
                {
                    if ((i & 1) == 0) thread.EnqueueDeferred(new Draw(i), isDraw: true);
                    else
                    {
                        var value = i;
                        thread.Post(() => observed.Add(value));
                    }
                    if (i % 127 == 0)
                    {
                        var expected = new InvalidOperationException($"sync-{i}");
                        var caught = Assert.Throws<InvalidOperationException>(() => thread.Run(() => throw expected));
                        Assert.Same(expected, caught.InnerException);
                        syncFailures++;
                    }
                }
                // Completion of a synchronous callback publishes all preceding output.
                thread.Run(Nothing);
            }
            catch (Exception exception) { failure = exception; }
            finally { completed.Set(); }
        }, Nothing));
        thread.Start();
        try
        {
            var deadline = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
            while (!completed.IsSet && Stopwatch.GetTimestamp() < deadline)
            {
                thread.ConsumerIdle = true;
                if (thread.RunBatch(long.MaxValue, 17, Nothing) == 0) wake.WaitOne(1);
                thread.ConsumerIdle = false;
            }
            Assert.True(completed.IsSet);
            Assert.Null(failure);
            thread.ThrowIfFailed();
            Assert.Equal(158, syncFailures);
            Assert.Equal(Enumerable.Range(0, count), observed);
        }
        finally
        {
            thread.Stop(() => thread.RunBatch(long.MaxValue, int.MaxValue, Nothing));
        }
        Assert.False(thread.IsAlive);
        AssertCacheContainsNoPayloads(thread, reuse);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedAsynchronousItemsAreNotReturnedOrReexecuted(bool reuse)
    {
        var host = new RecordingCommandStreamHost();
        var thread = NewThread(host, reuse);
        var failure = new InvalidOperationException("asynchronous failure");
        thread.Post(() => throw failure);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(
            () => thread.RunBatch(long.MaxValue, 64, Nothing)));
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(thread.ThrowIfFailed).InnerException);
        if (reuse)
            foreach (var item in CachedItems(thread)!) Assert.Null(item);
    }

    [Fact]
    public void CacheStaysBoundedAcrossChangingBatchSizes()
    {
        var thread = NewThread(new RecordingCommandStreamHost(), true);
        for (var batch = 0; batch < 300; batch++)
        {
            var count = batch % 64 + 1;
            for (var i = 0; i < count; i++) thread.Post(Nothing);
            while (thread.HasWork) thread.RunBatch(long.MaxValue, 7, Nothing);
        }
        Assert.Equal(64, CachedItems(thread)!.Length);
        AssertCacheContainsNoPayloads(thread, true);
    }

    private static Array? CachedItems(DeferredCommandThread thread) =>
        (Array?)typeof(DeferredCommandThread).GetField("_recycledItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(thread);

    private static void AssertCacheContainsNoPayloads(DeferredCommandThread thread, bool enabled)
    {
        var cache = CachedItems(thread);
        if (!enabled) { Assert.Null(cache); return; }
        Assert.NotNull(cache);
        Assert.Equal(64, cache.Length);
        foreach (var item in cache)
        {
            if (item is null) continue;
            foreach (var field in new[] { "Work", "Action", "Data", "Failure" })
                Assert.Null(item.GetType().GetField(field)!.GetValue(item));
            foreach (var field in new[] { "IsDraw", "Sync", "Done" })
                Assert.Equal(false, item.GetType().GetField(field)!.GetValue(item));
            Assert.Equal(0UL, item.GetType().GetField("RangeSize")!.GetValue(item));
        }
    }

    [Fact]
    public void MeasureQueueItemReuseWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("SHARPEMU_COMMAND_ITEM_BENCH_OUTPUT");
        if (string.IsNullOrEmpty(path)) return;
        var rows = new List<object>();
        foreach (var batch in new[] { 1, 16, 64 })
        {
            var baseline = NewThread(new RecordingCommandStreamHost(), false);
            var candidate = NewThread(new RecordingCommandStreamHost(), true);
            foreach (var thread in new[] { baseline, candidate }) Exercise(thread, batch, 10000);
            for (var trial = 0; trial < 8; trial++)
            foreach (var enabled in (trial & 1) == 0 ? new[] { false, true } : new[] { true, false })
            {
                var thread = enabled ? candidate : baseline;
                const int count = 200000;
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                var ran = Exercise(thread, batch, count);
                var elapsed = Stopwatch.GetElapsedTime(start);
                bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
                Assert.Equal(count, ran);
                rows.Add(new { mode = "same-thread", batch, enabled, trial,
                    nsPerItem = elapsed.TotalNanoseconds / count, bytesPerItem = (double)bytes / count });
            }
        }
        for (var trial = 0; trial < 6; trial++)
        foreach (var enabled in (trial & 1) == 0 ? new[] { false, true } : new[] { true, false })
            rows.Add(MeasureConcurrent(enabled, trial));
        File.WriteAllText(path, JsonSerializer.Serialize(new { architecture = RuntimeInformation.ProcessArchitecture.ToString(), rows },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object MeasureConcurrent(bool reuse, int trial)
    {
        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host);
        using var wake = new AutoResetEvent(false);
        using var done = new ManualResetEventSlim();
        var thread = new DeferredCommandThread(queue, host.Memory, _ => { }, null, () => wake.Set(),
            recycleAsyncItems: reuse);
        Exception? failure = null;
        const int count = 100000;
        double ns = 0, allocated = 0;
        Assert.True(queue.TryEnqueueControlBarrier(() =>
        {
            try
            {
                for (var i = 0; i < 5000; i++) thread.Post(Nothing);
                thread.Run(Nothing);
                var bytes = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                for (var i = 0; i < count; i++) thread.Post(Nothing);
                thread.Run(Nothing);
                ns = Stopwatch.GetElapsedTime(start).TotalNanoseconds / count;
                allocated = (double)(GC.GetAllocatedBytesForCurrentThread() - bytes) / count;
            }
            catch (Exception exception) { failure = exception; }
            finally { done.Set(); }
        }, Nothing));
        thread.Start();
        try
        {
            var deadline = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
            while (!done.IsSet && Stopwatch.GetTimestamp() < deadline)
            {
                thread.ConsumerIdle = true;
                if (thread.RunBatch(long.MaxValue, 64, Nothing) == 0) wake.WaitOne(1);
                thread.ConsumerIdle = false;
            }
            Assert.True(done.IsSet);
            Assert.Null(failure);
        }
        finally { thread.Stop(() => thread.RunBatch(long.MaxValue, int.MaxValue, Nothing)); }
        return new { mode = "two-thread", batch = 64, enabled = reuse, trial, nsPerItem = ns, bytesPerItem = allocated };
    }

    private static int Exercise(DeferredCommandThread thread, int batch, int count)
    {
        var ran = 0;
        for (var i = 0; i < count;)
        {
            var length = Math.Min(batch, count - i);
            for (var j = 0; j < length; j++) thread.Post(Nothing);
            ran += thread.RunBatch(long.MaxValue, int.MaxValue, Nothing);
            i += length;
        }
        return ran;
    }
}
