// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Packets;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class CommandMemoryOperationTests
{
    [Fact]
    public void ClosedWriteOwnsBytesAndCannotRepeatItsEffect()
    {
        var runner = new StreamRunner();
        byte[] packet = [1, 2, 3, 4, 5, 6, 7, 8];
        var operation = CommandMemoryOperation.Write(StreamRunner.DataAddress, packet);
        Array.Fill(packet, (byte)0xEE);
        Assert.Equal(8UL, operation.OwnedByteCount);
        Assert.True(operation.TryGetGuestWriteRange(out var address, out var bytes));
        Assert.Equal(StreamRunner.DataAddress, address); Assert.Equal(8UL, bytes);
        ((ICommandStreamHost)runner.Host).ExecuteCommandMemoryOperation(operation);
        Assert.Equal(0x0807060504030201UL, runner.Host.ReadQword(address));
        runner.Host.WriteQword(address, 99);
        Assert.Throws<InvalidOperationException>(() => operation.Execute(runner.Host));
        Assert.Equal(99UL, runner.Host.ReadQword(address));
    }

    [Fact]
    public void FailedClosedWriteCannotBeRetriedAgainstAnotherAddressOwner()
    {
        var runner = new StreamRunner();
        var operation = CommandMemoryOperation.Write(ulong.MaxValue - 3, new byte[8]);
        Assert.False(operation.TryGetGuestWriteRange(out _, out _));
        Assert.Throws<InvalidOperationException>(() => operation.Execute(runner.Host));
        Assert.Throws<InvalidOperationException>(() => operation.Execute(runner.Host));
        Assert.Equal(0UL, runner.Host.ReadQword(StreamRunner.DataAddress));
    }

    [Fact]
    public void AtomicAndSemaphoreResultsBelongToTheOneCompletedPacket()
    {
        var runner = new StreamRunner(); var address = StreamRunner.DataAddress;
        runner.Host.WriteQword(address, 7);
        var atomic = CommandMemoryOperation.Atomic(new AtomicMemoryPacket(0x2F, 0, 0, address, 3, 0, 0, 0));
        ((ICommandStreamHost)runner.Host).ExecuteCommandMemoryOperation(atomic);
        Assert.Equal(7UL, atomic.PriorValue); Assert.Equal(10UL, runner.Host.ReadQword(address));
        var signal = CommandMemoryOperation.Semaphore(new MemorySemaphorePacket(address, false, false, 6));
        ((ICommandStreamHost)runner.Host).ExecuteCommandMemoryOperation(signal);
        Assert.Equal(11UL, runner.Host.ReadQword(address));
        var consume = CommandMemoryOperation.Semaphore(new MemorySemaphorePacket(address, false, false, 7));
        ((ICommandStreamHost)runner.Host).ExecuteCommandMemoryOperation(consume);
        Assert.Equal(11UL, consume.PriorValue); Assert.Equal(10UL, runner.Host.ReadQword(address));
        Assert.Throws<InvalidOperationException>(() => consume.Execute(runner.Host));
        Assert.Equal(10UL, runner.Host.ReadQword(address));
    }

    [Theory]
    [InlineData("gds-fill", true, 0UL, 0UL)]
    [InlineData("gds-to-guest", true, StreamRunner.DataAddress, 8UL)]
    [InlineData("guest-to-gds", true, 0UL, 0UL)]
    [InlineData("gds-to-gds", false, 0UL, 0UL)]
    [InlineData("gds-overflow", false, 0UL, 0UL)]
    [InlineData("unaligned-fill", false, 0UL, 0UL)]
    [InlineData("unsupported-atomic", false, 0UL, 0UL)]
    [InlineData("unsupported-semaphore", false, 0UL, 0UL)]
    public void OnlyClosedBoundedSelectorsDescribeGuestWrites(string selector, bool known, ulong address, ulong bytes)
    {
        var operation = selector switch
        {
            "gds-fill" => CommandMemoryOperation.Fill(16, 8, 42, true),
            "gds-to-guest" => CommandMemoryOperation.Copy(StreamRunner.DataAddress, 16, 8, false, true),
            "guest-to-gds" => CommandMemoryOperation.Copy(16, StreamRunner.DataAddress, 8, true, false),
            "gds-to-gds" => CommandMemoryOperation.Copy(16, 24, 8, true, true),
            "gds-overflow" => CommandMemoryOperation.Copy(StreamRunner.DataAddress, (ulong)ManagedCommandStreamHost.GdsBytes - 4, 8, false, true),
            "unaligned-fill" => CommandMemoryOperation.Fill(StreamRunner.DataAddress + 1, 8, 42, false),
            "unsupported-atomic" => CommandMemoryOperation.Atomic(new AtomicMemoryPacket(0, 0, 0, StreamRunner.DataAddress, 0, 0, 0, 0)),
            _ => CommandMemoryOperation.Semaphore(new MemorySemaphorePacket(StreamRunner.DataAddress, false, false, 5)),
        };
        Assert.Equal(known, operation.TryGetGuestWriteRange(out var actualAddress, out var actualBytes));
        if (known) { Assert.Equal(address, actualAddress); Assert.Equal(bytes, actualBytes); }
        // Effect inspection never executes a packet or changes immediate host memory.
        var runner = new StreamRunner(); Assert.Equal(0UL, runner.Host.ReadQword(StreamRunner.DataAddress));
    }
}
