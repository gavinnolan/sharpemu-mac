// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text.Json;
using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed unsafe partial class MetalEndOfPipeOrderTests
{
    [Fact]
    public void CompletedSnapshotDoesNotCompleteRecordedOrBlockedSubmissions()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var host = rig.Host;
        var gate = MetalNative.Send(Device.Value, MetalNative.Selector("newSharedEvent"));
        Assert.NotEqual(0, gate);
        try
        {
            Assert.Equal(0UL, host.ReadCompletedSerial(true));
            var completed = 0UL;
            for (var round = 1u; round <= 3; round++)
            {
                var transfer = ((IMetalImageCacheOwner)host).BeginTransfer(MetalTransferCause.GuestFill);
                MetalV2Native.SendEventValue(transfer.CommandBuffer,
                    MetalNative.Selector("encodeWaitForEvent:value:"), gate, round);
                host.FillBufferInOrder(WindowedLabel, sizeof(uint), round);
                // Opening and recording the next buffer must not report it completed.
                Assert.True(host.SubmissionSerial > completed);
                Assert.Equal(completed, host.ReadCompletedSerial(true));
                var serial = host.FlushCommandBuffer();
                Assert.True(serial > completed);
                // A stale hint must fall back to the live event while submitted work
                // remains blocked. Neither reading mode may publish its serial early.
                for (var read = 0; read < 20; read++)
                {
                    Assert.Equal(completed, host.ReadCompletedSerial(true));
                    Assert.Equal(completed, host.ReadCompletedSerial(false));
                }
                MetalNative.SendVoid(gate, MetalNative.Selector("setSignaledValue:"), (nint)round);
                WaitForGpu(host, serial);
                Assert.Equal(serial, host.ReadCompletedSerial(true));
                Assert.Equal(serial, host.ReadCompletedSerial(false));
                Assert.Equal(round, rig.Backing.Read32(WindowedLabel));
                completed = serial;
            }
            WaitForCompletions(host);
        }
        finally
        {
            // Always unblock the queue before the rig drains it, including assertions.
            MetalNative.SendVoid(gate, MetalNative.Selector("setSignaledValue:"), (nint)3);
            MetalNative.SendVoid(gate, MetalNative.Selector("release"));
        }
    }

    [Fact]
    public void MeasureCompletedSnapshotWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("SHARPEMU_COMPLETION_SNAPSHOT_BENCH_OUTPUT");
        if (path is null || !RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var host = rig.Host;
        host.FillBufferInOrder(WindowedLabel, sizeof(uint), 0x1234);
        var serial = host.FlushCommandBuffer();
        WaitForGpu(host, serial);
        WaitForCompletions(host);
        const int iterations = 1_000_000;
        var rounds = new List<object>();
        void Measure(string condition)
        {
            for (var warmup = 0; warmup < 10_000; warmup++)
            {
                _ = host.ReadCompletedSerial(false);
                _ = host.ReadCompletedSerial(true);
            }
            for (var round = 0; round < 9; round++)
            {
                foreach (var snapshot in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
                {
                    ulong checksum = 0;
                    var allocation = GC.GetAllocatedBytesForCurrentThread();
                    var started = Stopwatch.GetTimestamp();
                    for (var index = 0; index < iterations; index++)
                        checksum += host.ReadCompletedSerial(snapshot);
                    var elapsed = Stopwatch.GetTimestamp() - started;
                    allocation = GC.GetAllocatedBytesForCurrentThread() - allocation;
                    Assert.Equal(serial * iterations, checksum);
                    rounds.Add(new { condition, round, snapshot, checksum,
                        nanosecondsPerRead = elapsed * 1e9 / Stopwatch.Frequency / iterations,
                        bytesPerRead = (double)allocation / iterations });
                }
            }
        }
        Measure("completed");
        var gate = MetalNative.Send(Device.Value, MetalNative.Selector("newSharedEvent"));
        Assert.NotEqual(0, gate);
        try
        {
            var transfer = ((IMetalImageCacheOwner)host).BeginTransfer(MetalTransferCause.GuestFill);
            MetalV2Native.SendEventValue(transfer.CommandBuffer,
                MetalNative.Selector("encodeWaitForEvent:value:"), gate, 1);
            Assert.True(host.FlushCommandBuffer() > serial);
            // Exercise the guard's cost when its shortcut is unavailable, too.
            Measure("pending");
        }
        finally
        {
            MetalNative.SendVoid(gate, MetalNative.Selector("setSignaledValue:"), (nint)1);
            MetalNative.SendVoid(gate, MetalNative.Selector("release"));
        }
        WaitForGpu(host, host.LastWorkSerial);
        WaitForCompletions(host);
        File.WriteAllText(path, JsonSerializer.Serialize(new {
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            iterations, serial, rounds }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
