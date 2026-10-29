// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests.Scenes;

public sealed class CallerResolutionSceneTests(ITestOutputHelper output) : SceneTestBase(output)
{
    [Fact]
    public void SpeculativeReadsOnTheDrawingThreadRefusePendingQueryBytes()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        var host = rig.Host;
        host.NativeOcclusionEnabled = true;
        var device = (nint)typeof(MetalRenderHost).GetField("_device", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var gate = MetalNative.Send(device, MetalNative.Selector("newSharedEvent"));
        Assert.NotEqual(0, gate);
        var signal = MetalNative.Selector("setSignaledValue:");
        var query = rig.Memory.Allocate(8, 64);
        var fired = 0;
        using var cancel = new ManualResetEventSlim();
        var watchdog = new Thread(() =>
        {
            if (cancel.Wait(TimeSpan.FromSeconds(10))) return;
            Interlocked.Exchange(ref fired, 1);
            MetalNative.SendVoid(gate, signal, (nint)1);
        }) { IsBackground = true };
        watchdog.Start();
        try
        {
            MetalV2Native.SendEventValue(host.CommandBuffer, MetalNative.Selector("encodeWaitForEvent:value:"), gate, 1);
            Assert.True(host.DumpOcclusionCounter(0, query, 1, 8));
            var waits = host.HostGpuWaits;
            var concurrent = (IConcurrentResolveHost)host;
            concurrent.BeginConcurrentResolve();
            try
            {
                Assert.Throws<GpuFatal.AbandonedWorkException>(() => host.TryReadGuestWord(query + 4, out _));
                Assert.Equal(waits, host.HostGpuWaits);
                Assert.Equal(0, Volatile.Read(ref fired));
            }
            finally { concurrent.ReleaseAssumptions(concurrent.EndConcurrentResolve()); }
            MetalNative.SendVoid(gate, signal, (nint)1);
            Assert.True(host.TryReadGuestWord(query + 4, out var ready));
            Assert.Equal(0x80000000u, ready);
            rig.Drain();
        }
        finally
        {
            MetalNative.SendVoid(gate, signal, (nint)1);
            cancel.Set();
            watchdog.Join();
            MetalNative.SendVoid(gate, MetalNative.Selector("release"));
        }
    }

    [Fact]
    public void CallerResolutionPreservesPixelsAndEarlierOcclusionSnapshots()
    {
        using var rig = CreateRig();
        if (rig is null) return;
        rig.Host.NativeOcclusionEnabled = true;
        const uint size = 32;
        const ulong ready = 1UL << 63;
        var target = rig.ColorTarget(size, size);
        var query = rig.Memory.Allocate(256, 64);
        var state = new SceneState(size, size).ColorTarget(0, target, size, size);
        state.Context[Cx.DbCountControl] = 1u << 8;
        var pm4 = new ScenePm4();
        pm4.EventWithAddress(ScenePm4.EventPixelPipeStatDump, 1, query);
        rig.DrawMesh(pm4, state, SceneDraws.Quad(0.1f, 1, 0, 0));
        pm4.EventWithAddress(ScenePm4.EventPixelPipeStatDump, 1, query + 8);
        rig.DrawMesh(pm4, state, SceneDraws.Quad(0.1f, 0, 1, 0));
        var translation = (AgcExports.CommandStreamTranslation)typeof(MetalRenderHost)
            .GetField("_translation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Host)!;
        var prefetch = translation.Prefetch;
        var forceCaller = prefetch is not null && !rig.Host.UsesCommandThread && ((IConcurrentResolveHost)rig.Host).AllowsCallerResolution;
        prefetch?.TakeCallerResolveCounters();
        if (forceCaller)
        {
            // Serial interpretation and drawing run on this test thread. Block the resolver
            // from claiming the queued jobs so this exercises the caller path deterministically.
            var queueLock = typeof(ParallelProgramPrefetch).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(prefetch)!;
            lock (queueLock) rig.Submit(pm4);
        }
        else rig.Submit(pm4);
        var callers = prefetch?.TakeCallerResolveCounters().Count ?? 0;
        if (forceCaller) Assert.True(callers >= 2, $"Expected both draws to resolve on the caller, got {callers}.");
        Output.WriteLine($"forced_caller={forceCaller} caller_jobs={callers}");
        var values = rig.Read<ulong>(query, 32);
        Assert.Equal(ready, values[0]);
        Assert.Equal(ready | (ulong)size * size, values[1]);
        Assert.All(values.Skip(2), value => Assert.Equal(ready, value));
        Assert.True(rig.Host.TryReadOcclusionCounter(0, out var finalCount));
        Assert.Equal(2UL * size * size, finalCount);
        Assert.Equal(0xFF00FF00u, SceneDraws.Pixel(rig.ReadImage(target, size, size), size, size / 2, size / 2));
    }
}
