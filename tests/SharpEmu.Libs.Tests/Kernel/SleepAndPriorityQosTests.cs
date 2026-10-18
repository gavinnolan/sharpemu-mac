// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

/// <summary>The usleep/nanosleep nap policy and the guest priority to host QoS mapping.</summary>
public sealed class SleepAndPriorityQosTests
{
    [Fact]
    public void GuestPriorityPicksAHigherQosOnlyForHighPriorityThreads()
    {
        Assert.True(GuestThreadQosPolicy.TryGetClassForGuestPriority(256, out var qos));
        Assert.Equal(HostThreadQos.UserInteractive, qos);
        Assert.True(GuestThreadQosPolicy.TryGetClassForGuestPriority(450, out qos));
        Assert.Equal(HostThreadQos.UserInitiated, qos);
        Assert.False(GuestThreadQosPolicy.TryGetClassForGuestPriority(700, out _));
        Assert.False(GuestThreadQosPolicy.TryGetClassForGuestPriority(767, out _));
    }

    [Fact]
    public void ShortUsleepStreakStartsNappingAfterTheSpinWindow()
    {
        if (KernelRuntimeCompatExports.UsleepNapMicroseconds == 0)
        {
            return; // disabled by the environment
        }

        // A fresh streak yields first.
        Thread.Sleep(5);
        Assert.False(KernelRuntimeCompatExports.ShortUsleep(1));

        var napped = 0;
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 20)
        {
            var before = Stopwatch.GetTimestamp();
            if (KernelRuntimeCompatExports.ShortUsleep(1))
            {
                napped++;
                var tookUs = (Stopwatch.GetTimestamp() - before) * 1_000_000 / Stopwatch.Frequency;
                Assert.True(tookUs >= KernelRuntimeCompatExports.UsleepNapMicroseconds / 2, $"nap took {tookUs} us");
            }
        }

        Assert.True(napped > 0);

        // A pause longer than the streak gap starts over without napping.
        Thread.Sleep(5);
        Assert.False(KernelRuntimeCompatExports.ShortUsleep(1));
    }

    [Fact]
    public void YieldStreakNapsOnlyAfterSustainedPollingAndRestartsOnAGap()
    {
        const long gap = 2_000; // ticks between yields that still count as one streak
        const long after = 500; // streak age at which yields start napping
        long start = 0;
        long last = 0;

        // The first yield of a streak never naps; neither does one within the window.
        Assert.False(KernelRuntimeCompatExports_YieldNap(ref start, ref last, 1_000, gap, after));
        Assert.False(KernelRuntimeCompatExports_YieldNap(ref start, ref last, 1_400, gap, after));

        // Continuous yielding past the window naps, and keeps napping while the streak holds.
        Assert.True(KernelRuntimeCompatExports_YieldNap(ref start, ref last, 1_500, gap, after));
        Assert.True(KernelRuntimeCompatExports_YieldNap(ref start, ref last, 3_000, gap, after));

        // A pause longer than the gap ends the streak: the next yield does not nap.
        Assert.False(KernelRuntimeCompatExports_YieldNap(ref start, ref last, 3_000 + gap + 1, gap, after));
        Assert.False(KernelRuntimeCompatExports_YieldNap(ref start, ref last, 3_000 + gap + 100, gap, after));
        Assert.True(KernelRuntimeCompatExports_YieldNap(ref start, ref last, 3_000 + gap + 600, gap, after));
    }

    private static bool KernelRuntimeCompatExports_YieldNap(ref long start, ref long last, long now, long gap, long after) =>
        KernelPthreadCompatExports.YieldNapPolicy(ref start, ref last, now, gap, after);

    [Fact]
    public void ShortUsleepNeverReturnsBeforeTheRequestedTime()
    {
        Thread.Sleep(5); // a fresh streak, so this call does not nap
        var before = Stopwatch.GetTimestamp();
        Assert.False(KernelRuntimeCompatExports.ShortUsleep(200));
        var tookUs = (Stopwatch.GetTimestamp() - before) * 1_000_000 / Stopwatch.Frequency;
        Assert.True(tookUs >= 200, $"usleep(200) returned after {tookUs} us");
    }

    [Fact]
    public void PollBudgetCountedInUsleepCallsOutlastsAGpuLabelThatLandsLate()
    {
        if (KernelRuntimeCompatExports.UsleepNapMicroseconds == 0)
        {
            return; // disabled by the environment
        }

        // The guest pattern: "for (n = 500; n; n--) { if (*label) break; usleep(1); }
        // fatal('GPU hanged')". The label is written by the GPU some milliseconds later.
        // When usleep(1) only yielded, the 500 polls finished in well under the nap
        // window and the guest declared a hang.
        Thread.Sleep(5);
        var label = 0;
        using var writer = new Timer(_ => Volatile.Write(ref label, 1), null, 20, Timeout.Infinite);
        var polls = 500;
        while (Volatile.Read(ref label) == 0 && --polls > 0)
        {
            KernelRuntimeCompatExports.ShortUsleep(1);
        }

        Assert.True(Volatile.Read(ref label) != 0, "the 500-poll budget ran out before the label landed");
    }
}
