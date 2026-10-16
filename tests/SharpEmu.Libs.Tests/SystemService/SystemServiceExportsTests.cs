// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.SystemService;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.SystemService;

public sealed class SystemServiceExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    public SystemServiceExportsTests()
    {
        SystemServiceExports.ResetForTests();
    }

    [Fact]
    public void EmptyEventPollPreservesTheEntireEventRecord()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x2004);
        var sentinel = Enumerable.Repeat((byte)0xA5, 0x2004).ToArray();
        Assert.True(memory.TryWrite(MemoryBase, sentinel));
        var context = new CpuContext(memory, Generation.Gen5) { [CpuRegister.Rdi] = MemoryBase };
        for (var poll = 0; poll < 3; poll++)
            Assert.Equal(unchecked((int)0x80A10004), SystemServiceExports.SystemServiceReceiveEvent(context));
        var actual = new byte[sentinel.Length];
        Assert.True(memory.TryRead(MemoryBase, actual));
        Assert.Equal(sentinel, actual);
        context[CpuRegister.Rdi] = 0;
        Assert.Equal(unchecked((int)0x80A10003), SystemServiceExports.SystemServiceReceiveEvent(context));
    }

    [Fact]
    public void GetNoticeScreenSkipFlagWritesOneByteAtMemoryBoundary()
    {
        var memory = new FakeCpuMemory(MemoryBase, 1);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(MemoryBase, new byte[] { 0xA5 }));
        context[CpuRegister.Rdi] = MemoryBase;

        Assert.Equal(0, SystemServiceExports.SystemServiceGetNoticeScreenSkipFlag(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Span<byte> flag = stackalloc byte[1];
        Assert.True(memory.TryRead(MemoryBase, flag));
        Assert.Equal(0, flag[0]);
    }

    [Fact]
    public void SetNoticeScreenSkipFlagRoundTripsThroughGetter()
    {
        var memory = new FakeCpuMemory(MemoryBase, 2);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = 1,
        };

        Assert.Equal(0, SystemServiceExports.SystemServiceSetNoticeScreenSkipFlag(context));

        context[CpuRegister.Rdi] = MemoryBase;
        Assert.Equal(0, SystemServiceExports.SystemServiceGetNoticeScreenSkipFlag(context));

        Span<byte> flag = stackalloc byte[1];
        Assert.True(memory.TryRead(MemoryBase, flag));
        Assert.Equal(1, flag[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(400)]
    public void ParamGetIntReturnsConfiguredSystemLanguage(int parameterId)
    {
        var memory = new FakeCpuMemory(MemoryBase, sizeof(int));
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = unchecked((ulong)parameterId),
            [CpuRegister.Rsi] = MemoryBase,
        };
        SystemServiceExports.ConfigureApplicationInfo(null, 19);

        Assert.Equal(0, SystemServiceExports.SystemServiceParamGetInt(context));

        Span<byte> value = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(MemoryBase, value));
        Assert.Equal(19, BinaryPrimitives.ReadInt32LittleEndian(value));
    }

    [Fact]
    public void QueuedEventsAreDeliveredInOrderThenNoEvent()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x2004);
        var context = new CpuContext(memory, Generation.Gen5) { [CpuRegister.Rdi] = MemoryBase };
        Assert.True(SystemServiceExports.EnqueueEvent(0x10000003, new byte[] { 1, 2, 3 }));
        Assert.True(SystemServiceExports.EnqueueEvent(0x10000000));

        Assert.Equal(0, SystemServiceExports.SystemServiceReceiveEvent(context));
        var record = new byte[0x2004];
        Assert.True(memory.TryRead(MemoryBase, record));
        Assert.Equal(0x10000003, BinaryPrimitives.ReadInt32LittleEndian(record));
        Assert.Equal(new byte[] { 1, 2, 3, 0 }, record[4..8]);
        Assert.All(record[8..], b => Assert.Equal(0, b));

        Assert.Equal(0, SystemServiceExports.SystemServiceReceiveEvent(context));
        Assert.True(memory.TryRead(MemoryBase, record));
        Assert.Equal(0x10000000, BinaryPrimitives.ReadInt32LittleEndian(record));
        Assert.Equal(unchecked((int)0x80A10004), SystemServiceExports.SystemServiceReceiveEvent(context));
    }

    [Fact]
    public void OversizedEventPayloadIsRejected()
    {
        Assert.False(SystemServiceExports.EnqueueEvent(1, new byte[SystemServiceExports.EventDataSize + 1]));
    }

    [Fact]
    public void FaultingEventBufferKeepsTheEventQueued()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x2004);
        var context = new CpuContext(memory, Generation.Gen5);
        SystemServiceExports.EnqueueEvent(0x10000000);

        // A record that does not fit in the mapped region faults and must not consume the event.
        context[CpuRegister.Rdi] = MemoryBase + 0x100;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, SystemServiceExports.SystemServiceReceiveEvent(context));
        context[CpuRegister.Rdi] = MemoryBase;
        Assert.Equal(0, SystemServiceExports.SystemServiceReceiveEvent(context));
    }

    [Fact]
    public void GetStatusReportsTheQueuedEventCount()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x100);
        var context = new CpuContext(memory, Generation.Gen5) { [CpuRegister.Rdi] = MemoryBase };
        SystemServiceExports.EnqueueEvent(0x10000000);
        SystemServiceExports.EnqueueEvent(0x10000003);

        Assert.Equal(0, SystemServiceExports.SystemServiceGetStatus(context));
        Span<byte> status = stackalloc byte[4];
        Assert.True(memory.TryRead(MemoryBase, status));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(status));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(99)]
    [InlineData(401)]
    [InlineData(-1)]
    public void ParamGetIntRejectsUnknownIdsWithoutWriting(int parameterId)
    {
        var memory = new FakeCpuMemory(MemoryBase, sizeof(int));
        Assert.True(memory.TryWrite(MemoryBase, BitConverter.GetBytes(0x5A5A5A5A)));
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = unchecked((ulong)parameterId),
            [CpuRegister.Rsi] = MemoryBase,
        };

        Assert.Equal(unchecked((int)0x80A10003), SystemServiceExports.SystemServiceParamGetInt(context));
        Span<byte> value = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(MemoryBase, value));
        Assert.Equal(0x5A5A5A5A, BinaryPrimitives.ReadInt32LittleEndian(value));
    }

    [Fact]
    public void ParamGetIntReportsHostTimeZoneAndFormats()
    {
        int Read(int id)
        {
            var memory = new FakeCpuMemory(MemoryBase, sizeof(int));
            var context = new CpuContext(memory, Generation.Gen5)
            {
                [CpuRegister.Rdi] = (ulong)id,
                [CpuRegister.Rsi] = MemoryBase,
            };
            Assert.Equal(0, SystemServiceExports.SystemServiceParamGetInt(context));
            Span<byte> value = stackalloc byte[sizeof(int)];
            Assert.True(memory.TryRead(MemoryBase, value));
            return BinaryPrimitives.ReadInt32LittleEndian(value);
        }

        Assert.Equal((int)TimeZoneInfo.Local.BaseUtcOffset.TotalMinutes, Read(4));
        Assert.InRange(Read(2), 0, 2);
        Assert.InRange(Read(3), 0, 1);
        Assert.InRange(Read(5), 0, 1);
        Assert.Equal(1, Read(1000));
    }

    [Fact]
    public void ParamGetStringOnlyKnowsTheSystemName()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x100);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = 6,
            [CpuRegister.Rsi] = MemoryBase,
            [CpuRegister.Rdx] = 65,
        };
        Assert.Equal(0, SystemServiceExports.SystemServiceParamGetString(context));
        var text = new byte[4];
        Assert.True(memory.TryRead(MemoryBase, text));
        Assert.Equal(new byte[] { (byte)'P', (byte)'S', (byte)'5', 0 }, text);

        foreach (var id in new[] { 1, 5, 7, 100 })
        {
            context[CpuRegister.Rdi] = (ulong)id;
            Assert.Equal(unchecked((int)0x80A10003), SystemServiceExports.SystemServiceParamGetString(context));
        }
    }

    [Fact]
    public void LaunchAndKillCallsReturnDocumentedErrorsAndNeverCrash()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x100);
        var context = new CpuContext(memory, Generation.Gen5);

        Assert.Equal(0, SystemServiceExports.SystemServicePowerTick(context));
        Assert.Equal(0, SystemServiceExports.SystemServiceShowControllerSettings(context));
        Assert.Equal(0, SystemServiceExports.SystemServiceDisableMediaPlay(context));
        Assert.Equal(1, SystemServiceExports.SystemServiceGetAppIdOfRunningBigApp(context));

        context[CpuRegister.Rdi] = 0;
        Assert.Equal(unchecked((int)0x80A10003), SystemServiceExports.SystemServiceLoadExec(context));
        Assert.Equal(unchecked((int)0x80A10003), SystemServiceExports.SystemServiceLaunchWebBrowser(context));
        Assert.Equal(unchecked((int)0x80A10003), SystemServiceExports.SystemServiceLaunchApp(context));
        Assert.Equal(unchecked((int)0x80A10003), SystemServiceExports.SystemServiceKillApp(context));

        context[CpuRegister.Rdi] = MemoryBase;
        Assert.Equal(unchecked((int)0x8002002D), SystemServiceExports.SystemServiceLoadExec(context));
        Assert.Equal(unchecked((int)0x8002002D), SystemServiceExports.SystemServiceLaunchWebBrowser(context));
        Assert.Equal(unchecked((int)0x8002002D), SystemServiceExports.SystemServiceLaunchApp(context));

        context[CpuRegister.Rdi] = 1;
        Assert.Equal(unchecked((int)0x8002002D), SystemServiceExports.SystemServiceKillApp(context));
    }
}
