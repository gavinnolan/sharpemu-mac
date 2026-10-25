// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.GpuRemote.Tests;

public sealed unsafe class RemoteWaitTests
{
    [MacOSFact]
    public void UlockIsAvailableOnThisMac()
    {
        // The fallback exists for a missing symbol; on any supported macOS it must not be needed.
        Assert.True(RemoteNative.UlockWait2 != null);
        Assert.True(RemoteNative.UlockWake != null);
    }

    [MacOSFact]
    public void WaitTimesOutWhenNothingChanges()
    {
        var word = (uint*)NativeMemory.AlignedAlloc(128, 128);
        try
        {
            *word = 5;
            var watch = Stopwatch.StartNew();
            Assert.False(RemoteWait.Wait(word, 5, 20_000_000));
            Assert.InRange(watch.ElapsedMilliseconds, 15, 2_000);
            Assert.True(RemoteWait.Wait(word, 4, 20_000_000));
            Assert.False(RemoteWait.Wait(word, 5, 0));
        }
        finally
        {
            NativeMemory.AlignedFree(word);
        }
    }

    [MacOSFact]
    public void DoorbellWakesASleepingWaiter()
    {
        var state = (DoorbellState*)NativeMemory.AlignedAlloc(128, 128);
        try
        {
            *state = default;
            var bell = new Doorbell(state);
            var woke = 0;
            var waiter = new Thread(() =>
            {
                var ticket = bell.PrepareWait();
                if (bell.CommitWait(ticket, 10_000_000_000))
                {
                    woke = 1;
                }
            });
            waiter.Start();
            while (Volatile.Read(ref state->Sleepers) == 0)
            {
                Thread.Yield();
            }

            Thread.Sleep(20);
            bell.Ring();
            Assert.True(waiter.Join(5_000));
            Assert.Equal(1, woke);
            Assert.Equal(0u, state->Sleepers);
        }
        finally
        {
            NativeMemory.AlignedFree(state);
        }
    }

    [MacOSFact]
    public void PollingFallbackSeesTheChangeAndHonoursTheTimeout()
    {
        var word = (uint*)NativeMemory.AlignedAlloc(128, 128);
        try
        {
            *word = 1;
            Assert.False(RemoteWait.Poll(word, 1, 5_000_000));
            var changer = new Thread(() =>
            {
                Thread.Sleep(30);
                Volatile.Write(ref *word, 2);
            });
            changer.Start();
            var watch = Stopwatch.StartNew();
            Assert.True(RemoteWait.Poll(word, 1, 10_000_000_000));
            Assert.InRange(watch.ElapsedMilliseconds, 20, 2_000);
            changer.Join();
        }
        finally
        {
            NativeMemory.AlignedFree(word);
        }
    }
}
