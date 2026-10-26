// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

/// <summary>
/// The write history records only when its diagnostics switch is on (SHARPEMU_CP_HISTORY=1); the
/// tests that read it back from the command processor turn it on for the whole run.
/// </summary>
internal static class CommandProcessorWriteHistoryFixture
{
    [ModuleInitializer]
    internal static void Initialize() => CommandProcessorWriteHistory.Enabled = true;
}

public sealed class CommandProcessorWriteHistorySwitchTests
{
    private static bool Enabled(params (string Name, string Value)[] environment) =>
        CommandProcessorWriteHistory.EnabledByEnvironment(name =>
            environment.Where(pair => pair.Name == name).Select(pair => pair.Value).FirstOrDefault());

    [Fact]
    public void TheHistoryIsOffUnlessADiagnosticsSwitchAsksForIt()
    {
        Assert.False(Enabled());
        Assert.False(Enabled(("SHARPEMU_CP_HISTORY", "0")));
        Assert.True(Enabled(("SHARPEMU_CP_HISTORY", "1")));
        Assert.True(Enabled(("SHARPEMU_CP_WATCH", "7F0000001000")));
        Assert.True(Enabled(("SHARPEMU_GPU_CLOBBER_DIAG", "1")));
    }

    [Fact]
    public void AnEnabledHistoryReturnsWhatWasRecorded()
    {
        Assert.True(CommandProcessorWriteHistory.Enabled);
        const ulong address = 0x7F00_1230_0000UL;
        CommandProcessorWriteHistory.Record(0, address, 4, 0x22, "SWITCH_ON", deferred: false);
        var entry = Assert.Single(CommandProcessorWriteHistory.Find(address, 4));
        Assert.Equal("SWITCH_ON", entry.Kind);
        Assert.Equal(0x22UL, entry.Value);
    }
}
