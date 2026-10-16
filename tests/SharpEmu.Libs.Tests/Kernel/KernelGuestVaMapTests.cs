// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelGuestVaMapTests
{
    private const int Eacces = unchecked((int)0x8002000D);
    private const int Einval = unchecked((int)0x80020016);
    private const int Efault = unchecked((int)0x8002000E);
    private const int EnameTooLong = unchecked((int)0x8002003F);

    internal readonly record struct QueryInfo(ulong Start, ulong End, ulong Offset, int Protection,
        int MemoryType, byte State, string Name);

    internal static int TryQuery(BackedKernelMemory test, ulong address, bool findNext, out QueryInfo info)
    {
        info = default;
        var infoAddress = test.Output + 0x100;
        test.Context[CpuRegister.Rdi] = address;
        test.Context[CpuRegister.Rsi] = findNext ? 1UL : 0UL;
        test.Context[CpuRegister.Rdx] = infoAddress;
        test.Context[CpuRegister.Rcx] = 0x48;
        var result = KernelMemoryCompatExports.KernelVirtualQuery(test.Context);
        if (result != 0)
            return result;
        var bytes = new byte[0x48];
        Assert.True(test.Context.Memory.TryRead(infoAddress, bytes));
        var nameLength = Array.IndexOf(bytes, (byte)0, 33, 32) - 33;
        info = new QueryInfo(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8),
            BitConverter.ToUInt64(bytes, 16), BitConverter.ToInt32(bytes, 24), BitConverter.ToInt32(bytes, 28),
            bytes[32], Encoding.ASCII.GetString(bytes, 33, nameLength < 0 ? 32 : nameLength));
        return 0;
    }

    internal static QueryInfo Query(BackedKernelMemory test, ulong address, bool findNext = false)
    {
        Assert.Equal(0, TryQuery(test, address, findNext, out var info));
        return info;
    }

    private static ulong WriteString(BackedKernelMemory test, ulong offset, string text)
    {
        var address = test.Output + offset;
        Assert.True(test.Context.Memory.TryWrite(address, Encoding.ASCII.GetBytes(text + "\0")));
        return address;
    }

    private static int SetName(BackedKernelMemory test, ulong address, ulong length, ulong nameAddress)
    {
        test.Context[CpuRegister.Rdi] = address;
        test.Context[CpuRegister.Rsi] = length;
        test.Context[CpuRegister.Rdx] = nameAddress;
        return KernelMemoryCompatExports.KernelSetVirtualRangeName(test.Context);
    }

    [Fact]
    public void UnmappedAddressIsAccessDeniedAndFindNextReturnsTheNextRange()
    {
        using var test = new BackedKernelMemory();
        var address = test.Reserve(0x20000);
        Assert.Equal(0, test.Unmap(address, 0x10000));

        Assert.Equal(Eacces, TryQuery(test, address, findNext: false, out _));
        var next = Query(test, address, findNext: true);
        Assert.Equal((address + 0x10000, address + 0x20000), (next.Start, next.End));
        Assert.Equal(0, next.State & 0x10);
    }

    [Fact]
    public void VirtualQueryRejectsUnknownFlagsAndShortBuffers()
    {
        using var test = new BackedKernelMemory();
        var address = test.Reserve(0x10000);
        test.Context[CpuRegister.Rdi] = address;
        test.Context[CpuRegister.Rsi] = 2;
        test.Context[CpuRegister.Rdx] = test.Output + 0x100;
        test.Context[CpuRegister.Rcx] = 0x48;
        Assert.Equal(Einval, KernelMemoryCompatExports.KernelVirtualQuery(test.Context));
        test.Context[CpuRegister.Rsi] = 0;
        test.Context[CpuRegister.Rcx] = 0x47;
        Assert.Equal(Einval, KernelMemoryCompatExports.KernelVirtualQuery(test.Context));
    }

    [Fact]
    public void QueryMemoryProtectionReportsAnExclusiveEndAndRejectsUnmappedAddresses()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0x8000);
        var address = test.Map(0, 0x8000);
        test.Context[CpuRegister.Rdi] = address + 0x4000;
        test.Context[CpuRegister.Rsi] = test.Output + 0x200;
        test.Context[CpuRegister.Rdx] = test.Output + 0x208;
        test.Context[CpuRegister.Rcx] = test.Output + 0x210;
        Assert.Equal(0, KernelMemoryCompatExports.KernelQueryMemoryProtection(test.Context));
        Assert.True(test.Context.TryReadUInt64(test.Output + 0x200, out var start));
        Assert.True(test.Context.TryReadUInt64(test.Output + 0x208, out var end));
        Assert.True(test.Context.TryReadUInt32(test.Output + 0x210, out var protection));
        Assert.Equal((address, address + 0x8000, 0x33u), (start, end, protection));

        Assert.Equal(0, test.Unmap(address, 0x8000));
        test.Context[CpuRegister.Rdi] = address;
        Assert.Equal(Eacces, KernelMemoryCompatExports.KernelQueryMemoryProtection(test.Context));
    }

    [Fact]
    public void SubRangeNameSplitsTheMappingAndUnmapDropsIt()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0xC000);
        var address = test.Map(0, 0xC000);
        var name = WriteString(test, 0x300, "SceLibcHeap");
        Assert.Equal(0, SetName(test, address + 0x4000, 0x4000, name));

        Assert.Equal("direct", Query(test, address).Name);
        var named = Query(test, address + 0x4000);
        Assert.Equal(("SceLibcHeap", address + 0x4000, address + 0x8000), (named.Name, named.Start, named.End));
        Assert.Equal("direct", Query(test, address + 0x8000).Name);

        Assert.Equal(0, test.Unmap(address, 0xC000));
        Assert.Equal(address, test.Map(0, 0xC000, address));
        Assert.Equal("direct", Query(test, address + 0x4000).Name);
    }

    [Fact]
    public void NameArgumentsAreValidated()
    {
        using var test = new BackedKernelMemory();
        var address = test.Reserve(0x10000);
        Assert.Equal(Efault, SetName(test, address, 0x4000, 0));
        Assert.Equal(Einval, SetName(test, address, 0, WriteString(test, 0x300, "x")));
        Assert.Equal(EnameTooLong, SetName(test, address, 0x4000, WriteString(test, 0x300, new string('n', 32))));
        Assert.Equal(0, SetName(test, address, 0x4000, WriteString(test, 0x300, new string('n', 31))));
        Assert.Equal(new string('n', 31), Query(test, address).Name);

        test.Context[CpuRegister.Rdi] = address;
        test.Context[CpuRegister.Rsi] = 0x4000;
        Assert.Equal(0, KernelMemoryCompatExports.KernelClearVirtualRangeName(test.Context));
        Assert.Equal(string.Empty, Query(test, address).Name);
    }

    [Fact]
    public void NamedDirectAndFlexibleMappingsCarryTheirNames()
    {
        using var test = new BackedKernelMemory();
        var previous = KernelMemoryCompatExports.SetFlexibleBackingForTests(new FlexibleBackingPool(0x4000000, 0x4000000));
        try
        {
            test.Allocate(0, 0x4000);
            var name = WriteString(test, 0x300, "named-direct");
            // The name is the 7th argument: the first stack slot above the return address.
            var stack = test.Output + 0x800;
            Assert.True(test.Context.TryWriteUInt64(stack + 8, name));
            Assert.True(test.Context.TryWriteUInt64(test.Output, 0));
            test.Context[CpuRegister.Rsp] = stack;
            test.Context[CpuRegister.Rdi] = test.Output;
            test.Context[CpuRegister.Rsi] = 0x4000;
            test.Context[CpuRegister.Rdx] = 0x33;
            test.Context[CpuRegister.Rcx] = 0;
            test.Context[CpuRegister.R8] = 0;
            test.Context[CpuRegister.R9] = 0;
            Assert.Equal(0, KernelMemoryCompatExports.KernelMapNamedDirectMemory(test.Context));
            Assert.True(test.Context.TryReadUInt64(test.Output, out var direct));
            Assert.Equal("named-direct", Query(test, direct).Name);

            var flexibleName = WriteString(test, 0x340, "named-flexible");
            Assert.True(test.Context.TryWriteUInt64(test.Output, 0));
            test.Context[CpuRegister.Rdi] = test.Output;
            test.Context[CpuRegister.Rsi] = 0x4000;
            test.Context[CpuRegister.Rdx] = 0x3;
            test.Context[CpuRegister.Rcx] = 0;
            test.Context[CpuRegister.R8] = flexibleName;
            Assert.Equal(0, KernelMemoryCompatExports.KernelMapNamedFlexibleMemory(test.Context));
            Assert.True(test.Context.TryReadUInt64(test.Output, out var flexible));
            var info = Query(test, flexible);
            Assert.Equal(("named-flexible", (byte)0x11), (info.Name, info.State));
        }
        finally
        {
            KernelMemoryCompatExports.ResetBackingMappings(test.Memory);
            KernelMemoryCompatExports.SetFlexibleBackingForTests(previous);
        }
    }

    [Fact]
    public void MemoryTypeIsPerMappingAndLeavesTheAllocationAndAliasesAlone()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0x8000);
        var first = test.Map(0, 0x8000);
        var alias = test.Map(0, 0x8000);

        test.Context[CpuRegister.Rdi] = first + 0x4000;
        test.Context[CpuRegister.Rsi] = 0x4000;
        test.Context[CpuRegister.Rdx] = 11;
        test.Context[CpuRegister.Rcx] = 0x33;
        Assert.Equal(0, KernelMemoryCompatExports.KernelMtypeprotect(test.Context));

        Assert.Equal(0, Query(test, first).MemoryType);
        var retyped = Query(test, first + 0x4000);
        Assert.Equal((11, first + 0x4000, 0x4000UL), (retyped.MemoryType, retyped.Start, retyped.Offset));
        Assert.Equal(0, Query(test, alias + 0x4000).MemoryType);

        test.Context[CpuRegister.Rdi] = 0;
        test.Context[CpuRegister.Rsi] = 0;
        test.Context[CpuRegister.Rdx] = test.Output + 0x200;
        test.Context[CpuRegister.Rcx] = 24;
        Assert.Equal(0, KernelMemoryCompatExports.KernelDirectMemoryQuery(test.Context));
        Assert.True(test.Context.TryReadUInt32(test.Output + 0x210, out var allocationType));
        Assert.Equal(0u, allocationType);

        test.Context[CpuRegister.Rdi] = first;
        test.Context[CpuRegister.Rsi] = 0x4000;
        test.Context[CpuRegister.Rdx] = 21;
        test.Context[CpuRegister.Rcx] = 0x33;
        Assert.Equal(Einval, KernelMemoryCompatExports.KernelMtypeprotect(test.Context));
    }

    [Fact]
    public void MapDirectMemory2AppliesItsMemoryType()
    {
        using var test = new BackedKernelMemory();
        test.Allocate(0, 0x4000);
        var stack = test.Output + 0x800;
        Assert.True(test.Context.TryWriteUInt64(stack + 8, 0));
        Assert.True(test.Context.TryWriteUInt64(test.Output, 0));
        test.Context[CpuRegister.Rsp] = stack;
        test.Context[CpuRegister.Rdi] = test.Output;
        test.Context[CpuRegister.Rsi] = 0x4000;
        test.Context[CpuRegister.Rdx] = 12;
        test.Context[CpuRegister.Rcx] = 0x33;
        test.Context[CpuRegister.R8] = 0;
        test.Context[CpuRegister.R9] = 0;
        Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory2(test.Context));
        Assert.True(test.Context.TryReadUInt64(test.Output, out var address));
        var info = Query(test, address);
        Assert.Equal((12, (byte)0x12), (info.MemoryType, info.State));

        test.Context[CpuRegister.Rdx] = 99;
        Assert.Equal(Einval, KernelMemoryCompatExports.KernelMapDirectMemory2(test.Context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NeighbouringFlexibleMappingsCoalesceUnlessNoCoalesce(bool noCoalesce)
    {
        using var test = new BackedKernelMemory();
        var previous = KernelMemoryCompatExports.SetFlexibleBackingForTests(new FlexibleBackingPool(0x4000000, 0x4000000));
        try
        {
            var address = test.Reserve(0x10000);
            foreach (var offset in new ulong[] { 0, 0x4000 })
            {
                Assert.True(test.Context.TryWriteUInt64(test.Output, address + offset));
                test.Context[CpuRegister.Rdi] = test.Output;
                test.Context[CpuRegister.Rsi] = 0x4000;
                test.Context[CpuRegister.Rdx] = 0x3;
                test.Context[CpuRegister.Rcx] = 0x10UL | (noCoalesce ? 0x400000UL : 0);
                Assert.Equal(0, KernelMemoryCompatExports.KernelMapFlexibleMemory(test.Context));
            }

            var info = Query(test, address);
            Assert.Equal((address, address + (noCoalesce ? 0x4000UL : 0x8000UL)), (info.Start, info.End));
            // The reserved tail stays its own entry.
            var tail = Query(test, address + 0x8000);
            Assert.Equal((address + 0x8000, address + 0x10000), (tail.Start, tail.End));
        }
        finally
        {
            KernelMemoryCompatExports.ResetBackingMappings(test.Memory);
            KernelMemoryCompatExports.SetFlexibleBackingForTests(previous);
        }
    }

    [Fact]
    public void DirectMappingsKeepTheirOwnEntries()
    {
        using var test = new BackedKernelMemory();
        var address = test.Reserve(0x8000);
        test.Allocate(0, 0x8000);
        Assert.Equal(address, test.Map(0, 0x4000, address));
        Assert.Equal(address + 0x4000, test.Map(0x4000, 0x4000, address + 0x4000));
        var info = Query(test, address);
        Assert.Equal((address, address + 0x4000), (info.Start, info.End));
    }

    [Fact]
    public void PrtApertureSetsTheGpuPrtBit()
    {
        using var test = new BackedKernelMemory();
        var address = test.Reserve(0x10000);
        Assert.Equal(0, Query(test, address).State & 0x20);
        SetPrtAperture(test, 0, address, 0x10000);
        try
        {
            Assert.Equal(0x20, Query(test, address).State & 0x20);
        }
        finally
        {
            SetPrtAperture(test, 0, address, 0);
        }
    }

    internal static void SetPrtAperture(BackedKernelMemory test, int index, ulong address, ulong size)
    {
        test.Context[CpuRegister.Rdi] = (ulong)index;
        test.Context[CpuRegister.Rsi] = address;
        test.Context[CpuRegister.Rdx] = size;
        Assert.Equal(0, KernelRuntimeCompatExports.KernelSetPrtAperture(test.Context));
    }

    [Fact]
    public void IsStackClearsBoundsForMappedMemoryAndRejectsUnmappedAddresses()
    {
        using var test = new BackedKernelMemory();
        var address = test.Reserve(0x10000);
        Assert.True(test.Context.TryWriteUInt64(test.Output + 0x200, 0xDEAD));
        Assert.True(test.Context.TryWriteUInt64(test.Output + 0x208, 0xBEEF));
        test.Context[CpuRegister.Rdi] = address;
        test.Context[CpuRegister.Rsi] = test.Output + 0x200;
        test.Context[CpuRegister.Rdx] = test.Output + 0x208;
        Assert.Equal(0, KernelMemoryCompatExports.KernelIsStack(test.Context));
        Assert.True(test.Context.TryReadUInt64(test.Output + 0x200, out var start));
        Assert.True(test.Context.TryReadUInt64(test.Output + 0x208, out var end));
        Assert.Equal((0UL, 0UL), (start, end));

        Assert.Equal(0, test.Unmap(address, 0x10000));
        Assert.Equal(Eacces, KernelMemoryCompatExports.KernelIsStack(test.Context));
    }
}
