// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelClocksTests
{
    private const ulong BaseAddress = 0x1_0000;
    private const ulong TlsBase = BaseAddress + 0x100;
    private const ulong TimespecAddress = BaseAddress + 0x800;

    private static CpuContext NewContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(BaseAddress, 0x2000);
        return new CpuContext(memory, Generation.Gen5) { FsBase = TlsBase };
    }

    private static (long Seconds, long Nanoseconds) ReadTimespec(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[16];
        Assert.True(memory.TryRead(address, bytes));
        return (BinaryPrimitives.ReadInt64LittleEndian(bytes), BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]));
    }

    public static TheoryData<int> ValidClockIds => new() { 0, 1, 2, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

    [Theory]
    [MemberData(nameof(ValidClockIds))]
    public void EveryFreeBsdClockIdReads(int clockId)
    {
        var ctx = NewContext(out var memory);
        ctx[CpuRegister.Rdi] = unchecked((ulong)clockId);
        ctx[CpuRegister.Rsi] = TimespecAddress;

        Assert.Equal(0, KernelMemoryCompatExports.ClockGettime(ctx));
        var (seconds, nanoseconds) = ReadTimespec(memory, TimespecAddress);
        Assert.InRange(nanoseconds, 0, 999_999_999);
        Assert.True(seconds >= 0);
        if (clockId == KernelClocks.Second)
        {
            Assert.Equal(0, nanoseconds);
        }
    }

    [Theory]
    [InlineData(KernelClocks.Realtime)]
    [InlineData(KernelClocks.RealtimePrecise)]
    [InlineData(KernelClocks.RealtimeFast)]
    [InlineData(KernelClocks.Second)]
    public void RealtimeFamilyIsWallClock(int clockId)
    {
        Assert.Equal(0, KernelClocks.TryGetTime(clockId, out var seconds, out _));
        Assert.InRange(seconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1);
    }

    [Theory]
    [InlineData(KernelClocks.Monotonic)]
    [InlineData(KernelClocks.Uptime)]
    [InlineData(KernelClocks.MonotonicFast)]
    public void MonotonicFamilyNeverGoesBackwards(int clockId)
    {
        long previous = 0;
        for (var i = 0; i < 1000; i++)
        {
            KernelClocks.TryGetTime(clockId, out var seconds, out var nanoseconds);
            var now = seconds * 1_000_000_000L + nanoseconds;
            Assert.True(now >= previous);
            previous = now;
        }
    }

    [Fact]
    public void ThreadCpuTimeAdvancesWithWork()
    {
        KernelClocks.TryGetTime(KernelClocks.ThreadCputime, out var s0, out var n0);
        var spin = Stopwatch.StartNew();
        var sink = 0UL;
        while (spin.ElapsedMilliseconds < 30)
        {
            sink += (ulong)spin.ElapsedTicks;
        }

        KernelClocks.TryGetTime(KernelClocks.ThreadCputime, out var s1, out var n1);
        Assert.True(s1 * 1_000_000_000L + n1 > s0 * 1_000_000_000L + n0, sink.ToString());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(16)]
    [InlineData(-5)]
    public void UnknownClockIdIsEinval(int clockId)
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = unchecked((ulong)clockId);
        ctx[CpuRegister.Rsi] = TimespecAddress;

        Assert.Equal(-1, KernelMemoryCompatExports.ClockGettime(ctx));
        Assert.Equal(OrbisErrno.EINVAL, KernelErrno.Current(ctx));

        ctx[CpuRegister.Rdi] = unchecked((ulong)clockId);
        Assert.Equal(KernelErrno.Sce(OrbisErrno.EINVAL), KernelRuntimeCompatExports.KernelClockGettime(ctx));
        ctx[CpuRegister.Rdi] = unchecked((ulong)clockId);
        Assert.Equal(-1, KernelMemoryCompatExports.ClockGetres(ctx));
        Assert.Equal(OrbisErrno.EINVAL, KernelErrno.Current(ctx));
    }

    [Fact]
    public void NullTimespecIsEfaultForPosixAndSce()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = KernelClocks.Monotonic;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(-1, KernelMemoryCompatExports.ClockGettime(ctx));
        Assert.Equal(OrbisErrno.EFAULT, KernelErrno.Current(ctx));

        ctx[CpuRegister.Rdi] = KernelClocks.Monotonic;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelRuntimeCompatExports.KernelClockGettime(ctx));
    }

    [Theory]
    [InlineData(KernelClocks.Realtime, 0L, 1L)]
    [InlineData(KernelClocks.Monotonic, 0L, 1L)]
    [InlineData(KernelClocks.UptimeFast, 0L, 1L)]
    [InlineData(KernelClocks.Virtual, 0L, 1_000_000L)]
    [InlineData(KernelClocks.Prof, 0L, 1_000_000L)]
    [InlineData(KernelClocks.Second, 1L, 0L)]
    [InlineData(KernelClocks.ThreadCputime, 0L, 1_000L)]
    [InlineData(KernelClocks.ProcessCputime, 0L, 1_000L)]
    public void ResolutionMatchesFreeBsd(int clockId, long expectedSeconds, long expectedNanoseconds)
    {
        var ctx = NewContext(out var memory);
        ctx[CpuRegister.Rdi] = unchecked((ulong)clockId);
        ctx[CpuRegister.Rsi] = TimespecAddress;

        Assert.Equal(0, KernelMemoryCompatExports.ClockGetres(ctx));
        Assert.Equal((expectedSeconds, expectedNanoseconds), ReadTimespec(memory, TimespecAddress));

        ctx[CpuRegister.Rdi] = unchecked((ulong)clockId);
        ctx[CpuRegister.Rsi] = TimespecAddress + 16;
        Assert.Equal(0, KernelRuntimeCompatExports.KernelClockGetres(ctx));
        Assert.Equal((expectedSeconds, expectedNanoseconds), ReadTimespec(memory, TimespecAddress + 16));
    }

    [Fact]
    public void GettimeofdayFaultSetsErrno()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0xDEAD_0000;
        ctx[CpuRegister.Rsi] = 0;

        Assert.Equal(-1, KernelRuntimeCompatExports.PosixGettimeofday(ctx));
        Assert.Equal(OrbisErrno.EFAULT, KernelErrno.Current(ctx));
    }

    [Fact]
    public void ProcessTimeCounterRunsAtTheFixedFrequency()
    {
        var ctx = NewContext(out _);
        Assert.Equal(0, KernelRuntimeCompatExports.KernelGetProcessTimeCounterFrequency(ctx));
        Assert.Equal(1_000_000_000UL, ctx[CpuRegister.Rax]);

        var start = Stopwatch.GetTimestamp();
        var later = start + Stopwatch.Frequency / 2; // half a second of host time
        var delta = KernelRuntimeCompatExports.ReadProcessTimeCounterAt(later) -
            KernelRuntimeCompatExports.ReadProcessTimeCounterAt(start);
        Assert.InRange(delta, 499_999_000UL, 500_001_000UL);
    }

    [Fact]
    public void TscFrequencyMatchesSysctl()
    {
        var ctx = NewContext(out var memory);
        Assert.Equal(0, KernelRuntimeCompatExports.KernelGetTscFrequency(ctx));
        var frequency = ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, frequency);

        var sysctlValue = KernelSystemInfoExportsTests.SysctlByNameUInt64(ctx, memory, "machdep.tsc_freq");
        Assert.Equal(frequency, sysctlValue);
    }
}
