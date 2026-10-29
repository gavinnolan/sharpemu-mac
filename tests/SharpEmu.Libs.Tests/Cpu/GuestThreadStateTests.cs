// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>
/// GuestThreadExecution keeps one per-thread state object (one thread-static read per crossing);
/// its public statics must still behave per thread.
/// </summary>
public sealed class GuestThreadStateTests
{
    [Fact]
    public void GuestThreadHandleAndPendingOutcomeAreNotSharedBetweenThreads()
    {
        var previous = GuestThreadExecution.EnterGuestThread(0xABC0);
        try
        {
            Assert.Equal(0xABC0UL, GuestThreadExecution.CurrentGuestThreadHandle);
            Assert.True(GuestThreadExecution.RequestCurrentThreadBlock("state-test"));
            Assert.True(GuestThreadExecution.HasPendingCurrentThreadBlock);
            Assert.True(GuestThreadExecution.State.HasPendingDispatchOutcome);

            ulong otherHandle = 1;
            var otherPending = true;
            var thread = new Thread(() =>
            {
                otherHandle = GuestThreadExecution.CurrentGuestThreadHandle;
                otherPending = GuestThreadExecution.HasPendingCurrentThreadBlock || GuestThreadExecution.State.HasPendingDispatchOutcome;
            });
            thread.Start();
            thread.Join();

            Assert.Equal(0UL, otherHandle);
            Assert.False(otherPending);

            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(out var reason));
            Assert.Equal("state-test", reason);
            Assert.False(GuestThreadExecution.State.HasPendingDispatchOutcome);
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previous);
        }
    }

    [Fact]
    public void EntryExitAndContextTransferCountAsPendingOutcomes()
    {
        Assert.False(GuestThreadExecution.State.HasPendingDispatchOutcome);

        GuestThreadExecution.RequestCurrentEntryExit("state-test-exit", 7UL);
        Assert.True(GuestThreadExecution.State.HasPendingDispatchOutcome);
        Assert.True(GuestThreadExecution.TryConsumeCurrentEntryExit(out var value, out var reason));
        Assert.Equal(7UL, value);
        Assert.Equal("state-test-exit", reason);
        Assert.False(GuestThreadExecution.State.HasPendingDispatchOutcome);
        Assert.False(GuestThreadExecution.TryConsumeCurrentEntryExit(out _, out _));
    }
}
