// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// The v2 hold-until-done gate: the guest's first splash hide, GPU submission or flip
// waits while the shader prewarm is undecided or running, and nothing else does.
public sealed class MetalShaderPrewarmGateTests
{
    private static readonly TimeSpan Held = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan Released = TimeSpan.FromSeconds(5);

    [Fact]
    public void NothingIsHeldBeforeThePresenterStarts()
    {
        var gate = new MetalShaderPrewarmGate(disabled: false);
        Assert.False(gate.IsHolding);
        Assert.True(WaitOn(gate).Wait(Released));
    }

    [Fact]
    public void AnUndecidedPrewarmHoldsUntilItIsDecidedOff()
    {
        var gate = new MetalShaderPrewarmGate(disabled: false);
        gate.Begin();
        var waiter = WaitOn(gate);
        Assert.False(waiter.Wait(Held));

        gate.SetRunning(false);
        Assert.True(waiter.Wait(Released));
        Assert.False(gate.IsHolding);
    }

    [Fact]
    public void ARunningPrewarmHoldsUntilItFinishes()
    {
        var gate = new MetalShaderPrewarmGate(disabled: false);
        gate.Begin();
        gate.SetTotal(2);
        gate.SetRunning(true);
        var waiter = WaitOn(gate);
        Assert.False(waiter.Wait(Held));
        Assert.Equal(1, gate.Advance());
        Assert.False(waiter.Wait(Held));

        Assert.Equal(2, gate.Advance());
        gate.SetRunning(false);
        Assert.True(waiter.Wait(Released));
        // Once released, a later holder passes straight through.
        Assert.True(WaitOn(gate).Wait(Released));
    }

    [Fact]
    public void TheOptOutNeverHolds()
    {
        var gate = new MetalShaderPrewarmGate(disabled: true);
        gate.Begin();
        gate.SetRunning(true);
        Assert.True(WaitOn(gate).Wait(Released));
    }

    [Fact]
    public void ClosingTheSessionReleasesAHolder()
    {
        var gate = new MetalShaderPrewarmGate(disabled: false);
        gate.Begin();
        gate.SetRunning(true);
        var waiter = WaitOn(gate);
        Assert.False(waiter.Wait(Held));

        gate.Cancel();
        Assert.True(waiter.Wait(Released));
        Assert.False(gate.IsHolding);
    }

    [Fact]
    public void AShutdownRequestReleasesAHolder()
    {
        var gate = new MetalShaderPrewarmGate(disabled: false);
        gate.Begin();
        gate.SetRunning(true);
        var shutdown = false;
        var waiter = Task.Run(() => gate.Wait("test", () => Volatile.Read(ref shutdown)));
        Assert.False(waiter.Wait(Held));

        // The holder re-checks at least once a second.
        Volatile.Write(ref shutdown, true);
        Assert.True(waiter.Wait(Released));
    }

    private static Task WaitOn(MetalShaderPrewarmGate gate) =>
        Task.Run(() => gate.Wait("test", static () => false));
}
