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

        // A streak needs sleeps less than 2 ms apart, which a thread competing for a loaded
        // runner's cores can miss for a while; keep polling (bounded) until one forms.
        var napped = 0;
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 20 || (napped == 0 && watch.ElapsedMilliseconds < 5000))
        {
            var before = Stopwatch.GetTimestamp();
            if (KernelRuntimeCompatExports.ShortUsleep(1))
            {
                napped++;
                var tookUs = (Stopwatch.GetTimestamp() - before) * 1_000_000 / Stopwatch.Frequency;
                // The first nap of a streak is the floor (the ceiling under the legacy switch).
                var shortestNap = KernelRuntimeCompatExports.UsleepNapLegacy
                    ? KernelRuntimeCompatExports.UsleepNapMicroseconds
                    : KernelRuntimeCompatExports.UsleepNapFloorMicroseconds;
                Assert.True(tookUs >= shortestNap / 2, $"nap took {tookUs} us");
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

    // Policy tunables pinned to the defaults: 250 us ceiling, 50 us floor, 4 ms ramp, nap after 500 us.
    private static long Policy(long ageUs, long requestedUs, bool legacy, out bool napped) =>
        KernelRuntimeCompatExports.NapPolicyMicroseconds(
            ageUs * System.Diagnostics.Stopwatch.Frequency / 1_000_000, requestedUs,
            napMicroseconds: 250, floorMicroseconds: 50, rampMicroseconds: 4000,
            afterTicks: 500 * System.Diagnostics.Stopwatch.Frequency / 1_000_000, legacy, out napped);

    [Fact]
    public void NapPolicyHonoursRequestsAtOrAboveTheFloorEvenInALongStreak()
    {
        Assert.Equal(100, Policy(ageUs: 60_000, requestedUs: 100, legacy: false, out var napped));
        Assert.False(napped);
        Assert.Equal(50, Policy(ageUs: 60_000, requestedUs: 50, legacy: false, out napped));
        Assert.False(napped);
        // The legacy policy stretches the same request to the ceiling (the measured 285 us).
        Assert.Equal(250, Policy(ageUs: 60_000, requestedUs: 100, legacy: true, out napped));
        Assert.True(napped);
    }

    [Fact]
    public void NapPolicyEscalatesTinyRequestsAndYieldsFromFloorToCeiling()
    {
        // Before the streak window nothing naps.
        Assert.Equal(1, Policy(ageUs: 400, requestedUs: 1, legacy: false, out var napped));
        Assert.False(napped);

        // At the window the nap starts at the floor, not the ceiling.
        Assert.Equal(50, Policy(ageUs: 500, requestedUs: 1, legacy: false, out napped));
        Assert.True(napped);
        Assert.Equal(50, Policy(ageUs: 500, requestedUs: 0, legacy: false, out _));

        // It rises monotonically over the ramp and then stays at the ceiling.
        var halfway = Policy(ageUs: 500 + 2000, requestedUs: 1, legacy: false, out _);
        Assert.InRange(halfway, 140, 160);
        Assert.Equal(250, Policy(ageUs: 500 + 4000, requestedUs: 1, legacy: false, out _));
        Assert.Equal(250, Policy(ageUs: 1_000_000, requestedUs: 0, legacy: false, out _));

        // Legacy: straight to the ceiling.
        Assert.Equal(250, Policy(ageUs: 500, requestedUs: 1, legacy: true, out _));
    }

    [Fact]
    public void NapPolicyIsOffWhenTheCeilingIsZero()
    {
        var micros = KernelRuntimeCompatExports.NapPolicyMicroseconds(
            streakAgeTicks: 1_000_000_000, requestedMicroseconds: 7, napMicroseconds: 0, floorMicroseconds: 0, rampMicroseconds: 4000,
            afterTicks: 1, legacy: false, out var napped);
        Assert.Equal(7, micros);
        Assert.False(napped);
    }

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

        // On a host without the precise clock a usleep(1) is a ~1 us spin, so the 500 polls
        // finish inside the 500 us streak window on a warm fast core and nothing ever naps; the
        // budget this test models belongs to the mach_wait_until hosts.
        if (!HostPreciseClock.IsPrecise)
        {
            return;
        }

        // The guest pattern: "for (n = 500; n; n--) { if (*label) break; usleep(1); }
        // fatal('GPU hanged')". The label is written by the GPU some milliseconds later.
        // When usleep(1) only yielded, the 500 polls finished in well under the nap
        // window and the guest declared a hang.
        Thread.Sleep(5);
        var label = 0;
        // A dedicated thread, not a pool timer: the pool can run the timer seconds late on a
        // loaded runner, which is the label landing later than the budget models.
        var writer = new Thread(() =>
        {
            Thread.Sleep(20);
            Volatile.Write(ref label, 1);
        }) { IsBackground = true };
        writer.Start();
        var polls = 500;
        while (Volatile.Read(ref label) == 0 && --polls > 0)
        {
            KernelRuntimeCompatExports.ShortUsleep(1);
        }

        Assert.True(Volatile.Read(ref label) != 0, "the 500-poll budget ran out before the label landed");
    }
}
