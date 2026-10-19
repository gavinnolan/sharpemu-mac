// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal.Tests.Scenes;
using SharpEmu.Gpu.Metal.V2;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.LeakTests;

/// <summary>
/// Every per-command-buffer path of the V2 host, one scene each, run for a few thousand
/// flushes through the worker's own pass: render passes with colour, depth and tiled
/// targets, sampled linear and tiled textures, uploads after a CPU write, writebacks for a
/// host read, end-of-pipe labels (32- and 64-bit, and on a quiet queue, where the commit
/// goes to the schedule-nudge thread), occlusion dumps, LOD statistics packing and a
/// compute queue beside the graphics one. Each must leave the number of live malloc blocks
/// flat: a command buffer that outlives its pass is two blocks (the AGX object and its
/// _impl) and a blit encoder a third, so a leak of one per flush is thousands of blocks,
/// while a clean run moves by a few hundred at most (runtime and driver pools settling).
/// Blocks rather than bytes, because the driver returns whole 64 KB chunks now and then
/// and the byte count swings by megabytes either way.
/// </summary>
/// <remarks>
/// SHARPEMU_LEAK_HEAP_DIR=dir writes a <c>heap</c> class histogram of the process before
/// and after the measured run, which names whatever grew.
/// </remarks>
public sealed class MetalCommandBufferPathLeakTests(ITestOutputHelper output) : SceneTestBase(output)
{
    private const uint Size = 64;
    private const int WarmupFlushes = 1000;
    private const int MeasuredFlushes = 3000;

    // The quiet-queue scene sleeps past the 4 ms quiet interval before every flush.
    private const int QuietWarmupFlushes = 100;
    private const int QuietMeasuredFlushes = 400;

    [Theory]
    [InlineData("draw")]
    [InlineData("depth")]
    [InlineData("depth-sampled")]
    [InlineData("textured")]
    [InlineData("cpu-write-upload")]
    [InlineData("host-read-writeback")]
    [InlineData("tiled-read-writeback")]
    [InlineData("label32")]
    [InlineData("label64")]
    [InlineData("label-quiet-queue")]
    [InlineData("occlusion")]
    [InlineData("lod-stats")]
    [InlineData("compute-queue")]
    public void ThePathDoesNotGrowNativeMemoryPerFlush(string scene)
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var (words, beforeFlush) = Build(rig, scene);
        var quiet = scene == "label-quiet-queue";
        RunFlushes(rig, words, quiet ? QuietWarmupFlushes : WarmupFlushes, beforeFlush);
        var before = Sample();
        WriteHeap(scene, "before");
        var serialBefore = rig.Host.SubmissionSerial;
        var countersBefore = rig.Host.ImageCache.Counters;
        var measured = quiet ? QuietMeasuredFlushes : MeasuredFlushes;
        RunFlushes(rig, words, measured, beforeFlush);
        var after = Sample();
        WriteHeap(scene, "after");
        var flushes = rig.Host.SubmissionSerial - serialBefore;
        var counters = rig.Host.ImageCache.Counters;
        var uploads = counters.Uploads - countersBefore.Uploads;
        var writebacks = counters.Writebacks - countersBefore.Writebacks;

        var blocks = (long)after.Blocks - before.Blocks;
        var bytes = (long)after.Bytes - (long)before.Bytes;
        Output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{scene}: rounds={measured} command_buffers={flushes} uploads={uploads} writebacks={writebacks} malloc_blocks +{blocks} " +
            $"({blocks / (double)measured:F2}/round) malloc_bytes +{bytes} " +
            $"phys_footprint +{(long)after.Footprint - (long)before.Footprint}"));
        Assert.Empty(rig.Fatals);
        Assert.True(flushes >= (ulong)measured, $"Only {flushes} command buffers were recorded.");
        Assert.True(
            blocks < measured / 4,
            $"{scene}: malloc blocks in use grew by {blocks} over {measured} rounds ({blocks / (double)measured:F2} per round).");
    }

    private static (uint[] Words, Action<SceneRig>? BeforeFlush) Build(SceneRig rig, string scene)
    {
        var target = rig.ColorTarget(Size, Size);
        var state = new SceneState(Size, Size).ColorTarget(0, target, Size, Size);
        uint[] Quad() => rig.DrawMesh(new ScenePm4(), state, SceneDraws.Quad(0.5f, 1, 0, 0)).ToArray();
        switch (scene)
        {
            case "draw":
                return (Quad(), null);
            case "depth":
            case "depth-sampled":
            {
                var depth = rig.Memory.Allocate(Size * Size * 4 + 0x10000, 0x10000);
                var depthState = new SceneState(Size, Size).ColorTarget(0, rig.ColorTarget(Size, Size), Size, Size)
                    .DepthTarget(depth, Size, Size, zFormat: 3).DepthControl(test: true, write: true, CompareFunc.Always);
                var pm4 = rig.DrawMesh(new ScenePm4(), depthState, SceneDraws.Quad(0.25f, 1, 1, 1, right: 0f));
                if (scene == "depth-sampled")
                {
                    // The depth target read back through a tiled texture in a second pass.
                    var copy = new SceneState(Size, Size).ColorTarget(0, target, Size, Size, format: 4, numberType: 7);
                    uint[] userData = [.. Descriptors.Texture(depth, 22, Size, Size, tileMode: 24), .. Descriptors.Sampler()];
                    rig.DrawMesh(pm4, copy, SceneDraws.TexturedQuad(), ScenePrograms.SampledTexture, userData);
                }

                return (pm4.ToArray(), null);
            }
            case "textured":
            case "cpu-write-upload":
            {
                // Swizzled (64KB_R_X), so the texture is an owned copy the cache uploads,
                // not a view over the guest window.
                var bytes = new byte[0x10000];
                new Random(1).NextBytes(bytes);
                var image = rig.Memory.Allocate((ulong)bytes.Length, 0x10000);
                rig.Memory.Write(image, bytes);
                uint[] userData = [.. Descriptors.Texture(image, 56, Size, Size, tileMode: 27), .. Descriptors.Sampler()];
                var words = rig.DrawMesh(new ScenePm4(), state, SceneDraws.TexturedQuad(), ScenePrograms.SampledTexture, userData).ToArray();
                if (scene == "textured")
                {
                    return (words, null);
                }

                // A host write into the texture's bytes, which the next draw uploads.
                var round = 0u;
                return (words, r =>
                {
                    var offset = (ulong)(round++ % 256) * 256;
                    r.Memory.Write32(image + offset, round);
                    r.Host.ImageCache.InvalidateMemory(image + offset, 256);
                });
            }
            case "host-read-writeback":
                return (Quad(), r => r.Read(target, 256));
            case "tiled-read-writeback":
            {
                var tiled = rig.Memory.Allocate(Size * Size * 4 + 0x20000, 0x10000);
                var tiledState = new SceneState(Size, Size).ColorTarget(0, tiled, Size, Size, 10, 0, tileMode: 27);
                return (rig.DrawMesh(new ScenePm4(), tiledState, SceneDraws.Quad(0.5f, 1, 0, 0)).ToArray(), r => r.Read(tiled, 256));
            }
            case "label32":
            case "label64":
            case "label-quiet-queue":
            {
                // RELEASE_MEM at end of pipe: data select 1 (low 32 bits) or 2 (64 bits).
                var label = rig.Memory.Allocate(64, 64);
                var pm4 = rig.DrawMesh(new ScenePm4(), state, SceneDraws.Quad(0.5f, 1, 0, 0));
                var select = scene == "label64" ? 2u : 1u;
                pm4.Packet(0x49, 0x28u | (5u << 8), select << 29, (uint)label, (uint)(label >> 32), 7u, 0u, 0u);
                return (pm4.ToArray(), scene == "label-quiet-queue" ? _ => Thread.Sleep(5) : null);
            }
            case "occlusion":
            {
                var query = rig.Memory.Allocate(16 * 16, 64);
                var pm4 = new ScenePm4().EventWithAddress(ScenePm4.EventPixelPipeStatDump, 1, query);
                rig.DrawMesh(pm4, state, SceneDraws.Quad(0.5f, 1, 0, 0));
                pm4.EventWithAddress(ScenePm4.EventPixelPipeStatDump, 1, query + 8);
                return (pm4.ToArray(), null);
            }
            case "lod-stats":
            {
                // GET_LOD_STATS with a reset: the bank is packed by a kernel of its own.
                var report = rig.Memory.Allocate(0x1040, 0x100);
                var pm4 = rig.DrawMesh(new ScenePm4(), state, SceneDraws.Quad(0.5f, 1, 0, 0));
                pm4.Packet(0x8E, 0x1040u, (uint)report, (uint)(report >> 32), 1u << 18);
                return (pm4.ToArray(), null);
            }
            case "compute-queue":
            {
                const uint groups = 4;
                var buffer = rig.Memory.Allocate(groups * 64 * 4, 256);
                var compute = rig.Dispatch(new ScenePm4(), ScenePrograms.StoreGlobalId,
                    Descriptors.Buffer(buffer, 4, groups * 64, format: 20), groups).ToArray();
                return (Quad(), r => r.Enqueue(compute, 0x20));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(scene), scene, null);
        }
    }

    // One guest submission per round, run by the worker's pass as the worker loop runs it.
    // What the round does first stands for work another thread hands the worker (a host
    // read, a CPU write), which the relay runs inside a worker pass, so it gets a pool too.
    private static void RunFlushes(SceneRig rig, uint[] words, int count, Action<SceneRig>? beforeFlush)
    {
        var host = rig.Host;
        for (var index = 0; index < count; index++)
        {
            if (beforeFlush is not null)
            {
                using var relayed = MetalAutoreleasePool.Enter();
                beforeFlush(rig);
            }

            rig.Enqueue(words);
            while (!MetalV2Presenter.RunWorkerStep(host, rig.Relay))
            {
            }

            using var pool = MetalAutoreleasePool.Enter();
            host.FlushAndWaitForGpu();
            host.PollGpuCompletions();
        }
    }

    private static (uint Blocks, ulong Bytes, ulong Footprint) Sample()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        malloc_zone_statistics(0, out var statistics);
        return (statistics.BlocksInUse, statistics.SizeInUse, PhysFootprint());
    }

    private static void WriteHeap(string scene, string when)
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_LEAK_HEAP_DIR") is not { Length: > 0 } directory)
        {
            return;
        }

        var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/heap", Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        File.WriteAllText(Path.Combine(directory, $"heap-{scene}-{when}.txt"), text + errors.Result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MallocStatistics
    {
        public uint BlocksInUse;
        public nuint SizeInUse;
        public nuint MaxSizeInUse;
        public nuint SizeAllocated;
    }

    // Zone 0 is every zone: the sum over the default malloc zones.
    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern void malloc_zone_statistics(nint zone, out MallocStatistics statistics);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern uint mach_task_self();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern unsafe int task_info(uint task, uint flavor, void* info, ref uint count);

    // task_vm_info's phys_footprint, at byte 144 of the structure (TASK_VM_INFO = 22).
    private static unsafe ulong PhysFootprint()
    {
        var info = stackalloc byte[512];
        var count = 512u / 4;
        return task_info(mach_task_self(), 22, info, ref count) == 0 ? *(ulong*)(info + 144) : 0;
    }
}
