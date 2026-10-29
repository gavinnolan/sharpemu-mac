// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>
/// Runs the emitted clock stubs (clock_gettime, sceKernelClockGettime, gettimeofday,
/// sceKernelGetProcessTime) as machine code and holds their answers to the managed exports':
/// same epoch, same units, and a hand-off to the trampoline for every call the stub does not
/// answer.
/// </summary>
[Collection("GuestFastPath")]
public sealed unsafe class GuestClockStubTests : IDisposable
{
    private const ulong FallbackSentinel = 0x5A5A_1234_5678_9ABCUL;
    private const long SlackNanoseconds = 50_000_000;

    private readonly byte* _page;
    private readonly bool _canRun =
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public GuestClockStubTests()
    {
        if (_canRun)
        {
            _page = (byte*)HostMemory.Alloc(
                null,
                4096,
                HostMemory.MEM_COMMIT | HostMemory.MEM_RESERVE,
                HostMemory.PAGE_EXECUTE_READWRITE);
        }
    }

    public void Dispose()
    {
        if (_page != null)
        {
            _ = HostMemory.Free(_page, 0, HostMemory.MEM_RELEASE);
        }
    }

    [Fact]
    public void ClockGettime_MonotonicIdsMatchTheManagedClock()
    {
        if (!_canRun)
        {
            return;
        }

        var stub = Build(DirectExecutionBackend.GuestFastPathStub.ClockGettime);
        foreach (var clockId in new ulong[] { 4, 5, 7, 8, 11, 12 })
        {
            var timespec = stackalloc long[2];
            KernelRuntimeCompatExports.ReadMonotonicSinceStart(out var beforeSeconds, out var beforeNanos);
            var result = ((delegate* unmanaged<ulong, long*, ulong>)stub)(clockId, timespec);
            KernelRuntimeCompatExports.ReadMonotonicSinceStart(out var afterSeconds, out var afterNanos);

            Assert.Equal(0UL, result);
            var before = beforeSeconds * 1_000_000_000L + beforeNanos;
            var after = afterSeconds * 1_000_000_000L + afterNanos;
            var actual = timespec[0] * 1_000_000_000L + timespec[1];
            Assert.InRange(timespec[1], 0, 999_999_999);
            Assert.InRange(actual, before, after);
        }
    }

    [Fact]
    public void ClockGettime_RealtimeIdsReportUnixEpochNanoseconds()
    {
        if (!_canRun)
        {
            return;
        }

        var stub = Build(DirectExecutionBackend.GuestFastPathStub.ClockGettime);
        foreach (var clockId in new ulong[] { 0, 9, 10 })
        {
            var timespec = stackalloc long[2];
            var result = ((delegate* unmanaged<ulong, long*, ulong>)stub)(clockId, timespec);
            var now = DateTimeOffset.UtcNow;

            Assert.Equal(0UL, result);
            Assert.InRange(timespec[1], 0, 999_999_999);
            var actualTicks = timespec[0] * TimeSpan.TicksPerSecond + timespec[1] / 100;
            var expectedTicks = now.ToUnixTimeSeconds() * TimeSpan.TicksPerSecond + now.Ticks % TimeSpan.TicksPerSecond;
            Assert.InRange(actualTicks, expectedTicks - SlackNanoseconds / 100, expectedTicks + SlackNanoseconds / 100);
        }
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(13UL)]
    [InlineData(14UL)]
    [InlineData(15UL)]
    [InlineData(99UL)]
    [InlineData(0x1_0000_0004UL)]
    public void ClockGettime_LeavesOtherClockIdsToTheManagedExport(ulong clockId)
    {
        if (!_canRun)
        {
            return;
        }

        var stub = Build(DirectExecutionBackend.GuestFastPathStub.ClockGettime);
        var timespec = stackalloc long[2];
        timespec[0] = 111;
        timespec[1] = 222;

        Assert.Equal(FallbackSentinel, ((delegate* unmanaged<ulong, long*, ulong>)stub)(clockId, timespec));
        Assert.Equal(111, timespec[0]);
        Assert.Equal(222, timespec[1]);
    }

    [Fact]
    public void ClockGettime_LeavesANullTimespecToTheManagedExport()
    {
        if (!_canRun)
        {
            return;
        }

        var stub = Build(DirectExecutionBackend.GuestFastPathStub.ClockGettime);
        Assert.Equal(FallbackSentinel, ((delegate* unmanaged<ulong, long*, ulong>)stub)(4, null));
    }

    [Fact]
    public void GetTimeOfDay_WritesSecondsAndMicroseconds()
    {
        if (!_canRun)
        {
            return;
        }

        var stub = Build(DirectExecutionBackend.GuestFastPathStub.GetTimeOfDay);
        var timeval = stackalloc long[2];
        var result = ((delegate* unmanaged<long*, void*, ulong>)stub)(timeval, null);
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(0UL, result);
        Assert.InRange(timeval[1], 0, 999_999);
        var actualMicros = timeval[0] * 1_000_000L + timeval[1];
        var expectedMicros = now.ToUnixTimeSeconds() * 1_000_000L + now.Ticks % TimeSpan.TicksPerSecond / 10;
        Assert.InRange(actualMicros, expectedMicros - SlackNanoseconds / 1000, expectedMicros + SlackNanoseconds / 1000);
    }

    [Fact]
    public void GetTimeOfDay_LeavesATimezoneOrNullTimevalToTheManagedExport()
    {
        if (!_canRun)
        {
            return;
        }

        var stub = Build(DirectExecutionBackend.GuestFastPathStub.GetTimeOfDay);
        var timeval = stackalloc long[2];
        var timezone = stackalloc int[2];
        Assert.Equal(FallbackSentinel, ((delegate* unmanaged<long*, int*, ulong>)stub)(timeval, timezone));
        Assert.Equal(FallbackSentinel, ((delegate* unmanaged<long*, int*, ulong>)stub)(null, null));
    }

    [Fact]
    public void ProcessTime_MatchesTheManagedMicrosecondCounter()
    {
        if (!_canRun)
        {
            return;
        }

        var stub = Build(DirectExecutionBackend.GuestFastPathStub.ProcessTimeMicroseconds);
        var before = KernelRuntimeCompatExports.ReadProcessTimeMicroseconds();
        var actual = ((delegate* unmanaged<ulong>)stub)();
        var after = KernelRuntimeCompatExports.ReadProcessTimeMicroseconds();

        Assert.InRange(actual, before, after);
    }

    [Fact]
    public void Classification_RoutesTheFourClockNids()
    {
        Assert.Equal(DirectExecutionBackend.GuestFastPathStub.ClockGettime, DirectExecutionBackend.ClassifyGuestFastPathStub("lLMT9vJAck0"));
        Assert.Equal(DirectExecutionBackend.GuestFastPathStub.ClockGettime, DirectExecutionBackend.ClassifyGuestFastPathStub("QBi7HCK03hw"));
        Assert.Equal(DirectExecutionBackend.GuestFastPathStub.GetTimeOfDay, DirectExecutionBackend.ClassifyGuestFastPathStub("n88vx3C5nW8"));
        Assert.Equal(DirectExecutionBackend.GuestFastPathStub.ProcessTimeMicroseconds, DirectExecutionBackend.ClassifyGuestFastPathStub("4J2sUJmuHZQ"));
    }

    // Emits the stub at +0x200 of an executable page and a fallback trampoline at +0x100 that
    // returns a recognisable value.
    private nint Build(DirectExecutionBackend.GuestFastPathStub kind)
    {
        var libSystem = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        var targets = new DirectExecutionBackend.ClockStubTargets(
            NativeLibrary.GetExport(libSystem, "mach_absolute_time"),
            KernelRuntimeCompatExports.ProcessStartCounter,
            NativeLibrary.GetExport(libSystem, "clock_gettime"));

        // mov rax, imm64 / ret
        _page[0x100] = 0x48;
        _page[0x101] = 0xB8;
        *(ulong*)(_page + 0x102) = FallbackSentinel;
        _page[0x10A] = 0xC3;
        var code = DirectExecutionBackend.EmitGuestClockStub(kind, targets, (nint)(_page + 0x100));
        Assert.True(code.Count < 0x200);
        for (var i = 0; i < code.Count; i++)
        {
            _page[0x200 + i] = code[i];
        }

        Assert.True(HostMemory.Protect(_page, 4096, HostMemory.PAGE_EXECUTE_READ, out _));
        HostMemory.FlushInstructionCache(_page, 4096);
        return (nint)(_page + 0x200);
    }
}
