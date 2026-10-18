// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Threading;
using Xunit;

namespace SharpEmu.Libs.Tests.Threading;

// Host-thread waiters (the main-thread path) plus one staged cooperative waiter; no
// scheduler is installed, so nothing here touches the global scheduler slot.
public sealed class SemaphoreAndEventFlagWaitTests
{
    private const ulong Base = 0x2_4000_0000;
    private const ulong HandleSlot = Base + 0x100;
    private const ulong NameAddress = Base + 0x200;
    private const ulong ResultSlot = Base + 0x300;
    private const ulong CountSlot = Base + 0x340;
    private const ulong ScratchBase = Base + 0x1000;

    private static readonly int Ok = (int)OrbisGen2Result.ORBIS_GEN2_OK;
    private static readonly int Canceled = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED;
    private static readonly int Deleted = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED;
    private static readonly int TimedOut = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;

    private readonly FakeCpuMemory _memory = new(Base, 0x10000);
    private readonly CpuContext _probe;

    public SemaphoreAndEventFlagWaitTests()
    {
        _probe = new CpuContext(_memory, Generation.Gen5);
    }

    private CpuContext NewContext(int slot)
    {
        // Each thread gets its own errno slot (FsBase + 0x40) and scratch area.
        return new CpuContext(_memory, Generation.Gen5) { FsBase = ScratchBase + (ulong)slot * 0x200 };
    }

    private static ulong Scratch(int slot, ulong offset) => ScratchBase + (ulong)slot * 0x200 + 0x100 + offset;

    private uint CreateSema(int initial, int max, uint attr = 1)
    {
        var ctx = NewContext(0);
        Assert.True(_memory.TryWrite(NameAddress, "w1-sema\0"u8));
        ctx[CpuRegister.Rdi] = HandleSlot;
        ctx[CpuRegister.Rsi] = NameAddress;
        ctx[CpuRegister.Rdx] = attr;
        ctx[CpuRegister.Rcx] = (ulong)initial;
        ctx[CpuRegister.R8] = (ulong)max;
        ctx[CpuRegister.R9] = 0;
        Assert.Equal(Ok, KernelSemaphoreCompatExports.KernelCreateSema(ctx));
        Assert.True(ctx.TryReadUInt64(HandleSlot, out var handle));
        return (uint)handle;
    }

    private Task<int> StartSemaWait(uint handle, int slot, int need, uint? timeoutUsec)
    {
        var ready = new ManualResetEventSlim();
        var task = Task.Factory.StartNew(() =>
        {
            var ctx = NewContext(slot);
            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = (ulong)need;
            ctx[CpuRegister.Rdx] = 0;
            if (timeoutUsec is { } usec)
            {
                Assert.True(ctx.TryWriteUInt32(Scratch(slot, 0), usec));
                ctx[CpuRegister.Rdx] = Scratch(slot, 0);
            }

            ready.Set();
            return KernelSemaphoreCompatExports.KernelWaitSema(ctx);
        }, TaskCreationOptions.LongRunning);
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        return task;
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached");
            Thread.Sleep(1);
        }
    }

    private static void SemaWaiters(uint handle, int count) =>
        WaitUntil(() => KernelSemaphoreCompatExports.WaiterCountForTests(handle) == count);

    private static void FlagWaiters(ulong handle, int count) =>
        WaitUntil(() => KernelEventFlagCompatExports.WaiterCountForTests(handle) == count);

    [Fact]
    public void SemaphoreGrantsLargeHeadRequestBeforeLaterSmallOnes()
    {
        var handle = CreateSema(initial: 0, max: 10);
        var big = StartSemaWait(handle, slot: 1, need: 3, timeoutUsec: null);
        SemaWaiters(handle, 1);
        var small = StartSemaWait(handle, slot: 2, need: 1, timeoutUsec: null);
        SemaWaiters(handle, 2);

        var ctx = NewContext(0);
        Assert.Equal(Ok, KernelSemaphoreCompatExports.KernelSignalSema(ctx, handle, 2));
        Thread.Sleep(20);
        Assert.Equal(2, KernelSemaphoreCompatExports.WaiterCountForTests(handle));
        Assert.False(big.IsCompleted);
        Assert.False(small.IsCompleted); // FIFO: the small request may not overtake the head

        Assert.Equal(Ok, KernelSemaphoreCompatExports.KernelSignalSema(ctx, handle, 2));
        Assert.True(Task.WaitAll(new Task[] { big, small }, TimeSpan.FromSeconds(5)));
        Assert.Equal(Ok, big.Result);
        Assert.Equal(Ok, small.Result);
        Delete(handle);
    }

    [Fact]
    public void SemaphoreTimeoutWritesZeroAndSuccessWritesRemainingTime()
    {
        var handle = CreateSema(initial: 0, max: 1);
        var timedOut = StartSemaWait(handle, slot: 1, need: 1, timeoutUsec: 20_000);
        Assert.True(timedOut.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimedOut, timedOut.Result);
        Assert.True(_probe.TryReadUInt32(Scratch(1, 0), out var left));
        Assert.Equal(0u, left);

        var woken = StartSemaWait(handle, slot: 2, need: 1, timeoutUsec: 5_000_000);
        SemaWaiters(handle, 1);
        Assert.Equal(Ok, KernelSemaphoreCompatExports.KernelSignalSema(NewContext(0), handle, 1));
        Assert.True(woken.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(Ok, woken.Result);
        Assert.True(_probe.TryReadUInt32(Scratch(2, 0), out var remaining));
        Assert.InRange(remaining, 3_000_000u, 5_000_000u);
        Delete(handle);
    }

    [Fact]
    public void SemaphoreCancelReleasesWaitersWithCanceledAndReportsTheirCount()
    {
        var handle = CreateSema(initial: 0, max: 5);
        var first = StartSemaWait(handle, slot: 1, need: 1, timeoutUsec: null);
        var second = StartSemaWait(handle, slot: 2, need: 2, timeoutUsec: null);
        SemaWaiters(handle, 2);

        var ctx = NewContext(0);
        Assert.Equal(Ok, KernelSemaphoreCompatExports.KernelCancelSema(ctx, handle, 4, CountSlot));
        Assert.True(Task.WaitAll(new Task[] { first, second }, TimeSpan.FromSeconds(5)));
        Assert.Equal(Canceled, first.Result);
        Assert.Equal(Canceled, second.Result);
        Assert.True(_probe.TryReadUInt32(CountSlot, out var count));
        Assert.Equal(2u, count);

        // The count was reset to the cancel value.
        Assert.Equal(Ok, KernelSemaphoreCompatExports.KernelPollSema(ctx, handle, 4));
        Delete(handle);
    }

    [Fact]
    public void SemaphoreDeleteReleasesWaitersWithDeleted()
    {
        var handle = CreateSema(initial: 0, max: 1);
        var waiter = StartSemaWait(handle, slot: 1, need: 1, timeoutUsec: null);
        SemaWaiters(handle, 1);
        Delete(handle);
        Assert.True(waiter.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(Deleted, waiter.Result);
    }

    [Fact]
    public void CooperativeSemaphoreWaiterParksAndResumesWithTheResult()
    {
        var handle = CreateSema(initial: 0, max: 1);
        var ctx = NewContext(1);
        Assert.True(ctx.TryWriteUInt32(Scratch(1, 0), 2_000_000));
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = Scratch(1, 0);
        IGuestThreadBlockWaiter? parked = null;
        var previousThread = GuestThreadExecution.EnterGuestThread(0x4401);
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(0x1_0000, 0x2_0000, 0x3_0000);
        try
        {
            KernelSemaphoreCompatExports.KernelWaitSema(ctx);
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(out var reason, out _, out _, out _, out parked, out var deadline));
            Assert.Equal("sceKernelWaitSema", reason);
            Assert.Equal(0, deadline);
        }
        finally
        {
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }

        Assert.NotNull(parked);
        Assert.False(parked!.TryWake());
        Assert.Equal(Ok, KernelSemaphoreCompatExports.KernelSignalSema(NewContext(0), handle, 1));
        Assert.True(parked.TryWake());
        Assert.Equal(Ok, parked.Resume());
        Assert.True(_probe.TryReadUInt32(Scratch(1, 0), out var remaining));
        Assert.InRange(remaining, 1_000_000u, 2_000_000u);
        Delete(handle);
    }

    [Fact]
    public void PosixSemTimedwaitTakesAnAbsoluteRealtimeDeadlineAndSetsErrno()
    {
        var ctx = NewContext(1);
        const ulong sem = Base + 0x400;
        const ulong abstime = Base + 0x420;
        ctx[CpuRegister.Rdi] = sem;
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, KernelSemaphoreCompatExports.PosixSemInit(ctx));

        // Taken before the deadline is: a slow runner can spend milliseconds between reading
        // the clock and entering the wait, which a later start would leave out of the elapsed time.
        var started = DateTime.UtcNow;
        KernelRuntimeCompatExports.GetClockTime(0, out var seconds, out var nanoseconds);
        nanoseconds += 20_000_000;
        if (nanoseconds >= 1_000_000_000)
        {
            seconds++;
            nanoseconds -= 1_000_000_000;
        }

        Assert.True(ctx.TryWriteUInt64(abstime, (ulong)seconds));
        Assert.True(ctx.TryWriteUInt64(abstime + 8, (ulong)nanoseconds));
        ctx[CpuRegister.Rdi] = sem;
        ctx[CpuRegister.Rsi] = abstime;
        Assert.Equal(-1, KernelSemaphoreCompatExports.PosixSemTimedWait(ctx));
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(15));
        Assert.True(ctx.TryReadUInt32(ctx.FsBase + 0x40, out var errno));
        Assert.Equal((uint)GuestErrno.ETIMEDOUT, errno);
        // The guest timespec is left intact.
        Assert.True(ctx.TryReadUInt64(abstime, out var secondsAfter));
        Assert.Equal((ulong)seconds, secondsAfter);

        ctx[CpuRegister.Rdi] = sem;
        Assert.Equal(-1, KernelSemaphoreCompatExports.PosixSemTryWait(ctx));
        Assert.True(ctx.TryReadUInt32(ctx.FsBase + 0x40, out errno));
        Assert.Equal((uint)GuestErrno.EAGAIN, errno);

        ctx[CpuRegister.Rdi] = sem;
        Assert.Equal(0, KernelSemaphoreCompatExports.PosixSemDestroy(ctx));
    }

    private void Delete(uint handle)
    {
        var ctx = NewContext(0);
        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(Ok, KernelSemaphoreCompatExports.KernelDeleteSema(ctx));
    }

    // Event flags --------------------------------------------------------------------

    private ulong CreateEventFlag(uint attr, ulong initial)
    {
        var ctx = NewContext(0);
        Assert.True(_memory.TryWrite(NameAddress, "w1-evf\0"u8));
        ctx[CpuRegister.Rdi] = HandleSlot;
        ctx[CpuRegister.Rsi] = NameAddress;
        ctx[CpuRegister.Rdx] = attr;
        ctx[CpuRegister.Rcx] = initial;
        ctx[CpuRegister.R8] = 0;
        Assert.Equal(Ok, KernelEventFlagCompatExports.KernelCreateEventFlag(ctx));
        Assert.True(ctx.TryReadUInt64(HandleSlot, out var handle));
        return handle;
    }

    private Task<int> StartFlagWait(ulong handle, int slot, ulong pattern, uint mode, uint? timeoutUsec)
    {
        var ready = new ManualResetEventSlim();
        var task = Task.Factory.StartNew(() =>
        {
            var ctx = NewContext(slot);
            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = pattern;
            ctx[CpuRegister.Rdx] = mode;
            ctx[CpuRegister.Rcx] = Scratch(slot, 8);
            ctx[CpuRegister.R8] = 0;
            if (timeoutUsec is { } usec)
            {
                Assert.True(ctx.TryWriteUInt32(Scratch(slot, 0), usec));
                ctx[CpuRegister.R8] = Scratch(slot, 0);
            }

            ready.Set();
            return KernelEventFlagCompatExports.KernelWaitEventFlag(ctx);
        }, TaskCreationOptions.LongRunning);
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        return task;
    }

    private void SetFlag(ulong handle, ulong pattern)
    {
        var ctx = NewContext(0);
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = pattern;
        Assert.Equal(Ok, KernelEventFlagCompatExports.KernelSetEventFlag(ctx));
    }

    private ulong ResultPattern(int slot)
    {
        Assert.True(_probe.TryReadUInt64(Scratch(slot, 8), out var value));
        return value;
    }

    [Fact]
    public void SingleEventFlagRefusesASecondWaiterWithPermissionDenied()
    {
        var handle = CreateEventFlag(attr: 0x11, initial: 0);
        var first = StartFlagWait(handle, slot: 1, pattern: 1, mode: 0x01, timeoutUsec: null);
        FlagWaiters(handle, 1);
        var second = StartFlagWait(handle, slot: 2, pattern: 1, mode: 0x01, timeoutUsec: null);
        Assert.True(second.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED, second.Result);
        SetFlag(handle, 1);
        Assert.True(first.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(Ok, first.Result);
        DeleteFlag(handle);
    }

    [Fact]
    public void MultiEventFlagWakesInFifoOrderAndAppliesClearModePerWaiter()
    {
        var handle = CreateEventFlag(attr: 0x21, initial: 0);
        var first = StartFlagWait(handle, slot: 1, pattern: 0x3, mode: 0x02 | 0x10, timeoutUsec: null); // OR, CLEAR_ALL
        FlagWaiters(handle, 1);
        var second = StartFlagWait(handle, slot: 2, pattern: 0x1, mode: 0x02, timeoutUsec: null);
        FlagWaiters(handle, 2);

        SetFlag(handle, 0x1);
        Assert.True(first.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(Ok, first.Result);
        Assert.Equal(0x1UL, ResultPattern(1));
        Assert.Equal(1, KernelEventFlagCompatExports.WaiterCountForTests(handle));
        Assert.False(second.IsCompleted); // the first waiter cleared every bit

        SetFlag(handle, 0x1);
        Assert.True(second.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(Ok, second.Result);
        DeleteFlag(handle);
    }

    [Fact]
    public void EventFlagCancelReleasesWaitersWithCanceledPatternAndCount()
    {
        var handle = CreateEventFlag(attr: 0x21, initial: 0);
        var first = StartFlagWait(handle, slot: 1, pattern: 0x8, mode: 0x01, timeoutUsec: null);
        var second = StartFlagWait(handle, slot: 2, pattern: 0x8, mode: 0x01, timeoutUsec: null);
        FlagWaiters(handle, 2);

        var ctx = NewContext(0);
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 0x5;
        ctx[CpuRegister.Rdx] = CountSlot;
        Assert.Equal(Ok, KernelEventFlagCompatExports.KernelCancelEventFlag(ctx));
        Assert.True(Task.WaitAll(new Task[] { first, second }, TimeSpan.FromSeconds(5)));
        Assert.Equal(Canceled, first.Result);
        Assert.Equal(Canceled, second.Result);
        Assert.Equal(0x5UL, ResultPattern(1));
        Assert.Equal(0x5UL, ResultPattern(2));
        Assert.True(_probe.TryReadUInt32(CountSlot, out var count));
        Assert.Equal(2u, count);
        DeleteFlag(handle);
    }

    [Fact]
    public void EventFlagDeleteReleasesWaitersWithDeleted()
    {
        var handle = CreateEventFlag(attr: 0x21, initial: 0);
        var waiter = StartFlagWait(handle, slot: 1, pattern: 0x2, mode: 0x01, timeoutUsec: null);
        FlagWaiters(handle, 1);
        DeleteFlag(handle);
        Assert.True(waiter.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(Deleted, waiter.Result);
    }

    [Fact]
    public void EventFlagTimeoutReportsCurrentBitsAndRemainingTime()
    {
        var handle = CreateEventFlag(attr: 0x21, initial: 0x10);
        var timedOut = StartFlagWait(handle, slot: 1, pattern: 0x1, mode: 0x01, timeoutUsec: 20_000);
        Assert.True(timedOut.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimedOut, timedOut.Result);
        Assert.Equal(0x10UL, ResultPattern(1));
        Assert.True(_probe.TryReadUInt32(Scratch(1, 0), out var left));
        Assert.Equal(0u, left);

        var woken = StartFlagWait(handle, slot: 2, pattern: 0x1, mode: 0x01, timeoutUsec: 5_000_000);
        FlagWaiters(handle, 1);
        SetFlag(handle, 0x1);
        Assert.True(woken.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(Ok, woken.Result);
        Assert.Equal(0x11UL, ResultPattern(2));
        Assert.True(_probe.TryReadUInt32(Scratch(2, 0), out var remaining));
        Assert.InRange(remaining, 3_000_000u, 5_000_000u);
        DeleteFlag(handle);
    }

    private void DeleteFlag(ulong handle)
    {
        var ctx = NewContext(0);
        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(Ok, KernelEventFlagCompatExports.KernelDeleteEventFlag(ctx));
    }
}
