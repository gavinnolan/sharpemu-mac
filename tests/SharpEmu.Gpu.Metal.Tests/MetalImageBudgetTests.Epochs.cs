// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed unsafe partial class MetalImageBudgetTests
{
    // Frames that use the same image the same way leave the binding count still.
    [Fact]
    public void Epochs_SteadyFramesCountNothing()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        rig.Configure(retireFrames: 0, retireSeconds: 0);
        rig.Acquire(GuestBase, ImageRole.Texture);
        rig.Frame(seconds: 0.1, touch: GuestBase);
        var (imageSet, binding) = (rig.Cache.ImageSetVersion, rig.Cache.Epochs.Binding);

        for (var frame = 0; frame < 3; frame++)
        {
            rig.Frame(seconds: 0.1, touch: GuestBase);
        }

        Assert.Equal(imageSet, rig.Cache.ImageSetVersion);
        Assert.Equal(binding, rig.Cache.Epochs.Binding);
    }

    [Fact]
    public void Epochs_BudgetEvictionCounts()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        // Retirement off; any non-empty cache is over a 1-byte budget.
        rig.Configure(retireFrames: 0, retireSeconds: 0, budget: Settings24 with { FixedBudget = 1 });
        var old = rig.Acquire(GuestBase, ImageRole.Texture);
        rig.Frame(seconds: 3);
        rig.Acquire(GuestBase + 0x20000, ImageRole.Texture);
        var (imageSet, binding) = (rig.Cache.ImageSetVersion, rig.Cache.Epochs.Binding);

        for (var frame = 0; frame < 3; frame++)
        {
            rig.Frame(seconds: 0.3, touch: GuestBase + 0x20000);
        }

        Assert.False(rig.Cache.TryGetImage(old, out _));
        Assert.Equal(1, rig.Cache.Counters.Evictions);
        Assert.True(rig.Cache.ImageSetVersion > imageSet);
        Assert.True(rig.Cache.Epochs.Binding > binding);
    }
}
