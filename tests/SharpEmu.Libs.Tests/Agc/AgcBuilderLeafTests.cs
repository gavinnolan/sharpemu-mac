// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

/// <summary>
/// The sceAgc builders the import dispatcher runs as leaves: with
/// <see cref="CpuContext.DeclineGuestCallbacks"/> set they must write exactly the bytes the full path
/// writes, and when the command buffer is full they must back out with nothing written so the
/// dispatcher can repeat the call through the path that may call the buffer's guest callback.
/// </summary>
[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcBuilderLeafTests
{
    private const ulong BaseAddress = 0x2_6000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong ScratchAddress = BaseAddress + 0x900;
    private const ulong StackAddress = BaseAddress + 0xC00;
    private const int MemorySize = 0x1000;

    private delegate void Arguments(CpuContext context, FakeCpuMemory memory);

    // Every builder the leaf class lists, with arguments that make it emit a packet. Stack
    // arguments sit at StackAddress + 8 onwards (StackAddress itself is the return slot).
    private static readonly (string Nid, string Name, Arguments Setup)[] Builders =
    [
        ("q88lQ+GP5Yk", "DrawIndex", (c, m) => { c[CpuRegister.Rsi] = 6; c[CpuRegister.Rdx] = 0x2_6000_0800; c[CpuRegister.Rcx] = 0x28; }),
        ("Yw0jKSqop+E", "DrawIndexAuto", (c, m) => { c[CpuRegister.Rsi] = 3; c[CpuRegister.Rdx] = 0; }),
        ("B+aG9DUnTKA", "DrawIndexOffset", (c, m) => { c[CpuRegister.Rsi] = 4; c[CpuRegister.Rdx] = 9; c[CpuRegister.Rcx] = 0x28; }),
        ("t1vNu082-jM", "DrawIndexIndirect", (c, m) => { c[CpuRegister.Rsi] = 0x40; c[CpuRegister.Rdx] = 0x1_0000_0005; }),
        ("l4fM9K-Lyks", "SetIndexBuffer", (c, m) => { c[CpuRegister.Rsi] = 0x2_6000_0800; c[CpuRegister.Rdx] = 12; }),
        ("8N2tmT3jmC8", "SetIndexCount", (c, m) => { c[CpuRegister.Rsi] = 33; }),
        ("GIIW2J37e70", "SetIndexSize", (c, m) => { c[CpuRegister.Rsi] = 1; c[CpuRegister.Rdx] = 2; }),
        ("tSBxhAPyytQ", "SetNumInstances", (c, m) => { c[CpuRegister.Rsi] = 7; }),
        ("pFLArOT53+w", "SetShRegisterDirect", (c, m) => c[CpuRegister.Rsi] = (0xFEDC_BA98UL << 32) | 0xABCD_00C8),
        ("LHFXRrlTPD8", "SetCxRegisterDirect", (c, m) => c[CpuRegister.Rsi] = (0x1357_9BDFUL << 32) | 0x1_0028),
        ("w4-d0n60hdo", "SetUcRegisterDirect", (c, m) => c[CpuRegister.Rsi] = (0x2468_ACE0UL << 32) | 0x0309),
        ("BVFg3CWU6Eo", "SetCfRegisterRangeDirect", (c, m) => { Fill(m, ScratchAddress, 5); c[CpuRegister.Rsi] = 0x44; c[CpuRegister.Rdx] = ScratchAddress; c[CpuRegister.Rcx] = 5; }),
        ("n2fD4A+pb+g", "CbSetShRegisterRangeDirect", (c, m) => { Fill(m, ScratchAddress, 3); c[CpuRegister.Rsi] = 0x40; c[CpuRegister.Rdx] = ScratchAddress; c[CpuRegister.Rcx] = 3; }),
        ("-HOOCn0JY48", "SetShRegistersIndirect", (c, m) => { c[CpuRegister.Rsi] = 0x2_6000_0A00; c[CpuRegister.Rdx] = 4; }),
        ("ZvwO9euwYzc", "SetCxRegistersIndirect", (c, m) => { c[CpuRegister.Rsi] = 0x2_6000_0A00; c[CpuRegister.Rdx] = 4; }),
        ("hvUfkUIQcOE", "SetUcRegistersIndirect", (c, m) => { c[CpuRegister.Rsi] = 0x2_6000_0A00; c[CpuRegister.Rdx] = 4; }),
        ("k3GhuSNmBLU", "CbDispatch", (c, m) => { c[CpuRegister.Rsi] = 8; c[CpuRegister.Rdx] = 4; c[CpuRegister.Rcx] = 2; c[CpuRegister.R8] = 0x8000; }),
        ("CtB+A9-VxO0", "DcbDispatchIndirect", (c, m) => { c[CpuRegister.Rsi] = 0x20; c[CpuRegister.Rdx] = 0x8000; }),
        ("j3EtxFkSIhQ", "AcbDispatchIndirect", (c, m) => { c[CpuRegister.Rsi] = 0x2_6000_0A00; c[CpuRegister.Rdx] = 0x20; }),
        ("RmaJwLtc8rY", "SetBaseIndirectArgs", (c, m) => { c[CpuRegister.Rsi] = 1; c[CpuRegister.Rdx] = 0x2_6000_0A00; }),
        ("VmW0Tdpy420", "DcbWaitRegMem32", (c, m) => DcbWaitRegMem(c, m, size: 0)),
        ("VmW0Tdpy420", "DcbWaitRegMem64", (c, m) => DcbWaitRegMem(c, m, size: 1)),
        ("htn36gPnBk4", "AcbWaitRegMem32", (c, m) => AcbWaitRegMem(c, m, size: 0)),
        ("htn36gPnBk4", "AcbWaitRegMem64", (c, m) => AcbWaitRegMem(c, m, size: 1)),
        ("i1jyy49AjXU", "DcbWriteData", WriteData),
        ("eZ4+17OQz4Q", "AcbWriteData", WriteData),
        ("aJf+j5yntiU", "DcbEventWrite", (c, m) => { c[CpuRegister.Rsi] = 0x07; c[CpuRegister.Rdx] = 0; }),
        ("aJf+j5yntiU", "DcbEventWriteWithAddress", (c, m) => { c[CpuRegister.Rsi] = 0x38; c[CpuRegister.Rdx] = 0x2_6000_0A08; }),
        ("cFazmnXpJOE", "AcbEventWrite", (c, m) => { c[CpuRegister.Rsi] = 0x07; c[CpuRegister.Rdx] = 0; }),
        ("cFazmnXpJOE", "AcbEventWriteWithAddress", (c, m) => { c[CpuRegister.Rsi] = 0x38; c[CpuRegister.Rdx] = 0x2_6000_0A08; }),
        ("57labkp+rSQ", "DcbAcquireMem", (c, m) =>
        {
            Stack(m, 400);
            c[CpuRegister.Rsi] = 1; c[CpuRegister.Rdx] = 0x2000; c[CpuRegister.Rcx] = 0x5555; c[CpuRegister.R8] = 0x2_6000_0000; c[CpuRegister.R9] = 0x1000;
        }),
        ("KT-hTp-Ch14", "AcbAcquireMem", (c, m) => { c[CpuRegister.Rsi] = 0x5555; c[CpuRegister.Rdx] = 0x2_6000_0000; c[CpuRegister.Rcx] = 0x1000; c[CpuRegister.R8] = 800; }),
        ("wr23dPKyWc0", "CbReleaseMem", (c, m) =>
        {
            Stack(m, 1, 0xAAAA_BBBB_CCCC_DDDD, 0, 0, 0, 0);
            c[CpuRegister.Rsi] = 5; c[CpuRegister.Rdx] = 0x1234; c[CpuRegister.Rcx] = 1; c[CpuRegister.R8] = 2; c[CpuRegister.R9] = 0x2_6000_0A00;
        }),
    ];

    public static IEnumerable<object[]> BuilderNames() => Builders.Select(builder => new object[] { builder.Name });

    [Theory]
    [MemberData(nameof(BuilderNames))]
    public void LeafModeWritesTheBytesTheFullPathWrites(string name)
    {
        var builder = Builders.Single(candidate => candidate.Name == name);
        var full = Run(builder, leaf: false);
        var leaf = Run(builder, leaf: true);

        Assert.False(leaf.Declined);
        Assert.NotEqual(0UL, full.Rax);
        Assert.Equal(full.Rax, leaf.Rax);
        Assert.Equal(full.Memory, leaf.Memory);
        // The builder really emitted: the cursor moved and the packet is not blank.
        Assert.NotEqual(PacketAddress, BinaryPrimitives.ReadUInt64LittleEndian(leaf.Memory.AsSpan((int)(CommandBufferAddress - BaseAddress) + 0x10)));
    }

    [Theory]
    [MemberData(nameof(BuilderNames))]
    public void LeafModeDeclinesWithNothingWrittenWhenTheBufferIsFull(string name)
    {
        var builder = Builders.Single(candidate => candidate.Name == name);
        // Room for one dword: smaller than any packet, with a callback the full path would call.
        var leaf = Run(builder, leaf: true, remainingBytes: 4);
        var reference = Run(builder, leaf: false, remainingBytes: 4, runBuilder: false);

        Assert.True(leaf.Declined, name + " ran to completion with no room instead of declining");
        Assert.Equal(reference.Memory, leaf.Memory);
    }

    [Theory]
    [InlineData("DrawIndex", 40u)]
    [InlineData("CbSetShRegisterRangeDirect", 16u)]
    public void SplitAllocationBuildersDeclineBeforeTheirFirstPacket(string name, uint remainingBytes)
    {
        // Room for the first packet of a builder that allocates its packets one at a time but not
        // for all of them: declining only at the second would leave the first written twice.
        var builder = Builders.Single(candidate => candidate.Name == name);
        var leaf = Run(builder, leaf: true, remainingBytes: remainingBytes);
        var reference = Run(builder, leaf: false, remainingBytes: remainingBytes, runBuilder: false);

        Assert.True(leaf.Declined);
        Assert.Equal(reference.Memory, leaf.Memory);
    }

    [Fact]
    public void EveryLeafBuilderIsListedAndEveryListedBuilderIsCovered()
    {
        var covered = Builders.Select(builder => builder.Nid).ToHashSet(StringComparer.Ordinal);
        foreach (var nid in covered)
        {
            Assert.True(DirectExecutionBackend.IsAgcBuilderLeafImport(nid), nid + " is tested as a leaf builder but not classified as one");
        }

        var manager = CreateManager();
        foreach (var nid in covered)
        {
            Assert.True(manager.TryGetExport(nid, out _), nid + " exports nothing");
        }
    }

    private readonly record struct Result(byte[] Memory, ulong Rax, bool Declined);

    private static Result Run(
        (string Nid, string Name, Arguments Setup) builder,
        bool leaf,
        uint remainingBytes = 0x300,
        bool runBuilder = true)
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + remainingBytes);
        // A callback that is never called here (Scheduler is null): present so a full buffer would use it.
        WriteUInt64(memory, CommandBufferAddress + 0x20, 0x1234);
        WriteUInt64(memory, CommandBufferAddress + 0x28, 0x5678);
        WriteUInt32(memory, ScratchAddress + 0x100, 0xDEADBEEF);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsp] = StackAddress;
        builder.Setup(context, memory);
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = null;
        try
        {
            if (runBuilder)
            {
                context.DeclineGuestCallbacks = leaf;
                CreateManager().Dispatch(builder.Nid, context);
                context.DeclineGuestCallbacks = false;
            }
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }

        var bytes = new byte[MemorySize];
        Assert.True(memory.TryRead(BaseAddress, bytes));
        return new Result(bytes, context[CpuRegister.Rax], context.GuestCallbackDeclined);
    }

    private static void DcbWaitRegMem(CpuContext c, FakeCpuMemory m, ulong size)
    {
        // reference, mask, poll cycles
        Stack(m, 0x1111_2222_3333_4444, 0x5555_6666_7777_8888, 320);
        c[CpuRegister.Rsi] = size; c[CpuRegister.Rdx] = 3; c[CpuRegister.Rcx] = 0; c[CpuRegister.R8] = 1; c[CpuRegister.R9] = 0x2_6000_0A08;
    }

    private static void AcbWaitRegMem(CpuContext c, FakeCpuMemory m, ulong size)
    {
        // mask, poll cycles
        Stack(m, 0x5555_6666_7777_8888, 320);
        c[CpuRegister.Rsi] = size; c[CpuRegister.Rdx] = 3; c[CpuRegister.Rcx] = 1; c[CpuRegister.R8] = 0x2_6000_0A08; c[CpuRegister.R9] = 0x1111_2222_3333_4444;
    }

    private static void WriteData(CpuContext c, FakeCpuMemory m)
    {
        Fill(m, ScratchAddress, 4);
        // increment, write confirm
        Stack(m, 1, 1);
        c[CpuRegister.Rsi] = 5; c[CpuRegister.Rdx] = 2; c[CpuRegister.Rcx] = 0x2_6000_0A08; c[CpuRegister.R8] = ScratchAddress; c[CpuRegister.R9] = 4;
    }

    private static void Stack(FakeCpuMemory memory, params ulong[] arguments)
    {
        for (var index = 0; index < arguments.Length; index++)
        {
            WriteUInt64(memory, StackAddress + 8 + (ulong)index * 8, arguments[index]);
        }
    }

    private static void Fill(FakeCpuMemory memory, ulong address, int dwords)
    {
        for (var index = 0; index < dwords; index++)
        {
            WriteUInt32(memory, address + (ulong)index * 4, 0xC0DE0000u + (uint)index);
        }
    }

    private static ModuleManager CreateManager()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        return manager;
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
