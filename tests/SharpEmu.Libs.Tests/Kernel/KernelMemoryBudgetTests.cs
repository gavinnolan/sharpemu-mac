// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.Core.Loader;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelMemoryBudgetTests
{
    private const ulong GuestMemoryBase = 0x1_0000_0000;

    [Theory]
    [InlineData("{\"kernel\":{\"flexibleMemorySize\":268435456}}", 268435456UL)]
    [InlineData("{\"kernel\":{\"flexibleMemorySize\":\"0x10000000\"}}", 0x10000000UL)]
    [InlineData("{\"kernel\":{\"flexibleMemorySize\":\"1048576\"}}", 1048576UL)]
    [InlineData("{\"kernel\":{}}", null)]
    [InlineData("{\"titleId\":\"PPSA00000\"}", null)]
    [InlineData("{\"kernel\":{\"flexibleMemorySize\":-1}}", null)]
    [InlineData("not json", null)]
    public void ParamJsonFlexibleMemorySizeIsRead(string json, ulong? expected)
    {
        Assert.Equal(expected, Ps5ParamJsonReader.TryReadFlexibleMemorySize(Encoding.UTF8.GetBytes(json)));
    }

    private static (ulong Direct, ulong Configured, ulong Available) ReadBudget()
    {
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);
        Assert.Equal(0, KernelMemoryCompatExports.KernelGetDirectMemorySize(context));
        var direct = context[CpuRegister.Rax];
        context[CpuRegister.Rdi] = GuestMemoryBase + 0x100;
        Assert.Equal(0, KernelMemoryCompatExports.KernelConfiguredFlexibleMemorySize(context));
        Assert.True(context.TryReadUInt64(GuestMemoryBase + 0x100, out var configured));
        Assert.Equal(0, KernelMemoryCompatExports.KernelAvailableFlexibleMemorySize(context));
        Assert.True(context.TryReadUInt64(GuestMemoryBase + 0x100, out var available));
        return (direct, configured, available);
    }

    [Fact]
    public void TitleBudgetSetsFlexibleMemoryAndDefaultsApplyWithoutOne()
    {
        try
        {
            KernelMemoryCompatExports.ConfigureMemoryBudget(256UL << 20);
            Assert.Equal((13824UL << 20, 256UL << 20, 256UL << 20), ReadBudget());

            KernelMemoryCompatExports.ConfigureMemoryBudget(null);
            Assert.Equal((13824UL << 20, 448UL << 20, 448UL << 20), ReadBudget());

            // More than the backing object holds beside direct memory is clamped to what fits.
            KernelMemoryCompatExports.ConfigureMemoryBudget(64UL << 30);
            var room = GuestMemoryLayout.BackingBytes - (13824UL << 20);
            Assert.Equal(room, ReadBudget().Configured);
        }
        finally
        {
            KernelMemoryCompatExports.ConfigureMemoryBudget(null);
        }
    }

    [Fact]
    public void BudgetIsNotChangedUnderLiveAllocationsAndBoundsDirectAllocation()
    {
        var context = new CpuContext(new FakeCpuMemory(GuestMemoryBase, 0x1000), Generation.Gen5);
        try
        {
            KernelMemoryCompatExports.ConfigureMemoryBudget(null);
            var direct = ReadBudget().Direct;
            context[CpuRegister.Rdi] = 0;
            context[CpuRegister.Rsi] = direct + 0x4000;
            context[CpuRegister.Rdx] = 0x4000;
            context[CpuRegister.Rcx] = 0;
            context[CpuRegister.R8] = 0;
            context[CpuRegister.R9] = GuestMemoryBase + 0x200;
            Assert.Equal(0, KernelMemoryCompatExports.KernelAllocateDirectMemory(context));

            KernelMemoryCompatExports.ConfigureMemoryBudget(128UL << 20);
            Assert.Equal(448UL << 20, ReadBudget().Configured);

            // A search past the budget cannot hand out memory beyond it.
            context[CpuRegister.Rdi] = direct;
            context[CpuRegister.Rsi] = direct + 0x10000;
            Assert.Equal(unchecked((int)0x80020023), KernelMemoryCompatExports.KernelAllocateDirectMemory(context));
        }
        finally
        {
            context[CpuRegister.Rdi] = 0;
            context[CpuRegister.Rsi] = 0x4000;
            Assert.Equal(0, KernelMemoryCompatExports.KernelCheckedReleaseDirectMemory(context));
            KernelMemoryCompatExports.ConfigureMemoryBudget(null);
        }
    }
}
