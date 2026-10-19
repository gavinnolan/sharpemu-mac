// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

/// <summary>
/// Rwlock writer preference and hand-off, timed and try variants; barriers; deferred
/// cancellation state; join/detach bookkeeping; sleep and priority helpers.
/// </summary>
public sealed class PthreadRwlockBarrierCancelTests
{
    private const int EPERM = 1;
    private const int ESRCH = 3;
    private const int EBUSY = 16;
    private const int EINVAL = 22;
    private const int ETIMEDOUT = 60;

    private static CpuContext NewContext(PthreadMutexSemanticsTests.AllocatingCpuMemory memory, ulong rdi)
    {
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = rdi;
        return context;
    }

    private static PthreadMutexSemanticsTests.AllocatingCpuMemory NewMemory(ulong memoryBase) =>
        new(memoryBase, 0x10000);

    [Theory]
    [InlineData("XD3mDeybCnk", "scePthreadRwlockTryrdlock")]
    [InlineData("iPtZRWICjrM", "scePthreadRwlockTimedrdlock")]
    [InlineData("adh--6nIqTk", "scePthreadRwlockTimedwrlock")]
    [InlineData("lb8lnYo-o7k", "pthread_rwlock_timedrdlock")]
    [InlineData("9zklzAl9CGM", "pthread_rwlock_timedwrlock")]
    [InlineData("IafI2PxcPnQ", "scePthreadMutexTimedlock")]
    [InlineData("Io9+nTKXZtA", "pthread_mutex_timedlock")]
    [InlineData("K953PF5u6Pc", "pthread_cond_reltimedwait_np")]
    [InlineData("t9vVyTglqHQ", "scePthreadBarrierWait")]
    [InlineData("CawZgCYqXWk", "pthread_barrier_wait")]
    [InlineData("0D4-FVvEikw", "pthread_cancel")]
    [InlineData("-ZR+hG7aDHw", "sceKernelSleep")]
    [InlineData("CBNtXOoef-E", "sched_get_priority_max")]
    [InlineData("gquEhBrS2iw", "scePthreadMutexattrGettype")]
    public void NewExportsAreRegistered(string nid, string name)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
    }

    [Fact]
    public void WriteRelockAndReadUnderWriteAreDeadlocks()
    {
        var memory = NewMemory(0x7_2000_0000);
        var ctx = NewContext(memory, 0x7_2000_0100);
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockInit(ctx));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockWrlock(ctx));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK, KernelPthreadExtendedCompatExports.PthreadRwlockWrlock(ctx));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK, KernelPthreadExtendedCompatExports.PthreadRwlockRdlock(ctx));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(ctx));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED, KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(ctx));
        Assert.Equal(EPERM, KernelPthreadExtendedCompatExports.PosixPthreadRwlockUnlock(ctx));
    }

    [Fact]
    public void AWaitingWriterBlocksNewReadersAndGetsTheLockFromTheLastReader()
    {
        var memory = NewMemory(0x7_2001_0000);
        const ulong rwlock = 0x7_2001_0100;
        var ctx = NewContext(memory, rwlock);
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockInit(ctx));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockRdlock(ctx));

        using var writerHolds = new ManualResetEventSlim();
        using var writerRelease = new ManualResetEventSlim();
        var writerResult = -1;
        var writer = new Thread(() =>
        {
            var c = NewContext(memory, rwlock);
            writerResult = KernelPthreadExtendedCompatExports.PthreadRwlockWrlock(c);
            writerHolds.Set();
            writerRelease.Wait(TimeSpan.FromSeconds(10));
            Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(c));
        }) { IsBackground = true };
        writer.Start();
        Assert.False(writerHolds.Wait(50));

        // Writer preference: a fresh reader on another thread must wait behind the writer.
        var readerTry = 0;
        var reader = new Thread(() => readerTry = KernelPthreadExtendedCompatExports.PosixPthreadRwlockTryrdlock(NewContext(memory, rwlock)));
        reader.Start();
        Assert.True(reader.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(EBUSY, readerTry);

        // The current reader may still re-enter (libthr's exemption for read holders).
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockRdlock(ctx));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(ctx));
        Assert.False(writerHolds.IsSet);

        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(ctx));
        Assert.True(writerHolds.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, writerResult);
        writerRelease.Set();
        Assert.True(writer.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockDestroy(ctx));
    }

    [Fact]
    public void AWriterUnlockGrantsEveryQueuedReaderAtOnce()
    {
        var memory = NewMemory(0x7_2002_0000);
        const ulong rwlock = 0x7_2002_0100;
        var ctx = NewContext(memory, rwlock);
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockInit(ctx));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockWrlock(ctx));

        const int readers = 3;
        var holding = 0;
        var maxHolding = 0;
        using var allIn = new CountdownEvent(readers);
        using var release = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, readers).Select(_ => new Thread(() =>
        {
            var c = NewContext(memory, rwlock);
            Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockRdlock(c));
            var now = Interlocked.Increment(ref holding);
            int seen;
            while ((seen = Volatile.Read(ref maxHolding)) < now && Interlocked.CompareExchange(ref maxHolding, now, seen) != seen)
            {
            }

            allIn.Signal();
            release.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Decrement(ref holding);
            Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(c));
        }) { IsBackground = true }).ToArray();
        foreach (var thread in threads)
        {
            thread.Start();
        }

        Thread.Sleep(50);
        Assert.Equal(0, Volatile.Read(ref holding));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(ctx));
        Assert.True(allIn.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(readers, Volatile.Read(ref maxHolding));
        release.Set();
        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public void TimedLocksExpireAndAnExpiredWriterNoLongerBlocksReaders()
    {
        var memory = NewMemory(0x7_2003_0000);
        const ulong rwlock = 0x7_2003_0100;
        const ulong timeAddress = 0x7_2003_0400;
        var ctx = NewContext(memory, rwlock);
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockInit(ctx));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockRdlock(ctx));

        var sceResult = 0;
        var posixResult = 0;
        long elapsedMs = 0;
        var writer = new Thread(() =>
        {
            var c = NewContext(memory, rwlock);
            c[CpuRegister.Rsi] = 30_000;
            var watch = Stopwatch.StartNew();
            sceResult = KernelPthreadExtendedCompatExports.PthreadRwlockTimedwrlock(c);
            elapsedMs = watch.ElapsedMilliseconds;

            KernelRuntimeCompatExports.GetClockTime(0, out var seconds, out var nanoseconds);
            nanoseconds += 20_000_000;
            seconds += nanoseconds / 1_000_000_000;
            nanoseconds %= 1_000_000_000;
            Assert.True(c.TryWriteUInt64(timeAddress, (ulong)seconds));
            Assert.True(c.TryWriteUInt64(timeAddress + 8, (ulong)nanoseconds));
            c[CpuRegister.Rdi] = rwlock;
            c[CpuRegister.Rsi] = timeAddress;
            posixResult = KernelPthreadExtendedCompatExports.PosixPthreadRwlockTimedwrlock(c);
        });
        writer.Start();
        Assert.True(writer.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, sceResult);
        Assert.Equal(ETIMEDOUT, posixResult);
        Assert.InRange(elapsedMs, 25, 10_000);

        // The departed writer no longer holds readers back.
        var tryResult = -1;
        var reader = new Thread(() => tryResult = KernelPthreadExtendedCompatExports.PthreadRwlockTryrdlock(NewContext(memory, rwlock)));
        reader.Start();
        Assert.True(reader.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, tryResult);
    }

    [Fact]
    public void RwlockStressKeepsWritersExclusive()
    {
        var memory = NewMemory(0x7_2004_0000);
        const ulong rwlock = 0x7_2004_0100;
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockInit(NewContext(memory, rwlock)));
        var writers = 0;
        var readers = 0;
        var violations = 0;
        var failures = 0;
        var threads = Enumerable.Range(0, 8).Select(index => new Thread(() =>
        {
            var c = NewContext(memory, rwlock);
            var write = index % 2 == 0;
            for (var i = 0; i < 1500; i++)
            {
                var r = write
                    ? KernelPthreadExtendedCompatExports.PthreadRwlockWrlock(c)
                    : KernelPthreadExtendedCompatExports.PthreadRwlockRdlock(c);
                if (r != 0) { Interlocked.Increment(ref failures); return; }
                if (write)
                {
                    if (Interlocked.Increment(ref writers) != 1 || Volatile.Read(ref readers) != 0) Interlocked.Increment(ref violations);
                    Thread.SpinWait(20);
                    Interlocked.Decrement(ref writers);
                }
                else
                {
                    Interlocked.Increment(ref readers);
                    if (Volatile.Read(ref writers) != 0) Interlocked.Increment(ref violations);
                    Thread.SpinWait(20);
                    Interlocked.Decrement(ref readers);
                }

                if (KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(c) != 0) { Interlocked.Increment(ref failures); return; }
            }
        }) { IsBackground = true }).ToArray();
        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        }

        Assert.Equal(0, failures);
        Assert.Equal(0, violations);
    }

    [Fact]
    public void BarrierReleasesEachCycleWithOneSerialThread()
    {
        var memory = NewMemory(0x7_2005_0000);
        const ulong barrier = 0x7_2005_0100;
        var ctx = NewContext(memory, barrier);
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(EINVAL, KernelPthreadBarrierCancelCompatExports.PosixPthreadBarrierInit(ctx));
        const int parties = 4;
        const int cycles = 200;
        ctx[CpuRegister.Rdx] = parties;
        Assert.Equal(0, KernelPthreadBarrierCancelCompatExports.PosixPthreadBarrierInit(ctx));

        var serials = 0;
        var failures = 0;
        var phase = new int[cycles];
        var threads = Enumerable.Range(0, parties).Select(index => new Thread(() =>
        {
            var c = NewContext(memory, barrier);
            for (var cycle = 0; cycle < cycles; cycle++)
            {
                Interlocked.Increment(ref phase[cycle]);
                var r = index % 2 == 0
                    ? KernelPthreadBarrierCancelCompatExports.PosixPthreadBarrierWait(c)
                    : KernelPthreadBarrierCancelCompatExports.PthreadBarrierWait(c);
                if (r == KernelPthreadBarrierCancelCompatExports.BarrierSerialThread)
                {
                    Interlocked.Increment(ref serials);
                }
                else if (r != 0)
                {
                    Interlocked.Increment(ref failures);
                    return;
                }

                // Nobody passes the barrier before every party arrived.
                if (Volatile.Read(ref phase[cycle]) != parties)
                {
                    Interlocked.Increment(ref failures);
                }
            }
        }) { IsBackground = true }).ToArray();
        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        }

        Assert.Equal(0, failures);
        Assert.Equal(cycles, serials);
        Assert.Equal(0, KernelPthreadBarrierCancelCompatExports.PosixPthreadBarrierDestroy(ctx));
        Assert.Equal(EINVAL, KernelPthreadBarrierCancelCompatExports.PosixPthreadBarrierWait(ctx));
    }

    [Fact]
    public void BarrierDestroyWithAWaiterIsBusy()
    {
        var memory = NewMemory(0x7_2006_0000);
        const ulong barrier = 0x7_2006_0100;
        var ctx = NewContext(memory, barrier);
        ctx[CpuRegister.Rdx] = 2;
        Assert.Equal(0, KernelPthreadBarrierCancelCompatExports.PthreadBarrierInit(ctx));
        var waiterResult = 99;
        var waiter = new Thread(() => waiterResult = KernelPthreadBarrierCancelCompatExports.PosixPthreadBarrierWait(NewContext(memory, barrier)));
        waiter.Start();
        Thread.Sleep(30);
        Assert.Equal(EBUSY, KernelPthreadBarrierCancelCompatExports.PosixPthreadBarrierDestroy(ctx));
        Assert.Equal(KernelPthreadBarrierCancelCompatExports.BarrierSerialThread, KernelPthreadBarrierCancelCompatExports.PosixPthreadBarrierWait(ctx));
        Assert.True(waiter.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, waiterResult);
        Assert.Equal(0, KernelPthreadBarrierCancelCompatExports.PthreadBarrierDestroy(ctx));
    }

    [Fact]
    public void CancelStateAndTypeValidateAndReportTheOldValue()
    {
        var memory = NewMemory(0x7_2007_0000);
        const ulong oldAddress = 0x7_2007_0100;
        var ctx = NewContext(memory, 1); // DISABLE
        ctx[CpuRegister.Rsi] = oldAddress;
        Assert.Equal(0, KernelPthreadBarrierCancelCompatExports.PosixPthreadSetcancelstate(ctx));
        Assert.True(ctx.TryReadUInt32(oldAddress, out var old));
        Assert.Equal(0u, old);

        // Cancelled while disabled: testcancel does nothing.
        var self = KernelPthreadState.GetCurrentThreadHandle();
        var cancelContext = NewContext(memory, self);
        Assert.Equal(0, KernelPthreadBarrierCancelCompatExports.PosixPthreadCancel(cancelContext));
        Assert.False(KernelPthreadBarrierCancelCompatExports.IsCancelPending(self));
        Assert.Equal(0, KernelPthreadBarrierCancelCompatExports.PosixPthreadTestcancel(ctx));

        ctx[CpuRegister.Rdi] = 0; // ENABLE: now pending
        Assert.Equal(0, KernelPthreadBarrierCancelCompatExports.PosixPthreadSetcancelstate(ctx));
        Assert.True(ctx.TryReadUInt32(oldAddress, out old));
        Assert.Equal(1u, old);
        Assert.True(KernelPthreadBarrierCancelCompatExports.IsCancelPending(self));

        ctx[CpuRegister.Rdi] = 5;
        Assert.Equal(EINVAL, KernelPthreadBarrierCancelCompatExports.PosixPthreadSetcancelstate(ctx));
        ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(EINVAL, KernelPthreadBarrierCancelCompatExports.PosixPthreadSetcanceltype(ctx));
        ctx[CpuRegister.Rdi] = 2; // ASYNCHRONOUS
        Assert.Equal(0, KernelPthreadBarrierCancelCompatExports.PosixPthreadSetcanceltype(ctx));

        Assert.Equal(ESRCH, KernelPthreadBarrierCancelCompatExports.PosixPthreadCancel(NewContext(memory, 0)));
        Assert.NotEqual(0, KernelPthreadBarrierCancelCompatExports.PthreadCancel(NewContext(memory, 0)));

        // Leave this host thread with cancellation disabled again.
        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelPthreadBarrierCancelCompatExports.PosixPthreadSetcancelstate(ctx));
        ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(0, KernelPthreadBarrierCancelCompatExports.PosixPthreadSetcanceltype(ctx));
    }

    [Fact]
    public void JoinAndDetachFollowLibthrErrors()
    {
        var memory = NewMemory(0x7_2008_0000);
        var previous = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = null;
        try
        {
            var self = KernelPthreadState.GetCurrentThreadHandle();
            var ctx = NewContext(memory, self);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK, KernelExports.PthreadJoin(ctx));
            Assert.Equal(11, KernelExports.PosixPthreadJoin(NewContext(memory, self)));
            Assert.Equal(ESRCH, KernelExports.PosixPthreadJoin(NewContext(memory, 0)));

            const ulong other = 0x7777_0001;
            var detachContext = NewContext(memory, other);
            Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadDetach(detachContext));
            Assert.Equal(EINVAL, KernelPthreadExtendedCompatExports.PosixPthreadDetach(NewContext(memory, other)));
            Assert.Equal(EINVAL, KernelExports.PosixPthreadJoin(NewContext(memory, other)));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previous;
        }
    }

    [Fact]
    public void SchedPriorityRangeIsTheOrbisRealtimeRange()
    {
        var memory = NewMemory(0x7_2009_0000);
        Assert.Equal(767, KernelPthreadBarrierCancelCompatExports.SchedGetPriorityMax(NewContext(memory, 1)));
        Assert.Equal(256, KernelPthreadBarrierCancelCompatExports.SchedGetPriorityMin(NewContext(memory, 3)));
        Assert.Equal(-1, KernelPthreadBarrierCancelCompatExports.SchedGetPriorityMax(NewContext(memory, 2)));
        Assert.Equal(0, KernelPthreadBarrierCancelCompatExports.KernelSleep(NewContext(memory, 0)));
    }

    [Fact]
    public void ARwlockSlotReinitialisedWithoutDestroyGetsAFreshLock()
    {
        var memory = NewMemory(0x7_200A_0000);
        const ulong slot = 0x7_200A_0100;
        var ctx = NewContext(memory, slot);
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockWrlock(ctx));
        Assert.True(ctx.TryReadUInt64(slot, out var firstHandle));
        Assert.NotEqual(0UL, firstHandle);

        Assert.True(ctx.TryWriteUInt64(slot, 0));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockWrlock(ctx));
        Assert.True(ctx.TryReadUInt64(slot, out var secondHandle));
        Assert.NotEqual(firstHandle, secondHandle);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_DEADLOCK, KernelPthreadExtendedCompatExports.PthreadRwlockWrlock(ctx));
        Assert.Equal(0, KernelPthreadExtendedCompatExports.PthreadRwlockUnlock(ctx));
    }
}
