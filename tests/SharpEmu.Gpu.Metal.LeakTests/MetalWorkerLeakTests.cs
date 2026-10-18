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
/// The GPU worker has no autorelease pool of its own, so everything Metal autoreleases while
/// it records (command buffers, encoders, the strings and descriptors class factories make)
/// stayed allocated for the life of the process: Malloc Tiny grew about 5 KB per flush, a
/// gigabyte in fifteen minutes of a busy title. The worker's pass now runs in a pool; this
/// drives that same pass (<see cref="MetalV2Presenter.RunWorkerStep"/>) through the real
/// command stream, render host and Metal device, and checks that the process's malloc bytes
/// in use stay flat across a few thousand flushes.
/// </summary>
public sealed class MetalWorkerLeakTests(ITestOutputHelper output) : SceneTestBase(output)
{
    private const int WarmupFlushes = 1500;
    private const int MeasuredFlushes = 3000;

    // The unpooled worker pass grew by about 1.9 KB a flush on this stream (a game's busier
    // flushes leak about 5 KB): 5.6 MB over the measured run. The pooled pass grows by well
    // under 0.5 KB a flush, which is the runtime's own incidental allocation (tiered JIT
    // bookkeeping; flat to 17 B a flush with DOTNET_TieredCompilation=0).
    private const long AllowedGrowthBytes = 2 * 1024 * 1024;

    [Fact]
    public void FlushesRecordedByTheWorkerPassDoNotGrowNativeMemory()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        const uint groups = 4;
        var target = rig.Memory.Allocate(groups * 64 * 4, 256);
        var words = rig.Dispatch(
            new ScenePm4(), ScenePrograms.StoreGlobalId, Descriptors.Buffer(target, 4, groups * 64, format: 20), groups).ToArray();

        RunFlushes(rig, words, WarmupFlushes);
        var before = Sample();
        var serialBefore = rig.Host.SubmissionSerial;
        RunFlushes(rig, words, MeasuredFlushes);
        var after = Sample();
        var flushes = rig.Host.SubmissionSerial - serialBefore;

        var growth = (long)after.MallocInUse - (long)before.MallocInUse;
        Output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"flushes={flushes} malloc_in_use {before.MallocInUse} -> {after.MallocInUse} " +
            $"(+{growth} B, {growth / (double)Math.Max(flushes, 1):F1} B/flush); " +
            $"phys_footprint {before.PhysFootprint} -> {after.PhysFootprint} " +
            $"(+{(long)after.PhysFootprint - (long)before.PhysFootprint} B)"));
        Assert.True(flushes >= MeasuredFlushes, $"Only {flushes} command buffers were recorded.");
        Assert.True(
            growth < AllowedGrowthBytes,
            $"Malloc bytes in use grew by {growth} over {flushes} flushes ({growth / (double)flushes:F1} B/flush).");
    }

    // Each round is one guest submission and the worker passes that run it, then the idle
    // pass that commits it, as the worker's loop does; the wait keeps the GPU in step.
    private static void RunFlushes(SceneRig rig, uint[] words, int count)
    {
        var host = rig.Host;
        for (var index = 0; index < count; index++)
        {
            rig.Enqueue(words);
            while (!MetalV2Presenter.RunWorkerStep(host, rig.Relay))
            {
            }

            using var pool = MetalAutoreleasePool.Enter();
            host.FlushAndWaitForGpu();
            host.PollGpuCompletions();
        }
    }

    private static (ulong MallocInUse, ulong PhysFootprint) Sample()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        malloc_zone_statistics(0, out var statistics);
        return (statistics.SizeInUse, PhysFootprint());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MallocStatistics
    {
        public uint BlocksInUse;
        public nuint SizeInUse;
        public nuint MaxSizeInUse;
        public nuint SizeAllocated;
    }

    // Zone 0 is every zone: the sum over the default malloc zones, Tiny, Small and Large.
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
