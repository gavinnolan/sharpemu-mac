// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text.Json;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed unsafe partial class MetalEndOfPipeOrderTests
{
    // SHARPEMU_NONDRAW_BENCH_OUTPUT=<path>: the worker's cost of one end-of-pipe label and one
    // occlusion dump, with no encoder open (the copy is its own transfer) and with a compute
    // encoder open (the copy is held for the encoder's end, as it is behind an open render pass),
    // and of the commit that lands what was held. Calls run in batches of 128, a commit between.
    [Fact]
    public void MeasureEndOfPipeAndOcclusionDumpWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("SHARPEMU_NONDRAW_BENCH_OUTPUT");
        if (path is null || !RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var host = rig.Host;
        host.NativeOcclusionEnabled = true;
        const ulong labels = GuestBase + 0x20000;
        const ulong queries = GuestBase + 0x30000;
        const int batch = 128;
        var results = new List<object>();
        // SHARPEMU_NONDRAW_BENCH_ROUNDS lengthens a run for a sampling profiler; ..._ONLY keeps the cases whose name contains it.
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_NONDRAW_BENCH_ROUNDS"), out var requested) ? requested : 14;
        var only = Environment.GetEnvironmentVariable("SHARPEMU_NONDRAW_BENCH_ONLY");

        void Measure(string name, bool computeOpen, Action<int> call)
        {
            if (only is not null && !name.Contains(only, StringComparison.Ordinal)) return;
            var callNs = new List<double>();
            var commitNs = new List<double>();
            long allocated = 0;
            long calls = 0;
            for (var round = 0; round < rounds; round++)
            {
                if (computeOpen) _ = host.EnsureComputeEncoder();
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                for (var index = 0; index < batch; index++) call(index);
                var called = Stopwatch.GetTimestamp();
                var allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
                var serial = host.FlushCommandBuffer();
                var flushed = Stopwatch.GetTimestamp();
                WaitForGpu(host, serial);
                WaitForCompletions(host);
                if (round < 4) continue;
                callNs.Add((called - started) * 1e9 / Stopwatch.Frequency / batch);
                commitNs.Add((flushed - called) * 1e9 / Stopwatch.Frequency);
                allocated += allocatedAfter - allocatedBefore;
                calls += batch;
            }

            callNs.Sort();
            commitNs.Sort();
            results.Add(new
            {
                name,
                computeOpen,
                nsPerCallMedian = callNs[callNs.Count / 2],
                nsPerCallBest = callNs[0],
                commitMicrosecondsMedian = commitNs[commitNs.Count / 2] / 1000,
                allocatedBytesPerCall = (double)allocated / calls,
            });
        }

        foreach (var computeOpen in new[] { false, true })
        {
            var open = computeOpen ? "held" : "immediate";
            Measure($"eop_write32_{open}", computeOpen, index =>
                host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write32, 1, labels + (ulong)index * 16, (uint)index)));
            Measure($"eop_write64_{open}", computeOpen, index =>
                host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write64, 1, labels + (ulong)index * 16, (ulong)index)));
            Measure($"eop_interrupt_only_{open}", computeOpen, index =>
                host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.InterruptOnly, 1, EventId: 0, ContextId: (uint)index)));
            Measure($"occlusion_dump_16_blocks_{open}", computeOpen, index =>
                Assert.True(host.DumpOcclusionCounter(0, queries + (ulong)index * 256, 0xFFFF, 16)));
            Measure($"occlusion_dump_1_block_{open}", computeOpen, index =>
                Assert.True(host.DumpOcclusionCounter(0, queries + (ulong)index * 256, 1, 8)));
            Measure($"cp_write_8_bytes_{open}", computeOpen, index =>
                host.RecordCommandProcessorWrite(labels + (ulong)index * 16, BitConverter.GetBytes((ulong)index), "WRITE_DATA"));
        }

        // The dump as it runs behind an open render pass: a visibility batch is open, so the copies are held for the
        // encoder's end. The batch is made by hand (no pass is drawn here) and rewound each round.
        {
            var hostType = typeof(SharpEmu.Gpu.Metal.V2.MetalRenderHost);
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var batchType = hostType.GetNestedType("OcclusionBatch", System.Reflection.BindingFlags.NonPublic)!;
            var newBuffer = hostType.GetMethod("NewOcclusionBuffer", any)!;
            var counts = (nint)newBuffer.Invoke(host, [4096 * 8])!;
            var resultBuffer = (nint)newBuffer.Invoke(host, [4096 * 8])!;
            var openBatch = Activator.CreateInstance(batchType, any | System.Reflection.BindingFlags.Public, null, [counts, resultBuffer], null)!;
            hostType.GetField("_oqOpen", any)!.SetValue(host, openBatch);
            var slot = batchType.GetField("Slot", any | System.Reflection.BindingFlags.Public)!;
            foreach (var coalesce in new[] { false, true, false, true })
            {
                host.CoalesceOcclusionDumps = coalesce;
                Measure($"occlusion_dump_16_blocks_batch_open_coalesce_{(coalesce ? "on" : "off")}", true, index =>
                {
                    if (index == 0) slot.SetValue(openBatch, 0);
                    Assert.True(host.DumpOcclusionCounter(0, queries + (ulong)index * 256, 0xFFFF, 16));
                });
            }

            hostType.GetField("_oqOpen", any)!.SetValue(host, null);
            host.CoalesceOcclusionDumps = true;
        }

        File.WriteAllText(path, JsonSerializer.Serialize(new {
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), results },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
