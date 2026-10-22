// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

public sealed partial class MetalRenderHostTests
{
    [Theory]
    [InlineData(EndOfPipeWriteKind.ClockWrite, false, false, false)]
    [InlineData(EndOfPipeWriteKind.ClockWrite, false, true, false)]
    [InlineData(EndOfPipeWriteKind.ClockWrite, true, false, true)]
    [InlineData(EndOfPipeWriteKind.ClockWrite, true, true, false)]
    [InlineData(EndOfPipeWriteKind.ClockWriteBack, false, false, false)]
    [InlineData(EndOfPipeWriteKind.ClockWriteBack, false, true, false)]
    [InlineData(EndOfPipeWriteKind.ClockWriteBack, true, false, true)]
    [InlineData(EndOfPipeWriteKind.ClockWriteBack, true, true, false)]
    [InlineData(EndOfPipeWriteKind.Write32, false, false, false)]
    [InlineData(EndOfPipeWriteKind.Write32, false, true, false)]
    [InlineData(EndOfPipeWriteKind.Write32, true, false, true)]
    [InlineData(EndOfPipeWriteKind.Write32, true, true, true)]
    public void V2DefersClockLabelsUnlessParseOptOut(EndOfPipeWriteKind kind, bool defersLabels, bool publishClocksWhenParsed, bool expected) =>
        Assert.Equal(expected, SharpEmu.Gpu.Metal.V2.MetalRenderHost.DefersLabelKind(kind, defersLabels, publishClocksWhenParsed));

    [Fact]
    public void V2ClockWriteLandsInOrderWithTheLabelBeforeIt()
    {
        var runner = new StreamRunner();
        runner.Host.DefersEndOfPipeLabels = true;
        runner.Host.DefersKind = k => SharpEmu.Gpu.Metal.V2.MetalRenderHost.DefersLabelKind(k, true, false);
        var l1 = StreamRunner.LabelAddress;
        var l2 = StreamRunner.LabelAddress + 0x100;
        var label = StreamRunner.Packet(PacketOpcode.EventWriteEndOfPipe, 0x504u,
            (uint)l1, (uint)(l1 >> 32) | (1u << 29), 1, 0);
        var clock = StreamRunner.Packet(PacketOpcode.EventWriteEndOfPipe, 0x504u,
            (uint)l2, (uint)(l2 >> 32) | (4u << 29), 0, 0);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(label, clock));
        Assert.Equal(0u, runner.Host.ReadDword(l1));
        Assert.Equal(0u, runner.Host.ReadDword(l2));
        Assert.Equal(
            new[] { (EndOfPipeWriteKind.Write32, l1), (EndOfPipeWriteKind.ClockWrite, l2) },
            runner.Host.EndOfPipeWrites.Select(w => (w.Kind, w.Destination)).ToArray());
    }

    // COPY_DATA from the GPU reference clock (raw source 4 with the engine bit) to memory (raw 1).
    private static uint[] CopyReferenceClock(ulong destination, bool is64Bit) =>
        StreamRunner.Packet(PacketOpcode.CopyData, 4u | (1u << 8) | (is64Bit ? 1u << 16 : 0) | (1u << 30),
            0, 0, (uint)destination, (uint)(destination >> 32));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void V2ReferenceClockCopyLandsInOrderWithTheLabelBeforeIt(bool is64Bit)
    {
        var runner = new StreamRunner();
        runner.Host.DefersEndOfPipeLabels = true;
        runner.Host.DefersKind = k => SharpEmu.Gpu.Metal.V2.MetalRenderHost.DefersLabelKind(k, true, false);
        var l1 = StreamRunner.LabelAddress;
        var l2 = StreamRunner.LabelAddress + 0x100;
        var label = StreamRunner.Packet(PacketOpcode.EventWriteEndOfPipe, 0x504u,
            (uint)l1, (uint)(l1 >> 32) | (1u << 29), 1, 0);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(label, CopyReferenceClock(l2, is64Bit)));

        Assert.Equal(0u, runner.Host.ReadDword(l1));
        Assert.Equal(0UL, runner.Host.ReadQword(l2));
        var clockKind = is64Bit ? EndOfPipeWriteKind.ClockWrite : EndOfPipeWriteKind.Write32;
        Assert.Equal(
            new[] { (EndOfPipeWriteKind.Write32, l1), (clockKind, l2) },
            runner.Host.EndOfPipeWrites.Select(w => (w.Kind, w.Destination)).ToArray());
        var clock = runner.Host.EndOfPipeWrites[1].Value;
        Assert.NotEqual(0UL, clock);
        if (!is64Bit)
        {
            Assert.True(clock <= uint.MaxValue);
        }

        var history = CommandProcessorWriteHistory.Find(l2, is64Bit ? 8UL : 4UL)
            .Where(e => e.Kind == "COPY_DATA_CLOCK" && e.Value == clock).ToArray();
        var entry = Assert.Single(history);
        Assert.True(entry.Deferred);
        Assert.Equal(is64Bit ? 8 : 4, entry.Size);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void V2ReferenceClockCopyIsWrittenAtParseWithTheClockOptOut(bool is64Bit)
    {
        var runner = new StreamRunner();
        runner.Host.DefersEndOfPipeLabels = true;
        runner.Host.DefersKind = k => SharpEmu.Gpu.Metal.V2.MetalRenderHost.DefersLabelKind(k, true, true);
        var address = StreamRunner.LabelAddress + 0x200;
        runner.Host.WriteQword(address, 0);

        Assert.Equal(SubmissionProgress.Complete, runner.Run(CopyReferenceClock(address, is64Bit)));

        Assert.Empty(runner.Host.EndOfPipeWrites);
        Assert.NotEqual(0u, runner.Host.ReadDword(address));
        if (!is64Bit)
        {
            Assert.Equal(0u, runner.Host.ReadDword(address + 4));
        }
    }

}
