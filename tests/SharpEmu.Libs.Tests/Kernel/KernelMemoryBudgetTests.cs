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
            // Direct is the 12736 MiB title total less flexible: a retail PS5 reports 12288 MiB
            // (0x3_0000_0000) at the 448 MiB default.
            Assert.Equal((12480UL << 20, 256UL << 20, 256UL << 20), ReadBudget());

            KernelMemoryCompatExports.ConfigureMemoryBudget(null);
            Assert.Equal((0x3_0000_0000UL, 448UL << 20, 448UL << 20), ReadBudget());

            // More than the backing object holds beside direct memory is clamped to what fits.
            KernelMemoryCompatExports.ConfigureMemoryBudget(64UL << 30);
            var room = GuestMemoryLayout.BackingBytes - (12288UL << 20);
            var clamped = ReadBudget();
            Assert.Equal(room, clamped.Configured);
            Assert.Equal(KernelMemoryCompatExports.TitleMemoryBytes - room, clamped.Direct);
        }
        finally
        {
            KernelMemoryCompatExports.ConfigureMemoryBudget(null);
        }
    }

    [Theory]
    [InlineData(448UL, null, 12288UL)]
    [InlineData(320UL, null, 12416UL)]
    [InlineData(448UL, "", 12288UL)]
    [InlineData(448UL, "0", 12288UL)]
    [InlineData(448UL, "junk", 12288UL)]
    [InlineData(448UL, "8192", 8192UL)]
    [InlineData(448UL, "20000", 16384UL)] // clamped: the backing object keeps the flexible share
    public void DirectMemoryIsTitleTotalLessFlexibleUnlessOverridden(ulong flexibleMiB, string? overrideMiB, ulong expectedMiB)
    {
        Assert.Equal(expectedMiB << 20, KernelMemoryCompatExports.ResolveDirectMemoryBytes(flexibleMiB << 20, overrideMiB));
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
