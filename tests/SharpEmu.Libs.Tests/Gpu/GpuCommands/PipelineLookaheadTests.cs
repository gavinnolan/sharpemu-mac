// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.GpuCommands;

public sealed class PipelineLookaheadTests
{
    private const uint ComputePgmLo = 0x20C;
    private const ulong CalledBuffer = RecordingCommandStreamHost.MemoryBase + 0xC000;
    private const ulong ChainedBuffer = RecordingCommandStreamHost.MemoryBase + 0xD000;

    private static uint[] SetProgram(ulong address) =>
        StreamRunner.Packet(PacketOpcode.SetShaderRegister, ComputePgmLo, (uint)(address >> 8), (uint)(address >> 40));

    private static uint[] Dispatch(uint groupsX) => StreamRunner.Packet(PacketOpcode.DispatchDirect, groupsX, 1, 1, 1);

    private static uint[] IndirectBuffer(ulong address, uint dwords, bool chain) =>
        StreamRunner.Packet(PacketOpcode.IndirectBuffer, StreamRunner.Low(address), StreamRunner.High(address),
            dwords | (chain ? 1u << 20 : 0));

    [Fact]
    public void TheDispatchesAfterOneWaitingOnItsPipelineAreHandedOverWithTheirOwnProgramsAndNotRun()
    {
        var runner = new StreamRunner();
        runner.Host.PrefetchesPipelines = true;
        runner.Host.PipelineNotReady.Enqueue(true);

        var progress = runner.Run(
            SetProgram(0x1_0000_0100), Dispatch(1),
            SetProgram(0x1_0000_0200), Dispatch(2),
            SetProgram(0x1_0000_0300), Dispatch(3));

        Assert.Equal(SubmissionProgress.Blocked, progress);
        Assert.Equal([(0x1_0000_0200UL, 2u), (0x1_0000_0300UL, 3u)], runner.Host.PrefetchedDispatches);
        Assert.Single(runner.Host.Calls, call => call.StartsWith("dispatch", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLookAheadFollowsACalledBufferAndAChainAndADispatchItCoveredDoesNotReadAgain()
    {
        var runner = new StreamRunner();
        runner.Host.PrefetchesPipelines = true;
        runner.Host.WriteWords(CalledBuffer, StreamRunner.Concat(SetProgram(0x1_0000_0400), Dispatch(4)));
        runner.Host.WriteWords(ChainedBuffer, StreamRunner.Concat(SetProgram(0x1_0000_0500), Dispatch(5)));
        runner.Host.PipelineNotReady.Enqueue(true);
        runner.Host.PipelineNotReady.Enqueue(false);
        runner.Host.PipelineNotReady.Enqueue(true);

        var calledDwords = (uint)(SetProgram(0).Length + Dispatch(0).Length);
        runner.Load(
            SetProgram(0x1_0000_0100), Dispatch(1),
            SetProgram(0x1_0000_0200), Dispatch(2),
            IndirectBuffer(CalledBuffer, calledDwords, chain: false),
            IndirectBuffer(ChainedBuffer, calledDwords, chain: true));
        Assert.Equal(SubmissionProgress.Blocked, runner.Run());
        Assert.Equal(
            [(0x1_0000_0200UL, 2u), (0x1_0000_0400UL, 4u), (0x1_0000_0500UL, 5u)],
            runner.Host.PrefetchedDispatches);

        // The dispatch runs again and goes through, and the next one waits: the look-ahead already
        // handed over every dispatch after it, so it reads nothing again.
        Assert.Equal(SubmissionProgress.Blocked, runner.Run());
        Assert.Equal(3, runner.Host.PrefetchedDispatches.Count);
    }

    [Fact]
    public void TheLookAheadReadsOnIntoTheSubmissionsQueuedBehindAndOneItCoveredDoesNotReadAgain()
    {
        var host = new RecordingCommandStreamHost { PrefetchesPipelines = true };
        var queue = new CommandStreamQueue(host);
        var first = StreamRunner.Concat(SetProgram(0x1_0000_0100), Dispatch(1));
        var second = StreamRunner.Concat(SetProgram(0x1_0000_0200), Dispatch(2), SetProgram(0x1_0000_0300), Dispatch(3));
        host.WriteWords(StreamRunner.CommandAddress, first);
        host.WriteWords(CalledBuffer, second);
        queue.EnqueueGraphics(StreamRunner.CommandAddress, (uint)first.Length, 1, null);
        queue.EnqueueGraphics(CalledBuffer, (uint)second.Length, 2, null);
        host.PipelineNotReady.Enqueue(true);

        _ = queue.ProcessOne();

        Assert.Equal([(0x1_0000_0200UL, 2u), (0x1_0000_0300UL, 3u)], host.PrefetchedDispatches);

        // The first dispatch goes through, and the second submission's first dispatch waits: the
        // look-ahead already read that submission to its end.
        host.PipelineNotReady.Enqueue(false);
        host.PipelineNotReady.Enqueue(true);
        queue.NotifyPipelineReady();
        for (var slice = 0; slice < 4 && host.PipelineNotReady.Count != 0; slice++)
        {
            _ = queue.ProcessOne();
        }

        Assert.Empty(host.PipelineNotReady);
        Assert.Equal(2, host.PrefetchedDispatches.Count);
    }

    [Fact]
    public void AHostThatDoesNotAskIsHandedNothing()
    {
        var runner = new StreamRunner();
        runner.Host.PipelineNotReady.Enqueue(true);

        _ = runner.Run(SetProgram(0x1_0000_0100), Dispatch(1), SetProgram(0x1_0000_0200), Dispatch(2));

        Assert.Empty(runner.Host.PrefetchedDispatches);
    }
}
