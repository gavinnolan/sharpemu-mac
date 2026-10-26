// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Diagnostics;
using System.Reflection;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

[CollectionDefinition("GuestSchedulerWakeStress", DisableParallelization = true)]
public sealed class GuestSchedulerWakeStressCollection;

// Many simulated guest threads run on the real cooperative scheduler (runners, dispatcher,
// park-time re-check, direct wake handoff, timer-driven timeouts) and hammer the real HLE
// primitives with randomized timing. Each slice runs managed code inside a simulated import
// frame instead of native guest code (DirectExecutionBackend.TestGuestSlice). A lost wakeup or
// a deadlock leaves a thread blocked and fails the run at the deadline.
[Collection("GuestSchedulerWakeStress")]
public sealed class GuestSchedulerWakeStressTests
{
    private const ulong Base = 0x2_5000_0000;
    private const int MemorySize = 0x40000;
    private const ulong HandleSlot = Base + 0x100;
    private const ulong NameAddress = Base + 0x200;
    private const ulong MutexBase = Base + 0x1000;
    private const ulong ScratchBase = Base + 0x8000;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);
    private static readonly int Ok = (int)OrbisGen2Result.ORBIS_GEN2_OK;
    private static readonly int TimedOut = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;

    [NativeX64Fact]
    public void SemaphoreProducersAndTimedConsumersLoseNoWakeups()
    {
        using var harness = new Harness();
        var sema = harness.CreateSema();
        const int consumers = 8;
        const int perConsumer = 300;
        const int producers = 4;
        const int perProducer = consumers * perConsumer / producers;
        var consumed = 0;
        var produced = 0;
        for (var i = 0; i < consumers; i++)
        {
            var timed = i % 2 == 0;
            harness.AddThread(perConsumer, (ctx, slot, rng) =>
            {
                ctx[CpuRegister.Rdi] = sema;
                ctx[CpuRegister.Rsi] = 1;
                ctx[CpuRegister.Rdx] = 0;
                if (timed && rng.Next(3) == 0)
                {
                    Assert.True(ctx.TryWriteUInt32(harness.Scratch(slot), (uint)rng.Next(5, 200)));
                    ctx[CpuRegister.Rdx] = harness.Scratch(slot);
                }
                return KernelSemaphoreCompatExports.KernelWaitSema(ctx);
            }, result =>
            {
                if (result == TimedOut) return false; // retry the same step
                Assert.Equal(Ok, result);
                Interlocked.Increment(ref consumed);
                return true;
            });
        }
        for (var i = 0; i < producers; i++)
        {
            harness.AddThread(perProducer, (ctx, _, rng) =>
            {
                Harness.Jitter(rng);
                return KernelSemaphoreCompatExports.KernelSignalSema(ctx, sema, 1);
            }, result =>
            {
                Assert.Equal(Ok, result);
                Interlocked.Increment(ref produced);
                return true;
            });
        }

        harness.RunToCompletion();
        Assert.Equal(consumers * perConsumer, produced);
        Assert.Equal(consumers * perConsumer, consumed);
    }

    [NativeX64Fact]
    public void BlockingPingPongPairsLoseNoWakeups()
    {
        using var harness = new Harness();
        const int pairs = 6;
        const int rounds = 400;
        var completed = 0;
        for (var p = 0; p < pairs; p++)
        {
            var ping = harness.CreateSema();
            var pong = harness.CreateSema();
            // Even steps signal, odd steps wait: every round parks both threads once.
            harness.AddThread(rounds * 2, (ctx, _, rng, step) =>
            {
                Harness.Jitter(rng);
                return step % 2 == 0
                    ? KernelSemaphoreCompatExports.KernelSignalSema(ctx, ping, 1)
                    : harness.WaitSema(ctx, pong);
            }, result => { Assert.Equal(Ok, result); return true; });
            harness.AddThread(rounds * 2, (ctx, _, rng, step) =>
            {
                Harness.Jitter(rng);
                return step % 2 == 0
                    ? harness.WaitSema(ctx, ping)
                    : KernelSemaphoreCompatExports.KernelSignalSema(ctx, pong, 1);
            }, result => { Assert.Equal(Ok, result); Interlocked.Increment(ref completed); return true; });
        }

        harness.RunToCompletion();
        Assert.Equal(pairs * rounds * 2, completed);
    }

    [NativeX64Fact]
    public void ContendedMutexCounterStaysExactWhileOwnersPark()
    {
        using var harness = new Harness();
        const int threads = 8;
        const int iterations = 250;
        var never = harness.CreateSema();
        var mutex = harness.InitMutex(0);
        var counter = 0L;
        var owner = 0;
        for (var i = 0; i < threads; i++)
        {
            // Steps: lock, (claim + optional park while holding the mutex), release + unlock.
            harness.AddThread(iterations * 3, (ctx, slot, rng, step) =>
            {
                switch (step % 3)
                {
                    case 0:
                        ctx[CpuRegister.Rdi] = mutex;
                        return KernelPthreadCompatExports.PthreadMutexLock(ctx);
                    case 1:
                        Assert.Equal(0, Interlocked.Exchange(ref owner, slot + 1));
                        counter++;
                        if (rng.Next(8) == 0)
                        {
                            // The owner parks inside its critical section: waiters must stop
                            // spinning on it and park too, and nobody may enter meanwhile.
                            Assert.True(ctx.TryWriteUInt32(harness.Scratch(slot), (uint)rng.Next(20, 300)));
                            ctx[CpuRegister.Rdi] = never;
                            ctx[CpuRegister.Rsi] = 1;
                            ctx[CpuRegister.Rdx] = harness.Scratch(slot);
                            return KernelSemaphoreCompatExports.KernelWaitSema(ctx);
                        }
                        Harness.Spin(rng.Next(0, 20));
                        return TimedOut;
                    default:
                        Assert.Equal(slot + 1, Interlocked.Exchange(ref owner, 0));
                        ctx[CpuRegister.Rdi] = mutex;
                        return KernelPthreadCompatExports.PthreadMutexUnlock(ctx);
                }
            }, result => true);
        }

        harness.RunToCompletion();
        Assert.Equal(threads * iterations, Interlocked.Read(ref counter));
    }

    [NativeX64Fact]
    public void UltMutexAndQueueWakeOnlyTheirOwnWaiters()
    {
        using var harness = new Harness();
        var ultMutex = MutexBase + 0x800;
        var queue = MutexBase + 0x900;
        var setup = new CpuContext(harness.Memory, Generation.Gen5) { FsBase = ScratchBase + 0x7F00 };
        setup[CpuRegister.Rdi] = queue;
        setup[CpuRegister.Rsi] = 0;
        setup[CpuRegister.Rdx] = 8;
        Assert.Equal(0, SharpEmu.Libs.Ult.UltExports.QueueCreate(setup));
        const int lockers = 6;
        const int iterations = 200;
        const int consumers = 4;
        const int perConsumer = 250;
        var counter = 0L;
        var owner = 0;
        var popped = 0L;
        for (var i = 0; i < lockers; i++)
        {
            harness.AddThread(iterations * 3, (ctx, slot, rng, step) =>
            {
                ctx[CpuRegister.Rdi] = ultMutex;
                switch (step % 3)
                {
                    case 0:
                        return (int)(long)SharpEmu.Libs.Ult.UltExports.MutexLock(ctx);
                    case 1:
                        Assert.Equal(0, Interlocked.Exchange(ref owner, slot + 1));
                        counter++;
                        Harness.Spin(rng.Next(0, 15));
                        return 0;
                    default:
                        Assert.Equal(slot + 1, Interlocked.Exchange(ref owner, 0));
                        return SharpEmu.Libs.Ult.UltExports.MutexUnlock(ctx);
                }
            }, result => { Assert.Equal(0, result); return true; });
        }
        for (var i = 0; i < consumers; i++)
        {
            harness.AddThread(perConsumer, (ctx, slot, _, _) =>
            {
                ctx[CpuRegister.Rdi] = queue;
                ctx[CpuRegister.Rsi] = harness.Scratch(slot);
                return SharpEmu.Libs.Ult.UltExports.QueuePop(ctx);
            }, result => { Assert.Equal(0, result); Interlocked.Increment(ref popped); return true; });
        }
        for (var i = 0; i < 2; i++)
        {
            harness.AddThread(consumers * perConsumer / 2, (ctx, slot, rng, step) =>
            {
                Harness.Jitter(rng);
                Assert.True(ctx.TryWriteUInt64(harness.Scratch(slot), (ulong)step));
                ctx[CpuRegister.Rdi] = queue;
                ctx[CpuRegister.Rsi] = harness.Scratch(slot);
                return SharpEmu.Libs.Ult.UltExports.QueuePush(ctx);
            }, result => { Assert.Equal(0, result); return true; });
        }

        harness.RunToCompletion();
        Assert.Equal(lockers * iterations, Interlocked.Read(ref counter));
        Assert.Equal(consumers * perConsumer, Interlocked.Read(ref popped));
        setup[CpuRegister.Rdi] = queue;
        _ = SharpEmu.Libs.Ult.UltExports.QueueDestroy(setup);
    }

    private sealed class Harness : IDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Type BackendType = typeof(DirectExecutionBackend);
        private static readonly Type ThreadType = BackendType.GetNestedType("GuestThreadState", BindingFlags.NonPublic)!;
        private static readonly Type RunStateType = BackendType.GetNestedType("GuestThreadRunState", BindingFlags.NonPublic)!;

        private readonly FakeCpuMemory _memory = new(Base, MemorySize);
        private readonly DirectExecutionBackend _backend;
        private readonly object _gate;
        private readonly IDictionary _threads;
        private readonly IDictionary _lockFreeThreads;
        private readonly Dictionary<ulong, SimThread> _sims = new();
        private readonly IGuestThreadScheduler? _previousScheduler;
        private readonly List<uint> _semas = new();
        private ulong _nextHandle = 0x7700;

        internal Harness()
        {
            var modules = new ModuleManager();
            modules.Freeze();
            _backend = new DirectExecutionBackend(modules);
            _gate = BackendType.GetField("_guestThreadGate", PrivateInstance)!.GetValue(_backend)!;
            _threads = (IDictionary)BackendType.GetField("_guestThreads", PrivateInstance)!.GetValue(_backend)!;
            _lockFreeThreads = (IDictionary)BackendType.GetField("_guestThreadsLockFree", PrivateInstance)!.GetValue(_backend)!;
            _backend.TestGuestSlice = Slice;
            _previousScheduler = GuestThreadExecution.Scheduler;
            GuestThreadExecution.Scheduler = _backend;
        }

        internal ulong Scratch(int slot) => ScratchBase + (ulong)slot * 0x100;

        internal FakeCpuMemory Memory => _memory;

        internal uint CreateSema()
        {
            var ctx = new CpuContext(_memory, Generation.Gen5) { FsBase = ScratchBase + 0x7F00 };
            Assert.True(_memory.TryWrite(NameAddress, "w3-sema\0"u8));
            ctx[CpuRegister.Rdi] = HandleSlot;
            ctx[CpuRegister.Rsi] = NameAddress;
            ctx[CpuRegister.Rdx] = 1;
            ctx[CpuRegister.Rcx] = 0;
            ctx[CpuRegister.R8] = 0x7FFFFFFF;
            ctx[CpuRegister.R9] = 0;
            Assert.Equal(Ok, KernelSemaphoreCompatExports.KernelCreateSema(ctx));
            Assert.True(ctx.TryReadUInt64(HandleSlot, out var handle));
            _semas.Add((uint)handle);
            return (uint)handle;
        }

        internal ulong InitMutex(int index)
        {
            var address = MutexBase + (ulong)index * 0x40;
            var ctx = new CpuContext(_memory, Generation.Gen5) { FsBase = ScratchBase + 0x7F00 };
            ctx[CpuRegister.Rdi] = address;
            ctx[CpuRegister.Rsi] = 0;
            Assert.Equal(Ok, KernelPthreadCompatExports.PthreadMutexInit(ctx));
            return address;
        }

        internal int WaitSema(CpuContext ctx, uint sema)
        {
            ctx[CpuRegister.Rdi] = sema;
            ctx[CpuRegister.Rsi] = 1;
            ctx[CpuRegister.Rdx] = 0;
            return KernelSemaphoreCompatExports.KernelWaitSema(ctx);
        }

        internal static void Jitter(System.Random rng)
        {
            switch (rng.Next(16))
            {
                case 0: Thread.Yield(); break;
                case 1: Spin(rng.Next(1, 50)); break;
                case 2: Thread.SpinWait(rng.Next(1, 200)); break;
            }
        }

        internal static void Spin(int microseconds)
        {
            var end = Stopwatch.GetTimestamp() + Stopwatch.Frequency * microseconds / 1_000_000;
            while (Stopwatch.GetTimestamp() < end) Thread.SpinWait(8);
        }

        internal void AddThread(int steps, Func<CpuContext, int, System.Random, int> op, Func<int, bool> onResult) =>
            AddThread(steps, (ctx, slot, rng, _) => op(ctx, slot, rng), onResult);

        internal void AddThread(int steps, Func<CpuContext, int, System.Random, int, int> op, Func<int, bool> onResult)
        {
            var handle = _nextHandle++;
            var slot = _sims.Count;
            var sim = new SimThread(steps, op, onResult, new System.Random(unchecked((int)handle * 7919)), slot);
            _sims.Add(handle, sim);
            var thread = Activator.CreateInstance(ThreadType)!;
            ThreadType.GetProperty("ThreadHandle")!.SetValue(thread, handle);
            ThreadType.GetProperty("Name")!.SetValue(thread, $"w3-stress-{handle:X}");
            ThreadType.GetProperty("State")!.SetValue(thread, Enum.Parse(RunStateType, "Ready"));
            ThreadType.GetProperty("Context")!.SetValue(
                thread,
                new CpuContext(_memory, Generation.Gen5) { FsBase = Scratch(slot) + 0x80 });
            lock (_gate)
            {
                _threads.Add(handle, thread);
                _lockFreeThreads[handle] = thread;
            }
        }

        internal void RunToCompletion()
        {
            lock (_gate)
            {
                foreach (var thread in _threads.Values)
                {
                    BackendType.GetMethod("EnqueueReadyGuestThreadLocked", PrivateInstance)!
                        .Invoke(_backend, new object?[] { thread, null });
                }
            }
            BackendType.GetMethod("StartReadyThreadDispatcher", PrivateInstance)!.Invoke(_backend, null);

            var finished = SpinWait.SpinUntil(AllExited, Deadline);
            if (!finished)
            {
                Assert.Fail("guest threads did not finish (lost wakeup or deadlock): " + DescribeThreads());
            }
            foreach (var sim in _sims.Values)
            {
                if (sim.Failure is { } failure) throw new Xunit.Sdk.XunitException("slice failed: " + failure);
            }
        }

        private bool AllExited()
        {
            lock (_gate)
            {
                foreach (var thread in _threads.Values)
                {
                    var state = ThreadType.GetProperty("State")!.GetValue(thread)!.ToString();
                    if (state is "Faulted")
                    {
                        return true; // reported by DescribeThreads/Failure below
                    }
                    if (state != "Exited" || (bool)ThreadType.GetProperty("ExecutorActive")!.GetValue(thread)!)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        private string DescribeThreads()
        {
            lock (_gate)
            {
                return string.Join("; ", _threads.Values.Cast<object>().Select(thread =>
                {
                    var handle = (ulong)ThreadType.GetProperty("ThreadHandle")!.GetValue(thread)!;
                    return $"0x{handle:X} {ThreadType.GetProperty("State")!.GetValue(thread)} " +
                        $"active={ThreadType.GetProperty("ExecutorActive")!.GetValue(thread)} " +
                        $"step={_sims[handle].Step}/{_sims[handle].Steps} " +
                        $"block={ThreadType.GetProperty("BlockReason")!.GetValue(thread)}";
                }));
            }
        }

        private bool Slice(ulong handle, CpuContext ctx, bool resumed, ulong resumeValue)
        {
            var sim = _sims[handle];
            try
            {
                if (resumed && sim.OnResult((int)unchecked((long)resumeValue)))
                {
                    sim.Step++;
                }

                while (sim.Step < sim.Steps)
                {
                    var previous = GuestThreadExecution.EnterImportCallFrame(
                        0x40_0000UL + (ulong)sim.Step,
                        Scratch(sim.Slot) + 0x40,
                        Scratch(sim.Slot) + 0x38);
                    int result;
                    bool blocked;
                    try
                    {
                        result = sim.Op(ctx, sim.Slot, sim.Rng, sim.Step);
                        blocked = GuestThreadExecution.TryConsumeCurrentThreadBlock(
                            out var reason, out _, out _, out var wakeKey, out var waiter, out var deadline);
                        if (blocked)
                        {
                            // Re-issue the request so the scheduler consumes it as the import
                            // dispatcher would, with the continuation of this import frame.
                            Assert.True(GuestThreadExecution.RequestCurrentThreadBlock(ctx, reason, wakeKey, waiter, deadline));
                        }
                    }
                    finally
                    {
                        GuestThreadExecution.RestoreImportCallFrame(previous);
                    }

                    if (blocked)
                    {
                        return true;
                    }
                    if (sim.OnResult(result))
                    {
                        sim.Step++;
                    }
                }
            }
            catch (Exception exception)
            {
                sim.Failure ??= exception.ToString();
                sim.Step = sim.Steps;
            }

            return false;
        }

        public void Dispose()
        {
            GuestThreadExecution.Scheduler = _previousScheduler;
            BackendType.GetMethod("StopReadyThreadDispatcher", PrivateInstance)!.Invoke(_backend, null);
            var ctx = new CpuContext(_memory, Generation.Gen5) { FsBase = ScratchBase + 0x7F00 };
            foreach (var sema in _semas)
            {
                ctx[CpuRegister.Rdi] = sema;
                _ = KernelSemaphoreCompatExports.KernelDeleteSema(ctx);
            }
            _backend.Dispose();
        }

        private sealed class SimThread(
            int steps,
            Func<CpuContext, int, System.Random, int, int> op,
            Func<int, bool> onResult,
            System.Random rng,
            int slot)
        {
            public int Steps { get; } = steps;
            public Func<CpuContext, int, System.Random, int, int> Op { get; } = op;
            public Func<int, bool> OnResult { get; } = onResult;
            public System.Random Rng { get; } = rng;
            public int Slot { get; } = slot;
            public int Step;
            public string? Failure;
        }
    }
}
