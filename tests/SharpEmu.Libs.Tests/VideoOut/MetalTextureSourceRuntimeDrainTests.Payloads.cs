// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalTextureSourceRuntimeDrainTests
{
    private static object PoolGate => GuestDataPool.Shared.GetType().GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(GuestDataPool.Shared)!;
    private static object PayloadOwner(object work)
    {
        var table = Field("_guestPayloadOwners").GetValue(null)!;
        object?[] args = [work, null]; Assert.True((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!); return args[1]!;
    }
    private static object PayloadDraw(object work) => work.GetType().GetProperty("Draw")!.GetValue(work)!;
    private static void SubmitPooledColor(byte[][] arrays, GuestIndexBuffer? index = null)
    {
        MetalVideoPresenter.SubmitOffscreenTranslatedDraw(new(new Gen5MslShader("", "managed invalid color", Gen5MslStage.Pixel, 0)), [],
            arrays.Select(data => new GuestMemoryBuffer(0, data, data.Length, (ulong)data.Length, true)).ToArray(), 0,
            [ControlColorTarget()], new(new Gen5MslShader("", "managed vertex", Gen5MslStage.Vertex, 0)), 6, 1, 4, index,
            [new GuestVertexBuffer(0, 4, 0, 0, 0, 16, 0, arrays[0], arrays[0].Length, true)], null,
            new GuestDepthTarget(Address + 0x1000, Address + 0x1000, 4, 4, 3, 0, 1, false), 0);
    }
    private static void AssertNoLivePayloadReceipts()
    {
        Assert.Empty((IDictionary)Field("_activeGuestPayloadReceipts").GetValue(null)!);
        Assert.Empty((IEnumerable)Field("_activeGuestPayloadOwners").GetValue(null)!);
    }

    [Fact]
    public void ActualTakenColorFatalReturnsSharedGlobalVertexIndexOnceAndCannotStealRerent()
    {
        using var state = new State(); var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
        lock (PoolGate)
        {
            var before = GuestDataPool.DiagnosticStats().LeaseCount;
            var data = GuestDataPool.Shared.Rent(64); byte[]? rerented = null;
            var index = new GuestIndexBuffer(data, 64, false, true, new GuestIndexBufferLease(data));
            try
            {
                SubmitPooledColor([data], index); var work = LastQueuedWork(); var draw = PayloadDraw(work);
                var failure = Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
                Assert.Contains("no depth", Assert.IsType<NotSupportedException>(failure.InnerException).Message);
                Assert.Single(reports); Assert.True(index.LeaseReturned); AssertNoLivePayloadReceipts();
                Assert.Equal(before, GuestDataPool.DiagnosticStats().LeaseCount);
                rerented = GuestDataPool.Shared.Rent(64); Assert.Same(data, rerented);
                Call("ReturnPooledGuestData", draw); Assert.False(index.TryReturnPooledData());
                Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount);
                Call("CompletePresenterShutdown"); Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount);
            }
            finally { GuestDataPool.Shared.Return(rerented ?? data); }
        }
    }

    [Fact]
    public void ActualTakenFatalPartialReturnRerentPreservesGpuCauseAndRemainingCleanup()
    {
        using var state = new State(); var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
        var oldHook = Field("_textureSourceAfterCanceledPayloadReturn").GetValue(null);
        var oldCleanup = Field("_textureSourceCleanupFailure").GetValue(null);
        lock (PoolGate)
        {
            var before = GuestDataPool.DiagnosticStats().LeaseCount;
            var first = GuestDataPool.Shared.Rent(64); var second = GuestDataPool.Shared.Rent(128); byte[]? rerented = null;
            var cleanup = new InvalidOperationException("failure after real taken-work pool return and rerent");
            try
            {
                SubmitPooledColor([first, second]); var work = LastQueuedWork(); var owner = PayloadOwner(work);
                var returns = 0;
                Field("_textureSourceAfterCanceledPayloadReturn").SetValue(null, (Action)(() =>
                {
                    if (++returns != 1) return;
                    var receipts = (IEnumerable)Get(owner, "Receipts")!;
                    var returned = receipts.Cast<object>().Single(receipt => (bool)Get(receipt, "Returned")!);
                    var data = (byte[])Get(returned, "Data")!;
                    rerented = GuestDataPool.Shared.Rent(data.Length); Assert.Same(data, rerented);
                    throw cleanup;
                }));
                var fatal = Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
                Assert.Contains("no depth", Assert.IsType<NotSupportedException>(fatal.InnerException).Message);
                Assert.Same(fatal.InnerException, Field("_guestWorkFailure").GetValue(null));
                Assert.Same(cleanup, Field("_textureSourceCleanupFailure").GetValue(null));
                Assert.Equal(2, returns); Assert.Single(reports); AssertNoLivePayloadReceipts();
                Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount);
                Call("ReturnPooledGuestData", PayloadDraw(work)); Call("CompletePresenterShutdown");
                Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount);
                Assert.NotNull(rerented);
            }
            finally
            {
                Field("_textureSourceAfterCanceledPayloadReturn").SetValue(null, oldHook);
                Field("_textureSourceCleanupFailure").SetValue(null, oldCleanup);
                if (rerented is not null) GuestDataPool.Shared.Return(rerented);
                // Originals may now name a newer rent; that rent was released
                // above first. Only final test teardown, never production retry.
                GuestDataPool.Shared.Return(first); GuestDataPool.Shared.Return(second);
            }
        }
    }

    [Fact]
    public void SharedComputeAndColorPayloadDefersCanonicalIndexReturnUntilLastWork()
    {
        using var state = new State(); lock (PoolGate)
        {
            var before = GuestDataPool.DiagnosticStats().LeaseCount; var data = GuestDataPool.Shared.Rent(64);
            var index = new GuestIndexBuffer(data, 64, false, true, new GuestIndexBufferLease(data));
            try
            {
                var first = state.AdmitComputePayload([new GuestMemoryBuffer(0, data, 64, 64, true)]);
                SubmitPooledColor([data], index); var second = LastQueuedWork();
                Call("ReturnPooledComputeData", first);
                Assert.False(index.LeaseReturned); Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount);
                Call("ReturnPooledGuestData", PayloadDraw(second));
                Assert.True(index.LeaseReturned); Assert.Equal(before, GuestDataPool.DiagnosticStats().LeaseCount);
                Call("ReturnPooledComputeData", first); Call("ReturnPooledGuestData", PayloadDraw(second));
                AssertNoLivePayloadReceipts();
                Call("CompletePresenterShutdown"); Assert.Equal(before, GuestDataPool.DiagnosticStats().LeaseCount);
            }
            finally { GuestDataPool.Shared.Return(data); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexLeaseRejectionLeavesExistingOrRerentedOwnerUntouched(bool alreadyReturned)
    {
        using var state = new State(); lock (PoolGate)
        {
            var before = GuestDataPool.DiagnosticStats().LeaseCount; var data = GuestDataPool.Shared.Rent(64); byte[]? rerented = null;
            var accepted = new GuestIndexBufferLease(data);
            try
            {
                if (alreadyReturned) { Assert.True(accepted.TryReturn()); rerented = GuestDataPool.Shared.Rent(64); Assert.Same(data, rerented); }
                else SubmitPooledColor([data], new(data, 64, false, true, accepted));
                var count = (int)Field("_pendingGuestWorkCount").GetValue(null)!;
                var conflicting = alreadyReturned ? accepted : new GuestIndexBufferLease(data);
                var error = Assert.Throws<InvalidOperationException>(() => SubmitPooledColor([data], new(data, 64, false, true, conflicting)));
                Assert.Contains(alreadyReturned ? "already-returned" : "conflicting", error.Message);
                Assert.Equal(count, Field("_pendingGuestWorkCount").GetValue(null));
                Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount);
                Call("CompletePresenterShutdown"); AssertNoLivePayloadReceipts();
                Assert.Equal(alreadyReturned ? before + 1 : before, GuestDataPool.DiagnosticStats().LeaseCount);
            }
            finally { GuestDataPool.Shared.Return(rerented ?? data); }
        }
    }

    [Fact]
    public void CompletedWorkCannotRecaptureRerentedArrayOrBeAdmittedTwice()
    {
        using var state = new State(); lock (PoolGate)
        {
            var before = GuestDataPool.DiagnosticStats().LeaseCount; var data = GuestDataPool.Shared.Rent(64); byte[]? rerented = null;
            try
            {
                var old = state.AdmitComputePayload([new GuestMemoryBuffer(0, data, 64, 64, true)]);
                object?[] take = [null]; Assert.True((bool)Call("TryTakeGuestWork", take)!);
                Call("ReturnPooledComputeData", old); Call("CompleteGuestWork", take[0]);
                rerented = GuestDataPool.Shared.Rent(64); Assert.Same(data, rerented);
                var current = state.AdmitComputePayload([new GuestMemoryBuffer(0, rerented, 64, 64, true)]);
                var rejection = Assert.Throws<TargetInvocationException>(() => Call("EnqueueGuestWorkLocked", old));
                Assert.Contains("transferred twice", Assert.IsType<InvalidOperationException>(rejection.InnerException).Message);
                Call("ReturnPooledComputeData", old);
                Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount);
                Assert.False((bool)Get(PayloadOwner(current), "Released")!);
                Call("CompletePresenterShutdown"); Assert.Equal(before, GuestDataPool.DiagnosticStats().LeaseCount); AssertNoLivePayloadReceipts();
            }
            finally { GuestDataPool.Shared.Return(rerented ?? data); }
        }
    }

    [Fact]
    public void PresentationRepeatedFramesAndSharedReplacementRetainCpuPayloadUntilClose()
    {
        using var state = new State(); lock (PoolGate)
        {
            var before = GuestDataPool.DiagnosticStats().LeaseCount; var data = GuestDataPool.Shared.Rent(64);
            void Submit() => MetalVideoPresenter.SubmitTranslatedDraw(new(new Gen5MslShader("", "managed presentation", Gen5MslStage.Pixel, 0)), [],
                [new GuestMemoryBuffer(0, data, 64, 64, true)], 4, 4, 0, null, 6, 1, 4, null, null, null);
            try
            {
                Submit(); var old = Field("_latestPresentation").GetValue(null)!;
                var draw = old.GetType().GetProperty("TranslatedDraw")!.GetValue(old)!;
                for (var i = 0; i < 3; i++) Call("ReturnPooledGuestData", draw);
                Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount);
                Submit(); Assert.NotSame(old, Field("_latestPresentation").GetValue(null));
                Assert.True((bool)Get(PayloadOwner(old), "Released")!);
                Call("ReturnPooledGuestData", draw); Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount);
                MetalVideoPresenter.SubmitGuestDraw(GuestDrawKind.FullscreenBarycentric, 4, 4);
                Assert.Equal(before, GuestDataPool.DiagnosticStats().LeaseCount);
                Call("CompletePresenterShutdown"); AssertNoLivePayloadReceipts();
            }
            finally { GuestDataPool.Shared.Return(data); }
        }
    }
    [Fact]
    public void RejectedSourceRetainRollsBackRegisteredPayloadWithoutReturningCallerLease()
    {
        using var state = new State(); lock (PoolGate)
        {
            var before = GuestDataPool.DiagnosticStats().LeaseCount;
            var data = GuestDataPool.Shared.Rent(64);
            try
            {
                var source = state.AdmitPendingSource();
                MetalVideoPresenter.ReleasePreparedTextureSources([source]);
                var oldCount = (int)Field("_pendingGuestWorkCount").GetValue(null)!;
                var shader = new MetalCompiledGuestShader(new Gen5MslShader("", "rejected retain", Gen5MslStage.Compute, 0));
                Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.SubmitComputeDispatch(0, shader, [source],
                    [new GuestMemoryBuffer(0, data, 64, 64, true)], 1, 1, 1, 0, 0, 0, true, null));
                Assert.Equal(oldCount, Field("_pendingGuestWorkCount").GetValue(null));
                Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount);
                Assert.Empty((IDictionary)Field("_activeGuestPayloadReceipts").GetValue(null)!);
                Call("CompletePresenterShutdown");
                Assert.Equal(before + 1, GuestDataPool.DiagnosticStats().LeaseCount); AssertNoLivePayloadReceipts();
            }
            finally { GuestDataPool.Shared.Return(data); }
        }
    }

    [Fact]
    public void MismatchedIndexLeaseDataRejectsBeforeTransferAndLeavesBothCallerLeasesUntouched()
    {
        using var state = new State(); lock (PoolGate)
        {
            var before = GuestDataPool.DiagnosticStats().LeaseCount;
            var data = GuestDataPool.Shared.Rent(64); var different = GuestDataPool.Shared.Rent(128);
            var lease = new GuestIndexBufferLease(different);
            try
            {
                Assert.True(lease.Owns(different)); Assert.False(lease.Owns(data));
                var error = Assert.Throws<InvalidOperationException>(() => SubmitPooledColor([data], new(data, 64, false, true, lease)));
                Assert.Contains("mismatched lease/data", error.Message);
                Assert.False(lease.Returned); Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null));
                AssertNoLivePayloadReceipts(); Assert.Equal(before + 2, GuestDataPool.DiagnosticStats().LeaseCount);
                Call("CompletePresenterShutdown"); Assert.Equal(before + 2, GuestDataPool.DiagnosticStats().LeaseCount);
            }
            finally { GuestDataPool.Shared.Return(data); lease.TryReturn(); }
        }
    }

}
