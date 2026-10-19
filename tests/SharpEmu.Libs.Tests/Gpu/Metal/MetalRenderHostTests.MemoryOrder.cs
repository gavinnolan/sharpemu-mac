// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

// Genuine managed interpreter→accepted queue→Drain controls. No command buffer
// or native status is fabricated; these cases never encode GPU work.
public sealed partial class MetalRenderHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualInterpreterOrdinaryWriteAndEopKeepTheirLogicalQueueOrder(bool ordinaryFirst)
    {
        using var state = CompletionState();
        var address = MemoryBase + 0x8000;
        var eop = MemoryOrderEop(address, 42);
        var ordinary = StreamRunner.Packet(PacketOpcode.WriteData, 0x500u,
            (uint)address, (uint)(address >> 32), 99u);
        var words = ordinaryFirst ? StreamRunner.Concat(ordinary, eop) : StreamRunner.Concat(eop, ordinary);
        await RunOrderedMemoryPackets(words, ordinaryFirst ? 1 : 2, () => Assert.Equal(0UL, CompletionLabel(address)));
        Assert.Equal(ordinaryFirst ? 42UL : 99UL, CompletionLabel(address));
    }

    [Fact]
    public async Task ActualInterpreterAtomicReadsCompletedEarlierEopAndReturnsItsPriorValue()
    {
        using var state = CompletionState();
        var address = MemoryBase + 0x8000;
        var atomic = StreamRunner.Packet(PacketOpcode.AtomicMemory, 0x2Fu,
            (uint)address, (uint)(address >> 32), 3u, 0u, 0u, 0u, 0u);
        await RunOrderedMemoryPackets(StreamRunner.Concat(MemoryOrderEop(address, 7), atomic), 2,
            () => Assert.Equal(0UL, CompletionLabel(address)));
        Assert.Equal(7UL, _host.Queue.GetInterpreter(0).AtomicReturnMeData);
        Assert.Equal(10UL, CompletionLabel(address));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualMetalFillAndCopyCannotOvertakeEarlierEop(bool copy)
    {
        using var state = CompletionState();
        var address = MemoryBase + 0x8000;
        _host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write64, 1, address, 7));
        var task = Task.Run(() =>
        {
            using var queue = MetalVideoPresenter.EnterGuestQueue("dcb.graphics", 1);
            if (copy) _host.CopyBuffer(address + 8, address, 8, false, false);
            else _host.FillBuffer(address, 8, 0x11223344, false);
        });
        await FinishOrderedMemoryWorker(task, 2, () => Assert.Equal(0UL, CompletionLabel(address)));
        Assert.Equal(copy ? 7UL : 0x1122334411223344UL, CompletionLabel(address));
        if (copy) Assert.Equal(7UL, CompletionLabel(address + 8));
    }

    [Fact]
    public async Task AcceptedOrdinaryWriteOwnsOriginalBytesUntilItsQueueActionFinishes()
    {
        using var state = CompletionState();
        var address = MemoryBase + 0x8000;
        byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
        var expected = original.ToArray();
        var task = Task.Run(() =>
        {
            using var queue = MetalVideoPresenter.EnterGuestQueue("dcb.graphics", 1);
            Assert.True(_host.TryWriteGuest(address, original));
        });
        await FinishOrderedMemoryWorker(task, 1, () =>
        {
            Array.Fill(original, (byte)0xCC);
            Assert.Equal(0UL, CompletionLabel(address));
        });
        Span<byte> actual = stackalloc byte[8];
        Assert.True(_memory.TryRead(address, actual));
        Assert.Equal(expected, actual.ToArray());
    }

    [Fact]
    public void RenderThreadOrdinaryWriteFailsBeforeAdmissionRatherThanWaitingOnItself()
    {
        using var state = CompletionState();
        state.Set("_renderOwner", Thread.CurrentThread);
        var address = MemoryBase + 0x8000;
        Assert.Throws<GpuFatalException>(() => _host.TryWriteGuest(address, new byte[] { 1 }));
        Assert.Equal(0UL, CompletionLabel(address));
        Assert.Equal(0, CompletionField("_pendingGuestWorkCount").GetValue(null));
    }

    [Fact]
    public async Task StartupRequestingThreadCanWriteWhileAnotherThreadDrains()
    {
        using var state = CompletionState();
        var address = MemoryBase + 0x8000;
        var task = Task.Run(() =>
        {
            // Reproduce the actual AppKit startup marker: it names the caller,
            // not the main-thread render owner. The real Drain runs below.
            lock (CompletionGate) state.Set("_thread", Thread.CurrentThread);
            using var queue = MetalVideoPresenter.EnterGuestQueue("dcb.graphics", 1);
            Assert.True(_host.TryWriteGuest(address, BitConverter.GetBytes(99UL)));
        });
        await FinishOrderedMemoryWorker(task, 1, () => Assert.Equal(0UL, CompletionLabel(address)));
        Assert.Equal(99UL, CompletionLabel(address));
    }

    [Fact]
    public void ActualDrainCallbackCannotWaitOnItsOwnQueue()
    {
        using var state = CompletionState();
        var address = MemoryBase + 0x8000;
        MetalVideoPresenter.SubmitOrderedGuestAction(
            () => _host.TryWriteGuest(address, new byte[] { 1 }), "actual drain self wait");
        Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
        Assert.Equal(0UL, CompletionLabel(address));
        Assert.Equal(0, CompletionField("_pendingGuestWorkCount").GetValue(null));
        Assert.Equal(0, CompletionField("_guestWorkDrainDepth").GetValue(null));
    }

    [Fact]
    public void DefaultHostOrdinaryWriteAndEopRemainImmediate()
    {
        var runner = new StreamRunner();
        var address = StreamRunner.LabelAddress;
        var write = StreamRunner.Packet(PacketOpcode.WriteData, 0x500u, (uint)address, 0u, 99u);
        var eop = StreamRunner.Packet(PacketOpcode.EventWriteEndOfPipe, 0x28u, (uint)address, 2u << 29, 42u, 0u);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(eop, write));
        Assert.Equal(99UL, runner.Host.ReadQword(address));
    }

    [Fact]
    public async Task EarlierEopReceiptSeesItsLabelBeforeLaterOrdinaryOverwrite()
    {
        using var state = CompletionState();
        var address = MemoryBase + 0x8000;
        _host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write64, 1, address, 42));
        var observed = 0UL;
        MetalVideoPresenter.SubmitOrderedGuestAction(() => observed = CompletionLabel(address), "observe earlier EOP receipt");
        var task = Task.Run(() =>
        {
            using var queue = MetalVideoPresenter.EnterGuestQueue("dcb.graphics", 1);
            Assert.True(_host.TryWriteGuest(address, BitConverter.GetBytes(99UL)));
        });
        await FinishOrderedMemoryWorker(task, 3, () => Assert.Equal(0UL, CompletionLabel(address)));
        Assert.Equal(42UL, observed);
        Assert.Equal(99UL, CompletionLabel(address));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrdinaryWriterKeepsOriginalCauseAndSuppressesFollowingReceipt(bool throwing)
    {
        using var state = CompletionState();
        var address = MemoryBase + 0x8000;
        var original = new NotSupportedException("ordered ordinary writer failure");
        var memory = new CompletionRejectMemory(_memory, throwing ? original : null);
        var host = new MetalCommandStreamHost(memory, _backend, _backend);
        var task = Task.Run(() =>
        {
            using var queue = MetalVideoPresenter.EnterGuestQueue("dcb.graphics", 1);
            return Record.Exception(() => host.TryWriteGuest(address, BitConverter.GetBytes(99UL)));
        });
        var following = false;
        var error = await Record.ExceptionAsync(() => FinishOrderedMemoryWorker(task, 1, () =>
        {
            using var queue = MetalVideoPresenter.EnterGuestQueue("dcb.graphics", 1);
            MetalVideoPresenter.SubmitOrderedGuestAction(() => following = true, "must not acknowledge failed ordinary write");
        }));
        Assert.IsType<GpuFatalException>(error);
        var callerError = Assert.IsType<GpuFatalException>(await task);
        if (throwing) Assert.Same(original, callerError.InnerException);
        else Assert.Contains("memory write failed", callerError.InnerException!.Message);
        Assert.False(following);
        Assert.Equal(0UL, CompletionLabel(address));
        Assert.Equal(1, memory.Writes);
    }

    private static uint[] MemoryOrderEop(ulong address, uint value) =>
        StreamRunner.Packet(PacketOpcode.EventWriteEndOfPipe, 0x28u,
            (uint)address, (uint)(address >> 32) | (2u << 29), value, 0u);

    private async Task RunOrderedMemoryPackets(uint[] words, int pending, Action beforeDrain)
    {
        var address = MemoryBase + 0x1000;
        Assert.True(_memory.TryWrite(address, MemoryMarshal.AsBytes(words.AsSpan())));
        var task = Task.Run(() =>
        {
            using var queue = MetalVideoPresenter.EnterGuestQueue("dcb.graphics", 1);
            Assert.Equal(SubmissionProgress.Complete,
                _host.Queue.GetInterpreter(0).Process(new PacketCursorStack(), address, (uint)words.Length));
        });
        await FinishOrderedMemoryWorker(task, pending, beforeDrain);
    }

    private static async Task FinishOrderedMemoryWorker(Task task, int pending, Action beforeDrain)
    {
        var gate = typeof(MetalVideoPresenter).GetField("_gate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        try
        {
            var deadline = Environment.TickCount64 + 5000;
            while (true)
            {
                int count;
                lock (gate) count = (int)CompletionField("_pendingGuestWorkCount").GetValue(null)!;
                if (count >= pending) break;
                Assert.False(task.IsCompleted, "The ordinary operation must wait for its accepted queue action.");
                Assert.True(Environment.TickCount64 < deadline, "The expected queue actions were not admitted.");
                await Task.Yield();
            }
            beforeDrain();
            while (!task.IsCompleted)
            {
                MetalVideoPresenter.DrainGuestWork(0, 0);
                Assert.True(Environment.TickCount64 < deadline, "The accepted ordinary operation failed to complete.");
                await Task.Yield();
            }
            await task;
            DrainCompletions(); // a following EOP may have been posted just after the synchronous write returned
        }
        finally
        {
            if (!task.IsCompleted)
            {
                _ = MetalVideoPresenter.FailGuestCompletion("managed memory-order control unwound before its worker");
                try { await task.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
                Assert.True(task.IsCompleted, "Never restore scheduling state while its worker still owns an action.");
            }
        }
    }
}
