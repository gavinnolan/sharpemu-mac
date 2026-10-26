// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Gpu.Metal;
using SharpEmu.Gpu.Metal.Tests.Scenes;

namespace SharpEmu.PerfAudit;

/// <summary>
/// Whole-path scenario: a PM4 stream of textured draws through the real interpreter, RenderExecutor,
/// ShaderPipelineCache, resource materialization, Metal host binding/encoding, commit and GPU wait,
/// driven the way the Metal worker drives it, over the scene rig (real device, real shader compiler).
/// </summary>
internal static class E2EBenches
{
    private const uint Target = 256;
    private const int Pairs = 50;
    private const int StateVariants = 4;
    private const int Textures = 8;
    private const int Meshes = 16;

    private static uint[] WithNops(uint[] code, int nops)
    {
        // Insert nops before the final s_endpgm so each variant has a distinct hash and code size.
        var result = new uint[code.Length + nops];
        Array.Copy(code, result, code.Length - 1);
        for (var i = 0; i < nops; i++) result[code.Length - 1 + i] = 0xBF800000;
        result[^1] = code[^1];
        return result;
    }

    /// <summary>
    /// A pixel program that s_loads 8 T#, 2 S# and 4 V# through the pointer in s[0:1] (the user data), samples all
    /// 8 textures through the two samplers, loads a record from each of the 4 buffers and sums everything into MRT0.
    /// </summary>
    private static uint[] HeavyPixel()
    {
        var code = new List<uint>
        {
            0xBEFC0302,                // s_mov_b32 m0, s2 (two user SGPRs, so the primitive mask is s2)
            0xC8080000, 0xC8090001,    // v_interp p1/p2 attr0.x -> v2
            0xC80C0100, 0xC80D0101,    // attr0.y -> v3
        };
        for (var i = 0u; i < 8; i++) { code.Add(0xF40C0000 | ((16 + 8 * i) << 6)); code.Add(0xFA000000 | (i * 0x20)); }   // s_load_dwordx8 T#i
        for (var j = 0u; j < 2; j++) { code.Add(0xF4080000 | ((80 + 4 * j) << 6)); code.Add(0xFA000100 | (j * 0x10)); }   // s_load_dwordx4 S#j
        for (var k = 0u; k < 4; k++) { code.Add(0xF4080000 | ((88 + 4 * k) << 6)); code.Add(0xFA000000 | (0x120 + k * 0x10)); } // s_load_dwordx4 V#k
        code.Add(0xBF8CC07F);          // s_waitcnt lgkmcnt(0)
        for (var i = 0u; i < 8; i++)   // image_sample v[4+4i:7+4i], v[2:3], T#i, S#(i%2)
        {
            code.Add(0xF0800F08);
            code.Add(((20 + (i % 2)) << 21) | ((4 + 2 * i) << 16) | ((4 + 4 * i) << 8) | 2);
        }
        for (var k = 0u; k < 4; k++)   // buffer_load_dwordx4 v[36+4k:39+4k], v0, V#k idxen
        {
            code.Add(0xE0382000);
            code.Add(0x80000000 | ((22 + k) << 16) | ((36 + 4 * k) << 8) | 0);
        }
        code.Add(0xBF8C3F70);          // s_waitcnt vmcnt(0)
        for (var i = 1u; i < 8; i++) code.Add(0x06000000 | (4u << 17) | (4u << 9) | (256 + 4 + 4 * i));   // v_add_f32 v4, v(4+4i), v4
        for (var k = 0u; k < 4; k++) code.Add(0x06000000 | (4u << 17) | (4u << 9) | (256 + 36 + 4 * k));
        code.Add(0xF800180F); code.Add(0x07060504);   // exp mrt0 v4, v5, v6, v7 done vm
        code.Add(0xBF810000);          // s_endpgm
        return [.. code];
    }

    private sealed class Scenario
    {
        public required SceneRig Rig;
        public required uint[] Stream;
        public required int Draws;
        public required int PacketWords;
        public ulong Address;
    }

    private static ulong _submissionId = 1_000_000;

    /// <param name="descriptorChurn">0 = 8 rotating texture descriptors (warm); 1 = every draw gets fresh T#/S# words (cold materialization).</param>
    private static Scenario Build(SceneRig rig, int draws, int pairs, int variants, int descriptorChurn, bool delta = false, bool heavy = false)
    {
        var vs = new ulong[pairs];
        var ps = new ulong[pairs];
        for (var p = 0; p < pairs; p++)
        {
            vs[p] = rig.Program(WithNops(ScenePrograms.FetchVertex, p));
            ps[p] = rig.Program(WithNops(heavy ? HeavyPixel() : ScenePrograms.SampledTexture, p));
        }

        // Eight small linear textures, sixteen meshes (VS user data = position V# + param V#).
        var images = new ulong[Textures];
        for (var t = 0; t < Textures; t++)
        {
            var texels = new byte[16 * 16 * 4];
            for (var i = 0; i < texels.Length; i += 4) BitConverter.TryWriteBytes(texels.AsSpan(i), 0xFF000000u | (uint)(i * 2654435761u >> 8 & 0xFFFFFF) | (uint)t);
            images[t] = rig.LinearImage(16, 16, 4, texels);
        }

        var meshes = new uint[Meshes][];
        for (var m = 0; m < Meshes; m++)
        {
            var x = -1f + (m % 4) * 0.5f;
            var y = -1f + (m / 4) * 0.5f;
            meshes[m] = rig.UploadMesh(
            [
                SceneVertex.At(x, y, 0.5f, 1, 0, 0),
                SceneVertex.At(x + 0.4f, y, 0.5f, 1, 1, 0),
                SceneVertex.At(x, y + 0.4f, 0.5f, 0, 0, 1),
            ]);
        }

        // Heavy: descriptor tables in guest memory. Warm = 8 tables reused; churn = one table (distinct T#/S# words) per draw.
        var tableCount = descriptorChurn == 0 ? 8 : Math.Min(draws, 6000);
        var tables = new ulong[heavy ? tableCount : 0];
        if (heavy)
        {
            var bufferBase = rig.Memory.Allocate(4 * 0x1000, 0x1000);
            for (var t = 0; t < tableCount; t++)
            {
                var table = new uint[0x160 / 4];
                for (var i = 0; i < 8; i++)
                    Descriptors.Texture(images[(t + i) % Textures], 56, 16, 16, minLodFixed: descriptorChurn == 0 ? 0u : (uint)((t * 8 + i) & 0xFFF)).CopyTo(table, i * 8);
                for (var j = 0; j < 2; j++)
                    Descriptors.Sampler(lodBias: descriptorChurn == 0 ? 0 : ((t + j) % 64) / 64f).CopyTo(table, 0x100 / 4 + j * 4);
                for (var k = 0; k < 4; k++)
                    Descriptors.Buffer(bufferBase + (ulong)k * 0x1000, 16, 256).CopyTo(table, 0x120 / 4 + k * 4);
                tables[t] = rig.Memory.Upload<uint>(table, 256);
            }
        }

        var target = rig.ColorTarget(Target, Target);
        var pm4 = new ScenePm4();
        var sampler = Descriptors.Sampler();
        for (var d = 0; d < draws; d++)
        {
            var pair = d % pairs;
            var variant = (d / pairs) % variants;
            var state = new SceneState(Target, Target).ColorTarget(0, target, Target, Target);
            // Four pipeline-state variants: write mask and blending change the pipeline.
            state.Context[Cx.CbTargetMask] = variant < 2 ? 0xFu : 0x7u;
            state.Context[Cx.CbBlend0Control] = (variant & 1) == 0 ? 0u : (1u << 30) | 4u | (5u << 8) | (4u << 16) | (5u << 24);
            var texture = Descriptors.Texture(images[d % Textures], 56, 16, 16,
                minLodFixed: descriptorChurn == 0 ? 0u : (uint)(d & 0xFFF));
            var smp = descriptorChurn == 0 ? sampler : Descriptors.Sampler(lodBias: descriptorChurn == 0 ? 0 : (d % 64) / 64f);
            state.VertexShader(vs[pair], meshes[d % Meshes]);
            state.PixelShader(ps[pair], heavy ? [(uint)tables[d % tableCount], (uint)(tables[d % tableCount] >> 32)] : [.. texture, .. smp]);
            if (!delta || d == 0)
            {
                state.Emit(pm4);
            }
            else
            {
                // What a lean engine re-sends per draw: batched program/user-data blocks plus the two pipeline-state registers.
                var psWords = state.Shader.Where(r => r.Key is >= Sh.PgmLoPs and <= Sh.PgmRsrc2Ps).Select(r => r.Value).ToArray();
                pm4.Shader(Sh.PgmLoPs, psWords);
                pm4.Shader(Sh.UserDataPs0, state.Shader.Where(r => r.Key >= Sh.UserDataPs0 && r.Key < Sh.UserDataPs0 + 16).Select(r => r.Value).ToArray());
                pm4.Shader(Sh.PgmLoEs, state.Shader[Sh.PgmLoEs], state.Shader[Sh.PgmHiEs]);
                pm4.Shader(Sh.PgmRsrc1Gs, state.Shader[Sh.PgmRsrc1Gs], state.Shader[Sh.PgmRsrc2Gs]);
                pm4.Shader(Sh.UserDataGs0, state.Shader.Where(r => r.Key >= Sh.UserDataGs0 && r.Key < Sh.UserDataGs0 + 16).Select(r => r.Value).ToArray());
                pm4.Context(Cx.CbTargetMask, state.Context[Cx.CbTargetMask]);
                pm4.Context(Cx.CbBlend0Control, state.Context[Cx.CbBlend0Control]);
            }

            pm4.NumInstances(1);
            pm4.DrawAuto(3);
        }

        var words = pm4.ToArray();
        return new Scenario { Rig = rig, Stream = words, Draws = draws, PacketWords = words.Length, Address = rig.Memory.Upload<uint>(words, 256) };
    }

    private sealed record RunTimes(double CpuMs, double CommitWaitMs, double AllocBytes, double GcCount);

    /// <summary>Enqueue the stream and drain it like SceneRig.Drain, timing the CPU part and the commit/GPU wait separately.</summary>
    private static RunTimes RunOnce(Scenario scenario)
    {
        var rig = scenario.Rig;
        rig.Host.CommandStream.EnqueueGraphics(scenario.Address, (uint)scenario.Stream.Length, _submissionId++, null);
        var gcBefore = GC.CollectionCount(0);
        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var cpu = new Stopwatch();
        var wait = new Stopwatch();
        var host = rig.Host;
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (true)
        {
            cpu.Start();
            host.RunPendingCommands();
            host.RunCommandStreamSlices(64);
            cpu.Stop();
            if (host.CommandStream.HasUnblockedPending || rig.Relay.HasPendingCommands) continue;
            wait.Start();
            host.CommitCoalescedFlush();
            host.FlushAndWaitForGpu();
            host.PollGpuCompletions();
            wait.Stop();
            if (!host.CommandStream.HasPending) break;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("stream stayed blocked");
            host.CommandStream.RetryBlocked();
        }

        return new RunTimes(cpu.Elapsed.TotalMilliseconds, wait.Elapsed.TotalMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - allocBefore, GC.CollectionCount(0) - gcBefore);
    }

    public static string[]? OnlyScenarios;
    public static double SoakSeconds;
    public static bool TrackAllocations;

    /// <summary>GCAllocationTick sampler (one event per ~100 KB allocated): bytes by type, all threads.</summary>
    private sealed class AllocationTypes : System.Diagnostics.Tracing.EventListener
    {
        private readonly Dictionary<string, (long Bytes, long Ticks)> _types = [];
        private readonly object _gate = new();

        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
        }

        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            if (e.EventName is null || !e.EventName.StartsWith("GCAllocationTick")) return;
            var type = e.Payload![e.PayloadNames!.IndexOf("TypeName")] as string ?? "?";
            var amount = Convert.ToInt64(e.Payload[e.PayloadNames.IndexOf("AllocationAmount64")]);
            lock (_gate)
            {
                _types.TryGetValue(type, out var current);
                _types[type] = (current.Bytes + amount, current.Ticks + 1);
            }
        }

        public void Report(string id, double draws)
        {
            lock (_gate)
            {
                var total = _types.Values.Sum(v => v.Bytes);
                Console.WriteLine($"[{id}] allocation by type (GCAllocationTick sample, {total / draws:F0} B/draw sampled over {draws:F0} draws):");
                foreach (var (type, v) in _types.OrderByDescending(t => t.Value.Bytes).Take(25))
                    Console.WriteLine($"[{id}]   {v.Bytes / draws,8:F1} B/draw  {100.0 * v.Bytes / total,5:F1}%  ticks={v.Ticks,5}  {type}");
            }
        }
    }

    private static void Scenario5(string id, string name, SceneRig rig, int draws, int pairs, int variants, int churn, int runs, bool delta = false, bool heavy = false)
    {
        if (OnlyScenarios is { } only && !only.Contains(id)) return;
        var scenario = Build(rig, draws, pairs, variants, churn, delta, heavy);
        Console.WriteLine($"[{id}] stream {scenario.PacketWords * 4 / 1024} KiB, {scenario.PacketWords / (double)draws:F0} dwords/draw, pairs={pairs} pipelineStates<={pairs * variants}");
        var sw = Stopwatch.StartNew();
        var first = RunOnce(scenario);
        Console.WriteLine($"[{id}] first (cold) run: cpu {first.CpuMs:F0} ms, wait {first.CommitWaitMs:F0} ms ({sw.Elapsed.TotalSeconds:F1} s total incl. shader+pipeline compile)");
        // Pipelines compile asynchronously and draws may be left out meanwhile: warm until two runs agree within 8%.
        var previous = double.MaxValue;
        for (var warm = 0; warm < 12; warm++)
        {
            var t = RunOnce(scenario);
            if (Math.Abs(t.CpuMs - previous) / previous < 0.08) break;
            previous = t.CpuMs;
        }

        if (SoakSeconds > 0 || TrackAllocations)
        {
            AllocationTypes? listener = TrackAllocations ? new AllocationTypes() : null;
            Console.WriteLine($"[{id}] SOAK START {DateTime.Now:HH:mm:ss} pid={Environment.ProcessId}");
            var soak = Stopwatch.StartNew();
            var iterations = 0;
            do { RunOnce(scenario); iterations++; } while (soak.Elapsed.TotalSeconds < SoakSeconds);
            Console.WriteLine($"[{id}] SOAK END {iterations} iterations in {soak.Elapsed.TotalSeconds:F1} s");
            listener?.Report(id, iterations * (double)draws);
            listener?.Dispose();
        }

        var cpuNs = new List<double>();
        var waitNs = new List<double>();
        var bytes = new List<double>();
        for (var run = 0; run < runs; run++)
        {
            var t = RunOnce(scenario);
            cpuNs.Add(t.CpuMs * 1e6 / draws);
            waitNs.Add(t.CommitWaitMs * 1e6 / draws);
            bytes.Add(t.AllocBytes / draws);
            Console.WriteLine($"[{id}]   run {run}: cpu {t.CpuMs:F1} ms = {t.CpuMs * 1e6 / draws:F0} ns/draw, commit+wait {t.CommitWaitMs:F1} ms, alloc {t.AllocBytes / draws:F0} B/draw, gen0 GCs {t.GcCount}");
        }

        Harness.Record(id, name, cpuNs, bytes, Pm4Benches.FrameDraws, note: "CPU worker time per draw, GPU wait excluded");
        Harness.Record(id + "w", name + " [commit + GPU wait only]", waitNs, bytes, Pm4Benches.FrameDraws);
        if (SharpEmu.Libs.VideoOut.RenderPhaseProfile.Enabled) PhaseReport(id, scenario);
    }

    private static void PhaseReport(string id, Scenario scenario)
    {
        var type = typeof(SharpEmu.Libs.VideoOut.RenderPhaseProfile);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var ticks = (long[])type.GetField("_ticks", flags)!.GetValue(null)!;
        var entries = (long[])type.GetField("_entries", flags)!.GetValue(null)!;
        Array.Clear(ticks);
        Array.Clear(entries);
        const int Runs = 3;
        for (var run = 0; run < Runs; run++) RunOnce(scenario);
        var total = Runs * (double)scenario.Draws;
        var rows = Enum.GetValues<SharpEmu.Libs.VideoOut.RenderPhaseProfile.Phase>()
            .Where(phase => (int)phase < ticks.Length).Select(phase => (Phase: phase, Us: ticks[(int)phase] * 1e6 / Stopwatch.Frequency / total, Entries: entries[(int)phase] / total))
            .Where(row => row.Us > 0.005)
            .OrderByDescending(row => row.Us)
            .ToList();
        Console.WriteLine($"[{id}] exclusive phase time per draw over {Runs} profiled runs (profile on: scope overhead inflates small phases), sum {rows.Sum(r => r.Us):F2} us/draw");
        foreach (var row in rows.Take(32))
            Console.WriteLine($"[{id}]   {row.Phase,-34} {row.Us,8:F3} us/draw   {row.Entries,7:F2} entries/draw   x6331 = {row.Us * 6331 / 1000.0,7:F2} ms/scaled workload");
    }

    public static void Run(int draws = 6000)
    {
        Console.WriteLine("--- 8. End-to-end per draw over the real Metal host (SceneRig) ---");
        var device = MetalNative.MTLCreateSystemDefaultDevice();
        using var rig = new SceneRig(device);
        Scenario5("8a", $"E2E {draws} textured draws, 50 VS/PS pairs x 4 pipeline states, 8 rotating T#/S# (warm)", rig, draws, Pairs, StateVariants, 0, 5);
        Scenario5("8b", $"E2E {draws} draws, same but fresh T#/S# words every draw (cold materialization)", rig, draws, Pairs, StateVariants, 1, 5);
        Scenario5("8d", $"E2E {draws} draws, lean engine (delta state: ~11 packets/draw), 50 pairs x 4 states, 8 rotating T#/S#", rig, draws, Pairs, StateVariants, 0, 5, delta: true);
        Scenario5("8e", $"E2E {draws} draws, HEAVY bind: 8 T# + 2 S# + 4 V# via table pointer (warm, 8 tables), 50 pairs x 4 states", rig, draws, Pairs, StateVariants, 0, 5, delta: true, heavy: true);
        Scenario5("8f", $"E2E {draws} draws, HEAVY bind, fresh table + T#/S# words every draw (cold descriptors)", rig, draws, Pairs, StateVariants, 1, 5, delta: true, heavy: true);
        Scenario5("8c", $"E2E {draws} draws, ONE shader pair, ONE pipeline state, 8 rotating T#/S#", rig, draws, 1, 1, 0, 5);
    }
}
