// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// A recorded writeback runs when the GPU reaches it, and the guest may have reused the bytes
/// by then: Silent Hill rebuilt an object in a released render target's memory and the late
/// writeback turned its vtable into pixels. The range stays watched until the submission
/// carrying the writeback completes, and a guest store that faults there waits for it.
/// </summary>
public sealed partial class MetalImageBudgetTests
{
    [Fact]
    public void AReleasedImagesWritebackKeepsItsRangeWatchedUntilItsSubmissionCompletes()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var (identifier, _) = ReleaseAGpuWrittenImage(rig);

        Assert.False(rig.Cache.TryGetImage(identifier, out _));
        Assert.Equal(1, rig.Cache.FencedWritebackCount);
        Assert.False(rig.Pages.Allows(GuestBase, FaultKind.Write));
        Assert.False(rig.Pages.Allows(GuestBase + ImageSize - 1, FaultKind.Write));

        // Still recorded: collecting keeps the watch.
        rig.Cache.Collect();
        Assert.Equal(1, rig.Cache.FencedWritebackCount);

        rig.Harness.Submit();
        rig.Harness.Begin();
        rig.Cache.Collect();
        Assert.Equal(0, rig.Cache.FencedWritebackCount);
        Assert.True(rig.Pages.Allows(GuestBase, FaultKind.Write));
    }

    [Fact]
    public void WithTheFenceOffAReleasedImageLeavesNoWatch()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        rig.Cache.WritebackFenceEnabled = false;
        var (identifier, _) = ReleaseAGpuWrittenImage(rig);

        Assert.False(rig.Cache.TryGetImage(identifier, out _));
        Assert.Equal(0, rig.Cache.FencedWritebackCount);
        Assert.True(rig.Pages.Allows(GuestBase, FaultKind.Write));
    }

    // The store faults while the writeback is recorded and not run, waits for exactly that
    // submission, and lands after the writeback instead of under it.
    [Fact]
    public void AGuestStoreIntoARecordedWritebackWaitsForItsSubmission()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var (_, written) = ReleaseAGpuWrittenImage(rig);
        var writebackSerial = rig.Harness.SubmissionSerial;

        using var waiting = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        ulong waitedFor = 0;
        rig.CacheOwner.WaitOffWorker = (serial, timeout) =>
        {
            waitedFor = serial;
            waiting.Set();
            return completed.Wait(timeout) && rig.Harness.CompletedSerial >= serial;
        };
        rig.Cache.WritebackFenceTimeoutMilliseconds = 10_000;

        var store = Task.Run(() => ((IGuestImageStore)rig.Cache).MarkCpuWrite(GuestBase + 0x40, 8));
        Assert.True(waiting.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(writebackSerial, waitedFor);
        Assert.False(store.Wait(TimeSpan.FromMilliseconds(100)));

        rig.Harness.Submit();
        rig.Harness.Begin();
        completed.Set();
        Assert.True(store.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(store.Result);
        Assert.Equal(0, rig.Cache.FencedWritebackCount);
        Assert.True(rig.Pages.Allows(GuestBase, FaultKind.Write));
        Assert.Equal((1L, 0L), rig.Cache.FenceWaitCounts);

        // The writeback has landed; the store the guest retries now lands after it.
        Assert.Equal(written, rig.ReadGuest(GuestBase, ImageSize));
        var reused = MetalTileOracle.Pattern((int)ImageSize, 0x3C);
        rig.WriteGuest(GuestBase, reused);
        rig.Cache.Collect();
        Assert.Equal(reused, rig.ReadGuest(GuestBase, ImageSize));
    }

    // A fault outside every fenced page is none of the fence's business and costs no wait.
    [Fact]
    public void AStoreBesideAFencedWritebackDoesNotWait()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        _ = ReleaseAGpuWrittenImage(rig);
        var waits = 0;
        rig.CacheOwner.WaitOffWorker = (_, _) => { waits++; return true; };

        Assert.False(((IGuestImageStore)rig.Cache).MarkCpuWrite(GuestBase + 0x40000, 8));
        Assert.Equal(0, waits);
        Assert.Equal(1, rig.Cache.FencedWritebackCount);
    }

    // A store that cannot wait - the timeout, or the GPU worker itself - drops the fence and
    // proceeds as it did before, counted.
    [Fact]
    public void AStoreThatCannotWaitDropsTheFenceAndIsCounted()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        _ = ReleaseAGpuWrittenImage(rig);
        rig.CacheOwner.WaitOffWorker = (_, _) => false;

        Assert.True(((IGuestImageStore)rig.Cache).MarkCpuWrite(GuestBase, 8));
        Assert.Equal((1L, 1L), rig.Cache.FenceWaitCounts);
        Assert.Equal(0, rig.Cache.FencedWritebackCount);
        Assert.True(rig.Pages.Allows(GuestBase, FaultKind.Write));
    }

    // A GPU-written image retired with its writeback recorded into the open submission.
    private static (ResourceSlotIdentifier Identifier, byte[] Written) ReleaseAGpuWrittenImage(Rig rig)
    {
        rig.Configure(retireFrames: 2, retireSeconds: 1);
        var written = MetalTileOracle.Pattern((int)ImageSize, 0x5A);
        rig.WriteGuest(GuestBase, written);
        var identifier = rig.Acquire(GuestBase, ImageRole.ColorTarget);
        Assert.True(rig.Cache.GetImage(identifier).IsGpuModified);
        rig.Harness.Submit();
        rig.Harness.Begin();
        rig.WriteGuest(GuestBase, MetalTileOracle.Pattern((int)ImageSize, 0xC3));
        for (var frame = 0; frame < 4 && rig.Cache.TryGetImage(identifier, out _); frame++)
        {
            rig.Frame(seconds: 1, submit: false);
        }

        Assert.False(rig.Cache.TryGetImage(identifier, out _));
        return (identifier, written);
    }
}
