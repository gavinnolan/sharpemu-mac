// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

/// <summary>
/// On a host that defers end-of-pipe labels, the command-processor stores of every queue
/// are handed to the host to land in record order with them instead of being written as
/// parsed (SHARPEMU_METAL_V2_CP_WRITES=parse restores the latter).
/// </summary>
public sealed class CommandProcessorWriteOrderTests
{
    private const ulong Label = StreamRunner.LabelAddress;
    private const ulong Data = StreamRunner.DataAddress;

    private static uint[] Eop32(ulong address, uint value) =>
        StreamRunner.Packet(PacketOpcode.EventWriteEndOfPipe, 0x504u,
            (uint)address, (uint)(address >> 32) | (1u << 29), value, 0);

    private static uint[] WriteData(ulong destination, params uint[] values)
    {
        var payload = new uint[3 + values.Length];
        payload[0] = 5u << 8;
        payload[1] = StreamRunner.Low(destination);
        payload[2] = StreamRunner.High(destination);
        values.CopyTo(payload, 3);
        return StreamRunner.Packet(PacketOpcode.WriteData, payload);
    }

    private static uint[] DmaData(uint sourceSelect, ulong source, ulong destination, uint bytes) =>
        StreamRunner.Packet(
            PacketOpcode.DmaData,
            (sourceSelect & 3) << 29,
            StreamRunner.Low(source), StreamRunner.High(source),
            StreamRunner.Low(destination), StreamRunner.High(destination),
            bytes);

    private static uint[] CopyDataImmediate64(ulong value, ulong destination) =>
        StreamRunner.Packet(
            PacketOpcode.CopyData,
            5u | (1u << 8) | (1u << 16),
            StreamRunner.Low(value), StreamRunner.High(value),
            StreamRunner.Low(destination), StreamRunner.High(destination));

    private static StreamRunner OrderedRunner(int queueId = 0, bool ordered = true)
    {
        var runner = new StreamRunner(queueId);
        runner.Host.DefersEndOfPipeLabels = true;
        runner.Host.DefersKind = k => SharpEmu.Gpu.Metal.V2.MetalRenderHost.DefersLabelKind(k, true, false);
        runner.Host.DefersCommandProcessorWrites = ordered;
        return runner;
    }

    [Fact]
    public void AWriteDataAfterALabelIsRecordedBehindItAndNotWrittenAtParse()
    {
        var runner = OrderedRunner();
        Assert.Equal(SubmissionProgress.Complete, runner.Run(Eop32(Label, 1), WriteData(Data, 0x11, 0x22)));

        Assert.Equal(0u, runner.Host.ReadDword(Label));
        Assert.Equal(0UL, runner.Host.ReadQword(Data));
        Assert.Equal(new[] { "eop Write32", $"cp_write WRITE_DATA {Data:X} 8" },
            runner.Host.Calls.Where(c => c.StartsWith("eop", StringComparison.Ordinal) || c.StartsWith("cp_write", StringComparison.Ordinal)));
        var write = Assert.Single(runner.Host.CommandProcessorWrites);
        Assert.Equal(new byte[] { 0x11, 0, 0, 0, 0x22, 0, 0, 0 }, write.Bytes);
    }

    [Fact]
    public void TheParseOptOutWritesTheWriteDataAsParsed()
    {
        var runner = OrderedRunner(ordered: false);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(Eop32(Label, 1), WriteData(Data, 0x33)));

        Assert.Equal(0u, runner.Host.ReadDword(Label));
        Assert.Equal(0x33u, runner.Host.ReadDword(Data));
        Assert.Empty(runner.Host.CommandProcessorWrites);
    }

    [Fact]
    public void DmaFillsCopiesAndCopyDataImmediatesAreRecordedInOrder()
    {
        var runner = OrderedRunner();
        runner.Host.WriteQword(Data + 0x40, 0xFEED);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(
            DmaData(2, 0xAB17, Data, 4),
            DmaData(0, Data + 0x40, Data + 0x20, 8),
            CopyDataImmediate64(0x1234_5678_9ABC, Data + 0x60)));

        Assert.Equal(0u, runner.Host.ReadDword(Data));
        Assert.Equal(0UL, runner.Host.ReadQword(Data + 0x20));
        Assert.Equal(0UL, runner.Host.ReadQword(Data + 0x60));
        Assert.Equal(
            new[]
            {
                $"fill_in_order {Data:X} 4 0000AB17",
                $"copy_in_order {Data + 0x20:X} {Data + 0x40:X} 8",
                $"cp_write COPY_DATA_IMM {Data + 0x60:X} 8",
            },
            runner.Host.Calls.Where(c => c.Contains("_in_order", StringComparison.Ordinal) || c.StartsWith("cp_write", StringComparison.Ordinal)));
        var fill = Assert.Single(CommandProcessorWriteHistory.Find(Data, sizeof(uint)), e => e.Kind == "DMA_FILL" && e.Value == 0xAB17);
        Assert.True(fill.Deferred);
    }

    private static uint[] ZPassDump(ulong address) =>
        StreamRunner.Packet(PacketOpcode.EventWrite, 0x39 | (1u << 8), StreamRunner.Low(address), StreamRunner.High(address));

    // UE4 zeroes an occlusion query's 16 depth-block pairs with WRITE_DATA right before the
    // begin dump. Dumps written as parsed reached memory first, and the deferred clear then
    // wiped them, so every query read as not ready with no samples and the props it tested
    // (Poppy Playtime's TV, monitor, VCR) were culled.
    [Fact]
    public void OcclusionDumpsLandInOrderAfterTheClearRecordedBeforeThem()
    {
        var runner = OrderedRunner();
        Assert.Equal(SubmissionProgress.Complete, runner.Run(
            WriteData(Data, new uint[64]),
            ZPassDump(Data),
            ZPassDump(Data + 8)));

        Assert.Equal(0UL, runner.Host.ReadQword(Data));
        Assert.Equal(1 + 32, runner.Host.CommandProcessorWrites.Count);
        foreach (var (_, address, bytes) in runner.Host.CommandProcessorWrites)
        {
            Assert.True(runner.Host.GuestMemory.TryWrite(address, bytes));
        }

        const ulong ready = 1UL << 63;
        for (var block = 0UL; block < 16; block++)
        {
            var begin = runner.Host.ReadQword(Data + (block * 16));
            var end = runner.Host.ReadQword(Data + (block * 16) + 8);
            Assert.NotEqual(0UL, begin & ready);
            Assert.NotEqual(0UL, end & ready);
            Assert.True((end & ~ready) > (begin & ~ready));
        }
    }

    [Fact]
    public void TheParseOptOutWritesOcclusionDumpsAsParsed()
    {
        var runner = OrderedRunner(ordered: false);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(ZPassDump(Data)));

        Assert.NotEqual(0UL, runner.Host.ReadQword(Data) & (1UL << 63));
        Assert.Empty(runner.Host.CommandProcessorWrites);
    }

    // GTA V's culling queue writes each mesh's descriptor table with WRITE_DATA into memory
    // an earlier dispatch of the same queue stores into. Written as parsed, the table was in
    // place before that dispatch ran, and its stores then landed over the descriptors.
    [Fact]
    public void ComputeQueuesRecordTheirWritesInOrderToo()
    {
        var runner = OrderedRunner(queueId: 1);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(WriteData(Data + 0x80, 0x44), DmaData(2, 0x55, Data + 0x90, 4)));

        Assert.Equal(0u, runner.Host.ReadDword(Data + 0x80));
        Assert.Equal(0u, runner.Host.ReadDword(Data + 0x90));
        var write = Assert.Single(runner.Host.CommandProcessorWrites);
        Assert.Equal(new byte[] { 0x44, 0, 0, 0 }, write.Bytes);
        Assert.Contains($"fill_in_order {Data + 0x90:X} 4 00000055", runner.Host.Calls);
    }

    [Fact]
    public void TheParseOptOutLeavesComputeQueueWritesToTheParser()
    {
        var runner = OrderedRunner(queueId: 1, ordered: false);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(WriteData(Data + 0x80, 0x44), DmaData(2, 0x55, Data + 0x90, 4)));

        Assert.Equal(0x44u, runner.Host.ReadDword(Data + 0x80));
        Assert.Equal(0x55u, runner.Host.ReadDword(Data + 0x90));
        Assert.Empty(runner.Host.CommandProcessorWrites);
        Assert.DoesNotContain(runner.Host.Calls, c => c.Contains("_in_order", StringComparison.Ordinal));
    }
}
