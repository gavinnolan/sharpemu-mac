// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

public sealed partial class MetalRenderHostTests
{
    [Fact]
    public void InitialCompletionBlocksAnotherPayloadUntilItsOutsideGateCallbackFinishes()
    {
        using var state = CompletionState();
        state.Set("_thread", null);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var address = MemoryBase + 0x8000;
        var first = CompletionWorker(() => MetalVideoPresenter.SubmitOrderedGuestCompletion(() =>
        {
            Assert.False(Monitor.IsEntered(CompletionGate));
            entered.Set();
            Assert.True(release.Wait(10000));
            InvokeLabelWriter(_host, new EndOfPipeWrite(EndOfPipeWriteKind.Write64, 7, address, 1));
        }, "first initial payload"));
        Task<Exception?>? second = null;
        try
        {
            Assert.True(entered.Wait(10000));
            var receipt = ReadCompletionState("_initialGuestCompletion");
            Assert.NotNull(receipt);
            second = CompletionWorker(() => _host.RecordEndOfPipe(new EndOfPipeWrite(EndOfPipeWriteKind.Write64, 7, address, 42)));
            WaitForInitialCompletionWaiters(1);
            Assert.Equal(0UL, CompletionLabel(address));
            Assert.Same(receipt, ReadCompletionState("_initialGuestCompletion"));
            release.Set();
            Assert.Null(WorkerResult(first));
            Assert.Null(WorkerResult(second));
            Assert.Equal(42UL, CompletionLabel(address));
            Assert.Null(ReadCompletionState("_initialGuestCompletion"));
        }
        finally { release.Set(); JoinCompletionWorkers(first, second); }
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("publication")]
    [InlineData("close")]
    public void InitialCallbackBarriersActualAdmissionPublicationAndClose(string operation)
    {
        using var state = CompletionState();
        state.Set("_thread", null);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = CompletionWorker(() => MetalVideoPresenter.SubmitOrderedGuestCompletion(() =>
        { entered.Set(); Assert.True(release.Wait(10000)); }, "paused initial"));
        Task<Exception?>? later = null;
        var executed = false;
        try
        {
            Assert.True(entered.Wait(10000));
            later = CompletionWorker(() =>
            {
                if (operation == "close") InvokeCompletionMethod("CompletePresenterShutdown");
                else lock (CompletionGate)
                {
                    if (operation == "publication") InvokeCompletionMethod("PublishGuestPresentationLocked", InitialControlPresentation());
                    else InvokeCompletionMethod("EnqueueGuestWorkLocked", InitialControlAction(() => executed = true));
                }
            });
            WaitForInitialCompletionWaiters(1); // actual production Monitor.Wait, not a scheduling delay.
            Assert.False((bool)ReadCompletionState("_closed")!);
            Assert.Null(ReadCompletionState("_latestPresentation"));
            Assert.Equal(0L, ReadCompletionState("_enqueuedGuestWorkSequence"));
            // The waiter dropped _gate: the test can inspect the exact live receipt.
            Assert.NotNull(ReadCompletionState("_initialGuestCompletion"));
            release.Set();
            Assert.Null(WorkerResult(first));
            Assert.Null(WorkerResult(later));
            if (operation == "close") Assert.True((bool)ReadCompletionState("_closed")!);
            else if (operation == "publication") Assert.NotNull(ReadCompletionState("_latestPresentation"));
            else
            {
                Assert.Equal(1L, ReadCompletionState("_enqueuedGuestWorkSequence"));
                Assert.False(executed);
                DrainCompletions();
                Assert.True(executed);
            }
        }
        finally { release.Set(); JoinCompletionWorkers(first, later); }
    }

    [Fact]
    public void InitialCallbackFailurePreventsActualStartupWithoutNativeCalls()
    {
        using var state = CompletionState();
        state.Set("_thread", null);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var original = new InvalidOperationException("initial memory publication failed");
        var first = CompletionWorker(() => MetalVideoPresenter.SubmitOrderedGuestCompletion(() =>
        { entered.Set(); Assert.True(release.Wait(10000)); throw original; }, "failed initial"));
        Task<Exception?>? startup = null;
        try
        {
            Assert.True(entered.Wait(10000));
            startup = CompletionWorker(() => { lock (CompletionGate) InvokeCompletionMethod("StartPresenterLocked"); });
            WaitForInitialCompletionWaiters(1);
            Assert.Null(ReadCompletionState("_thread"));
            release.Set();
            Assert.Same(original, Assert.IsType<GpuFatalException>(WorkerResult(first)).InnerException);
            Assert.Same(original, WorkerResult(startup));
            Assert.Null(ReadCompletionState("_thread"));
            Assert.Equal((nint)0, ReadCompletionState("_device"));
        }
        finally { release.Set(); JoinCompletionWorkers(first, startup); }
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("translated")]
    [InlineData("fixed")]
    [InlineData("BGRA")]
    [InlineData("splash")]
    [InlineData("image")]
    public void DirectPresentationCallersWaitBeforeDerivingOrPublishingReplacementMetadata(string caller)
    {
        using var state = CompletionState();
        state.Set("_thread", null);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var original = new InvalidOperationException("paused initial publisher fails");
        var first = CompletionWorker(() => MetalVideoPresenter.SubmitOrderedGuestCompletion(() =>
        { entered.Set(); Assert.True(release.Wait(10000)); throw original; }, "direct caller barrier"));
        Task<Exception?>? later = null;
        try
        {
            Assert.True(entered.Wait(10000));
            later = CompletionWorker(() =>
            {
                switch (caller)
                {
                    case "initialize": MetalVideoPresenter.EnsureStarted(1, 1); break;
                    case "translated": SubmitCompletionPresentation([]); break;
                    case "fixed": MetalVideoPresenter.SubmitGuestDraw(GuestDrawKind.FullscreenBarycentric, 1, 1); break;
                    case "BGRA": MetalVideoPresenter.Submit(new byte[4], 1, 1); break;
                    case "splash": MetalVideoPresenter.HideSplashScreen(); break;
                    case "image": MetalVideoPresenter.TrySubmitGuestImage(1, 1, 1, 1); break;
                    default: throw new InvalidOperationException(caller);
                }
            });
            WaitForInitialCompletionWaiters(1);
            Assert.Null(ReadCompletionState("_latestPresentation"));
            Assert.Equal(0L, ReadCompletionState("_unorderedPresentationWriteSequence"));
            Assert.Null(ReadCompletionState("_thread"));
            release.Set();
            Assert.Same(original, Assert.IsType<GpuFatalException>(WorkerResult(first)).InnerException);
            Assert.Same(original, WorkerResult(later));
            Assert.Null(ReadCompletionState("_latestPresentation"));
            Assert.Null(ReadCompletionState("_thread"));
        }
        finally { release.Set(); JoinCompletionWorkers(first, later); }
    }

    [Theory]
    [InlineData("completion")]
    [InlineData("startup")]
    [InlineData("admission")]
    [InlineData("publication")]
    [InlineData("close")]
    [InlineData("drain")]
    public void InitialReceiptRejectsSelfReentryWithoutReleasingTheExactOwner(string operation)
    {
        using var state = CompletionState();
        state.Set("_thread", null);
        var fatal = Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.SubmitOrderedGuestCompletion(() =>
        {
            var receipt = ReadCompletionState("_initialGuestCompletion");
            Assert.NotNull(receipt);
            Assert.ThrowsAny<Exception>(() =>
            {
                if (operation == "completion") MetalVideoPresenter.SubmitOrderedGuestCompletion(() => Assert.Fail("must not run"), "nested");
                else if (operation == "close") InvokeCompletionMethod("CompletePresenterShutdown");
                else if (operation == "drain") DrainCompletions();
                else lock (CompletionGate)
                {
                    if (operation == "startup") InvokeCompletionMethod("StartPresenterLocked");
                    else if (operation == "publication") InvokeCompletionMethod("PublishGuestPresentationLocked", InitialControlPresentation());
                    else InvokeCompletionMethod("EnqueueGuestWorkLocked", InitialControlAction(() => Assert.Fail("must not execute")));
                }
            });
            Assert.Same(receipt, ReadCompletionState("_initialGuestCompletion"));
            Assert.False((bool)ReadCompletionState("_closed")!);
            Assert.Null(ReadCompletionState("_latestPresentation"));
            Assert.Equal(0L, ReadCompletionState("_enqueuedGuestWorkSequence"));
        }, "self-reentry"));
        Assert.Contains("self-reentry", fatal.InnerException!.Message);
        Assert.Null(ReadCompletionState("_initialGuestCompletion"));
    }

    [Fact]
    public void CloseDeadlineDoesNotStealCallbackMemoryAndRetryKeepsOriginalFailure()
    {
        using var state = CompletionState();
        state.Set("_thread", null);
        state.Set("_initialGuestCompletionTimeoutMilliseconds", 100);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = CompletionWorker(() => MetalVideoPresenter.SubmitOrderedGuestCompletion(() =>
        { entered.Set(); Assert.True(release.Wait(3000)); }, "deadline owner"));
        try
        {
            Assert.True(entered.Wait(10000));
            var receipt = ReadCompletionState("_initialGuestCompletion");
            var failure = Assert.Throws<InvalidOperationException>(() => InvokeCompletionMethod("CompletePresenterShutdown"));
            Assert.Contains("deadline expired", failure.Message);
            Assert.Same(receipt, ReadCompletionState("_initialGuestCompletion"));
            Assert.False((bool)ReadCompletionState("_closed")!);
            release.Set();
            Assert.Same(failure, Assert.IsType<GpuFatalException>(WorkerResult(first)).InnerException);
            Assert.Null(ReadCompletionState("_initialGuestCompletion"));
            InvokeCompletionMethod("CompletePresenterShutdown");
            Assert.True((bool)ReadCompletionState("_closed")!);
            Assert.Same(failure, ReadCompletionState("_guestWorkFailure"));
        }
        finally { release.Set(); JoinCompletionWorkers(first); }
    }

    [Theory]
    [InlineData(true, true, 4, true)]
    [InlineData(false, true, 4, false)]
    [InlineData(true, false, 4, false)]
    [InlineData(true, true, 0, false)]
    public void ActualPresentationWritebackIntentVetoesOnlyUnorderedNonemptyWriters(bool writable, bool writeback, int length, bool rejected)
    {
        using var state = CompletionState();
        var buffer = new GuestMemoryBuffer(MemoryBase + 0x8000, new byte[4], length, 4, Pooled: false, Writable: writable, WriteBackToGuest: writeback);
        SubmitCompletionPresentation([buffer]);
        var address = MemoryBase + 0x8100;
        var write = new EndOfPipeWrite(EndOfPipeWriteKind.Write64, 7, address, 42);
        if (rejected)
        {
            var fatal = Assert.Throws<GpuFatalException>(() => _host.RecordEndOfPipe(write));
            Assert.Contains("unordered writable presentation", fatal.InnerException!.Message);
            Assert.Equal(0UL, CompletionLabel(address));
            Assert.Equal(0L, ReadCompletionState("_enqueuedGuestWorkSequence"));
        }
        else
        {
            _host.RecordEndOfPipe(write);
            DrainCompletions();
            Assert.Equal(42UL, CompletionLabel(address));
        }
    }

    [Theory]
    [InlineData(GuestImageAccess.Read, false)]
    [InlineData(GuestImageAccess.Write, true)]
    [InlineData(GuestImageAccess.Atomic, true)]
    public void ActualPresentationImageIntentIsNotInferredFromStorageClass(GuestImageAccess access, bool rejected)
    {
        using var state = CompletionState();
        SubmitCompletionPresentation([], [new GuestDrawTexture(1, 1, 1, 10, 0, new byte[4], false, true, Access: access)]);
        var address = MemoryBase + 0x8100;
        if (rejected) Assert.Throws<GpuFatalException>(() => _host.RecordEndOfPipe(new(EndOfPipeWriteKind.Write64, 7, address, 42)));
        else { _host.RecordEndOfPipe(new(EndOfPipeWriteKind.Write64, 7, address, 42)); DrainCompletions(); Assert.Equal(42UL, CompletionLabel(address)); }
    }

    [Fact]
    public void ReplacementAndCloseCannotEraseUnconfirmedPresentationWriteDebt()
    {
        using var state = CompletionState();
        SubmitCompletionPresentation([new GuestMemoryBuffer(1, new byte[4], 4, 4, false, Writable: true)]);
        var debt = ReadCompletionState("_unorderedPresentationWriteSequence");
        MetalVideoPresenter.SubmitGuestDraw(GuestDrawKind.FullscreenBarycentric, 1, 1); // no guest output buffers.
        Assert.Equal(debt, ReadCompletionState("_unorderedPresentationWriteSequence"));
        var fatal = Assert.Throws<GpuFatalException>(() => _host.RecordEndOfPipe(new(EndOfPipeWriteKind.Write64, 7, MemoryBase + 0x8100, 42)));
        Assert.Contains("unordered writable presentation", fatal.InnerException!.Message);
        InvokeCompletionMethod("CompletePresenterShutdown");
        Assert.Equal(debt, ReadCompletionState("_unorderedPresentationWriteSequence"));
    }

    [Fact]
    public void PresentationPublishedAfterEopAdmissionCannotFabricateCompletionAtDrain()
    {
        using var state = CompletionState();
        var address = MemoryBase + 0x8100;
        _host.RecordEndOfPipe(new(EndOfPipeWriteKind.Write64, 7, address, 42));
        var sequence = (long)ReadCompletionState("_enqueuedGuestWorkSequence")!;
        SubmitCompletionPresentation([new GuestMemoryBuffer(1, new byte[4], 4, 4, false, Writable: true)]);
        Assert.Throws<GpuFatalException>(DrainCompletions);
        Assert.Equal(0UL, CompletionLabel(address));
        Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
    }

    private void SubmitCompletionPresentation(GuestMemoryBuffer[] buffers, GuestDrawTexture[]? textures = null) =>
        MetalVideoPresenter.SubmitTranslatedDraw(new(new Gen5MslShader("", "managed presentation never compiled", Gen5MslStage.Pixel, 0)),
            textures ?? [], buffers, 1, 1, 0, null, 3, 1, 4, null, null, null);

    private static object CompletionGate => CompletionField("_gate").GetValue(null)!;
    private static object? ReadCompletionState(string name) { lock (CompletionGate) return CompletionField(name).GetValue(null); }
    private static Task<Exception?> CompletionWorker(Action action) => Task.Factory.StartNew(() =>
    { try { action(); return (Exception?)null; } catch (Exception exception) { return exception; } },
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    private static Exception? WorkerResult(Task<Exception?> worker) { Assert.True(worker.Wait(10000)); return worker.Result; }
    private static void JoinCompletionWorkers(params Task<Exception?>?[] workers)
    { foreach (var worker in workers) if (worker is not null) Assert.True(worker.Wait(10000)); }
    private static void WaitForInitialCompletionWaiters(int count) => Assert.True(SpinWait.SpinUntil(() =>
        (int)ReadCompletionState("_initialGuestCompletionWaiters")! >= count, 10000), "worker must reach the actual receipt wait");
    private static object InitialControlAction(Action action) => Activator.CreateInstance(typeof(MetalVideoPresenter)
        .GetNestedType("OrderedGuestAction", BindingFlags.NonPublic)!, [action, "actual admission barrier control"])!;
    private static object InitialControlPresentation()
    {
        var constructor = typeof(MetalVideoPresenter).GetNestedType("Presentation", BindingFlags.NonPublic)!.GetConstructors().Single();
        return constructor.Invoke(constructor.GetParameters().Select(parameter => parameter.Name switch
        {
            "Pixels" => (object)new byte[4], "Width" or "Height" => 1u, "Sequence" => 1L, "IsSplash" => false,
            _ => parameter.DefaultValue,
        }).ToArray());
    }
    private static object? InvokeCompletionMethod(string name, params object?[] arguments)
    {
        try { return typeof(MetalVideoPresenter).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner) { ExceptionDispatchInfo.Capture(inner).Throw(); throw; }
    }
}
