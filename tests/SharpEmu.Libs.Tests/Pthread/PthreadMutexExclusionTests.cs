// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

/// <summary>
/// The property every allocator in every title leans on: while one thread holds a mutex,
/// no other thread holds it. A lock that returns success without excluding does not fault
/// — it lets two threads run a critical section at once, and the damage surfaces later as
/// a structure the owner never wrote.
///
/// These run real host threads through the HLE exports and check a guest-memory counter,
/// so a lost acquisition shows up as a count, not as a timing guess.
/// </summary>
public sealed class PthreadMutexExclusionTests
{
    private const ulong MemoryBase = 0x7_0000_0000;
    private const ulong MutexAddress = MemoryBase + 0x100;
    private const ulong CounterAddress = MemoryBase + 0x200;
    private const ulong InsideAddress = MemoryBase + 0x300;

    [Theory]
    [InlineData(4, 400)]
    [InlineData(8, 200)]
    public void OnlyOneThreadIsInsideTheCriticalSectionAtATime(int threads, int iterations)
    {
        var memory = new PthreadMutexSemanticsTests.AllocatingCpuMemory(MemoryBase, 0x4000);
        var setup = new CpuContext(memory, Generation.Gen5);
        Assert.True(setup.TryWriteUInt64(MutexAddress, 1));
        Assert.True(setup.TryWriteUInt64(CounterAddress, 0));
        Assert.True(setup.TryWriteUInt64(InsideAddress, 0));

        var overlaps = 0;
        var failures = 0;
        var workers = new Thread[threads];
        for (var index = 0; index < threads; index++)
        {
            workers[index] = new Thread(() =>
            {
                var context = new CpuContext(memory, Generation.Gen5);
                context[CpuRegister.Rdi] = MutexAddress;
                for (var step = 0; step < iterations; step++)
                {
                    if (KernelPthreadCompatExports.PthreadMutexLock(context) != 0)
                    {
                        Interlocked.Increment(ref failures);
                        continue;
                    }

                    // Inside the section: a second thread here is the bug.
                    if (!context.TryReadUInt64(InsideAddress, out var inside) || inside != 0)
                    {
                        Interlocked.Increment(ref overlaps);
                    }

                    context.TryWriteUInt64(InsideAddress, 1);
                    // A deliberately non-atomic read-modify-write: only the mutex makes it safe.
                    context.TryReadUInt64(CounterAddress, out var counter);
                    Thread.SpinWait(16);
                    context.TryWriteUInt64(CounterAddress, counter + 1);
                    context.TryWriteUInt64(InsideAddress, 0);

                    if (KernelPthreadCompatExports.PthreadMutexUnlock(context) != 0)
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
            }) { IsBackground = true };
        }

        foreach (var worker in workers)
        {
            worker.Start();
        }

        foreach (var worker in workers)
        {
            Assert.True(worker.Join(TimeSpan.FromSeconds(60)), "a thread never finished its critical sections");
        }

        Assert.Equal(0, failures);
        Assert.Equal(0, overlaps);
        Assert.True(setup.TryReadUInt64(CounterAddress, out var total));
        Assert.Equal((ulong)(threads * iterations), total);
    }

    [Fact]
    public void AnUnlockedMutexIsAcquirableByAnotherThread()
    {
        var memory = new PthreadMutexSemanticsTests.AllocatingCpuMemory(MemoryBase, 0x4000);
        var owner = new CpuContext(memory, Generation.Gen5);
        owner[CpuRegister.Rdi] = MutexAddress;
        Assert.True(owner.TryWriteUInt64(MutexAddress, 1));
        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexLock(owner));

        var acquired = -1;
        var other = new Thread(() =>
        {
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = MutexAddress;
            // Busy while the owner holds it.
            acquired = KernelPthreadCompatExports.PthreadMutexTrylock(context);
        }) { IsBackground = true };
        other.Start();
        Assert.True(other.Join(TimeSpan.FromSeconds(10)));
        Assert.NotEqual(0, acquired);

        Assert.Equal(0, KernelPthreadCompatExports.PthreadMutexUnlock(owner));

        var after = -1;
        var again = new Thread(() =>
        {
            var context = new CpuContext(memory, Generation.Gen5);
            context[CpuRegister.Rdi] = MutexAddress;
            after = KernelPthreadCompatExports.PthreadMutexTrylock(context);
            if (after == 0)
            {
                KernelPthreadCompatExports.PthreadMutexUnlock(context);
            }
        }) { IsBackground = true };
        again.Start();
        Assert.True(again.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, after);
    }
}
