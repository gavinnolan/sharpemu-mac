// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelExitAndThreadTimeTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    private static CpuContext NewContext() => new(new FakeCpuMemory(MemoryBase, 0x20000), Generation.Gen5);

    [Fact]
    public void ExitAndUnderscoreExitEndTheHostSessionWithTheGuestStatus()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_EXIT_TERMINATES"), "0", StringComparison.Ordinal))
        {
            return; // disabled by the environment
        }

        var previous = KernelExports.GuestExitTermination;
        var seen = new List<int>();
        KernelExports.GuestExitTermination = seen.Add;
        try
        {
            var ctx = NewContext();
            ctx[CpuRegister.Rdi] = 3;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelExports.Exit(ctx));
            Assert.True(GuestThreadExecution.TryConsumeCurrentEntryExit(out _, out var reason));
            Assert.Equal("exit", reason);

            ctx[CpuRegister.Rdi] = 5;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelExports.UnderscoreExit(ctx));
            Assert.True(GuestThreadExecution.TryConsumeCurrentEntryExit(out _, out reason));
            Assert.Equal("_exit", reason);
            Assert.Equal(new[] { 3, 5 }, seen);
        }
        finally
        {
            KernelExports.GuestExitTermination = previous;
        }
    }

    [Fact]
    public void GetThreadTimeReportsCpuTimeTheThreadSpends()
    {
        var ctx = NewContext();
        KernelRuntimeCompatExports.KernelGetThreadTime(ctx);
        var before = ctx[CpuRegister.Rax];

        // Burn CPU on this thread (bounded by wall time so a loaded runner still ends).
        var wall = System.Diagnostics.Stopwatch.StartNew();
        var sink = 0L;
        ulong after = before;
        while (after - before < 5_000 && wall.ElapsedMilliseconds < 5_000)
        {
            for (var i = 0; i < 100_000; i++)
            {
                sink += i;
            }

            KernelRuntimeCompatExports.KernelGetThreadTime(ctx);
            after = ctx[CpuRegister.Rax];
        }

        GC.KeepAlive(sink);
        Assert.True(after >= before + 1_000, $"thread time moved {after - before} us after burning CPU");
    }

    [Fact]
    public void PthreadGetCpuClockIdAnswersForTheCallingThreadOnly()
    {
        var ctx = NewContext();
        const ulong ClockIdAddress = MemoryBase + 0x100;
        ctx[CpuRegister.Rdi] = KernelPthreadState.GetCurrentThreadHandle();
        ctx[CpuRegister.Rsi] = ClockIdAddress;
        Assert.Equal(0, KernelRuntimeCompatExports.PthreadGetCpuClockId(ctx));
        Span<byte> value = stackalloc byte[4];
        Assert.True(ctx.Memory.TryRead(ClockIdAddress, value));
        Assert.Equal(14, BitConverter.ToInt32(value)); // CLOCK_THREAD_CPUTIME_ID

        ctx[CpuRegister.Rdi] = KernelPthreadState.GetCurrentThreadHandle() + 0x1000;
        Assert.NotEqual(0, KernelRuntimeCompatExports.PthreadGetCpuClockId(ctx));
    }

    [Fact]
    public void CommandStreamWorkerRunsAtUserInteractiveQos()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return; // QoS classes are a Darwin concept
        }

        var host = new RecordingCommandStreamHost();
        var queue = new CommandStreamQueue(host);
        var worker = new CommandStreamWorker(queue, host, () => false, true, "qos check");
        worker.Start();
        try
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (worker.WorkerQosClass == 0 && wait.Elapsed < TimeSpan.FromSeconds(30))
            {
                Thread.Sleep(1);
            }

            // 0x21 is QOS_CLASS_USER_INTERACTIVE, read back from the worker thread itself.
            Assert.Equal(0x21u, worker.WorkerQosClass);
        }
        finally
        {
            worker.Stop();
        }
    }

    [Fact]
    public void LargeMemsetFillsTheWholeRangeWithTheByte()
    {
        var ctx = NewContext();
        const ulong Destination = MemoryBase + 0x1000;
        ctx[CpuRegister.Rdi] = Destination;
        ctx[CpuRegister.Rsi] = 0xAB;
        ctx[CpuRegister.Rdx] = 0x10000;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Memset(ctx));
        Assert.Equal(Destination, ctx[CpuRegister.Rax]);
        var bytes = new byte[0x10000];
        Assert.True(ctx.Memory.TryRead(Destination, bytes));
        Assert.All(bytes, b => Assert.Equal(0xAB, b));
        var after = new byte[1];
        Assert.True(ctx.Memory.TryRead(Destination + 0x10000, after));
        Assert.Equal(0, after[0]);
    }
}
