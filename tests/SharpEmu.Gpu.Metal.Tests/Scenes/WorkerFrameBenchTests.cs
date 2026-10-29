// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests.Scenes;

public sealed class WorkerFrameBenchTests(ITestOutputHelper output) : SceneTestBase(output)
{
    private const uint Size = 32;
    private const int Draws = 150;

    // SHARPEMU_WORKER_FRAME_BENCH_OUTPUT=<path>: a stream of 150 small draws with nothing else, and the
    // same stream with an occlusion dump, an end-of-pipe label, a WRITE_DATA or a pair of dumps after every
    // draw, each run through the whole path (interpreter, translation, Metal host, GPU). The difference
    // over the draws-only stream, per added packet, is what the packet costs the worker with a render
    // pass open. Variants alternate per round so a drift in the machine falls on all of them.
    [Fact]
    public void MeasureWorkerStreamCostsWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("SHARPEMU_WORKER_FRAME_BENCH_OUTPUT");
        if (path is null) return;
        using var rig = CreateRig();
        if (rig is null) return;
        rig.Host.NativeOcclusionEnabled = true;
        var target = rig.ColorTarget(Size, Size);
        var queries = rig.Memory.Allocate(Draws * 256UL, 256);
        var labels = rig.Memory.Allocate(Draws * 16UL, 256);
        var data = rig.Memory.Allocate(Draws * 16UL, 256);

        ScenePm4 Stream(Action<ScenePm4, int>? after, uint depthCount = 1u << 8)
        {
            var pm4 = new ScenePm4();
            var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size);
            state.Context[Cx.DbCountControl] = depthCount;
            for (var draw = 0; draw < Draws; draw++)
            {
                rig.DrawMesh(pm4, state, SceneDraws.Quad(0.1f, 1, 0, 0));
                after?.Invoke(pm4, draw);
            }

            return pm4;
        }

        var variants = new (string Name, int PerDraw, ScenePm4 Stream)[]
        {
            ("draws_only", 0, Stream(null)),
            ("dump_16_blocks", 1, Stream((pm4, draw) => pm4.EventWithAddress(ScenePm4.EventPixelPipeStatDump, 1, queries + (ulong)draw * 256))),
            ("dump_5x_16_blocks", 5, Stream((pm4, draw) =>
            {
                for (var dump = 0; dump < 5; dump++)
                    pm4.EventWithAddress(ScenePm4.EventPixelPipeStatDump, 1, queries + (ulong)draw * 256 + (ulong)dump * 8);
            })),
            ("write_data_256_bytes", 1, Stream((pm4, draw) =>
            {
                var query = queries + (ulong)draw * 256;
                pm4.Packet(ScenePm4.OpWriteData, [0x100u, (uint)query, (uint)(query >> 32), .. new uint[64]]);
            })),
            // UE4 and UE5 zero a query's 256 bytes with WRITE_DATA, then dump its begin and end into it.
            ("zero_fill_then_dump_pair", 3, Stream((pm4, draw) =>
            {
                var query = queries + (ulong)draw * 256;
                pm4.Packet(ScenePm4.OpWriteData, [0x100u, (uint)query, (uint)(query >> 32), .. new uint[64]]);
                pm4.EventWithAddress(ScenePm4.EventPixelPipeStatDump, 1, query);
                pm4.EventWithAddress(ScenePm4.EventPixelPipeStatDump, 1, query + 8);
            })),
            ("dump_pair_16_blocks", 2, Stream((pm4, draw) =>
            {
                pm4.EventWithAddress(ScenePm4.EventPixelPipeStatDump, 1, queries + (ulong)draw * 256);
                pm4.EventWithAddress(ScenePm4.EventPixelPipeStatDump, 1, queries + (ulong)draw * 256 + 8);
            })),
            ("release_mem_write32", 1, Stream((pm4, draw) => ReleaseMemory(pm4, labels + (ulong)draw * 16, 1u << 29, (uint)draw))),
            ("release_mem_write32_interrupt", 1, Stream((pm4, draw) => ReleaseMemory(pm4, labels + (ulong)draw * 16, (1u << 29) | (2u << 24), (uint)draw))),
            ("write_data_2_dwords", 1, Stream((pm4, draw) =>
                pm4.Packet(ScenePm4.OpWriteData, 0x100, (uint)(data + (ulong)draw * 16), (uint)((data + (ulong)draw * 16) >> 32), (uint)draw, 2))),
        };

        // SHARPEMU_WORKER_FRAME_BENCH_ONLY=<name> runs that variant alone (a sampling profiler wants one), and
        // ..._ROUNDS makes the run longer.
        var only = Environment.GetEnvironmentVariable("SHARPEMU_WORKER_FRAME_BENCH_ONLY");
        if (only is not null)
        {
            var names = only.Split(',');
            variants = variants.Where(variant => variant.Name == "draws_only" || names.Contains(variant.Name)).ToArray();
        }
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_WORKER_FRAME_BENCH_ROUNDS"), out var requested) ? requested : 12;
        // Each stream is uploaded once and queued again every round, so a long run does not use up the scene's memory.
        var words = variants.Select(variant => variant.Stream.ToArray()).ToArray();
        var uploaded = words.Select(stream => rig.Memory.Upload<uint>(stream, 256)).ToArray();
        ulong submission = 1000;
        var times = variants.Select(_ => new List<double>()).ToArray();
        var allocated = variants.Select(_ => new List<long>()).ToArray();
        for (var round = 0; round < rounds + 2; round++)
        {
            for (var index = 0; index < variants.Length; index++)
            {
                var variant = variants[(index + round) % variants.Length];
                var slot = Array.IndexOf(variants, variant);
                var before = GC.GetTotalAllocatedBytes(false);
                var started = Stopwatch.GetTimestamp();
                rig.Host.CommandStream.EnqueueGraphics(uploaded[slot], (uint)words[slot].Length, submission++, null);
                rig.Drain();
                var elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                var bytes = GC.GetTotalAllocatedBytes(false) - before;
                if (round < 2) continue;
                times[slot].Add(elapsed);
                allocated[slot].Add(bytes);
            }
        }

        var baseline = Median(times[0]);
        var baselineBytes = MedianLong(allocated[0]);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            draws = Draws,
            variants = variants.Select((variant, slot) => new
            {
                name = variant.Name,
                submitMillisecondsMedian = Median(times[slot]),
                submitMillisecondsBest = times[slot].Min(),
                microsecondsPerExtraDumpOverOne = variant.Name == "dump_5x_16_blocks"
                    ? (Median(times[slot]) - Median(times[Array.FindIndex(variants, other => other.Name == "dump_16_blocks")])) * 1000.0 / (Draws * 4)
                    : 0,
                microsecondsPerAddedPacket = variant.PerDraw == 0 ? 0 : (Median(times[slot]) - baseline) * 1000.0 / (Draws * variant.PerDraw),
                allocatedKilobytesPerSubmit = MedianLong(allocated[slot]) / 1024.0,
                allocatedBytesPerAddedPacket = variant.PerDraw == 0 ? 0 : (MedianLong(allocated[slot]) - baselineBytes) / (double)(Draws * variant.PerDraw),
            }),
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ReleaseMemory(ScenePm4 pm4, ulong label, uint control, uint value) =>
        pm4.Packet(0x49, 0x28u | (5u << 8), control, (uint)label, (uint)(label >> 32), value, 0, 0);

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private static long MedianLong(List<long> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
