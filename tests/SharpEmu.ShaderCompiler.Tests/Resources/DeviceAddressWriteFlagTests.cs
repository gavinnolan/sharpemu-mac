// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

/// <summary>
/// Whether a program stores through the device-address page table, and not merely reads
/// through it. A backend that keeps guest memory as the GPU's memory has to treat such a
/// submission as writing guest bytes whose range it cannot always name; one that only
/// reads through the table owes nothing.
/// </summary>
public sealed class DeviceAddressWriteFlagTests
{
    [Fact]
    public void AGlobalLoadReadsThroughThePageTableWithoutWritingIt()
    {
        var plan = Extract(Program(
            Vop1(0, "VMovB32", 0, Operand(0)),
            GlobalAccess(4, "GlobalLoadDword", 0, offset: 8),
            EndProgram(12)));

        Assert.True(plan.Info.UsesDeviceAddresses);
        Assert.False(plan.Info.WritesDeviceAddresses);
    }

    [Fact]
    public void AGlobalStoreWritesThroughThePageTable()
    {
        var plan = Extract(Program(
            Vop1(0, "VMovB32", 0, Operand(0)),
            GlobalAccess(4, "GlobalStoreDword", 0, offset: 8),
            EndProgram(12)));

        Assert.True(plan.Info.UsesDeviceAddresses);
        Assert.True(plan.Info.WritesDeviceAddresses);
    }

    [Fact]
    public void TheFlagSurvivesACloneOfTheResourceInfo()
    {
        var plan = Extract(Program(
            Vop1(0, "VMovB32", 0, Operand(0)),
            GlobalAccess(4, "GlobalStoreDword", 0, offset: 8),
            EndProgram(12)));

        var clone = plan.Info.Clone();
        Assert.True(clone.UsesDeviceAddresses);
        Assert.True(clone.WritesDeviceAddresses);
    }
}
