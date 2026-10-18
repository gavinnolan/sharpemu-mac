// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

/// <summary>
/// Mutex and condition semantics against FreeBSD libthr: self-lock and unowned-unlock codes,
/// timed locks, attribute validation, errno vs sce results, recursive mutexes in a condition
/// wait and 64-bit condition deadlines.
/// </summary>
public sealed class PthreadMutexCondErrnoTests
{
    private const int EPERM = 1;
    private const int EINVAL = 22;
    private const int EBUSY = 16;
    private const int EDEADLK = 11;
    private const int ETIMEDOUT = 60;

    private static (CpuContext Context, PthreadMutexSemanticsTests.AllocatingCpuMemory Memory) NewContext(ulong memoryBase)
    {
        var memory = new PthreadMutexSemanticsTests.AllocatingCpuMemory(memoryBase, 0x10000);
        return (new CpuContext(memory, Generation.Gen5), memory);
    }

    private static void InitMutex(CpuContext ctx, ulong attrAddress, ulong mutexAddress, int type)
    {
        ctx[CpuRegister.Rdi] = attrAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexattrInit(ctx));
        ctx[CpuRegister.Rsi] = (ulong)type;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexattrSettype(ctx));
        ctx[CpuRegister.Rdi] = mutexAddress;
        ctx[CpuRegister.Rsi] = attrAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexInit(ctx));
    }

    private static void WriteAbsoluteRealtime(CpuContext ctx, ulong address, long secondsFromNow, long extraNanoseconds = 0)
    {
        KernelRuntimeCompatExports.GetClockTime(0, out var seconds, out var nanoseconds);
        var totalNanoseconds = nanoseconds + extraNanoseconds;
        seconds += secondsFromNow + totalNanoseconds / 1_000_000_000L;
        totalNanoseconds %= 1_000_000_000L;
        Assert.True(ctx.TryWriteUInt64(address, unchecked((ulong)seconds)));
        Assert.True(ctx.TryWriteUInt64(address + 8, (ulong)totalNanoseconds));
    }

    [Fact]
    public void UnlockOfAFreeMutexIsEpermInBothForms()
    {
        var (ctx, _) = NewContext(0x7_1000_0000);
        InitMutex(ctx, 0x7_1000_0100, 0x7_1000_0200, type: 1);
        ctx[CpuRegister.Rdi] = 0x7_1000_0200;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED, KernelPthreadCompatExports.PthreadMutexUnlock(ctx));
        Assert.Equal(EPERM, KernelPthreadCompatExports.PosixPthreadMutexUnlock(ctx));
    }

    [Fact]
    public void PosixSelfLockAndTrylockReturnErrnoValues()
    {
        var (ctx, _) = NewContext(0x7_1001_0000);
        InitMutex(ctx, 0x7_1001_0100, 0x7_1001_0200, type: 1);
        ctx[CpuRegister.Rdi] = 0x7_1001_0200;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexLock(ctx));
        Assert.Equal(EDEADLK, KernelPthreadCompatExports.PosixPthreadMutexLock(ctx));
        Assert.Equal(EBUSY, KernelPthreadCompatExports.PosixPthreadMutexTrylock(ctx));
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexUnlock(ctx));
    }

    [Fact]
    public void UnlockByANonOwnerIsEperm()
    {
        var (ctx, memory) = NewContext(0x7_1002_0000);
        InitMutex(ctx, 0x7_1002_0100, 0x7_1002_0200, type: 1);
        ctx[CpuRegister.Rdi] = 0x7_1002_0200;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ctx));
        var result = -1;
        var other = new Thread(() =>
        {
            var otherContext = new CpuContext(memory, Generation.Gen5);
            otherContext[CpuRegister.Rdi] = 0x7_1002_0200;
            result = KernelPthreadCompatExports.PosixPthreadMutexUnlock(otherContext);
        });
        other.Start();
        Assert.True(other.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(EPERM, result);
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ctx));
    }

    [Fact]
    public void TimedlockOnAMutexHeldElsewhereTimesOutAndLeavesNoWaiter()
    {
        var (ctx, memory) = NewContext(0x7_1003_0000);
        const ulong mutexAddress = 0x7_1003_0200;
        InitMutex(ctx, 0x7_1003_0100, mutexAddress, type: 1);
        ctx[CpuRegister.Rdi] = mutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ctx));

        var sceResult = 0;
        var posixResult = 0;
        long elapsedMs = 0;
        var other = new Thread(() =>
        {
            var otherContext = new CpuContext(memory, Generation.Gen5);
            otherContext[CpuRegister.Rdi] = mutexAddress;
            otherContext[CpuRegister.Rsi] = 30_000; // 30 ms
            var watch = Stopwatch.StartNew();
            sceResult = KernelPthreadCompatExports.PthreadMutexTimedlock(otherContext);
            elapsedMs = watch.ElapsedMilliseconds;

            WriteAbsoluteRealtime(otherContext, 0x7_1003_0400, 0, 20_000_000);
            otherContext[CpuRegister.Rdi] = mutexAddress;
            otherContext[CpuRegister.Rsi] = 0x7_1003_0400;
            posixResult = KernelPthreadCompatExports.PosixPthreadMutexTimedlock(otherContext);
        });
        other.Start();
        Assert.True(other.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, sceResult);
        Assert.Equal(ETIMEDOUT, posixResult);
        Assert.InRange(elapsedMs, 25, 10_000);

        // The timed-out waiters left the queue: the owner's unlock is uncontended and a
        // later locker gets the mutex at once.
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ctx));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexTrylock(ctx));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ctx));
    }

    [Fact]
    public void TimedlockIsGrantedWhenTheOwnerUnlocksBeforeTheDeadline()
    {
        var (ctx, memory) = NewContext(0x7_1004_0000);
        const ulong mutexAddress = 0x7_1004_0200;
        InitMutex(ctx, 0x7_1004_0100, mutexAddress, type: 1);
        ctx[CpuRegister.Rdi] = mutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ctx));

        var result = -1;
        var other = new Thread(() =>
        {
            var otherContext = new CpuContext(memory, Generation.Gen5);
            otherContext[CpuRegister.Rdi] = mutexAddress;
            otherContext[CpuRegister.Rsi] = 5_000_000; // 5 s
            result = KernelPthreadCompatExports.PthreadMutexTimedlock(otherContext);
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(otherContext));
        });
        other.Start();
        Thread.Sleep(30);
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ctx));
        Assert.True(other.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, result);
    }

    [Fact]
    public void TimedSelfLockSleepsUntilTheDeadline()
    {
        var (ctx, _) = NewContext(0x7_1005_0000);
        InitMutex(ctx, 0x7_1005_0100, 0x7_1005_0200, type: 1);
        ctx[CpuRegister.Rdi] = 0x7_1005_0200;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ctx));
        ctx[CpuRegister.Rsi] = 20_000;
        var watch = Stopwatch.StartNew();
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, KernelPthreadCompatExports.PthreadMutexTimedlock(ctx));
        Assert.True(watch.ElapsedMilliseconds >= 18, $"returned after {watch.ElapsedMilliseconds} ms");
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ctx));
    }

    [Fact]
    public void MutexattrRejectsUnknownTypesAndProtocolsAndReportsStoredValues()
    {
        var (ctx, _) = NewContext(0x7_1006_0000);
        const ulong attrAddress = 0x7_1006_0100;
        const ulong outAddress = 0x7_1006_0300;
        ctx[CpuRegister.Rdi] = attrAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexattrInit(ctx));

        foreach (var invalidType in new ulong[] { 0, 5, 0xFFFF_FFFF })
        {
            ctx[CpuRegister.Rsi] = invalidType;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, KernelPthreadCompatExports.PthreadMutexattrSettype(ctx));
            Assert.Equal(EINVAL, KernelPthreadCompatExports.PosixPthreadMutexattrSettype(ctx));
        }

        ctx[CpuRegister.Rsi] = 2;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexattrSettype(ctx));
        ctx[CpuRegister.Rsi] = outAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexattrGettype(ctx));
        Assert.True(ctx.TryReadUInt32(outAddress, out var type));
        Assert.Equal(2u, type);

        ctx[CpuRegister.Rsi] = 3;
        Assert.Equal(EINVAL, KernelPthreadCompatExports.PosixPthreadMutexattrSetprotocol(ctx));
        ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexattrSetprotocol(ctx));
        ctx[CpuRegister.Rsi] = outAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexattrGetprotocol(ctx));
        Assert.True(ctx.TryReadUInt32(outAddress, out var protocol));
        Assert.Equal(1u, protocol);

        ctx[CpuRegister.Rsi] = 700;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexattrSetprioceiling(ctx));
        ctx[CpuRegister.Rsi] = outAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexattrGetprioceiling(ctx));
        Assert.True(ctx.TryReadUInt32(outAddress, out var ceiling));
        Assert.Equal(700u, ceiling);

        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(EINVAL, KernelPthreadCompatExports.PosixPthreadMutexattrGettype(ctx));
    }

    [Fact]
    public void RecursiveMutexIsReleasedFullyForACondWaitAndKeepsItsDepth()
    {
        var (ctx, memory) = NewContext(0x7_1007_0000);
        const ulong mutexAddress = 0x7_1007_0200;
        const ulong condAddress = 0x7_1007_0300;
        InitMutex(ctx, 0x7_1007_0100, mutexAddress, type: 2);
        ctx[CpuRegister.Rdi] = condAddress;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondInit(ctx));

        ctx[CpuRegister.Rdi] = mutexAddress;
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ctx));
        }

        // Another thread must be able to take the mutex while we wait, then signal.
        var otherLocked = -1;
        var other = new Thread(() =>
        {
            var otherContext = new CpuContext(memory, Generation.Gen5);
            otherContext[CpuRegister.Rdi] = mutexAddress;
            otherLocked = KernelPthreadCompatExports.PthreadMutexLock(otherContext);
            otherContext[CpuRegister.Rdi] = condAddress;
            Assert.Equal(0, KernelPthreadCompatExports.PthreadCondSignal(otherContext));
            otherContext[CpuRegister.Rdi] = mutexAddress;
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(otherContext));
        });

        ctx[CpuRegister.Rdi] = condAddress;
        ctx[CpuRegister.Rsi] = mutexAddress;
        ctx[CpuRegister.Rdx] = 5_000_000;
        other.Start();
        Assert.Equal(0, KernelPthreadCompatExports.PthreadCondTimedwait(ctx));
        Assert.True(other.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, otherLocked);

        ctx[CpuRegister.Rdi] = mutexAddress;
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ctx));
        }

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED, KernelPthreadCompatExports.PthreadMutexUnlock(ctx));
    }

    [Fact]
    public void PosixCondTimedwaitKeepsDeadlinesBeyondThe32BitMicrosecondRange()
    {
        var (ctx, memory) = NewContext(0x7_1008_0000);
        const ulong mutexAddress = 0x7_1008_0200;
        const ulong condAddress = 0x7_1008_0300;
        const ulong timeAddress = 0x7_1008_0400;
        InitMutex(ctx, 0x7_1008_0100, mutexAddress, type: 1);
        ctx[CpuRegister.Rdi] = condAddress;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondInit(ctx));

        // Two hours out: more than uint.MaxValue microseconds (~71.6 min). The old 32-bit
        // conversion clamped this; the wait must simply last until the signal.
        WriteAbsoluteRealtime(ctx, timeAddress, 2 * 3600);
        ctx[CpuRegister.Rdi] = mutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexLock(ctx));
        var signaller = new Thread(() =>
        {
            Thread.Sleep(30);
            var otherContext = new CpuContext(memory, Generation.Gen5);
            otherContext[CpuRegister.Rdi] = mutexAddress;
            Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexLock(otherContext));
            otherContext[CpuRegister.Rdi] = condAddress;
            Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondSignal(otherContext));
            otherContext[CpuRegister.Rdi] = mutexAddress;
            Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexUnlock(otherContext));
        });
        signaller.Start();
        ctx[CpuRegister.Rdi] = condAddress;
        ctx[CpuRegister.Rsi] = mutexAddress;
        ctx[CpuRegister.Rdx] = timeAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondTimedwait(ctx));
        Assert.True(signaller.Join(TimeSpan.FromSeconds(10)));

        // A past abstime times out with the mutex re-acquired.
        WriteAbsoluteRealtime(ctx, timeAddress, -1);
        Assert.Equal(ETIMEDOUT, KernelPthreadCompatExports.PosixPthreadCondTimedwait(ctx));

        // Malformed nanoseconds: EINVAL, the mutex is still held.
        Assert.True(ctx.TryWriteUInt64(timeAddress + 8, 1_000_000_000));
        Assert.Equal(EINVAL, KernelPthreadCompatExports.PosixPthreadCondTimedwait(ctx));

        // Relative form.
        Assert.True(ctx.TryWriteUInt64(timeAddress, 0));
        Assert.True(ctx.TryWriteUInt64(timeAddress + 8, 10_000_000));
        Assert.Equal(ETIMEDOUT, KernelPthreadCompatExports.PosixPthreadCondReltimedwaitNp(ctx));

        ctx[CpuRegister.Rdi] = mutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexUnlock(ctx));
    }

    [Fact]
    public void CondWaitWithAMutexHeldByAnotherThreadIsEperm()
    {
        var (ctx, memory) = NewContext(0x7_1009_0000);
        const ulong mutexAddress = 0x7_1009_0200;
        const ulong condAddress = 0x7_1009_0300;
        InitMutex(ctx, 0x7_1009_0100, mutexAddress, type: 1);
        ctx[CpuRegister.Rdi] = mutexAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(ctx));
        var result = -1;
        var other = new Thread(() =>
        {
            var otherContext = new CpuContext(memory, Generation.Gen5);
            otherContext[CpuRegister.Rdi] = condAddress;
            otherContext[CpuRegister.Rsi] = mutexAddress;
            result = KernelPthreadCompatExports.PosixPthreadCondWait(otherContext);
        });
        other.Start();
        Assert.True(other.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(EPERM, result);
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(ctx));
    }

    [Fact]
    public void ProducerConsumerStressWithTimedWaitsKeepsEveryItem()
    {
        var (ctx, memory) = NewContext(0x7_100A_0000);
        const ulong mutexAddress = 0x7_100A_0200;
        const ulong condAddress = 0x7_100A_0300;
        InitMutex(ctx, 0x7_100A_0100, mutexAddress, type: 1);
        ctx[CpuRegister.Rdi] = condAddress;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadCondInit(ctx));

        const int producers = 4;
        const int consumers = 4;
        const int itemsPerProducer = 2000;
        var queue = 0;
        var consumed = 0;
        var failures = 0;
        var done = false;
        var threads = new List<Thread>();
        for (var p = 0; p < producers; p++)
        {
            threads.Add(new Thread(() =>
            {
                var c = new CpuContext(memory, Generation.Gen5);
                for (var i = 0; i < itemsPerProducer; i++)
                {
                    c[CpuRegister.Rdi] = mutexAddress;
                    if (KernelPthreadCompatExports.PthreadMutexLock(c) != 0) { Interlocked.Increment(ref failures); return; }
                    queue++;
                    c[CpuRegister.Rdi] = condAddress;
                    _ = KernelPthreadCompatExports.PthreadCondSignal(c);
                    c[CpuRegister.Rdi] = mutexAddress;
                    if (KernelPthreadCompatExports.PthreadMutexUnlock(c) != 0) { Interlocked.Increment(ref failures); return; }
                }
            }));
        }

        for (var k = 0; k < consumers; k++)
        {
            threads.Add(new Thread(() =>
            {
                var c = new CpuContext(memory, Generation.Gen5);
                while (true)
                {
                    c[CpuRegister.Rdi] = mutexAddress;
                    if (KernelPthreadCompatExports.PthreadMutexLock(c) != 0) { Interlocked.Increment(ref failures); return; }
                    while (queue == 0 && !Volatile.Read(ref done))
                    {
                        c[CpuRegister.Rdi] = condAddress;
                        c[CpuRegister.Rsi] = mutexAddress;
                        c[CpuRegister.Rdx] = 2_000; // 2 ms: exercises the timeout path as well
                        var r = KernelPthreadCompatExports.PthreadCondTimedwait(c);
                        if (r != 0 && r != (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT)
                        {
                            Interlocked.Increment(ref failures);
                            return;
                        }
                    }

                    var finished = queue == 0 && Volatile.Read(ref done);
                    if (!finished)
                    {
                        queue--;
                        consumed++;
                    }

                    c[CpuRegister.Rdi] = mutexAddress;
                    if (KernelPthreadCompatExports.PthreadMutexUnlock(c) != 0) { Interlocked.Increment(ref failures); return; }
                    if (finished)
                    {
                        return;
                    }
                }
            }));
        }

        foreach (var thread in threads)
        {
            thread.IsBackground = true;
            thread.Start();
        }

        for (var p = 0; p < producers; p++)
        {
            Assert.True(threads[p].Join(TimeSpan.FromSeconds(60)));
        }

        Volatile.Write(ref done, true);
        ctx[CpuRegister.Rdi] = condAddress;
        Assert.Equal(0, KernelPthreadCompatExports.PthreadCondBroadcast(ctx));
        for (var k = producers; k < threads.Count; k++)
        {
            Assert.True(threads[k].Join(TimeSpan.FromSeconds(60)));
        }

        Assert.Equal(0, failures);
        Assert.Equal(producers * itemsPerProducer, consumed);
        Assert.Equal(0, queue);
    }

    [Fact]
    public void ASlotReinitialisedWithoutDestroyGetsAFreshMutex()
    {
        // The slot was locked through an implicit (zero-slot) mutex, then the memory was reused
        // and zeroed without a destroy: the next lock is on a new, free mutex rather than the
        // stale one cached under the slot's address.
        var (ctx, _) = NewContext(0x7_100A_0000);
        const ulong slot = 0x7_100A_0200;
        ctx[CpuRegister.Rdi] = slot;
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexLock(ctx));
        Assert.True(ctx.TryReadUInt64(slot, out var firstHandle));
        Assert.NotEqual(0UL, firstHandle);

        Assert.True(ctx.TryWriteUInt64(slot, 0));
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexLock(ctx));
        Assert.True(ctx.TryReadUInt64(slot, out var secondHandle));
        Assert.NotEqual(0UL, secondHandle);
        Assert.NotEqual(firstHandle, secondHandle);
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexUnlock(ctx));

        // A slot that still holds its handle keeps resolving to the same mutex.
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexLock(ctx));
        Assert.Equal(EDEADLK, KernelPthreadCompatExports.PosixPthreadMutexLock(ctx));
        Assert.Equal(0, KernelPthreadCompatExports.PosixPthreadMutexUnlock(ctx));
    }
}
