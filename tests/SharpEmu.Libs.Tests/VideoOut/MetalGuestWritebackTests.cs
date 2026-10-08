// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// Actual production helper/queue controls, entirely managed. The native status
// boundary is overridden; these are not device completion/ownership evidence.
[Collection(SchedulingStateCollection.Name)]
public sealed class MetalGuestWritebackTests
{
    [Theory]
    [InlineData(4, 4, 0, true)]
    [InlineData(0, 4, 1, true)]
    [InlineData(1, 4, 1, true)]
    [InlineData(2, 4, 1, true)]
    [InlineData(3, 4, 1, true)]
    [InlineData(5, 4, 0, false)]
    [InlineData(3, 5, 1, false)]
    [InlineData(3, 3, 1, false)]
    [InlineData(-1, 4, 0, false)]
    [InlineData(6, 4, 0, false)]
    public void OnlySuccessfulStatusAllowsWriteback(int before, int after, int expectedWaits, bool success)
    {
        using var state = new State();
        var waits = 0;
        var reads = 0;
        state.Set("_guestCommandBufferStatusOverride", (Func<nint, nint>)(_ => ++reads == 1 ? before : after));
        state.Set("_guestCommandBufferWaitOverride", (Action<nint>)(_ => waits++));
        if (success) Invoke("WaitForCommittedCommandBuffer", (nint)123);
        else Assert.Contains("did not complete successfully", Assert.Throws<InvalidOperationException>(
            () => Invoke("WaitForCommittedCommandBuffer", (nint)123)).Message);
        Assert.Equal(expectedWaits, waits);
        Assert.Equal(expectedWaits + 1, reads);
    }

    [Fact]
    public void NoPrecedingNativeBufferNeedsNoNativeCall()
    {
        using var state = new State();
        state.Set("_guestCommandBufferStatusOverride", (Func<nint, nint>)(_ => throw new Exception("unexpected status")));
        state.Set("_guestCommandBufferWaitOverride", (Action<nint>)(_ => throw new Exception("unexpected wait")));
        Invoke("WaitForCommittedCommandBuffer", (nint)0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeBoundaryFailurePreservesOriginalCause(bool failingWait)
    {
        using var state = new State();
        var original = new NotSupportedException("controlled native boundary failure");
        state.Set("_guestCommandBufferStatusOverride", (Func<nint, nint>)(_ => failingWait ? 3 : throw original));
        state.Set("_guestCommandBufferWaitOverride", (Action<nint>)(_ => throw original));
        Assert.Same(original, Record.Exception(() => Invoke("WaitForCommittedCommandBuffer", (nint)123)));
    }

    [Fact]
    public unsafe void SuccessfulWritebackCopiesEveryOwnedByteIncludingGdsStyleEmptySnapshot()
    {
        using var state = new State();
        var memory = new Memory();
        state.Set("_guestMemory", memory);
        byte[] bytes = [0xAD, 2, 0xF3, 4, 0x56, 0x80, 7];
        fixed (byte* pointer = bytes)
            Writeback((nint)pointer, Guest(bytes.Length));
        Assert.Equal(bytes, memory.Written);
        Assert.Equal(0x1000UL, memory.Address);
        Assert.Equal(1, memory.Calls);
    }

    [Theory]
    [InlineData(-1, 1UL, true)]
    [InlineData(4, 1UL, false)]
    [InlineData(4, ulong.MaxValue, true)]
    public void InvalidRangesRejectBeforeGuestWrite(int length, ulong address, bool pointerPresent)
    {
        using var state = new State();
        var memory = new Memory();
        state.Set("_guestMemory", memory);
        Assert.Contains("invalid range", Assert.Throws<InvalidOperationException>(() =>
            Writeback(pointerPresent ? (nint)123 : 0, Guest(length) with { BaseAddress = address })).Message);
        Assert.Equal(0, memory.Calls);
    }

    [Fact]
    public void MissingMemoryRejectsNonemptyWritebackButEmptyBatchRemainsValid()
    {
        using var state = new State();
        state.Set("_guestMemory", null);
        Invoke("WriteBuffersBackToGuest", new List<(nint Pointer, GuestMemoryBuffer Guest)>());
        Assert.Contains("attached guest memory", Assert.Throws<InvalidOperationException>(() =>
            Writeback((nint)123, Guest(4))).Message);
    }

    [Fact]
    public void EmptyRangeDoesNotDereferenceOrCallWriter()
    {
        using var state = new State();
        var memory = new Memory();
        state.Set("_guestMemory", memory);
        Writeback(0, Guest(0));
        Assert.Equal(0, memory.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void IncompleteWritableSnapshotRejectsEntireUploadBeforeNativeAccess(int length)
    {
        using var state = new State();
        // A valid prefix must not allocate an upload before the invalid suffix
        // is discovered. Device=0 is deliberately not a native test device.
        var first = Guest(4) with { Data = new byte[4] };
        var invalid = first with { Length = length };
        Assert.Contains("snapshot is incomplete", Assert.Throws<InvalidOperationException>(() =>
            Invoke("UploadDrawBuffers", (nint)0, new[] { first, invalid },
                new List<(nint Pointer, GuestMemoryBuffer Guest)>())).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void RejectedOrThrowingWriterLatchesFailureAndSuppressesFollowingCompletion(bool throwing)
    {
        using var state = new State();
        var original = new NotSupportedException("guest writer failed");
        var memory = new Memory { Reject = !throwing, Failure = throwing ? original : null };
        state.Set("_guestMemory", memory);
        GpuFatal.OnFatal = _ => { };
        var completed = false;
        byte[] bytes = [1, 2, 3, 4];
        fixed (byte* pointer = bytes)
        {
            var ownedPointer = (nint)pointer;
            var failed = MetalVideoPresenter.SubmitOrderedGuestAction(
                () => Writeback(ownedPointer, Guest(bytes.Length)), "buffer writeback");
            var following = MetalVideoPresenter.SubmitOrderedGuestAction(() => completed = true, "guest completion");
            var fatal = Assert.Throws<GpuFatalException>(() => DrainUntilFailure());
            if (throwing) Assert.Same(original, fatal.InnerException);
            else Assert.Contains("cannot write guest memory", fatal.InnerException!.Message);
            Assert.False(completed);
            Assert.False(MetalVideoPresenter.WaitForGuestWork(failed, Timeout.Infinite));
            Assert.False(MetalVideoPresenter.WaitForGuestWork(following, Timeout.Infinite));
            Assert.Equal(0, MetalVideoPresenter.SubmitOrderedGuestAction(() => { }, "after failure"));
        }
        Assert.Equal(1, memory.Calls);
    }

    [Fact]
    public unsafe void FailedCommandBufferPreventsCpuWritebackAndFollowingCompletion()
    {
        using var state = new State();
        var memory = new Memory();
        state.Set("_guestMemory", memory);
        state.Set("_guestCommandBufferStatusOverride", (Func<nint, nint>)(_ => 5));
        state.Set("_guestCommandBufferWaitOverride", (Action<nint>)(_ => throw new Exception("Error must not wait")));
        GpuFatal.OnFatal = _ => { };
        var completed = false;
        byte[] bytes = [1, 2, 3, 4];
        fixed (byte* pointer = bytes)
        {
            var ownedPointer = (nint)pointer;
            MetalVideoPresenter.SubmitOrderedGuestAction(() =>
            {
                Invoke("WaitForCommittedGuestBufferWriteback", (nint)123);
                Writeback(ownedPointer, Guest(4));
            }, "successful GPU completion then buffer writeback");
            MetalVideoPresenter.SubmitOrderedGuestAction(() => completed = true, "guest completion");
            var fatal = Assert.Throws<GpuFatalException>(() => DrainUntilFailure());
            Assert.Contains("status=5", fatal.InnerException!.Message);
        }
        Assert.Equal(0, memory.Calls);
        Assert.False(completed);
    }

    private static void DrainUntilFailure()
    {
        // Drain has a bounded frame budget; repeat at most once per item.
        for (var i = 0; i < 3; i++) MetalVideoPresenter.DrainGuestWork(0, 0);
    }

    [Fact]
    public unsafe void MissingWritebackCommandBufferPreventsCpuCopyAndCompletionWithoutNativeCall()
    {
        using var state = new State();
        var memory = new Memory();
        state.Set("_guestMemory", memory);
        state.Set("_guestCommandBufferStatusOverride", (Func<nint, nint>)(_ => throw new Exception("must reject before status")));
        state.Set("_guestCommandBufferWaitOverride", (Action<nint>)(_ => throw new Exception("must reject before wait")));
        GpuFatal.OnFatal = _ => { };
        var completed = false;
        byte[] bytes = [1, 2, 3, 4];
        fixed (byte* pointer = bytes)
        {
            var ownedPointer = (nint)pointer;
            MetalVideoPresenter.SubmitOrderedGuestAction(() =>
            {
                Invoke("WaitForCommittedGuestBufferWriteback", (nint)0);
                Writeback(ownedPointer, Guest(4));
            }, "missing writeback command buffer");
            MetalVideoPresenter.SubmitOrderedGuestAction(() => completed = true, "following completion");
            var fatal = Assert.Throws<GpuFatalException>(DrainUntilFailure);
            Assert.Contains("no committed command buffer", fatal.InnerException!.Message);
        }
        Assert.Equal(0, memory.Calls);
        Assert.False(completed);
    }

    [Fact]
    public unsafe void SuccessfulWaitAndWritebackPrecedeActualQueueAcknowledgement()
    {
        using var state = new State();
        var trace = new List<string>();
        var memory = new Memory { BeforeWrite = () => trace.Add("writeback") };
        state.Set("_guestMemory", memory);
        var status = 3;
        state.Set("_guestCommandBufferStatusOverride", (Func<nint, nint>)(_ => status));
        state.Set("_guestCommandBufferWaitOverride", (Action<nint>)(_ => { trace.Add("wait"); status = 4; }));
        byte[] bytes = [0xBE, 0xEF, 0x80, 0x41];
        fixed (byte* pointer = bytes)
        {
            var ownedPointer = (nint)pointer;
            var writer = MetalVideoPresenter.SubmitOrderedGuestAction(() =>
            {
                Invoke("WaitForCommittedGuestBufferWriteback", (nint)123);
                Writeback(ownedPointer, Guest(4));
            }, "wait then writeback");
            var completion = MetalVideoPresenter.SubmitOrderedGuestAction(() =>
            {
                Assert.True(MetalVideoPresenter.WaitForGuestWork(writer, 0));
                Assert.Equal(bytes, memory.Written);
                trace.Add("completion");
            }, "following completion");
            for (var i = 0; i < 3 && !MetalVideoPresenter.WaitForGuestWork(completion, 0); i++)
                MetalVideoPresenter.DrainGuestWork(0, 0);
            Assert.True(MetalVideoPresenter.WaitForGuestWork(completion, 0));
        }
        Assert.Equal(new[] { "wait", "writeback", "completion" }, trace);
    }

    [Fact]
    public unsafe void PartialWritebackFailureDoesNotRetryOrAcknowledgeEarlierWrites()
    {
        using var state = new State();
        var memory = new Memory { RejectCall = 2 };
        state.Set("_guestMemory", memory);
        GpuFatal.OnFatal = _ => { };
        byte[] bytes = [1, 2, 3, 4];
        fixed (byte* pointer = bytes)
        {
            var ownedPointer = (nint)pointer;
            var writer = MetalVideoPresenter.SubmitOrderedGuestAction(() =>
                Invoke("WriteBuffersBackToGuest", new List<(nint Pointer, GuestMemoryBuffer Guest)>
                {
                    (ownedPointer, Guest(4)),
                    (ownedPointer, Guest(4) with { BaseAddress = 0x2000 }),
                    (ownedPointer, Guest(4) with { BaseAddress = 0x3000 }),
                }), "three writes");
            var fatal = Assert.Throws<GpuFatalException>(() => DrainUntilFailure());
            Assert.Contains("0000000000002000", fatal.InnerException!.Message);
            Assert.Equal(2, memory.Calls);
            Assert.Equal(0x1000UL, memory.Address);
            Assert.False(MetalVideoPresenter.WaitForGuestWork(writer, 0));
            Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
            Assert.Equal(2, memory.Calls);
        }
    }

    private static GuestMemoryBuffer Guest(int length) => new(0x1000, [], length, (ulong)Math.Max(0, length), false, true);
    private static void Writeback(nint pointer, GuestMemoryBuffer guest) =>
        Invoke("WriteBuffersBackToGuest", new List<(nint Pointer, GuestMemoryBuffer Guest)> { (pointer, guest) });
    private static void Invoke(string method, params object?[] args)
    {
        try { typeof(MetalVideoPresenter).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args); }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
        { ExceptionDispatchInfo.Capture(inner).Throw(); }
    }

    private sealed class Memory : ICpuMemory
    {
        public bool Reject;
        public int RejectCall;
        public Action? BeforeWrite;
        public Exception? Failure;
        public int Calls;
        public ulong Address;
        public byte[]? Written;
        public bool TryRead(ulong address, Span<byte> destination) => false;
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source)
        {
            Calls++;
            BeforeWrite?.Invoke();
            if (Failure is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
            if (Reject || Calls == RejectCall) return false;
            Address = address;
            Written = source.ToArray();
            return true;
        }
    }

    internal sealed class State : IDisposable
    {
        private readonly MetalGuestFailureTests.PresenterState scheduler = new();
        private readonly Dictionary<FieldInfo, object?> saved = new();
        public State()
        {
            Set("_textureSourceGraphEnabled", false);
            Set("_guestPresentationShutdownAttempt", null);
            Set("_guestPresentationNativeOwnersDrained", false);
            Set("_initialGuestCompletion", null);
            Set("_initialGuestCompletionWaiters", 0);
            Set("_initialGuestCompletionTimeoutMilliseconds", 5000);
            Set("_unorderedPresentationWriteSequence", 0L);
            Set("_latestPresentation", null);
            Set("_lastFlushedCommandBuffer", (nint)0);
            Set("_guestMemory", null);
            Set("_guestCommandBufferStatusOverride", null);
            Set("_guestCommandBufferWaitOverride", null);
        }
        public void Set(string name, object? value)
        {
            var field = typeof(MetalVideoPresenter).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
            saved.TryAdd(field, field.GetValue(null));
            field.SetValue(null, value);
        }
        public void Dispose()
        {
            foreach (var entry in saved) entry.Key.SetValue(null, entry.Value);
            scheduler.Dispose();
        }
    }
}
