// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class AprAsyncSubmissionTests
{
    private const string WaitOnAddressNid = "V7GQTEeUfhw";
    private const string WriteAddressNid = "j0+3uJMxYJY";
    private const ulong MemoryBase = 0x3_0000_0000;
    private const ulong IdAddress = MemoryBase + 0x40;
    private const ulong ResultAddress = MemoryBase + 0x60;
    private const ulong GateAddress = MemoryBase + 0x800;
    private const ulong FirstDoneAddress = MemoryBase + 0x810;
    private const ulong SecondDoneAddress = MemoryBase + 0x818;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x2000);
    private readonly CpuContext _context;
    private readonly ModuleManager _manager = new();

    public AprAsyncSubmissionTests()
    {
        _context = new CpuContext(_memory, Generation.Gen5);
        _manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
    }

    [Fact]
    public void SubmitReturnsBeforeAWaitCommandIsSatisfiedAndWaitCollectsTheResult()
    {
        var buffer = BuildBuffer(0x100, waitFor: (GateAddress, 5), writes: [(FirstDoneAddress, 1)]);
        var id = SubmitAndGetResult(buffer, priority: 0);

        Thread.Sleep(20);
        Assert.Equal(0UL, Read(FirstDoneAddress));

        // A plain store the emulator does not observe: the worker's bounded re-check finds it.
        Assert.True(_context.TryWriteUInt64(GateAddress, 5));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Wait(id));
        Assert.Equal(1UL, Read(FirstDoneAddress));
        Assert.Equal(0UL, Read(ResultAddress));

        // The id is collected by the first wait.
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, Wait(id));
    }

    [Fact]
    public void SubmissionsOfOnePriorityCompleteInOrder()
    {
        var first = BuildBuffer(0x100, waitFor: (GateAddress + 0x20, 7), writes: [(FirstDoneAddress + 0x20, 1)]);
        var second = BuildBuffer(0x400, waitFor: null, writes: [(SecondDoneAddress + 0x20, 1)]);
        var firstId = SubmitAndGetResult(first, priority: 1);
        var secondId = SubmitAndGetResult(second, priority: 1);

        Thread.Sleep(20);
        Assert.Equal(0UL, Read(SecondDoneAddress + 0x20));

        Assert.True(_context.TryWriteUInt64(GateAddress + 0x20, 7));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Wait(secondId));
        Assert.Equal(1UL, Read(FirstDoneAddress + 0x20));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Wait(firstId));
    }

    [Fact]
    public void AnAmprAddressWriteWakesAWaitOnAnotherPriority()
    {
        var waiter = BuildBuffer(0x100, waitFor: (GateAddress + 0x40, 9), writes: [(FirstDoneAddress + 0x40, 1)]);
        var writer = BuildBuffer(0x400, waitFor: null, writes: [(GateAddress + 0x40, 9)]);
        var waiterId = SubmitAndGetResult(waiter, priority: 2);
        var writerId = SubmitAndGetResult(writer, priority: 3);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Wait(waiterId));
        Assert.Equal(1UL, Read(FirstDoneAddress + 0x40));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Wait(writerId));
    }

    [Fact]
    public void AprWaitCommandBufferCanPark()
    {
        // The wait must not be dispatched as a leaf, which could not park the thread.
        Assert.False(SharpEmu.Core.Cpu.Native.DirectExecutionBackend.IsNoBlockLeafImport("rqwFKI4PAiM"));
    }

    private ulong BuildBuffer(ulong offset, (ulong Address, ulong Value)? waitFor, (ulong Address, ulong Value)[] writes)
    {
        var commandBuffer = MemoryBase + offset;
        var records = commandBuffer + 0x40;
        _context[CpuRegister.Rdi] = commandBuffer;
        _context[CpuRegister.Rsi] = records;
        _context[CpuRegister.Rdx] = 0x100;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(_context));
        Assert.Equal(0, AmprExports.CommandBufferSetBuffer(_context));

        if (waitFor is { } wait)
        {
            _context[CpuRegister.Rdi] = commandBuffer;
            _context[CpuRegister.Rsi] = wait.Address;
            _context[CpuRegister.Rdx] = wait.Value;
            _context[CpuRegister.Rcx] = 0;
            _context[CpuRegister.R8] = 0;
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, _manager.Dispatch(WaitOnAddressNid, _context));
        }

        foreach (var (address, value) in writes)
        {
            _context[CpuRegister.Rdi] = commandBuffer;
            _context[CpuRegister.Rsi] = address;
            _context[CpuRegister.Rdx] = value;
            Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, _manager.Dispatch(WriteAddressNid, _context));
        }

        return commandBuffer;
    }

    private uint SubmitAndGetResult(ulong commandBuffer, ulong priority)
    {
        Assert.True(_context.TryWriteUInt64(ResultAddress, 0));
        _context[CpuRegister.Rdi] = commandBuffer;
        _context[CpuRegister.Rsi] = priority;
        _context[CpuRegister.Rdx] = ResultAddress;
        _context[CpuRegister.Rcx] = IdAddress;
        Assert.Equal(0, KernelAprCompatExports.KernelAprSubmitCommandBufferAndGetResult(_context));
        Assert.True(_context.TryReadUInt32(IdAddress, out var id));
        Assert.NotEqual(0u, id);
        return id;
    }

    private int Wait(uint id)
    {
        _context[CpuRegister.Rdi] = id;
        return KernelAprCompatExports.KernelAprWaitCommandBuffer(_context);
    }

    private ulong Read(ulong address)
    {
        Assert.True(_context.TryReadUInt64(address, out var value));
        return value;
    }
}
