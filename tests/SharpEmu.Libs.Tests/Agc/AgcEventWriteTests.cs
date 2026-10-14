// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcEventWriteTests
{
    [Fact]
    public void AnOcclusionQueryBuiltFromTwoCounterDumpsReadsAsVisible()
    {
        var runner = new StreamRunner();
        var begin = Build(0x39, StreamRunner.LabelAddress);
        var end = Build(0x39, StreamRunner.LabelAddress + 8);

        Assert.Equal(SubmissionProgress.Complete, runner.Run(begin, end));
        for (var block = 0u; block < 16; block++)
        {
            var address = StreamRunner.LabelAddress + (block * 16);
            var first = runner.Host.ReadQword(address);
            var last = runner.Host.ReadQword(address + 8);
            Assert.NotEqual(0UL, first & last & (1UL << 63));
            Assert.True(last > first);
        }
    }

    [Theory]
    [InlineData(0x38u, 0x1234567800UL, 4)]
    [InlineData(0x39u, 0x1234567800UL, 4)]
    [InlineData(0x3Au, 0UL, 2)]
    [InlineData(0x10u, 0UL, 2)]
    public void ACounterEventKeepsItsAddressAndOthersStayTwoDwords(uint eventType, ulong address, int words)
    {
        var packet = Build(eventType, address);

        Assert.Equal(words, packet.Length);
        Assert.Equal(eventType | (words == 4 ? 0x100u : 0), packet[1]);
        if (words == 4)
        {
            Assert.Equal((uint)address, packet[2]);
            Assert.Equal((uint)(address >> 32), packet[3]);
        }
    }

    [Fact]
    public void AnAddressOnAnEventThatTakesNoneIsRefused()
    {
        var (context, _) = Call(0x10, 0x1234567800UL);

        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    private static uint[] Build(uint eventType, ulong address)
    {
        var (context, memory) = Call(eventType, address);
        Assert.Equal(PacketAddress, context[CpuRegister.Rax]);
        Span<byte> bytes = stackalloc byte[16];
        Assert.True(memory.TryRead(CommandBuffer + 0x10, bytes[..8]));
        var length = checked((int)(BinaryPrimitives.ReadUInt64LittleEndian(bytes) - PacketAddress));
        Assert.InRange(length, 8, 16);
        Assert.True(memory.TryRead(PacketAddress, bytes[..length]));
        var packet = new uint[length / 4];
        for (var index = 0; index < packet.Length; index++)
        {
            packet[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(index * 4)..]);
        }

        return packet;
    }

    private const ulong MemoryAddress = 0x2_1000_0000;
    private const ulong CommandBuffer = MemoryAddress + 0x80;
    private const ulong PacketAddress = MemoryAddress + 0x200;

    // A command buffer whose cursor (+0x10) and end (+0x18) leave room for one packet.
    private static (CpuContext Context, FakeCpuMemory Memory) Call(uint eventType, ulong address)
    {
        var memory = new FakeCpuMemory(MemoryAddress, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, PacketAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], PacketAddress + 0x100);
        Assert.True(memory.TryWrite(CommandBuffer + 0x10, bytes));
        context[CpuRegister.Rdi] = CommandBuffer;
        context[CpuRegister.Rsi] = eventType;
        context[CpuRegister.Rdx] = address;

        AgcExports.DcbEventWrite(context);
        return (context, memory);
    }
}
