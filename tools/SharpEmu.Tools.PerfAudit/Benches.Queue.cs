// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.PerfAudit;

/// <summary>
/// 7. CommandStreamQueue: the guest-thread enqueue, the Monitor wake of the worker, and the worker's
/// slice loop (CommandStreamWorker.Run -> ProcessOne -> WaitForWork), with the real queue and worker
/// over a lean host so only the queue machinery is measured.
/// </summary>
internal static class QueueBenches
{
    private const double SlicesPerFrame = 83;
    private const double SubmitsPerFrame = 20;

    public static void Run()
    {
        Console.WriteLine("--- 7. CommandStreamQueue / CommandStreamWorker ---");
        using var memory = new NativeGuestMemory(64 << 20);
        var words = Pm4Benches.BuildStream(1, 5, false, out _);
        memory.WriteWords(Pm4Benches.StreamAddress, words);
        var dwords = (uint)words.Length;
        var address = Pm4Benches.StreamAddress;

        // 7a: enqueue only, no worker. The queue is drained (discarded) every 4096 pushes so it stays bounded;
        // that amortised cost (~nothing per push) is inside the figure.
        {
            var host = new CountingCommandHost(memory, true);
            var queue = new CommandStreamQueue(host);
            ulong submission = 1;
            Harness.Measure("7a", "CommandStreamQueue.EnqueueGraphics (no worker, uncontended)", 1, SlicesPerFrame, i =>
            {
                if ((i & 4095) == 4095) queue.DiscardAll();
                queue.EnqueueGraphics(address, dwords, submission++, null);
            }, unit: "slice", note: "lock + new CommandSubmission + list append");
        }

        // 7b-7e share one live worker that parks in Monitor.Wait between submissions.
        {
            var host = new CountingCommandHost(memory, true);
            var queue = new CommandStreamQueue(host);
            var worker = new CommandStreamWorker(queue, host, () => false, true, "bench worker");
            worker.Start();
            ulong submission = 1;
            Thread.Sleep(50);

            // 7b/7c: wake latency from a parked worker. Each sample spaces the push by ~1 ms of spinning so the worker is
            // really parked (not in its run-loop between slices).
            const int Samples = 3000;
            var latency = new double[Samples];
            var enqueueCost = new double[Samples];
            var roundTrip = new double[Samples];
            var tickNs = 1e9 / Stopwatch.Frequency;
            for (var warm = 0; warm < 200; warm++)
            {
                queue.EnqueueGraphics(address, dwords, submission++, null);
                queue.WaitForIdle();
            }

            for (var i = 0; i < Samples; i++)
            {
                SpinFor(Stopwatch.Frequency / 1000);
                var beginsBefore = Volatile.Read(ref host.Begins);
                var t0 = Stopwatch.GetTimestamp();
                queue.EnqueueGraphics(address, dwords, submission++, null);
                var t1 = Stopwatch.GetTimestamp();
                queue.WaitForIdle();
                var t2 = Stopwatch.GetTimestamp();
                if (Volatile.Read(ref host.Begins) == beginsBefore) { i--; continue; }
                latency[i] = (Volatile.Read(ref host.BeginTicks) - t0) * tickNs;
                enqueueCost[i] = (t1 - t0) * tickNs;
                roundTrip[i] = (t2 - t0) * tickNs;
            }

            Harness.Record("7b", "enqueue -> worker BeginSubmission, parked worker (wake latency)", latency, [0], SubmitsPerFrame, "submit",
                "Monitor.Pulse -> worker thread scheduled; each push separated by 1 ms idle");
            Harness.Record("7c", "EnqueueGraphics call cost with a parked worker (producer side, includes worker notification)", enqueueCost, [0], SlicesPerFrame, "slice");
            Harness.Record("7d", "enqueue + WaitForIdle round trip, parked worker, 1 tiny slice", roundTrip, [0], SubmitsPerFrame, "submit");

            // 7e: pipelined: push a frame's worth of slices, then wait; worker never parks until the end.
            var perRun = new List<double>();
            var perRunBytes = new List<double>();
            for (var run = 0; run < 7; run++)
            {
                GC.Collect();
                var before = GC.GetTotalAllocatedBytes(true);
                var start = Stopwatch.GetTimestamp();
                const int Frames = 200;
                for (var frame = 0; frame < Frames; frame++)
                {
                    for (var slice = 0; slice < (int)SlicesPerFrame; slice++) queue.EnqueueGraphics(address, dwords, submission++, null);
                    queue.WaitForIdle();
                }

                var elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
                var ops = Frames * SlicesPerFrame;
                perRun.Add(elapsed / ops);
                perRunBytes.Add((GC.GetTotalAllocatedBytes(true) - before) / ops);
            }

            Harness.Record("7e", "pipelined: 83 pushes then WaitForIdle, per slice (queue + worker loop + 1 draw each, tiny host)", perRun, perRunBytes, SlicesPerFrame, "slice",
                "process-wide allocations; includes the interpreter running a 4-packet stream per slice");
            worker.Stop();
        }
    }

    private static void SpinFor(long ticks)
    {
        var end = Stopwatch.GetTimestamp() + ticks;
        while (Stopwatch.GetTimestamp() < end) Thread.SpinWait(20);
    }
}
