// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalTextureDependencySchedulingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    public void ActualQueuedPrivateSnapshotsHaveNoDependencyAllocation(int textureCount)
    {
        using var state = new State();
        var textures = Enumerable.Range(0, textureCount).Select(index => new GuestDrawTexture(
            0x71F5C00000UL + (ulong)index * 4096, 4, 4, 10, 0, new byte[64], false, false, Pitch: 4)).ToArray();
        var sequence = Compute("private-snapshot", textures);
        Assert.True(TryTake(out var pending));
        Assert.Equal(sequence, pending!.GetType().GetProperty("Sequence")!.GetValue(pending));
        var work = pending.GetType().GetProperty("Work")!.GetValue(pending)!;
        var getter = typeof(MetalVideoPresenter).GetMethod("GetGuestTextureSourceDependenciesLocked",
            BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Func<object, Array>>();
        lock (Field("_gate").GetValue(null)!)
        {
            Assert.Empty(getter(work)); // Warm delegate/runtime work outside the measured region.
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var iteration = 0; iteration < 4096; iteration++)
            {
                if (getter(work).Length != 0) throw new InvalidOperationException("Private snapshots acquired an unexpected dependency.");
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
        }
    }
}
