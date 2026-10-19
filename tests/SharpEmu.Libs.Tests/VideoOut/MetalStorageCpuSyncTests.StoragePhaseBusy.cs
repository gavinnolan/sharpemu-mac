// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Actual public Submit/Take plus scalar refusal helpers only. All numeric
    // handles here are sentinels: no native query/encoding/completion/ack occurs.
    [Theory]
    [InlineData("replacement")] [InlineData("version")] [InlineData("session")]
    [InlineData("published")] [InlineData("encoded")] [InlineData("completed")]
    [InlineData("batch")] [InlineData("queue")] [InlineData("token")] [InlineData("unknown")]
    public void PreencodeContinuationRefusesChangedOriginalBeforeAnyEncoder(string change)
    {
        ActualArchitecture(); using var fixture = new PreencodeScalarFixture();
        switch (change)
        {
            case "replacement": Images[Address] = Activator.CreateInstance(fixture.Scalar.Image.GetType(), nonPublic: true); break;
            case "version": Set(fixture.Scalar.Image, "ContentVersion", 2); break;
            case "session": Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentStorageCpuSeedSession + 1); break;
            case "published": PrecommitSet(fixture.Receipt, "Published", true); break;
            case "encoded": PrecommitSet(fixture.Receipt, "Encoded", true); break;
            case "completed": PrecommitSet(fixture.Receipt, "SuccessfullyCompleted", true); break;
            case "batch": Field("_batchCommandBuffer").SetValue(null, (nint)95); break;
            case "queue": Field("_submittingGuestQueue").SetValue(null, null); break;
            case "token": fixture.Scalar.Protection.Generation(1); break;
            case "unknown": PrecommitSet(VolumeWatch(fixture.Scalar.Origin), "UnknownProducer", true); break;
        }
        var waits = 0; Field("_finiteStoragePreencodeBusyForControl").SetValue(null, (Action)(() => waits++));
        Assert.Throws<TargetInvocationException>(() => InvokeVolume("ValidateFiniteStoragePreencode", fixture.Continuation));
        Assert.Equal(0, waits); Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        Assert.True((bool)PrecommitGet(fixture.Scalar.Debt, "Pending")!);
    }

    [Fact]
    public void PreencodeHealthyOriginalBusyWaitKeepsSameReceiptAndRunsOutsideGate()
    {
        ActualArchitecture(); using var fixture = new PreencodeScalarFixture();
        var scalar = fixture.Scalar; scalar.Memory.WatchCurrent = false;
        scalar.Memory.ReadStatus = () => CpuMappedReadWatchStatus.Busy;
        var origin = scalar.Origin; var texture = PrecommitGet(fixture.Receipt, "Texture"); var waits = 0;
        Field("_finiteStoragePreencodeBusyForControl").SetValue(null, (Action)(() =>
        {
            Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!)); Assert.Equal(1, ++waits);
            Assert.False((bool)PrecommitGet(fixture.Receipt, "Encoded")!);
            Assert.False((bool)PrecommitGet(fixture.Receipt, "SuccessfullyCompleted")!);
            Assert.False((bool)PrecommitGet(fixture.Receipt, "Published")!);
            Assert.Same(origin, PrecommitGet(fixture.Receipt, "Origin"));
            Assert.Equal(texture, PrecommitGet(fixture.Receipt, "Texture"));
            Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
            scalar.Memory.WatchCurrent = true; scalar.Memory.ReadStatus = () => CpuMappedReadWatchStatus.Current;
        }));
        InvokeVolume("ValidateFiniteStorageBeforeEncoding", fixture.Dispatch, (nint)91, (nint)93, fixture.Receipts);
        Assert.Equal(1, waits); Assert.Equal(1, scalar.Memory.Copies); // no younger mapped copy
        Assert.False((bool)PrecommitGet(fixture.Receipt, "Encoded")!);
    }

    [Fact]
    public void PreencodeBusyDeadlineKeepsFirstExactRefusalAndNoCompletion()
    {
        ActualArchitecture(); using var fixture = new PreencodeScalarFixture();
        fixture.Scalar.Memory.WatchCurrent = false; fixture.Scalar.Memory.ReadStatus = () => CpuMappedReadWatchStatus.Busy;
        var first = Assert.Throws<TargetInvocationException>(() => InvokeVolume("ValidateFiniteStoragePreencode", fixture.Continuation)).InnerException!;
        PrecommitSet(fixture.Continuation, "FirstBusy", first);
        PrecommitSet(fixture.Continuation, "BusyStarted", Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        // The stored continuation is exercised directly: the production entry
        // captures once before the retry loop and never replaces it.
        var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("WaitFiniteStoragePreencodeBusy", fixture.Continuation, first));
        Assert.Same(first, failure.InnerException);
        Assert.Contains("compute encoder/dispatch not created", (string)first.Data["FiniteStoragePreencodeWait"]!);
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        Assert.False((bool)PrecommitGet(fixture.Receipt, "Encoded")!);
    }

    [Theory]
    [InlineData("unencoded")] [InlineData("wrong-command")] [InlineData("early-completion")] [InlineData("missing-staging")]
    public void PrebufferRefusesMissingExactCompletionBeforeNativeStatusQuery(string change)
    {
        ActualArchitecture(); using var fixture = new PrecommitScalarFixture();
        var listType = typeof(List<>).MakeGenericType(fixture.Receipt.GetType());
        var receipts = Assert.IsAssignableFrom<IList>(Activator.CreateInstance(listType)!); receipts.Add(fixture.Receipt);
        PrecommitSet(fixture.Receipt, "SuccessfullyCompleted", change == "early-completion");
        if (change == "unencoded") PrecommitSet(fixture.Receipt, "Encoded", false);
        if (change == "missing-staging") PrecommitSet(fixture.Receipt, "Staging", (nint)0);
        var committed = change == "wrong-command" ? (nint)95 : (nint)91;
        var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("ValidateFiniteStorageBeforeBufferPublication",
            committed, receipts, Array.Empty<SharpEmu.Libs.Gpu.GuestMemoryBuffer>()));
        Assert.Contains("exact actual status4", failure.InnerException!.Message);
        Assert.False((bool)PrecommitGet(fixture.Receipt, "Published")!);
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
    }

    [Fact]
    public void PrebufferValidationObjectCannotAuthorizeImagePublicationTransaction()
    {
        ActualArchitecture(); using var fixture = new PrecommitScalarFixture();
        // A negative phase-mismatch only, not a status4/currentness receipt.
        PrecommitSet(fixture.Continuation, "BeforeBufferPublication", true);
        var publisher = new PrecommitRefusingPublisher();
        var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("PrepareFiniteStoragePrecommit",
            fixture.Continuation, publisher, Array.Empty<CpuMappedWriteRun>()));
        Assert.Contains("pre-buffer validation", failure.InnerException!.Message);
        Assert.Equal(0, publisher.Preparations);
        Assert.False((bool)PrecommitGet(fixture.Receipt, "Published")!);
    }

    private sealed class PreencodeScalarFixture : IDisposable
    {
        internal readonly PrecommitScalarFixture Scalar = new();
        internal readonly object Dispatch, Receipt, Receipts, Continuation;
        private readonly string[] names = ["_finiteStorageBeforePreencodeValidationForControl", "_finiteStoragePreencodeBusyForControl",
            "_batchOpen", "_batchCommandBuffer", "_batchQueue", "_submittingGuestQueue", "_executingGuestWorkSequence",
            "_takenGuestWorkSequence", "_takenDepthCubeProducerWork", "_takenGuestPrefixProducerSequence"];
        private readonly object?[] saved;
        internal PreencodeScalarFixture()
        {
            saved = names.Select(name => Field(name).GetValue(null)).ToArray();
            Field(names[0]).SetValue(null, null); Field(names[1]).SetValue(null, null);
            var sequence = SubmitVolume([Scalar.Snapshot]); var pending = TakeVolumeWork();
            Dispatch = pending.GetType().GetProperty("Work")!.GetValue(pending)!;
            Field("_executingGuestWorkSequence").SetValue(null, sequence);
            Field("_submittingGuestQueue").SetValue(null, pending.GetType().GetProperty("Queue")!.GetValue(pending));
            Field("_batchOpen").SetValue(null, true); Field("_batchCommandBuffer").SetValue(null, (nint)91);
            Field("_batchQueue").SetValue(null, (nint)93);
            Receipt = Activator.CreateInstance(Presenter.GetNestedType("FiniteStorageWriteback", BindingFlags.NonPublic)!,
                BindingFlags.Instance | BindingFlags.NonPublic, null, [Scalar.Snapshot, Scalar.Image, Scalar.Origin, Scalar.Debt, 4], null)!;
            PrecommitSet(Receipt, "Texture", (nint)123); // scalar sentinel, never a native retain
            PrecommitSet(Scalar.Debt, "Work", PrecommitGet(Receipt, "Work")!);
            var listType = typeof(List<>).MakeGenericType(Receipt.GetType());
            var list = Assert.IsAssignableFrom<IList>(Activator.CreateInstance(listType)!); list.Add(Receipt); Receipts = list;
            Continuation = InvokeVolume("CaptureFiniteStoragePreencode", Dispatch, (nint)91, (nint)93, Receipts)!;
        }
        public void Dispose()
        {
            // Cancel bookkeeping only; sentinel handles never reach shutdown,
            // receipt.Dispose, native waits or native release.
            InvokeVolume("CancelQueuedSingletonVolumeOriginalsLocked", (object?)null);
            for (var i = 0; i < names.Length; i++) Field(names[i]).SetValue(null, saved[i]);
            Scalar.Dispose();
        }
    }
}
