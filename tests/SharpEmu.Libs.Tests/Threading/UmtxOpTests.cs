// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Threading;
using Xunit;

namespace SharpEmu.Libs.Tests.Threading;

// _umtx_op on host threads (kern_umtx.c semantics); every op returns 0 or -1 + errno.
public sealed class UmtxOpTests
{
    private const ulong Base = 0x2_5000_0000;
    private const ulong Word = Base + 0x100;
    private const ulong Word2 = Base + 0x108;
    private const ulong Mutex = Base + 0x200;
    private const ulong Cond = Base + 0x240;
    private const ulong Rwlock = Base + 0x280;
    private const ulong Sem = Base + 0x2C0;
    private const ulong Timeout = Base + 0x300;
    private const ulong AddressArray = Base + 0x340;
    private const ulong ErrnoBase = Base + 0x1000;

    private readonly FakeCpuMemory _memory = new(Base, 0x8000);
    private readonly CpuContext _probe;

    public UmtxOpTests()
    {
        _probe = new CpuContext(_memory, Generation.Gen5);
    }

    private CpuContext Context(int slot) =>
        new(_memory, Generation.Gen5) { FsBase = ErrnoBase + (ulong)slot * 0x100 };

    private static int Op(CpuContext ctx, ulong obj, int op, ulong val = 0, ulong uaddr1 = 0, ulong uaddr2 = 0)
    {
        ctx[CpuRegister.Rdi] = obj;
        ctx[CpuRegister.Rsi] = (ulong)op;
        ctx[CpuRegister.Rdx] = val;
        ctx[CpuRegister.Rcx] = uaddr1;
        ctx[CpuRegister.R8] = uaddr2;
        return KernelSyncOnAddressCompatExports.UmtxOp(ctx);
    }

    private static uint Errno(CpuContext ctx)
    {
        Assert.True(ctx.TryReadUInt32(ctx.FsBase + 0x40, out var errno));
        return errno;
    }

    private uint Read(ulong address)
    {
        Assert.True(_probe.TryReadUInt32(address, out var value));
        return value;
    }

    private void Write(ulong address, uint value) => Assert.True(_probe.TryWriteUInt32(address, value));

    private static Task<(int Result, uint Errno, uint ThreadId)> Run(Func<CpuContext, int> body, CpuContext ctx) =>
        Task.Factory.StartNew(() =>
        {
            var tid = unchecked((uint)KernelPthreadState.GetCurrentThreadUniqueId());
            var result = body(ctx);
            return (result, result == 0 ? 0u : Errno(ctx), tid);
        }, TaskCreationOptions.LongRunning);

    private static void Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached");
            Thread.Sleep(1);
        }
    }

    private static int Waiters(ulong address)
    {
        var bucket = AddressWaitTable.Shared.BucketFor(address);
        lock (bucket.Gate)
        {
            return AddressWaitTable.CountLocked(bucket, address);
        }
    }

    [Fact]
    public void WaitUintHonoursAnAbsoluteUmtxTimeAndValueMismatch()
    {
        var ctx = Context(0);
        Write(Word, 7);
        Assert.Equal(0, Op(ctx, Word, 11, val: 8)); // value differs: no sleep

        // Taken before the deadline is: time between reading the clock and entering the wait
        // (a loaded runner) must count toward the elapsed time.
        var started = DateTime.UtcNow;
        KernelRuntimeCompatExports.GetClockTime(4, out var seconds, out var nanoseconds);
        var deadlineNs = nanoseconds + 20_000_000;
        Assert.True(ctx.TryWriteUInt64(Timeout, (ulong)(seconds + deadlineNs / 1_000_000_000)));
        Assert.True(ctx.TryWriteUInt64(Timeout + 8, (ulong)(deadlineNs % 1_000_000_000)));
        Assert.True(ctx.TryWriteUInt32(Timeout + 16, 1)); // UMTX_ABSTIME
        Assert.True(ctx.TryWriteUInt32(Timeout + 20, 4)); // CLOCK_MONOTONIC
        Assert.Equal(-1, Op(ctx, Word, 15, val: 7, uaddr1: 24, uaddr2: Timeout));
        Assert.Equal((uint)GuestErrno.ETIMEDOUT, Errno(ctx));
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(15));
    }

    [Fact]
    public void WakePrivateAndNwakePrivateReleaseSleepers()
    {
        Write(Word, 1);
        Write(Word2, 1);
        var first = Run(ctx => Op(ctx, Word, 15, val: 1), Context(1));
        var second = Run(ctx => Op(ctx, Word2, 11, val: 1), Context(2));
        var third = Run(ctx => Op(ctx, Word2, 11, val: 1), Context(3));
        Eventually(() => Waiters(Word) == 1 && Waiters(Word2) == 2);

        Assert.Equal(0, Op(Context(0), Word, 16, val: 1));
        Assert.True(first.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, first.Result.Result);

        Assert.True(_memory.TryWrite(AddressArray, BitConverter.GetBytes(Word2)));
        Assert.Equal(0, Op(Context(0), AddressArray, 21, val: 1));
        Assert.True(Task.WaitAll(new Task[] { second, third }, TimeSpan.FromSeconds(10)));
        Assert.Equal(0, second.Result.Result);
        Assert.Equal(0, third.Result.Result);
    }

    [Fact]
    public void UmutexOwnershipErrorsFollowLibthr()
    {
        Write(Mutex, 0);
        var owner = Context(0);
        var ownerId = unchecked((uint)KernelPthreadState.GetCurrentThreadUniqueId());
        Assert.Equal(0, Op(owner, Mutex, 5)); // MUTEX_LOCK
        Assert.Equal(ownerId, Read(Mutex));
        Assert.Equal(-1, Op(owner, Mutex, 4)); // self trylock
        Assert.Equal((uint)GuestErrno.EDEADLK, Errno(owner));

        var other = Run(ctx => Op(ctx, Mutex, 4), Context(1)).Result; // trylock from another thread
        Assert.Equal((-1, (uint)GuestErrno.EBUSY), (other.Result, other.Errno));
        var stranger = Run(ctx => Op(ctx, Mutex, 6), Context(2)).Result; // unlock by non-owner
        Assert.Equal((-1, (uint)GuestErrno.EPERM), (stranger.Result, stranger.Errno));

        Assert.Equal(0, Op(owner, Mutex, 6));
        Assert.Equal(0u, Read(Mutex));
    }

    [Fact]
    public void ContestedUmutexIsHandedToTheWaiterOnUnlock()
    {
        Write(Mutex, 0);
        var owner = Context(0);
        Assert.Equal(0, Op(owner, Mutex, 5));
        var waiter = Run(ctx => Op(ctx, Mutex, 5), Context(1));
        Eventually(() => Waiters(Mutex) == 1);
        Assert.NotEqual(0u, Read(Mutex) & 0x80000000); // contested

        Assert.Equal(0, Op(owner, Mutex, 6));
        Assert.True(waiter.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, waiter.Result.Result);
        Assert.Equal(waiter.Result.ThreadId, Read(Mutex)); // owned by the waiter, no longer contested
    }

    [Fact]
    public void CondWaitReleasesTheMutexAndSignalWakesIt()
    {
        Write(Mutex, 0);
        Write(Cond, 0);
        var waiterCtx = Context(1);
        var waiter = Run(ctx =>
        {
            Assert.Equal(0, Op(ctx, Mutex, 5));
            return Op(ctx, Cond, 8, val: 0, uaddr1: Mutex, uaddr2: 0);
        }, waiterCtx);
        Eventually(() => Waiters(Cond) == 1);
        Assert.Equal(1u, Read(Cond)); // c_has_waiters

        var signaller = Context(0);
        Assert.Equal(0, Op(signaller, Mutex, 5)); // the waiter released it
        Assert.Equal(0, Op(signaller, Cond, 9));
        Assert.Equal(0, Op(signaller, Mutex, 6));
        Assert.True(waiter.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, waiter.Result.Result);
        Assert.Equal(0u, Read(Cond));
    }

    [Fact]
    public void RwlockReadersShareAndAWaitingWriterIsGrantedAfterTheLastReader()
    {
        Write(Rwlock, 0);
        Write(Rwlock + 4, 0);
        Write(Rwlock + 8, 0);
        Write(Rwlock + 12, 0);
        var reader = Context(0);
        Assert.Equal(0, Op(reader, Rwlock, 12));
        Assert.Equal(0, Op(reader, Rwlock, 12));
        Assert.Equal(2u, Read(Rwlock));

        var writer = Run(ctx => Op(ctx, Rwlock, 13), Context(1));
        Eventually(() => Waiters(Rwlock) == 1);
        Assert.NotEqual(0u, Read(Rwlock) & 0x40000000); // write waiters
        Assert.Equal(1u, Read(Rwlock + 12));

        // A new reader now queues behind the waiting writer (writer preference).
        var lateReader = Run(ctx => Op(ctx, Rwlock, 12), Context(2));
        Eventually(() => Waiters(Rwlock) == 2);

        Assert.Equal(0, Op(reader, Rwlock, 14));
        Assert.Equal(0, Op(reader, Rwlock, 14));
        Assert.True(writer.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, writer.Result.Result);
        Assert.Equal(0x80000000u, Read(Rwlock) & 0xC0000000u); // write owner, no writer waiting

        Assert.Equal(0, Op(Context(1), Rwlock, 14));
        Assert.True(lateReader.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, lateReader.Result.Result);
        Assert.Equal(1u, Read(Rwlock));
        Assert.Equal(0u, Read(Rwlock + 8));
    }

    [Fact]
    public void Sem2WaitSleepsUntilWakeAndClearsHasWaiters()
    {
        Write(Sem, 0);
        var waiter = Run(ctx => Op(ctx, Sem, 23), Context(1));
        Eventually(() => Waiters(Sem) == 1);
        Assert.Equal(0x80000000u, Read(Sem));

        Write(Sem, 0x80000001); // userland post: count 1, has-waiters still set
        Assert.Equal(0, Op(Context(0), Sem, 24));
        Assert.True(waiter.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, waiter.Result.Result);
        Assert.Equal(1u, Read(Sem));
    }

    [Fact]
    public void UnknownOperationIsEinval()
    {
        var ctx = Context(0);
        Assert.Equal(-1, Op(ctx, Word, 99));
        Assert.Equal((uint)GuestErrno.EINVAL, Errno(ctx));
    }

    [Fact]
    public void StressContendedUmutexKeepsMutualExclusion()
    {
        Write(Mutex, 0);
        const int threads = 8;
        const int iterations = 1500;
        var counter = 0;
        var inside = 0;
        var violations = 0;
        var tasks = Enumerable.Range(0, threads).Select(slot => Task.Factory.StartNew(() =>
        {
            var ctx = Context(10 + slot);
            for (var i = 0; i < iterations; i++)
            {
                Assert.Equal(0, Op(ctx, Mutex, 5));
                if (Interlocked.Increment(ref inside) != 1)
                {
                    Interlocked.Increment(ref violations);
                }

                counter++;
                Interlocked.Decrement(ref inside);
                Assert.Equal(0, Op(ctx, Mutex, 6));
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(60)));
        Assert.Equal(0, violations);
        Assert.Equal(threads * iterations, counter);
        Assert.Equal(0u, Read(Mutex));
    }
}
