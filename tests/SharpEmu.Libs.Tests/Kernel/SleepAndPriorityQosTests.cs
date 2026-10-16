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

        // A pause longer than the streak gap starts over with yields.
        Thread.Sleep(5);
        Assert.False(KernelRuntimeCompatExports.ShortUsleep(1));
    }
}
