// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalTextureSourceRuntimeDrainTests
{
    private static object PublishBorrowedDraw(byte[] data, GuestIndexBuffer? index = null)
    {
        MetalVideoPresenter.SubmitTranslatedDraw(new(new Gen5MslShader("", "borrowed presentation", Gen5MslStage.Pixel, 0)), [],
            [new GuestMemoryBuffer(0, data, 64, 64, true)], 4, 4, 0, null, 6, 1, 4, index,
            [new GuestVertexBuffer(0, 4, 0, 0, 0, 16, 0, data, 64, true)], null);
        return Field("_latestPresentation").GetValue(null)!;
    }
    private static object TakePayloadBorrow(object expected)
    {
        object?[] args = [-1L, null, null];
        Assert.True((bool)Call("TryTakePresentationWithPayloadBorrow", args)!);
        Assert.Same(expected, args[1]); Assert.NotNull(args[2]);
        Assert.Same(expected, Get(args[2]!, "Presentation")); return args[2]!;
    }
    private static void ReleaseBorrow(object? borrow, Exception? cause = null) => Call("ReleaseGuestPresentationPayloadBorrow", borrow, cause);
    private static int ActiveBorrows
    {
        get { lock (Field("_gate").GetValue(null)!) return ((IEnumerable)Field("_activeGuestPresentationPayloadBorrows").GetValue(null)!).Cast<object>().Count(); }
    }
    private sealed class BorrowState : IDisposable
    {
        private readonly State state = new();
        private readonly Dictionary<FieldInfo, object?> saved = [];
        public BorrowState()
        {
            Assert.Equal(0, ActiveBorrows);
            foreach (var name in new[] { "_textureSourceCleanupFailure", "_textureSourceAfterCanceledPayloadReturn", "_guestPresentationBorrowShutdownTimeoutMilliseconds", "_guestPresentationBorrowShutdownIncomplete" })
                saved.Add(Field(name), Field(name).GetValue(null));
            Field("_textureSourceCleanupFailure").SetValue(null, null);
            Field("_guestPresentationBorrowShutdownTimeoutMilliseconds").SetValue(null, 2000);
        }
        public GuestDrawTexture AdmitPendingSource() => state.AdmitPendingSource();
        public void Dispose()
        {
            Field("_textureSourceAfterCanceledPayloadReturn").SetValue(null, null);
            var borrows = ((IEnumerable)Field("_activeGuestPresentationPayloadBorrows").GetValue(null)!).Cast<object>().ToArray();
            foreach (var borrow in borrows) ReleaseBorrow(borrow);
            state.Dispose();
            foreach (var pair in saved) pair.Key.SetValue(null, pair.Value);
        }
    }

    [Fact]
    public void ActualTakePinsRepeatedReadersAcrossReplacementAndCanonicalIndexReturn()
    {
        using var state = new BorrowState(); lock (PoolGate)
        {
            var before = GuestDataPool.DiagnosticStats().LeaseCount; var data = GuestDataPool.Shared.Rent(64); byte[]? rerented = null;
            try
            {
                var lease = new GuestIndexBufferLease(data);
                var original = PublishBorrowedDraw(data, new(data, 64, false, true, lease));
                var first = TakePayloadBorrow(original); var second = TakePayloadBorrow(original);
                var receipt = ((IEnumerable)Get(PayloadOwner(original), "Receipts")!).Cast<object>().Single();
                Assert.Equal(3, Get(receipt, "References"));
                MetalVideoPresenter.Submit(new byte[64], 4, 4);
                Assert.True((bool)Get(PayloadOwner(original), "Released")!);
                Assert.False(lease.Returned); Assert.Equal(2, Get(receipt, "References"));
                ReleaseBorrow(first); ReleaseBorrow(first); Call("ReleaseGuestPayloadWork", original);
                Assert.False(lease.Returned); Assert.Equal(1, ActiveBorrows); Assert.Equal(1, Get(receipt, "References"));
                ReleaseBorrow(second); Assert.True(lease.Returned); Assert.Equal(0, ActiveBorrows);
                Assert.Equal(before, GuestDataPool.DiagnosticStats().LeaseCount);
                rerented = GuestDataPool.Shared.Rent(64); Assert.Same(data, rerented);
                ReleaseBorrow(first); ReleaseBorrow(second); Call("ReturnPooledGuestData", original.GetType().GetProperty("TranslatedDraw")!.GetValue(original));
                Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount); Assert.False(lease.TryReturn());
                Call("CompletePresenterShutdown"); AssertNoLivePayloadReceipts();
            }
            finally { GuestDataPool.Shared.Return(rerented ?? data); }
        }
    }

    [Fact]
    public void ReleasingOnePresentationCannotStealDifferentPresentationsSharedBorrow()
    {
        using var state = new BorrowState(); lock (PoolGate)
        {
            var before = GuestDataPool.DiagnosticStats().LeaseCount; var data = GuestDataPool.Shared.Rent(64);
            try
            {
                var original = PublishBorrowedDraw(data); var first = TakePayloadBorrow(original);
                var replacement = PublishBorrowedDraw(data); var second = TakePayloadBorrow(replacement);
                MetalVideoPresenter.Submit(new byte[64], 4, 4);
                ReleaseBorrow(first); Call("ReleaseGuestPayloadWork", original); ReleaseBorrow(first);
                Assert.Equal(1, ActiveBorrows); Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount);
                Assert.False((bool)Get(second, "Released")!);
                ReleaseBorrow(second); Assert.Equal(before, GuestDataPool.DiagnosticStats().LeaseCount);
                Call("CompletePresenterShutdown"); AssertNoLivePayloadReceipts();
            }
            finally { GuestDataPool.Shared.Return(data); }
        }
    }

    [Fact]
    public void ActualConcurrentCloseRevokesSelectionAndWaitsOutsideGateUntilBorrowReleased()
    {
        using var state = new BorrowState(); var data = GuestDataPool.Shared.Rent(64); Task? closing = null;
        var before = GuestDataPool.DiagnosticStats().LeaseCount - 1;
        try
        {
            var original = PublishBorrowedDraw(data); var borrow = TakePayloadBorrow(original);
            closing = Task.Run(() => Call("CompletePresenterShutdown"));
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lock (Field("_gate").GetValue(null)!) return (int)Field("_guestPresentationBorrowShutdownWaiters").GetValue(null)! == 1;
            }, 1500));
            // Acquiring this gate while the real shutdown waits proves that
            // its wait releases the gate. The borrower can still finish.
            lock (Field("_gate").GetValue(null)!)
            {
                Assert.True((bool)Field("_closed").GetValue(null)!);
                Assert.Null(Field("_latestPresentation").GetValue(null));
                Assert.True((bool)Get(PayloadOwner(original), "Released")!);
                Assert.Equal(1, ActiveBorrows); Assert.False(closing.IsCompleted);
                object?[] args = [-1L, null, null]; Assert.False((bool)Call("TryTakePresentationWithPayloadBorrow", args)!);
                Assert.Null(args[2]); Assert.Equal(0, MetalVideoPresenter.SubmitOrderedGuestAction(static () => { }, "closed"));
            }
            Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount);
            ReleaseBorrow(borrow); Assert.True(closing.Wait(1500));
            Assert.False((bool)Field("_guestPresentationBorrowShutdownIncomplete").GetValue(null)!);
            Assert.Equal(before, GuestDataPool.DiagnosticStats().LeaseCount); AssertNoLivePayloadReceipts();
        }
        finally
        {
            foreach (var borrow in ((IEnumerable)Field("_activeGuestPresentationPayloadBorrows").GetValue(null)!).Cast<object>().ToArray()) ReleaseBorrow(borrow);
            if (closing is not null) Assert.True(closing.Wait(3000));
            GuestDataPool.Shared.Return(data);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloseDeadlineOrOwnThreadRetainsBorrowAndNativeOwnerMetadataWithoutPretendingDrain(bool selfClose)
    {
        using var state = new BorrowState(); var data = GuestDataPool.Shared.Rent(64);
        var cause = new InvalidOperationException("original GPU failure before close");
        try
        {
            var original = PublishBorrowedDraw(data); var borrow = TakePayloadBorrow(original);
            // Real admitted pending producer remains present if shutdown cannot
            // drain the CPU reader. No fabricated native handle/completion.
            {
                var read = state.AdmitPendingSource();
                var versions = ((IEnumerable)Field("_textureSourceVersions").GetValue(null)!).Cast<object>().Count();
                Field("_guestWorkFailure").SetValue(null, cause);
                Field("_guestPresentationBorrowShutdownTimeoutMilliseconds").SetValue(null, 30);
                if (selfClose) Call("CompletePresenterShutdown");
                else Assert.True(Task.Run(() => Call("CompletePresenterShutdown")).Wait(1500));
                Assert.True((bool)Field("_guestPresentationBorrowShutdownIncomplete").GetValue(null)!);
                Assert.Same(cause, Field("_guestWorkFailure").GetValue(null));
                Assert.Contains(selfClose ? "closing thread" : "timed out", Assert.IsType<InvalidOperationException>(Field("_textureSourceCleanupFailure").GetValue(null)).Message);
                Assert.Equal(versions, ((IEnumerable)Field("_textureSourceVersions").GetValue(null)!).Cast<object>().Count());
                Assert.Equal(1, ActiveBorrows); Assert.False((bool)Get(borrow, "Released")!);
                Assert.True((bool)Get(PayloadOwner(original), "Released")!);
                Assert.Single((IDictionary)Field("_activeGuestPayloadReceipts").GetValue(null)!);
                ReleaseBorrow(borrow); Call("CompletePresenterShutdown");
                Assert.False((bool)Field("_guestPresentationBorrowShutdownIncomplete").GetValue(null)!);
                Assert.Empty((IEnumerable)Field("_textureSourceVersions").GetValue(null)!);
                MetalVideoPresenter.ReleasePreparedTextureSources([read]);
            }
        }
        finally { GuestDataPool.Shared.Return(data); }
    }

    [Fact]
    public void FrameGpuFailureCancelsPublishersButBorrowReleaseKeepsCauseAcrossPartialReturnRerent()
    {
        using var state = new BorrowState(); var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
        lock (PoolGate)
        {
            var before = GuestDataPool.DiagnosticStats().LeaseCount; var data = GuestDataPool.Shared.Rent(64); byte[]? rerented = null;
            var cause = new NotSupportedException("actual presentation writer unsupported");
            var cleanup = new InvalidOperationException("post-return borrower cleanup failed");
            try
            {
                var original = PublishBorrowedDraw(data); var borrow = TakePayloadBorrow(original);
                var fatal = Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.RenderFrameSafely(() =>
                    Call("ExecutePresentationDraw", (nint)0, (nint)0, original)));
                // Reflection preserves the actual pre-native rejection inside
                // its invocation exception. No renderer/native execution claim.
                var originalFailure = Assert.IsType<TargetInvocationException>(fatal.InnerException);
                Assert.Contains("presentation", Assert.IsType<NotSupportedException>(originalFailure.InnerException).Message, StringComparison.OrdinalIgnoreCase);
                Assert.Same(originalFailure, Field("_guestWorkFailure").GetValue(null));
                Assert.True((bool)Get(PayloadOwner(original), "Released")!); Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount);
                Field("_textureSourceAfterCanceledPayloadReturn").SetValue(null, (Action)(() =>
                {
                    rerented = GuestDataPool.Shared.Rent(64); Assert.Same(data, rerented); throw cleanup;
                }));
                ReleaseBorrow(borrow, originalFailure);
                Assert.Same(originalFailure, Field("_guestWorkFailure").GetValue(null));
                Assert.Same(cleanup, Field("_textureSourceCleanupFailure").GetValue(null));
                Assert.Equal(0, ActiveBorrows); Assert.Single(reports); AssertNoLivePayloadReceipts();
                ReleaseBorrow(borrow, cause); Call("ReleaseGuestPayloadWork", original);
                Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount);
            }
            finally { Field("_textureSourceAfterCanceledPayloadReturn").SetValue(null, null); GuestDataPool.Shared.Return(rerented ?? data); }
        }
    }

    [Fact]
    public void BorrowCleanupFailureWithoutGpuCauseIsFatalAndWakesCloseWaiter()
    {
        using var state = new BorrowState(); var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
        var data = GuestDataPool.Shared.Rent(64); Task? closing = null;
        var cleanup = new InvalidOperationException("last borrower post-return failure");
        try
        {
            var original = PublishBorrowedDraw(data); var borrow = TakePayloadBorrow(original);
            closing = Task.Run(() => Call("CompletePresenterShutdown"));
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lock (Field("_gate").GetValue(null)!) return (int)Field("_guestPresentationBorrowShutdownWaiters").GetValue(null)! == 1;
            }, 1500));
            Field("_textureSourceAfterCanceledPayloadReturn").SetValue(null, (Action)(() => throw cleanup));
            var failure = Assert.Throws<TargetInvocationException>(() => ReleaseBorrow(borrow));
            Assert.Same(cleanup, Assert.IsType<GpuFatalException>(failure.InnerException).InnerException);
            Assert.True(closing.Wait(1500)); Assert.Equal(0, ActiveBorrows);
            Assert.Same(cleanup, Field("_guestWorkFailure").GetValue(null)); Assert.Single(reports);
            AssertNoLivePayloadReceipts();
        }
        finally
        {
            Field("_textureSourceAfterCanceledPayloadReturn").SetValue(null, null);
            foreach (var borrow in ((IEnumerable)Field("_activeGuestPresentationPayloadBorrows").GetValue(null)!).Cast<object>().ToArray()) ReleaseBorrow(borrow);
            if (closing is not null) Assert.True(closing.Wait(3000));
            GuestDataPool.Shared.Return(data);
        }
    }

    [Fact]
    public void UnregisteredBorrowCannotConsumePeersReceiptAndLimitDoesNotPartiallyPin()
    {
        using var state = new BorrowState(); lock (PoolGate)
        {
            var before = GuestDataPool.DiagnosticStats().LeaseCount; var data = GuestDataPool.Shared.Rent(64);
            try
            {
                var original = PublishBorrowedDraw(data);
                var borrowed = Enumerable.Range(0, 64).Select(_ => TakePayloadBorrow(original)).ToArray();
                var receipts = Get(borrowed[0], "Receipts");
                var forged = Activator.CreateInstance(borrowed[0].GetType(), original, receipts)!;
                var failure = Assert.Throws<TargetInvocationException>(() => ReleaseBorrow(forged));
                Assert.Contains("no live owner", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
                Assert.Equal(64, ActiveBorrows);
                object?[] args = [-1L, null, null];
                var limit = Assert.Throws<TargetInvocationException>(() => Call("TryTakePresentationWithPayloadBorrow", args));
                Assert.Contains("borrow limit", Assert.IsType<InvalidOperationException>(limit.InnerException).Message);
                Assert.Null(args[2]);
                var receipt = ((IEnumerable)receipts!).Cast<object>().Single(); Assert.Equal(65, Get(receipt, "References"));
                MetalVideoPresenter.Submit(new byte[64], 4, 4);
                foreach (var borrow in borrowed) ReleaseBorrow(borrow);
                Assert.Equal(before, GuestDataPool.DiagnosticStats().LeaseCount);
                Call("CompletePresenterShutdown"); AssertNoLivePayloadReceipts();
            }
            finally { GuestDataPool.Shared.Return(data); }
        }
    }

    [Fact]
    public void ConcurrentCloseCallsJoinExactAttemptInsteadOfDuplicatingNativeShutdown()
    {
        using var state = new BorrowState(); var data = GuestDataPool.Shared.Rent(64); Task? first = null, second = null;
        try
        {
            var original = PublishBorrowedDraw(data); var borrow = TakePayloadBorrow(original);
            first = Task.Run(() => Call("CompletePresenterShutdown"));
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lock (Field("_gate").GetValue(null)!) return (int)Field("_guestPresentationBorrowShutdownWaiters").GetValue(null)! == 1;
            }, 1500));
            var accepted = Field("_guestPresentationShutdownAttempt").GetValue(null)!;
            second = Task.Run(() => Call("CompletePresenterShutdown"));
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lock (Field("_gate").GetValue(null)!) return (int)Field("_guestPresentationShutdownJoinWaiters").GetValue(null)! == 1;
            }, 1500));
            lock (Field("_gate").GetValue(null)!)
            {
                Assert.Same(accepted, Field("_guestPresentationShutdownAttempt").GetValue(null));
                Assert.False((bool)Get(accepted, "Finished")!); Assert.False((bool)Get(accepted, "Completed")!);
                Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
            }
            ReleaseBorrow(borrow); Assert.True(Task.WaitAll([first, second], 1500));
            Assert.Same(accepted, Field("_guestPresentationShutdownAttempt").GetValue(null));
            Assert.True((bool)Get(accepted, "Finished")!); Assert.True((bool)Get(accepted, "Completed")!);
            Call("CompletePresenterShutdown"); Assert.Same(accepted, Field("_guestPresentationShutdownAttempt").GetValue(null));
            AssertNoLivePayloadReceipts();
        }
        finally
        {
            foreach (var borrow in ((IEnumerable)Field("_activeGuestPresentationPayloadBorrows").GetValue(null)!).Cast<object>().ToArray()) ReleaseBorrow(borrow);
            if (first is not null) Assert.True(first.Wait(3000));
            if (second is not null) Assert.True(second.Wait(3000));
            GuestDataPool.Shared.Return(data);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentCloseJoinTimeoutOrSelfBorrowCannotClaimAcceptedAttemptCompleted(bool selfJoin)
    {
        using var state = new BorrowState(); var data = GuestDataPool.Shared.Rent(64); Task? first = null;
        try
        {
            var original = PublishBorrowedDraw(data); var borrow = TakePayloadBorrow(original);
            first = Task.Run(() => Call("CompletePresenterShutdown"));
            Assert.True(SpinWait.SpinUntil(() =>
            {
                lock (Field("_gate").GetValue(null)!) return (int)Field("_guestPresentationBorrowShutdownWaiters").GetValue(null)! == 1;
            }, 1500));
            var accepted = Field("_guestPresentationShutdownAttempt").GetValue(null)!;
            Field("_guestPresentationBorrowShutdownTimeoutMilliseconds").SetValue(null, 30);
            if (selfJoin) Call("CompletePresenterShutdown");
            else Assert.True(Task.Run(() => Call("CompletePresenterShutdown")).Wait(1500));
            Assert.Same(accepted, Field("_guestPresentationShutdownAttempt").GetValue(null));
            Assert.False((bool)Get(accepted, "Finished")!); Assert.False((bool)Get(accepted, "Completed")!);
            Assert.False(first.IsCompleted); Assert.Equal(1, ActiveBorrows);
            Assert.Contains(selfJoin ? "own active" : "joining", Assert.IsType<InvalidOperationException>(Field("_textureSourceCleanupFailure").GetValue(null)).Message);
            ReleaseBorrow(borrow); Assert.True(first.Wait(1500));
            Assert.True((bool)Get(accepted, "Completed")!); AssertNoLivePayloadReceipts();
        }
        finally
        {
            foreach (var borrow in ((IEnumerable)Field("_activeGuestPresentationPayloadBorrows").GetValue(null)!).Cast<object>().ToArray()) ReleaseBorrow(borrow);
            if (first is not null) Assert.True(first.Wait(3000));
            GuestDataPool.Shared.Return(data);
        }
    }

    [Fact]
    public void DisabledGraphSelectionAddsNoBorrowAndKeepsOriginalPublicationBehavior()
    {
        using var state = new BorrowState(); Field("_textureSourceGraphEnabled").SetValue(null, false);
        var data = new byte[64]; var presentation = PublishBorrowedDraw(data);
        try
        {
            object?[] args = [-1L, null, null]; Assert.True((bool)Call("TryTakePresentationWithPayloadBorrow", args)!);
            Assert.Same(presentation, args[1]); Assert.Null(args[2]); Assert.Equal(0, ActiveBorrows);
            AssertNoLivePayloadReceipts();
        }
        finally { Field("_textureSourceGraphEnabled").SetValue(null, true); }
    }
}
