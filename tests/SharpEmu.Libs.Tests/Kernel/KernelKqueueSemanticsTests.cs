// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

using Q = KernelEventQueueCompatExports;

public sealed class KernelKqueueSemanticsTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong HandleAddress = BaseAddress + 0x100;
    private const ulong OutCountAddress = BaseAddress + 0x180;
    private const ulong TimeoutAddress = BaseAddress + 0x1C0;
    private const ulong ChangeAddress = BaseAddress + 0x200;
    private const ulong EventsAddress = BaseAddress + 0x1000;
    private const int EventCapacity = 96;

    private const int Enoent = 2;
    private const int Ebadf = 9;
    private const int Einval = 22;

    private static readonly int TimedOut = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
    private static readonly int Ok = (int)OrbisGen2Result.ORBIS_GEN2_OK;

    [Fact]
    public void Kevent_OneShotIsDeliveredOnceAndThenDeleted()
    {
        var (memory, ctx, kq) = CreateKqueue();
        Assert.Equal(0, Kevent(ctx, memory, kq, [UserChange(1, Q.KernelEventFlagAdd | Q.KernelEventFlagOneShot, Q.NoteTrigger)]));

        var events = Poll(ctx, memory, kq);
        var delivered = Assert.Single(events);
        Assert.Equal(Q.KernelEventFilterUser, delivered.Filter);
        Assert.Equal(Q.KernelEventFlagOneShot, delivered.Flags);
        Assert.Empty(Poll(ctx, memory, kq));

        // The knote is gone, so deleting it reports ENOENT through EV_ERROR.
        var error = KeventErrors(ctx, memory, kq, UserChange(1, Q.KernelEventFlagDelete));
        Assert.Equal(Enoent, error);
        Close(kq);
    }

    [Fact]
    public void Kevent_DispatchDisablesAfterDeliveryUntilEnabled()
    {
        var (memory, ctx, kq) = CreateKqueue();
        Assert.Equal(0, Kevent(ctx, memory, kq, [UserChange(2, Q.KernelEventFlagAdd | Q.KernelEventFlagDispatch, Q.NoteTrigger)]));

        Assert.Single(Poll(ctx, memory, kq));
        Assert.Empty(Poll(ctx, memory, kq));

        // Still triggered (level), so enabling reports it again.
        Assert.Equal(0, Kevent(ctx, memory, kq, [UserChange(2, Q.KernelEventFlagEnable)]));
        var again = Assert.Single(Poll(ctx, memory, kq));
        Assert.Equal(Q.KernelEventFlagDispatch, again.Flags);
        Close(kq);
    }

    [Fact]
    public void Kevent_DisableHoldsTriggerAndEnableReleasesIt()
    {
        var (memory, ctx, kq) = CreateKqueue();
        Assert.Equal(0, Kevent(ctx, memory, kq, [UserChange(3, Q.KernelEventFlagAdd | Q.KernelEventFlagClear)]));
        Assert.Equal(0, Kevent(ctx, memory, kq, [UserChange(3, Q.KernelEventFlagDisable)]));
        Assert.Equal(0, Kevent(ctx, memory, kq, [UserChange(3, 0, Q.NoteTrigger)]));
        Assert.Empty(Poll(ctx, memory, kq));

        Assert.Equal(0, Kevent(ctx, memory, kq, [UserChange(3, Q.KernelEventFlagEnable)]));
        Assert.Single(Poll(ctx, memory, kq));
        // EV_CLEAR: reported once per trigger.
        Assert.Empty(Poll(ctx, memory, kq));
        Close(kq);
    }

    [Fact]
    public void Kevent_UserFflagsCombineAndClearAfterDelivery()
    {
        var (memory, ctx, kq) = CreateKqueue();
        Assert.Equal(0, Kevent(ctx, memory, kq, [UserChange(4, Q.KernelEventFlagAdd | Q.KernelEventFlagClear, 0x3)]));
        Assert.Equal(0, Kevent(ctx, memory, kq, [UserChange(4, 0, Q.NoteFfor | 0x10, data: 9)]));
        Assert.Equal(0, Kevent(ctx, memory, kq, [UserChange(4, 0, Q.NoteFfand | Q.NoteTrigger | 0x12, data: 7)]));

        var delivered = Assert.Single(Poll(ctx, memory, kq));
        Assert.Equal(0x12u, delivered.Fflags);
        Assert.Equal(7UL, delivered.Data);

        Assert.Equal(0, Kevent(ctx, memory, kq, [UserChange(4, 0, Q.NoteTrigger)]));
        delivered = Assert.Single(Poll(ctx, memory, kq));
        Assert.Equal(0u, delivered.Fflags);
        Close(kq);
    }

    [Fact]
    public void Kevent_ReportsErrorsAndReceiptsThroughEventList()
    {
        var (memory, ctx, kq) = CreateKqueue();

        Assert.Equal(Enoent, KeventErrors(ctx, memory, kq, UserChange(5, 0, Q.NoteTrigger)));
        Assert.Equal(
            Einval,
            KeventErrors(ctx, memory, kq, UserChange(5, Q.KernelEventFlagAdd | Q.KernelEventFlagEnable | Q.KernelEventFlagDisable)));
        Assert.Equal(
            Ebadf,
            KeventErrors(ctx, memory, kq, new Q.KernelQueuedEvent(0x7FFF_0000, Q.KernelEventFilterRead, Q.KernelEventFlagAdd, 0, 0, 0)));
        Assert.Equal(
            Einval,
            KeventErrors(ctx, memory, kq, new Q.KernelQueuedEvent(1, -99, Q.KernelEventFlagAdd, 0, 0, 0)));
        Assert.Equal(0, KeventErrors(ctx, memory, kq, UserChange(5, Q.KernelEventFlagAdd | Q.KernelEventFlagReceipt)));

        // With no room for the error the call itself fails.
        WriteChanges(memory, [UserChange(6, 0)]);
        Assert.Equal(-1, CallKevent(ctx, kq, 1, 0, 0));
        Close(kq);
    }

    [Fact]
    public void GetEventFflagsAndError_ReadTheEventRecord()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x4000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        WriteEvent(memory, EventsAddress, new Q.KernelQueuedEvent(1, Q.KernelEventFilterUser, Q.KernelEventFlagError, 0x55, 22, 0));

        ctx[CpuRegister.Rdi] = EventsAddress;
        _ = Q.KernelGetEventFflags(ctx);
        Assert.Equal(0x55UL, ctx[CpuRegister.Rax]);
        ctx[CpuRegister.Rdi] = EventsAddress;
        Assert.Equal(22, Q.KernelGetEventError(ctx));

        WriteEvent(memory, EventsAddress, new Q.KernelQueuedEvent(1, Q.KernelEventFilterUser, 0, 0, 22, 0));
        ctx[CpuRegister.Rdi] = EventsAddress;
        Assert.Equal(0, Q.KernelGetEventError(ctx));
    }

    [Fact]
    public void GraphicsOccurrences_AreDeliveredSeparatelyAndBoundedByFolding()
    {
        var (memory, ctx, handle) = CreateEqueue();
        const ulong ident = 0xE0E0;
        Assert.True(Q.RegisterEvent(handle, ident, Q.KernelEventFilterGraphics, 0x99));
        const int occurrences = Q.MaxPendingOccurrencesPerKnote + 6;
        for (var i = 1; i <= occurrences; i++)
        {
            Assert.True(Q.TriggerRegisteredEventsByFilter(Q.KernelEventFilterGraphics, (ulong)i, ident) >= 1);
        }

        var events = WaitEqueue(ctx, memory, handle, EventCapacity, timeoutUsec: 0, out var result);
        Assert.Equal(Ok, result);
        Assert.Equal(Q.MaxPendingOccurrencesPerKnote, events.Count);
        Assert.Equal(7UL, events[0].Data);
        Assert.Equal(7u, events[0].Fflags);
        Assert.Equal((ulong)occurrences, events[^1].Data);
        Assert.Equal((uint)occurrences, (uint)events.Sum(e => (long)e.Fflags));
        Assert.All(events, e => Assert.Equal(0x99UL, e.UserData));

        _ = WaitEqueue(ctx, memory, handle, EventCapacity, timeoutUsec: 0, out result);
        Assert.Equal(TimedOut, result);
        Delete(ctx, handle);
    }

    [Fact]
    public void GraphicsOccurrences_ResumeWhenCapacityIsSmall()
    {
        var (memory, ctx, handle) = CreateEqueue();
        const ulong ident = 0xE0E1;
        Assert.True(Q.RegisterEvent(handle, ident, Q.KernelEventFilterGraphics, 0));
        for (var i = 1; i <= 3; i++)
        {
            _ = Q.TriggerRegisteredEventsByFilter(Q.KernelEventFilterGraphics, (ulong)i, ident);
        }

        var first = WaitEqueue(ctx, memory, handle, 2, timeoutUsec: 0, out _);
        Assert.Equal([1UL, 2UL], first.Select(e => e.Data));
        var second = WaitEqueue(ctx, memory, handle, 2, timeoutUsec: 0, out _);
        Assert.Equal([3UL], second.Select(e => e.Data));
        Delete(ctx, handle);
    }

    [Fact]
    public void HostWaiter_IsWokenByEveryProducer()
    {
        var (_, _, handle) = CreateEqueue();
        Assert.True(Q.RegisterEvent(handle, 0x10, Q.KernelEventFilterGraphics, 0));
        Assert.True(Q.RegisterEvent(handle, 0x11, Q.KernelEventFilterAmpr, 0));

        // Each producer must wake a host waiter well before its 10 s timeout.
        AssertHostWake(handle, () => Q.TriggerRegisteredEventsByFilter(Q.KernelEventFilterGraphics, 1, 0x10));
        AssertHostWake(handle, () => Q.TriggerAmprEvent(handle, 0x11, 5));
        AssertHostWake(handle, () => Q.TriggerDisplayEvent(handle, 0x6, -13, 0, 0));

        var memory = new FakeCpuMemory(BaseAddress, 0x4000);
        Delete(new CpuContext(memory, Generation.Gen5), handle);
    }

    [Fact]
    public void HostWaiter_TimesOutWithoutCap()
    {
        var (memory, ctx, handle) = CreateEqueue();
        var stopwatch = Stopwatch.StartNew();
        _ = WaitEqueue(ctx, memory, handle, 1, timeoutUsec: 30_000, out var result);
        Assert.Equal(TimedOut, result);
        Assert.True(stopwatch.ElapsedMilliseconds >= 25, $"returned after {stopwatch.ElapsedMilliseconds} ms");
        Delete(ctx, handle);
    }

    [Fact]
    public void GuestWaiter_IsWokenByTriggerFromAnotherThread()
    {
        var (memory, ctx, handle) = CreateEqueue();
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 0x20;
        Assert.Equal(Ok, Q.KernelAddUserEventEdge(ctx));

        var waiter = StageGuestWait(ctx, handle, threadHandle: 0x731);
        Assert.False(waiter.TryWake());

        var trigger = new Thread(() =>
        {
            var hostCtx = new CpuContext(memory, Generation.Gen5);
            hostCtx[CpuRegister.Rdi] = handle;
            hostCtx[CpuRegister.Rsi] = 0x20;
            hostCtx[CpuRegister.Rdx] = 0xABCD;
            Assert.Equal(Ok, Q.KernelTriggerUserEvent(hostCtx));
        });
        trigger.Start();
        trigger.Join();

        Assert.True(waiter.TryWake());
        Assert.Equal(Ok, waiter.Resume());
        Assert.Equal(1u, ReadUInt32(memory, OutCountAddress));
        var delivered = ReadEvent(memory, EventsAddress);
        Assert.Equal(0x20UL, delivered.Ident);
        Assert.Equal(0xABCDUL, delivered.UserData);
        Delete(ctx, handle);
    }

    [Fact]
    public void TimerEvent_IsPeriodicAndCountsExpirations()
    {
        var (memory, ctx, handle) = CreateEqueue();
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 7;
        ctx[CpuRegister.Rdx] = 2_000;
        ctx[CpuRegister.Rcx] = 0x77;
        Assert.Equal(Ok, Q.KernelAddTimerEvent(ctx));

        var first = WaitEqueue(ctx, memory, handle, 1, timeoutUsec: 5_000_000, out var result);
        Assert.Equal(Ok, result);
        var expiry = Assert.Single(first);
        Assert.Equal(Q.KernelEventFilterTimer, expiry.Filter);
        Assert.Equal(Q.KernelEventFlagClear, expiry.Flags);
        Assert.True(expiry.Data >= 1);
        Assert.Equal(0x77UL, expiry.UserData);

        // The knote keeps expiring; the expirations add up across deliveries.
        var total = 0UL;
        var deadline = Stopwatch.StartNew();
        while (total < 5 && deadline.ElapsedMilliseconds < 5_000)
        {
            var later = WaitEqueue(ctx, memory, handle, 1, timeoutUsec: 5_000_000, out result);
            Assert.Equal(Ok, result);
            total += Assert.Single(later).Data;
        }

        Assert.True(total >= 5, $"only {total} expirations");

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 7;
        Assert.Equal(Ok, Q.KernelDeleteTimerEvent(ctx));
        Delete(ctx, handle);
    }

    [Fact]
    public void HrTimer_FiresOnceAsOneShot()
    {
        var (memory, ctx, handle) = CreateEqueue();
        WriteUInt64(memory, TimeoutAddress, 0);
        WriteUInt64(memory, TimeoutAddress + 8, 500_000);
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 3;
        ctx[CpuRegister.Rdx] = TimeoutAddress;
        ctx[CpuRegister.Rcx] = 0x33;
        Assert.Equal(Ok, Q.KernelAddHrTimerEvent(ctx));

        var events = WaitEqueue(ctx, memory, handle, 4, timeoutUsec: 5_000_000, out var result);
        Assert.Equal(Ok, result);
        var expiry = Assert.Single(events);
        Assert.Equal(Q.KernelEventFilterHrTimer, expiry.Filter);
        Assert.Equal(1UL, expiry.Data);

        _ = WaitEqueue(ctx, memory, handle, 4, timeoutUsec: 10_000, out result);
        Assert.Equal(TimedOut, result);
        Delete(ctx, handle);
    }

    [Fact]
    public void KeventTimer_HonoursUnitsAndOneShot()
    {
        var (memory, ctx, kq) = CreateKqueue();
        var change = new Q.KernelQueuedEvent(9, Q.KernelEventFilterTimer, Q.KernelEventFlagAdd | Q.KernelEventFlagOneShot, 0x4 /* NOTE_USECONDS */, 1_500, 0x9);
        Assert.Equal(0, Kevent(ctx, memory, kq, [change]));

        WriteUInt64(memory, TimeoutAddress, 5);
        WriteUInt64(memory, TimeoutAddress + 8, 0);
        WriteChanges(memory, []);
        Assert.Equal(1, CallKevent(ctx, kq, 0, EventsAddress, 4, TimeoutAddress));
        var expiry = ReadEvent(memory, EventsAddress);
        Assert.Equal(Q.KernelEventFilterTimer, expiry.Filter);
        Assert.Equal((ushort)(Q.KernelEventFlagOneShot | Q.KernelEventFlagClear), expiry.Flags);
        Assert.Empty(Poll(ctx, memory, kq));
        Close(kq);
    }

    [Fact]
    public void UserEventLevelAndDeleteSemantics()
    {
        var (memory, ctx, handle) = CreateEqueue();
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 0x40;
        Assert.Equal(Ok, Q.KernelAddUserEvent(ctx));
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 0x40;
        ctx[CpuRegister.Rdx] = 1;
        Assert.Equal(Ok, Q.KernelTriggerUserEvent(ctx));

        // Level: reported by every wait, once per wait.
        Assert.Single(WaitEqueue(ctx, memory, handle, 8, 0, out _));
        Assert.Single(WaitEqueue(ctx, memory, handle, 8, 0, out _));

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 0x40;
        Assert.Equal(Ok, Q.KernelDeleteUserEvent(ctx));
        _ = WaitEqueue(ctx, memory, handle, 8, 0, out var result);
        Assert.Equal(TimedOut, result);
        Delete(ctx, handle);
    }

    // ---- helpers ----

    private static void AssertHostWake(ulong handle, Func<object> trigger)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x4000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var result = -1;
        var waiter = new Thread(() => _ = WaitEqueue(ctx, memory, handle, 4, timeoutUsec: 10_000_000, out result));
        var stopwatch = Stopwatch.StartNew();
        waiter.Start();
        Thread.Sleep(30);
        _ = trigger();
        Assert.True(waiter.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(Ok, result);
        Assert.True(stopwatch.ElapsedMilliseconds < 9_000, $"woke after {stopwatch.ElapsedMilliseconds} ms");
    }

    private static Q.KernelQueuedEvent UserChange(ulong ident, int flags, uint fflags = 0, ulong data = 0) =>
        new(ident, Q.KernelEventFilterUser, (ushort)flags, fflags, data, 0x1000 + ident);

    private static (FakeCpuMemory Memory, CpuContext Context, int Kq) CreateKqueue()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x4000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var fd = Q.PosixKqueue(ctx);
        Assert.True(fd > 2);
        return (memory, ctx, fd);
    }

    private static void Close(int kq) => Assert.True(Q.TryCloseKqueueDescriptor(kq));

    private static int Kevent(CpuContext ctx, FakeCpuMemory memory, int kq, Q.KernelQueuedEvent[] changes)
    {
        WriteChanges(memory, changes);
        return CallKevent(ctx, kq, changes.Length, 0, 0);
    }

    private static int KeventErrors(CpuContext ctx, FakeCpuMemory memory, int kq, Q.KernelQueuedEvent change)
    {
        WriteChanges(memory, [change]);
        Assert.Equal(1, CallKevent(ctx, kq, 1, EventsAddress, 1));
        var report = ReadEvent(memory, EventsAddress);
        Assert.NotEqual(0, report.Flags & Q.KernelEventFlagError);
        return (int)report.Data;
    }

    private static List<Q.KernelQueuedEvent> Poll(CpuContext ctx, FakeCpuMemory memory, int kq)
    {
        WriteUInt64(memory, TimeoutAddress, 0);
        WriteUInt64(memory, TimeoutAddress + 8, 0);
        var count = CallKevent(ctx, kq, 0, EventsAddress, EventCapacity, TimeoutAddress);
        Assert.True(count >= 0);
        return Enumerable.Range(0, count)
            .Select(i => ReadEvent(memory, EventsAddress + ((ulong)i * 0x20)))
            .ToList();
    }

    private static int CallKevent(CpuContext ctx, int kq, int changes, ulong events, int capacity, ulong timeout = 0)
    {
        ctx[CpuRegister.Rdi] = unchecked((ulong)kq);
        ctx[CpuRegister.Rsi] = ChangeAddress;
        ctx[CpuRegister.Rdx] = unchecked((ulong)changes);
        ctx[CpuRegister.Rcx] = events;
        ctx[CpuRegister.R8] = unchecked((ulong)capacity);
        ctx[CpuRegister.R9] = timeout;
        return Q.PosixKevent(ctx);
    }

    private static void WriteChanges(FakeCpuMemory memory, Q.KernelQueuedEvent[] changes)
    {
        for (var i = 0; i < changes.Length; i++)
        {
            WriteEvent(memory, ChangeAddress + ((ulong)i * 0x20), changes[i]);
        }
    }

    private static (FakeCpuMemory Memory, CpuContext Context, ulong Handle) CreateEqueue()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x4000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = HandleAddress;
        Assert.Equal(Ok, Q.KernelCreateEqueue(ctx));
        return (memory, ctx, ReadUInt64(memory, HandleAddress));
    }

    private static void Delete(CpuContext ctx, ulong handle)
    {
        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(Ok, Q.KernelDeleteEqueue(ctx));
    }

    private static List<Q.KernelQueuedEvent> WaitEqueue(
        CpuContext ctx,
        FakeCpuMemory memory,
        ulong handle,
        int capacity,
        uint timeoutUsec,
        out int result)
    {
        WriteUInt32(memory, TimeoutAddress, timeoutUsec);
        WriteUInt32(memory, OutCountAddress, 0);
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = EventsAddress;
        ctx[CpuRegister.Rdx] = (ulong)capacity;
        ctx[CpuRegister.Rcx] = OutCountAddress;
        ctx[CpuRegister.R8] = TimeoutAddress;
        result = Q.KernelWaitEqueue(ctx);
        var count = result == Ok ? (int)ReadUInt32(memory, OutCountAddress) : 0;
        return Enumerable.Range(0, count)
            .Select(i => ReadEvent(memory, EventsAddress + ((ulong)i * 0x20)))
            .ToList();
    }

    private static IGuestThreadBlockWaiter StageGuestWait(CpuContext ctx, ulong handle, ulong threadHandle)
    {
        var previousThread = GuestThreadExecution.EnterGuestThread(threadHandle);
        var previousFrame = GuestThreadExecution.EnterImportCallFrame(
            returnRip: 0x1_0000 + threadHandle,
            resumeRsp: 0x2_0000 + threadHandle,
            returnSlotAddress: 0x3_0000 + threadHandle);
        try
        {
            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = EventsAddress;
            ctx[CpuRegister.Rdx] = 1;
            ctx[CpuRegister.Rcx] = OutCountAddress;
            ctx[CpuRegister.R8] = 0;
            Assert.Equal(Ok, Q.KernelWaitEqueue(ctx));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason, out _, out _, out _, out var waiter, out _));
            Assert.Equal("sceKernelWaitEqueue", reason);
            return Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(waiter);
        }
        finally
        {
            GuestThreadExecution.RestoreImportCallFrame(previousFrame);
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }

    private static void WriteEvent(FakeCpuMemory memory, ulong address, Q.KernelQueuedEvent value)
    {
        Span<byte> bytes = stackalloc byte[0x20];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value.Ident);
        BinaryPrimitives.WriteInt16LittleEndian(bytes[0x08..], value.Filter);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[0x0A..], value.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[0x0C..], value.Fflags);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[0x10..], value.Data);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[0x18..], value.UserData);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static Q.KernelQueuedEvent ReadEvent(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[0x20];
        Assert.True(memory.TryRead(address, bytes));
        return new Q.KernelQueuedEvent(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            BinaryPrimitives.ReadInt16LittleEndian(bytes[0x08..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[0x0A..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x0C..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[0x10..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[0x18..]));
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[4];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[8];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
