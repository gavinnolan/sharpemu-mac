// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

public sealed partial class MetalRenderHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetalActualInterpreterDefersLabelAndPairedWaitUntilSuccessfulDrain(bool qword)
    {
        using var state = CompletionState();
        var address = MemoryBase + 0x8000;
        const ulong value = 0x11223344;
        var eop = StreamRunner.Packet(PacketOpcode.EventWriteEndOfPipe, qword ? 0x28u : 0x504u,
            (uint)address, (uint)(address >> 32) | ((qword ? 2u : 1u) << 29), (uint)value, 0);
        var wait = qword
            ? StreamRunner.Packet(PacketOpcode.WaitRegisterMemory64, 0x13u, (uint)address, (uint)(address >> 32), (uint)value, 0, uint.MaxValue, uint.MaxValue, 0)
            : StreamRunner.Packet(PacketOpcode.WaitRegisterMemory, 0x13u, (uint)address, (uint)(address >> 32), (uint)value, uint.MaxValue, 0);
        var words = StreamRunner.Concat(eop, wait);
        var command = MemoryBase + 0x1000;
        Assert.True(_memory.TryWrite(command, System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan())));
        var interpreter = _host.Queue.GetInterpreter(0);
        var stack = new PacketCursorStack();
        Assert.Equal(SubmissionProgress.Blocked, interpreter.Process(stack, command, (uint)words.Length));
        Assert.Equal(0UL, CompletionLabel(address));
        Assert.Equal(0, typeof(GpuCommandInterpreter).GetField("_lastWriteLength", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(interpreter));
        var status = 3;
        state.Set("_lastFlushedCommandBuffer", (nint)123);
        state.Set("_guestCommandBufferStatusOverride", (Func<nint, nint>)(_ => status));
        state.Set("_guestCommandBufferWaitOverride", (Action<nint>)(_ =>
        {
            Assert.Equal(0UL, CompletionLabel(address));
            status = 4;
        }));
        DrainCompletions();
        Assert.Equal(value, CompletionLabel(address));
        Assert.Equal(SubmissionProgress.Complete, interpreter.Process(stack, command, (uint)words.Length));
    }

    [Fact]
    public void DefaultInterpreterStillWritesLabelAndCompletesPairedWaitImmediately()
    {
        var runner = new StreamRunner();
        Assert.False(((ICommandStreamHost)runner.Host).DefersEndOfPipeLabels);
        var address = StreamRunner.LabelAddress;
        var eop = StreamRunner.Packet(PacketOpcode.EventWriteEndOfPipe, 0x28u,
            (uint)address, 2u << 29, 42u, 0);
        var wait = StreamRunner.Packet(PacketOpcode.WaitRegisterMemory64, 0x13u,
            (uint)address, 0, 42u, 0, uint.MaxValue, uint.MaxValue, 0);
        Assert.Equal(SubmissionProgress.Complete, runner.Run(eop, wait));
        Assert.Equal(42UL, runner.Host.ReadQword(address));
        Assert.Single(runner.Host.EndOfPipeWrites);
    }

    [Theory]
    [InlineData(EndOfPipeWriteKind.Write32, 4)]
    [InlineData(EndOfPipeWriteKind.WriteBack32, 4)]
    [InlineData(EndOfPipeWriteKind.Interrupt32, 4)]
    [InlineData(EndOfPipeWriteKind.InterruptWriteBack32, 4)]
    [InlineData(EndOfPipeWriteKind.FlipWithWrite32, 4)]
    [InlineData(EndOfPipeWriteKind.FlipWithInterruptWriteBack32, 4)]
    [InlineData(EndOfPipeWriteKind.Write64, 8)]
    [InlineData(EndOfPipeWriteKind.WriteBack64, 8)]
    [InlineData(EndOfPipeWriteKind.Interrupt64, 8)]
    [InlineData(EndOfPipeWriteKind.InterruptWriteBack64, 8)]
    [InlineData(EndOfPipeWriteKind.ClockWrite, 8)]
    [InlineData(EndOfPipeWriteKind.ClockWriteBack, 8)]
    public void ActualMetalLabelPayloadPreservesScalarWidthAndNeighbors(EndOfPipeWriteKind kind, int bytes)
    {
        var address = MemoryBase + 0x8000;
        byte[] surrounding = Enumerable.Repeat((byte)0xCD, 24).ToArray();
        Assert.True(_memory.TryWrite(address - 8, surrounding));
        var write = new EndOfPipeWrite(kind, 42, address, 0xFEDCBA9876543210);
        InvokeLabelWriter(_host, write);
        Assert.True(_memory.TryRead(address - 8, surrounding));
        Assert.All(surrounding[..8], b => Assert.Equal(0xCD, b));
        Assert.All(surrounding[(8 + bytes)..], b => Assert.Equal(0xCD, b));
        Span<byte> expected = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(expected, write.Value);
        Assert.Equal(expected[..bytes].ToArray(), surrounding.AsSpan(8, bytes).ToArray());
    }

    [Fact]
    public void MetalClockPacketKeepsLabelOldUntilDrain()
    {
        using var state = CompletionState();
        var address = MemoryBase + 0x8000;
        var words = StreamRunner.Packet(PacketOpcode.EventWriteEndOfPipe, 0x504u,
            (uint)address, (uint)(address >> 32) | (4u << 29), 0, 0);
        var command = MemoryBase + 0x1000;
        Assert.True(_memory.TryWrite(command, System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan())));
        Assert.Equal(SubmissionProgress.Complete, _host.Queue.GetInterpreter(0).Process(new PacketCursorStack(), command, (uint)words.Length));
        Assert.Equal(0UL, CompletionLabel(address));
        DrainCompletions();
        Assert.NotEqual(0UL, CompletionLabel(address));
    }

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

    [Fact]
    public void InitialNeverAdmittedNoDeviceCompletionIsSynchronous()
    {
        using var state = CompletionState();
        state.Set("_thread", null);
        var address = MemoryBase + 0x8000;
        _host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write64, 7, address, 0xFACEB00C));
        Assert.Equal(0xFACEB00CUL, CompletionLabel(address));
        Assert.Equal(0L, CompletionField("_enqueuedGuestWorkSequence").GetValue(null));
        _host.SynchronizeGpu(); // The recording backend returns zero, legitimately empty here.
    }

    [Theory]
    [InlineData("_closed", true)]
    [InlineData("_enqueuedGuestWorkSequence", false)]
    [InlineData("_device", false)]
    [InlineData("_commandQueue", false)]
    [InlineData("_batchOpen", true)]
    [InlineData("_batchCommandBuffer", false)]
    [InlineData("_lastFlushedCommandBuffer", false)]
    public void MissingRendererNeverFallsBackAfterAcceptedOrNativeWork(string field, bool boolean)
    {
        using var state = CompletionState();
        state.Set("_thread", null);
        state.Set(field, boolean ? (object)true : field == "_enqueuedGuestWorkSequence" ? (object)1L : (object)(nint)123);
        var address = MemoryBase + 0x8000;
        var fatal = Assert.Throws<GpuFatalException>(() =>
            _host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write64, 7, address, 42)));
        Assert.Contains("not admitted", fatal.Message);
        Assert.Equal(0UL, CompletionLabel(address));
        Assert.NotNull(CompletionField("_guestWorkFailure").GetValue(null));
        // The artificial open batch belongs only to this managed preflight;
        // never drain or issue native calls with its synthetic pointer.
    }

    [Fact]
    public void RefusedCompletionKeepsExistingFailureAndNeverPublishesLabel()
    {
        using var state = CompletionState();
        var original = new NotSupportedException("earlier GPU writeback failed");
        state.Set("_guestWorkFailure", original);
        var address = MemoryBase + 0x8000;
        var fatal = Assert.Throws<GpuFatalException>(() =>
            _host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Interrupt64, 7, address, 42)));
        Assert.Same(original, fatal.InnerException);
        Assert.Equal(0UL, CompletionLabel(address));
    }

    [Fact]
    public void FailedNativeWaitStopsActualMetalLabelBeforeWriteAndQueueAcknowledgement()
    {
        using var state = CompletionState();
        state.Set("_lastFlushedCommandBuffer", (nint)123);
        state.Set("_guestCommandBufferStatusOverride", (Func<nint, nint>)(_ => 5));
        var address = MemoryBase + 0x8000;
        _host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Interrupt64, 7, address, 42));
        var following = false;
        var followingSequence = MetalVideoPresenter.SubmitOrderedGuestAction(() => following = true, "following acknowledgement");
        var fatal = Assert.Throws<GpuFatalException>(DrainCompletions);
        Assert.Contains("status=5", fatal.InnerException!.Message);
        Assert.Equal(0UL, CompletionLabel(address));
        Assert.False(following);
        Assert.False(MetalVideoPresenter.WaitForGuestWork(followingSequence, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedMetalLabelWriteSuppressesFollowingCompletionAndPreservesWriterCause(bool throwing)
    {
        using var state = CompletionState();
        var original = new NotSupportedException("label writer rejected mapping");
        var memory = new CompletionRejectMemory(_memory, throwing ? original : null);
        var host = new MetalCommandStreamHost(memory, _backend, _backend);
        var address = MemoryBase + 0x8000;
        host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Interrupt64, 7, address, 42));
        var following = false;
        MetalVideoPresenter.SubmitOrderedGuestAction(() => following = true, "following ack");
        var fatal = Assert.Throws<GpuFatalException>(DrainCompletions);
        if (throwing) Assert.Same(original, fatal.InnerException);
        else Assert.Contains("label cannot write", fatal.InnerException!.Message);
        Assert.False(following);
        Assert.Equal(0UL, CompletionLabel(address));
        Assert.Equal(1, memory.Writes);
    }

    [Fact]
    public void RefusedGdsSynchronizationFailsBeforeReadback()
    {
        using var state = CompletionState();
        var fatal = Assert.Throws<GpuFatalException>(_host.SynchronizeGpu);
        Assert.Contains("synchronize was not admitted", fatal.Message);
        Assert.Equal(0, _backend.GlobalDataShareReads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ActualRegisteredInterruptAppearsOnlyAfterSuccessfulWaitAndLabelWrite(int failureMode)
    {
        using var state = CompletionState();
        var ctx = new CpuContext(_memory, Generation.Gen5);
        var handleAddress = MemoryBase + 0x9000;
        ctx[CpuRegister.Rdi] = handleAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelEventQueueCompatExports.KernelCreateEqueue(ctx));
        var handle = CompletionLabel(handleAddress);
        const int eventId = 0x3A42;
        try
        {
            Assert.True(KernelEventQueueCompatExports.RegisterEvent(handle, eventId, KernelEventQueueCompatExports.KernelEventFilterGraphics, 0));
            var address = MemoryBase + 0x8000;
            var status = failureMode == 1 ? 5 : 3;
            state.Set("_lastFlushedCommandBuffer", (nint)123);
            state.Set("_guestCommandBufferStatusOverride", (Func<nint, nint>)(_ => status));
            state.Set("_guestCommandBufferWaitOverride", (Action<nint>)(_ =>
            {
                Assert.Equal(0UL, CompletionLabel(address));
                Assert.Equal(0, PollCompletionEvents(ctx, handle));
                status = 4;
            }));
            var host = failureMode == 2
                ? new MetalCommandStreamHost(new CompletionRejectMemory(_memory, null), _backend, _backend)
                : _host;
            host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Interrupt64, 7, address, 42, eventId, 0xCAFE));
            Assert.Equal(0, PollCompletionEvents(ctx, handle));
            if (failureMode == 0)
            {
                DrainCompletions();
                Assert.Equal(42UL, CompletionLabel(address));
                Assert.Equal(1, PollCompletionEvents(ctx, handle));
                Assert.Equal((ulong)eventId, CompletionLabel(MemoryBase + 0x9200));
                Assert.Equal(0xCAFEUL, CompletionLabel(MemoryBase + 0x9210));
            }
            else
            {
                Assert.Throws<GpuFatalException>(DrainCompletions);
                Assert.Equal(0UL, CompletionLabel(address));
                Assert.Equal(0, PollCompletionEvents(ctx, handle));
            }
        }
        finally
        {
            ctx[CpuRegister.Rdi] = handle;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelEventQueueCompatExports.KernelDeleteEqueue(ctx));
        }
    }

    private int PollCompletionEvents(CpuContext ctx, ulong handle)
    {
        Assert.True(_memory.TryWrite(MemoryBase + 0x9400, new byte[8])); // explicit zero-timeout poll
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = MemoryBase + 0x9200;
        ctx[CpuRegister.Rdx] = 4;
        ctx[CpuRegister.Rcx] = MemoryBase + 0x9300;
        ctx[CpuRegister.R8] = MemoryBase + 0x9400;
        _ = KernelEventQueueCompatExports.KernelWaitEqueue(ctx);
        return (int)(CompletionLabel(MemoryBase + 0x9300) & uint.MaxValue);
    }

    private MetalGuestWritebackTests.State CompletionState()
    {
        var state = new MetalGuestWritebackTests.State();
        state.Set("_device", (nint)0);
        state.Set("_commandQueue", (nint)0);
        GpuFatal.OnFatal = _ => { };
        return state;
    }

    private ulong CompletionLabel(ulong address)
    {
        Span<byte> bytes = stackalloc byte[8];
        Assert.True(_memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static FieldInfo CompletionField(string name) => typeof(MetalVideoPresenter).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static void DrainCompletions() { for (var i = 0; i < 3; i++) MetalVideoPresenter.DrainGuestWork(0, 0); }
    private static void InvokeLabelWriter(MetalCommandStreamHost host, EndOfPipeWrite write)
    {
        try { typeof(MetalCommandStreamHost).GetMethod("WriteDeferredEndOfPipeLabel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [write]); }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner) { ExceptionDispatchInfo.Capture(inner).Throw(); }
    }

    private sealed class CompletionRejectMemory(ICpuMemory original, Exception? failure) : ICpuMemory
    {
        public int Writes;
        public bool TryRead(ulong address, Span<byte> destination) => original.TryRead(address, destination);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            Writes++;
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            return false;
        }
    }
}
