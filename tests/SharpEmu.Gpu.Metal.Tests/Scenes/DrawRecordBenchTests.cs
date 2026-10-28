// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.VideoOut;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests.Scenes;

/// <summary>
/// The worker's per-draw recording cost (RenderExecutor.RecordDraw and everything below it): the same
/// three-texture, depth-tested draw repeated, then with one binding or the pixel program alternating.
/// </summary>
public sealed class DrawRecordBenchTests(ITestOutputHelper output) : SceneTestBase(output)
{
    private const uint Rgba8 = 56;
    private const uint TextureSize = 8;
    private const uint TargetSize = 64;
    private const int DrawsPerSubmit = 256;

    // Three images sampled through one sampler and summed (T# s[0:7], s[8:15], s[16:23], S# s[24:27]; the
    // primitive mask is s28), the way a material pass reads several textures.
    private static readonly uint[] SampledThree =
    [
        0xBEFC031C,                // s_mov_b32 m0, s28
        0xC8080000,                // v_interp_p1_f32_e32 v2, v0, attr0.x
        0xC8090001,                // v_interp_p2_f32_e32 v2, v1, attr0.x
        0xC80C0100,                // v_interp_p1_f32_e32 v3, v0, attr0.y
        0xC80D0101,                // v_interp_p2_f32_e32 v3, v1, attr0.y
        0xF0800F08, 0x00C00402,    // image_sample v[4:7], v[2:3], s[0:7], s[24:27] dmask:0xf dim:SQ_RSRC_IMG_2D
        0xF0800F08, 0x00C20802,    // image_sample v[8:11], v[2:3], s[8:15], s[24:27] dmask:0xf dim:SQ_RSRC_IMG_2D
        0xF0800F08, 0x00C40C02,    // image_sample v[12:15], v[2:3], s[16:23], s[24:27] dmask:0xf dim:SQ_RSRC_IMG_2D
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x06080908, 0x060A0B09, 0x060C0D0A, 0x060E0F0B,    // v_add_f32 v4..v7, v8..v11, v4..v7
        0x06080D08, 0x060A0F09, 0x060C110A, 0x060E130B,    // v_add_f32 v4..v7, v12..v15, v4..v7
        0xF800180F, 0x07060504,    // exp mrt0 v4, v5, v6, v7 done vm
        0xBF810000,                // s_endpgm
    ];

    // The same program under another hash: k s_nop instructions in front of it.
    private static uint[] SampledThreeNops(int count) => [.. Enumerable.Repeat(0xBF800000u, count), .. SampledThree];

    private static readonly Func<RenderPhaseProfile.Phase, long> PhaseTicks =
        typeof(RenderPhaseProfile).GetMethod("PhaseTicks", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<RenderPhaseProfile.Phase, long>>();

    // CLOCK_THREAD_CPUTIME_ID: the recording thread's own CPU time, which a busy machine does not stretch the way it does wall time.
    [DllImport("libSystem.dylib")]
    private static extern ulong clock_gettime_nsec_np(int clockId);

    private static ulong ThreadCpuNanoseconds() => clock_gettime_nsec_np(16);

    // ReportCounters zeroes the host's render-pass count every two seconds, which is what keeps its first-pass diagnostics
    // (strings built per pass while the count is under 4096) running in the emulator; the bench does the same per round.
    private static readonly System.Reflection.FieldInfo RenderPasses =
        typeof(MetalRenderHost).GetField("_renderPasses", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static ulong SolidTexture(SceneRig rig, uint abgr)
    {
        var texels = new uint[TextureSize * 64];
        Array.Fill(texels, abgr);
        var address = rig.Memory.Allocate(TextureSize * 64 * 4, 0x10000);
        rig.Memory.Write(address, MemoryMarshal.AsBytes(texels.AsSpan()));
        return address;
    }

    // Same: one state, 256 draws. AlternatingTexture: texture 0 flips every draw. AlternatingProgram: sixteen
    // pixel programs in turn. PassChurn: two colour targets, a new render pass every four draws. Mixed: all three.
    private enum Variation { Same, AlternatingTexture, AlternatingProgram, PassChurn, Mixed }

    // SHARPEMU_DRAW_RECORD_BENCH_OUTPUT=<path>: microseconds per recorded draw for each variation, and
    // with SHARPEMU_PROFILE_RENDER=1 the self time per render phase. Draws record on this thread
    // (no command thread), so the number is the worker's CPU for packets plus RecordDraw. _ROUNDS and _WARM set
    // the measured and warm-up submissions, _VARIATION keeps one case (a native sampler wants a long one).
    [Fact]
    public void MeasureDrawRecordWhenRequested()
    {
        var path = Environment.GetEnvironmentVariable("SHARPEMU_DRAW_RECORD_BENCH_OUTPUT");
        if (path is null) return;
        Environment.SetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE", "1");
        using var rig = CreateRig();
        Assert.NotNull(rig);
        var colors = new[] { rig.ColorTarget(TargetSize, TargetSize, 16, 0), rig.ColorTarget(TargetSize, TargetSize, 16, 0) };
        var depth = rig.Memory.Allocate(TargetSize * TargetSize * 4 + 0x10000, 0x10000);
        var textures = Enumerable.Range(0, 10).Select(index => SolidTexture(rig, 0x80000040u + (uint)index * 0x00101000u)).ToArray();
        var meshData = rig.UploadMesh(SceneDraws.TexturedQuad());
        var vertexProgram = rig.Program(ScenePrograms.FetchVertex);
        var programs = Enumerable.Range(0, 16).Select(count => rig.Program(SampledThreeNops(count))).ToArray();
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_DRAW_RECORD_BENCH_ROUNDS"), out var configured) ? configured : 60;
        var warm = int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_DRAW_RECORD_BENCH_WARM"), out var warmed) ? warmed : 400;
        var only = Environment.GetEnvironmentVariable("SHARPEMU_DRAW_RECORD_BENCH_VARIATION");
        var phases = Enum.GetValues<RenderPhaseProfile.Phase>().Where(phase => phase != RenderPhaseProfile.Phase.Count).ToArray();

        uint[] Stream(Variation variation)
        {
            var textureChanges = variation is Variation.AlternatingTexture or Variation.Mixed;
            var programChanges = variation is Variation.AlternatingProgram or Variation.Mixed;
            var passChanges = variation is Variation.PassChurn or Variation.Mixed;
            var passLength = variation == Variation.Mixed ? 8 : 4;
            var pm4 = new ScenePm4();
            for (var draw = 0; draw < DrawsPerSubmit; draw++)
            {
                var texture = textures[textureChanges ? (variation == Variation.Mixed ? draw % 7 : draw & 1) : 0];
                var program = programs[programChanges ? (variation == Variation.Mixed ? draw % 5 : draw) % programs.Length : 0];
                var color = colors[passChanges ? draw / passLength & 1 : 0];
                uint[] pixelData =
                [
                    .. Descriptors.Texture(texture, Rgba8, TextureSize, TextureSize),
                    .. Descriptors.Texture(textures[8], Rgba8, TextureSize, TextureSize),
                    .. Descriptors.Texture(textures[9], Rgba8, TextureSize, TextureSize),
                    .. Descriptors.Sampler(),
                ];
                if (draw == 0 || (passChanges && draw % passLength == 0))
                {
                    new SceneState(TargetSize, TargetSize)
                        .ColorTarget(0, color, TargetSize, TargetSize, format: 14, numberType: 7)
                        .DepthTarget(depth, TargetSize, TargetSize, zFormat: 3)
                        .DepthControl(test: true, write: true, CompareFunc.Always)
                        .VertexShader(vertexProgram, meshData)
                        .PixelShader(program, pixelData)
                        .Emit(pm4);
                }
                else if (textureChanges || programChanges)
                {
                    // A guest rewrites the pixel stage's registers when its material changes, nothing else.
                    var changed = new SceneState(TargetSize, TargetSize);
                    changed.Context.Clear();
                    changed.Uconfig.Clear();
                    changed.Shader.Clear();
                    changed.PixelShader(program, pixelData).Emit(pm4);
                }

                pm4.NumInstances(1);
                pm4.DrawAuto(6);
            }

            return pm4.ToArray();
        }

        var results = new List<object>();
        ulong submissionId = 1000;
        var profiling = RenderPhaseProfile.Enabled;
        foreach (var variation in Enum.GetValues<Variation>().Where(variation => only is null || variation.ToString() == only))
        {
            var words = Stream(variation);
            // The stream is placed once: every round submits the same packets.
            var streamAddress = rig.Memory.Upload<uint>(words, 256);
            var microseconds = new List<double>();
            var cpuMicroseconds = new List<double>();
            var phaseTotals = new Dictionary<string, double>();
            var hostDrawMilliseconds = 0.0;
            long allocatedBytes = 0;
            var gen0Collections = 0;
            // The first variation also pays for the JIT's tier-up of the shared recording path.
            var warmRounds = results.Count == 0 ? warm * 2 : warm;
            for (var round = 0; round < warmRounds + rounds; round++)
            {
                RenderPasses.SetValue(rig.Host, 0L);
                var before = profiling ? phases.Select(phase => PhaseTicks(phase)).ToArray() : null;
                rig.Host.CommandStream.EnqueueGraphics(streamAddress, (uint)words.Length, ++submissionId, null);
                long started;
                ulong cpuStarted;
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var collectionsBefore = GC.CollectionCount(0);
                using (profiling ? RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.CommandStream) : default)
                {
                    cpuStarted = ThreadCpuNanoseconds();
                    started = Stopwatch.GetTimestamp();
                    rig.RecordPending();
                }

                var elapsed = Stopwatch.GetTimestamp() - started;
                var cpuElapsed = ThreadCpuNanoseconds() - cpuStarted;
                var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                var collections = GC.CollectionCount(0) - collectionsBefore;
                var recordedBeforeDrain = rig.Host.ReadAndResetPerfCounters().Draws;
                var drainStarted = Stopwatch.GetTimestamp();
                rig.Drain();
                var drained = Stopwatch.GetTimestamp() - drainStarted;
                if (round == warmRounds + 3) Output.WriteLine($"{variation}: {recordedBeforeDrain} draws recorded by RecordPending, drain {drained * 1e6 / Stopwatch.Frequency:F0} us");
                // The warm-up rounds fill the pipeline and image caches and let the JIT reach its optimized code.
                var counters = rig.Host.ReadAndResetPerfCounters();
                if (round < warmRounds) continue;
                Assert.Equal(DrawsPerSubmit, recordedBeforeDrain + counters.Draws);
                microseconds.Add(elapsed * 1e6 / Stopwatch.Frequency / DrawsPerSubmit);
                cpuMicroseconds.Add(cpuElapsed / 1e3 / DrawsPerSubmit);
                allocatedBytes += allocated;
                gen0Collections += collections;
                hostDrawMilliseconds += counters.DrawMs;
                if (before is null) continue;
                for (var index = 0; index < phases.Length; index++)
                {
                    var delta = PhaseTicks(phases[index]) - before[index];
                    if (delta == 0) continue;
                    var key = phases[index].ToString();
                    phaseTotals[key] = phaseTotals.GetValueOrDefault(key) + delta * 1e6 / Stopwatch.Frequency / DrawsPerSubmit;
                }
            }

            microseconds.Sort();
            cpuMicroseconds.Sort();
            var median = microseconds[microseconds.Count / 2];
            Output.WriteLine($"{variation}: wall median {median:F2} us/draw, p25 {microseconds[microseconds.Count / 4]:F2}, min {microseconds[0]:F2}; " +
                $"thread cpu median {cpuMicroseconds[cpuMicroseconds.Count / 2]:F2}, p25 {cpuMicroseconds[cpuMicroseconds.Count / 4]:F2}; " +
                $"{allocatedBytes / (double)(rounds * DrawsPerSubmit):F0} B/draw");
            results.Add(new
            {
                variation = variation.ToString(),
                draws = DrawsPerSubmit,
                rounds,
                medianMicrosecondsPerDraw = median,
                minMicrosecondsPerDraw = microseconds[0],
                p25MicrosecondsPerDraw = microseconds[microseconds.Count / 4],
                cpuMedianMicrosecondsPerDraw = cpuMicroseconds[cpuMicroseconds.Count / 2],
                cpuP25MicrosecondsPerDraw = cpuMicroseconds[cpuMicroseconds.Count / 4],
                maxMicrosecondsPerDraw = microseconds[^1],
                allocatedBytesPerDraw = allocatedBytes / (double)(rounds * DrawsPerSubmit),
                gen0CollectionsPerThousandDraws = gen0Collections * 1000.0 / (rounds * DrawsPerSubmit),
                hostDrawMicrosecondsPerDraw = hostDrawMilliseconds * 1000.0 / (rounds * DrawsPerSubmit),
                phaseMicrosecondsPerDraw = phaseTotals.Where(pair => pair.Value / rounds >= 0.05)
                    .OrderByDescending(pair => pair.Value).ToDictionary(pair => pair.Key, pair => Math.Round(pair.Value / rounds, 2)),
            });
        }

        Assert.Empty(rig.Fatals);
        File.WriteAllText(path, JsonSerializer.Serialize(
            new { arch = RuntimeInformation.ProcessArchitecture.ToString(), profiling, results },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
