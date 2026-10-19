// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed class MetalOwnedFrameIdleTests
{
    private const BindingFlags Methods = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly object policy = Activator.CreateInstance(Assembly.Load("SharpEmu.Gpu.Metal")
        .GetType("SharpEmu.Gpu.Metal.MetalOwnedFrameIdlePolicy")!, nonPublic: true)!;

    private void Commit(long sequence = 7, nint texture = 17, double width = 1280,
        double height = 720, bool overlay = false, bool hud = false) => policy.GetType().GetMethod("Commit", Methods)!
        .Invoke(policy, [sequence, texture, width, height, overlay, hud]);
    private bool Skip(long sequence = 7, nint texture = 17, double width = 1280,
        double height = 720, bool owned = true, bool restored = false, bool overlay = false, bool hud = false) =>
        (bool)policy.GetType().GetMethod("CanSkip", Methods)!.Invoke(policy,
            [sequence, texture, width, height, owned, restored, overlay, hud])!;

    [Fact]
    public void FirstAndChangedGuestFramesMustCommitBeforeAnySkip()
    {
        Assert.False(Skip()); Commit(); Assert.True(Skip());
        Assert.False(Skip(sequence: 8)); Assert.False(Skip(texture: 18));
        Commit(sequence: 8, texture: 18); Assert.True(Skip(sequence: 8, texture: 18));
        Assert.False(Skip(sequence: 7));
    }

    [Fact]
    public void MutableUnresolvedAndNonGuestSourcesAlwaysPresent()
    {
        Commit(); Assert.False(Skip(owned: false)); Assert.False(Skip(texture: 0));
        Commit(sequence: -1); Assert.False(Skip(sequence: -1));
    }

    [Fact]
    public void ResizeRestoreAndScaleChangeForceAnOwnedFrameRedraw()
    {
        Commit(); Assert.False(Skip(width: 2560)); Assert.False(Skip(height: 1440));
        Assert.False(Skip(restored: true));
        Commit(width: 2560, height: 1440); Assert.True(Skip(width: 2560, height: 1440));
        Assert.False(Skip()); // shrinking back also redraws
    }

    [Fact]
    public void RestoreRetryMustRedrawUntilACommitEvenWhenOneShotSignalIsGone()
    {
        Commit(); Assert.True(Skip());
        policy.GetType().GetMethod("Invalidate", Methods)!.Invoke(policy, null);
        Assert.False(Skip(restored: true));
        Assert.False(Skip()); // minimized / nextDrawable==0 did not commit
        Assert.False(Skip());
        Commit(); Assert.True(Skip());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HudVisibilityAndHidingEachRequireOneRedraw(bool metalHud)
    {
        Commit(); Assert.False(metalHud ? Skip(hud: true) : Skip(overlay: true));
        Commit(overlay: !metalHud, hud: metalHud);
        Assert.False(Skip()); // erase the last visible overlay
        Commit(); Assert.True(Skip());
    }
}
