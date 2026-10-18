// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Scheduling;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

public sealed partial class MetalRenderHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactPrefixBarrierIncludesPendingLaterAndNestedRendererFollowups(bool attachDuringDescendant)
    {
        using var state = CompletionState(); state.Set("_renderOwner", null);
        state.Set("_lastFlushedCommandBuffer", (nint)123);
        state.Set("_guestCommandBufferStatusOverride", (Func<nint, nint>)(_ => 4));
        state.Set("_guestCommandBufferWaitOverride", (Action<nint>)(_ => throw new Exception("status4 must not wait")));
        using var producerEntered = new ManualResetEventSlim(); using var releaseProducer = new ManualResetEventSlim();
        using var childEntered = new ManualResetEventSlim(); using var releaseChild = new ManualResetEventSlim();
        using var futureEntered = new ManualResetEventSlim(); using var releaseFuture = new ManualResetEventSlim();
        var effects = new List<string>();
        using var queue = MetalVideoPresenter.EnterGuestQueue("dcb.graphics", 107);
        MetalVideoPresenter.BeginGuestSubmissionPrefixCapture(0, 107);
        var prefix = MetalVideoPresenter.SubmitOrderedGuestAction(() =>
        {
            Assert.True(MetalVideoPresenter.SubmitOrderedGuestCompletion(() =>
            {
                effects.Add("label");
                Assert.True(MetalVideoPresenter.SubmitOrderedGuestAction(() => effects.Add("nested writeback"), "nested renderer followup") > 0);
                childEntered.Set(); Assert.True(releaseChild.Wait(TimeSpan.FromSeconds(2)));
            }, "renderer label followup") > 0);
            producerEntered.Set(); Assert.True(releaseProducer.Wait(TimeSpan.FromSeconds(2)));
            // Created after End in the first row; it must still precede the barrier.
            Assert.True(MetalVideoPresenter.SubmitOrderedGuestAction(() => effects.Add("later writeback"), "later renderer followup") > 0);
        }, "captured producer");
        // A thread of its own: a Task.Run awaited with GetResult can be inlined onto this
        // thread, whose prefix capture (queue 107) would then refuse queue 108.
        long future = 0;
        var submitter = new Thread(() =>
        {
            using var laterQueue = MetalVideoPresenter.EnterGuestQueue("dcb.graphics", 108);
            future = MetalVideoPresenter.SubmitOrderedGuestAction(() =>
            { futureEntered.Set(); Assert.True(releaseFuture.Wait(TimeSpan.FromSeconds(2))); }, "unrelated future work");
        });
        submitter.Start();
        Assert.True(submitter.Join(TimeSpan.FromSeconds(10)));
        Assert.True(future > prefix);
        var drain = Task.Run(() => { for (var i = 0; i < 8; i++) MetalVideoPresenter.DrainGuestWork(0, 0); });
        try
        {
            Assert.True(producerEntered.Wait(TimeSpan.FromSeconds(2)));
            if (attachDuringDescendant)
            {
                releaseProducer.Set(); Assert.True(childEntered.Wait(TimeSpan.FromSeconds(2)));
            }
            var completion = Assert.IsAssignableFrom<IGuestSubmissionPrefixCompletion>(MetalVideoPresenter.EndGuestSubmissionPrefixCapture(true));
            var barrier = (long)completion.GetType().GetField("Sequence", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(completion)!;
            var waiter = Task.Run(completion.WaitUntilCompleted);
            releaseProducer.Set(); Assert.True(childEntered.Wait(TimeSpan.FromSeconds(2)));
            // Deterministic assertion: a wrongly prepended barrier has already
            // completed before the child can enter, regardless of task scheduling.
            Assert.False(MetalVideoPresenter.WaitForGuestWork(barrier, 0));
            Assert.False(waiter.IsCompleted); Assert.False(futureEntered.IsSet);
            releaseChild.Set(); Assert.True(futureEntered.Wait(TimeSpan.FromSeconds(2)));
            await waiter.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(new[] { "label", "nested writeback", "later writeback" }, effects);
            Assert.False(MetalVideoPresenter.WaitForGuestWork(future, 0));
        }
        finally
        {
            releaseProducer.Set(); releaseChild.Set(); releaseFuture.Set();
            await drain.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void ExactTakenPrefixCanAttachBarrierBetweenActionExecutionAndCompletionPublication()
    {
        using var state = CompletionState(); state.Set("_renderOwner", null);
        using var queue = MetalVideoPresenter.EnterGuestQueue("dcb.graphics", 106);
        MetalVideoPresenter.BeginGuestSubmissionPrefixCapture(0, 106);
        var effects = 0;
        var sequence = MetalVideoPresenter.SubmitOrderedGuestAction(() => effects++, "taken prefix");
        object?[] taken = [null];
        Assert.True((bool)InvokePrefixPresenter("TryTakeGuestWork", taken)!);
        var pending = taken[0]!;
        InvokePrefixPresenter("ExecuteOrderedGuestAction", [pending.GetType().GetProperty("Work")!.GetValue(pending)]);
        Assert.Equal(1, effects);
        Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
        var completion = Assert.IsAssignableFrom<IGuestSubmissionPrefixCompletion>(MetalVideoPresenter.EndGuestSubmissionPrefixCapture(true));
        InvokePrefixPresenter("CompleteGuestWork", [pending]);
        DrainCompletions(); completion.WaitUntilCompleted();
        Assert.Equal(1, effects); Assert.True(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
    }

    [Fact]
    public async Task ExactCapturedMetalPrefixCompletesBeforeUnexecutedFutureSameQueueWork()
    {
        using var state = CompletionState();
        state.Set("_renderOwner", null);
        state.Set("_lastFlushedCommandBuffer", (nint)123);
        var statusCalls = 0;
        state.Set("_guestCommandBufferStatusOverride", (Func<nint, nint>)(_ => { statusCalls++; return 4; }));
        state.Set("_guestCommandBufferWaitOverride", (Action<nint>)(_ => throw new Exception("status4 must not wait")));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var queue = MetalVideoPresenter.EnterGuestQueue("dcb.graphics", 101);
        MetalVideoPresenter.BeginGuestSubmissionPrefixCapture(0, 101);
        var prefixRan = false;
        var prefix = MetalVideoPresenter.SubmitOrderedGuestAction(() => prefixRan = true, "captured prefix");
        // A thread of its own: a Task.Run awaited with GetResult can be inlined onto this
        // thread, whose prefix capture (queue 101) would then refuse queue 102.
        long future = 0;
        var submitter = new Thread(() =>
        {
            using var laterQueue = MetalVideoPresenter.EnterGuestQueue("dcb.graphics", 102);
            future = MetalVideoPresenter.SubmitOrderedGuestAction(() => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(2))); }, "future work");
        });
        submitter.Start();
        Assert.True(submitter.Join(TimeSpan.FromSeconds(10)));
        Assert.True(future > prefix);
        var completion = Assert.IsAssignableFrom<IGuestSubmissionPrefixCompletion>(MetalVideoPresenter.EndGuestSubmissionPrefixCapture(true));
        var wait = Task.Run(completion.WaitUntilCompleted);
        var drain = Task.Run(DrainCompletions);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            await wait.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(prefixRan); Assert.True(statusCalls > 0);
            Assert.False(MetalVideoPresenter.WaitForGuestWork(future, 0));
        }
        finally { release.Set(); await drain.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public void FailedRealMetalWaitBoundaryCannotPublishPrefixLabelOrCertifyCompletion()
    {
        using var state = CompletionState(); state.Set("_renderOwner", null);
        state.Set("_lastFlushedCommandBuffer", (nint)123);
        state.Set("_guestCommandBufferStatusOverride", (Func<nint, nint>)(_ => 5));
        state.Set("_guestCommandBufferWaitOverride", (Action<nint>)(_ => throw new Exception("error must not wait")));
        using var queue = MetalVideoPresenter.EnterGuestQueue("dcb.graphics", 103);
        MetalVideoPresenter.BeginGuestSubmissionPrefixCapture(0, 103);
        var label = false;
        _ = MetalVideoPresenter.SubmitOrderedGuestCompletion(() => label = true, "prefix EOP");
        var completion = Assert.IsAssignableFrom<IGuestSubmissionPrefixCompletion>(MetalVideoPresenter.EndGuestSubmissionPrefixCapture(true));
        var fatal = Assert.Throws<GpuFatalException>(DrainCompletions);
        var original = Assert.IsType<InvalidOperationException>(fatal.InnerException);
        Assert.Same(original, Assert.Throws<InvalidOperationException>(completion.WaitUntilCompleted));
        Assert.False(label);
    }

    [Fact]
    public void ExactPrefixCallerRefusesClosedLifetimeWithoutNativeSelectors()
    {
        using var state = CompletionState(); state.Set("_renderOwner", null);
        using var queue = MetalVideoPresenter.EnterGuestQueue("dcb.graphics", 104);
        MetalVideoPresenter.BeginGuestSubmissionPrefixCapture(0, 104);
        _ = MetalVideoPresenter.SubmitOrderedGuestAction(() => throw new Exception("must not run"), "closed prefix");
        var completion = Assert.IsAssignableFrom<IGuestSubmissionPrefixCompletion>(MetalVideoPresenter.EndGuestSubmissionPrefixCapture(true));
        state.Set("_closed", true);
        Assert.Throws<InvalidOperationException>(completion.WaitUntilCompleted);
    }

    [Fact]
    public void ActualRenderOwnerNeverWaitsForItsOwnPrefix()
    {
        using var state = CompletionState();
        using var queue = MetalVideoPresenter.EnterGuestQueue("dcb.graphics", 105);
        MetalVideoPresenter.BeginGuestSubmissionPrefixCapture(0, 105);
        _ = MetalVideoPresenter.SubmitOrderedGuestAction(() => { }, "self prefix");
        var completion = Assert.IsAssignableFrom<IGuestSubmissionPrefixCompletion>(MetalVideoPresenter.EndGuestSubmissionPrefixCapture(true));
        state.Set("_renderOwner", Thread.CurrentThread);
        var failure = Assert.Throws<InvalidOperationException>(completion.WaitUntilCompleted);
        Assert.Contains("render owner", failure.Message);
    }

    private static object? InvokePrefixPresenter(string name, object?[] arguments)
    {
        try { return typeof(MetalVideoPresenter).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
        { ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
    }
}
