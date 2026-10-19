// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Public Submit/Take and scalar validation only. The phase seal below is
    // bookkeeping with sentinels, NOT an actual encoder/completion receipt.
    // Native rows separately exercise the trusted production endEncoding call.
    [Theory]
    [InlineData("replacement")] [InlineData("version")] [InlineData("session")]
    [InlineData("published")] [InlineData("encoded")] [InlineData("completed")]
    [InlineData("batch")] [InlineData("queue")] [InlineData("token")] [InlineData("unknown")]
    public void ReadbackOriginalChangeCannotBorrowHealthyBusyBeforeAllocation(string change)
    {
        ActualArchitecture(); using var scope = new ReadbackScalarFixture(); var fixture = scope.Phase;
        fixture.Scalar.Memory.WatchCurrent = false; fixture.Scalar.Memory.ReadStatus = () => CpuMappedReadWatchStatus.Busy;
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
        var waits = 0; Field("_finiteStorageReadbackBusyForControl").SetValue(null, (Action)(() => waits++));
        Assert.Throws<TargetInvocationException>(() => scope.Validate());
        Assert.Equal(0, waits); Assert.False((bool)PrecommitGet(fixture.Continuation, "ReadbackAllocationStarted")!);
        Assert.Equal((nint)0, PrecommitGet(fixture.Receipt, "CommandBuffer"));
        Assert.Equal((nint)0, PrecommitGet(fixture.Receipt, "Staging"));
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
    }

    [Fact]
    public void ReadbackHealthyBusyReusesSuccessfulPreencoderOriginalWithoutRecopyOrAllocation()
    {
        ActualArchitecture(); using var scope = new ReadbackScalarFixture(); var fixture = scope.Phase;
        fixture.Scalar.Memory.WatchCurrent = false; fixture.Scalar.Memory.ReadStatus = () => CpuMappedReadWatchStatus.Busy;
        var copies = fixture.Scalar.Memory.Copies; var waits = 0; var checks = 0;
        Field("_finiteStorageBeforeReadbackValidationForControl").SetValue(null, (Action<object>)(receipt =>
        { Assert.Same(fixture.Receipt, receipt); checks++; }));
        Field("_finiteStorageReadbackBusyForControl").SetValue(null, (Action)(() =>
        {
            Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!)); Assert.Equal(1, ++waits);
            Assert.Same(fixture.Scalar.Origin, PrecommitGet(fixture.Receipt, "Origin"));
            Assert.False((bool)PrecommitGet(fixture.Continuation, "ReadbackAllocationStarted")!);
            Assert.False((bool)PrecommitGet(fixture.Receipt, "Encoded")!);
            Assert.Equal((nint)0, PrecommitGet(fixture.Receipt, "CommandBuffer"));
            Assert.Equal((nint)0, PrecommitGet(fixture.Receipt, "Staging"));
            fixture.Scalar.Memory.WatchCurrent = true;
            fixture.Scalar.Memory.ReadStatus = () => CpuMappedReadWatchStatus.Current;
        }));
        scope.Validate(); Assert.Equal(1, waits); Assert.Equal(2, checks);
        Assert.Equal(copies, fixture.Scalar.Memory.Copies);
        Assert.True((bool)PrecommitGet(fixture.Continuation, "ReadbackAllocationStarted")!);
        Assert.False((bool)PrecommitGet(fixture.Receipt, "Encoded")!);
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        Assert.Throws<TargetInvocationException>(() => scope.Validate()); // consumed phase cannot allocate twice
    }

    [Fact]
    public void ReadbackStaleFirstStatusCannotBorrowLaterBusy()
    {
        ActualArchitecture(); using var scope = new ReadbackScalarFixture(); var fixture = scope.Phase;
        fixture.Scalar.Memory.WatchCurrent = false; var queries = 0; var waits = 0;
        fixture.Scalar.Memory.ReadStatus = () => ++queries == 1 ? CpuMappedReadWatchStatus.Stale : CpuMappedReadWatchStatus.Busy;
        Field("_finiteStorageReadbackBusyForControl").SetValue(null, (Action)(() => waits++));
        Assert.Throws<TargetInvocationException>(() => scope.Validate());
        Assert.Equal(0, waits); Assert.False((bool)PrecommitGet(fixture.Continuation, "ReadbackAllocationStarted")!);
    }

    [Fact]
    public void ReadbackBusyDeadlinePreservesFirstExactCauseWithoutEncodingOrCompletion()
    {
        ActualArchitecture(); using var scope = new ReadbackScalarFixture(); var fixture = scope.Phase;
        fixture.Scalar.Memory.WatchCurrent = false; fixture.Scalar.Memory.ReadStatus = () => CpuMappedReadWatchStatus.Busy;
        var first = Assert.Throws<TargetInvocationException>(() => InvokeVolume("ValidateFiniteStoragePhaseOriginal",
            fixture.Continuation, true, (nint)0)).InnerException!;
        PrecommitSet(fixture.Continuation, "FirstReadbackBusy", first);
        PrecommitSet(fixture.Continuation, "ReadbackBusyStarted", Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        var failure = Assert.Throws<TargetInvocationException>(() => InvokeVolume("WaitFiniteStorageReadbackBusy", fixture.Continuation, first));
        Assert.Same(first, failure.InnerException);
        Assert.Contains("no dispatch/readback allocation/encoding replay", (string)first.Data["FiniteStorageReadbackWait"]!);
        Assert.Equal((nint)0, PrecommitGet(fixture.Receipt, "Staging"));
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
    }

    [Theory]
    [InlineData("compute-not-ended")] [InlineData("other-receipt")] [InlineData("other-command")]
    public void ReadbackCannotAdoptAnotherPhaseReceiptOrCommand(string reason)
    {
        ActualArchitecture(); using var scope = new ReadbackScalarFixture(); var fixture = scope.Phase;
        if (reason == "compute-not-ended") PrecommitSet(fixture.Continuation, "ComputeEncodingEnded", false);
        var receipt = reason == "other-receipt" ? fixture.Scalar.Receipt : fixture.Receipt;
        Assert.Throws<TargetInvocationException>(() => InvokeVolume("ValidateFiniteStorageBeforeReadbackAllocation",
            fixture.Continuation, receipt, reason == "other-command" ? (nint)95 : (nint)91));
        Assert.False((bool)PrecommitGet(fixture.Continuation, "ReadbackAllocationStarted")!);
        Assert.Equal((nint)0, PrecommitGet(fixture.Receipt, "Staging"));
    }

    [Theory]
    [InlineData("healthy-busy")] [InlineData("token")] [InlineData("mapping")] [InlineData("version")]
    public void AllocatedReadbackStillChecksAllOriginalsWithoutReplayingOrWaiting(string reason)
    {
        ActualArchitecture(); using var scope = new ReadbackScalarFixture(); var fixture = scope.Phase;
        // Pure post-allocation validation sentinel. Never calls native allocation,
        // blit/dispatch, Dispose, completion or acknowledgement with these handles.
        PrecommitSet(fixture.Continuation, "ReadbackAllocationStarted", true);
        PrecommitSet(fixture.Receipt, "CommandBuffer", (nint)91); PrecommitSet(fixture.Receipt, "Staging", (nint)92);
        fixture.Scalar.Memory.WatchCurrent = false; fixture.Scalar.Memory.ReadStatus = () => CpuMappedReadWatchStatus.Busy;
        if (reason == "token") fixture.Scalar.Protection.Generation(1);
        if (reason == "mapping") fixture.Scalar.Memory.Current = false;
        if (reason == "version") Set(fixture.Scalar.Image, "ContentVersion", 2);
        var waits = 0; Field("_finiteStorageReadbackBusyForControl").SetValue(null, (Action)(() => waits++));
        if (reason == "healthy-busy") InvokeVolume("ValidateFiniteStoragePhaseOriginal", fixture.Continuation, true, (nint)92);
        else Assert.Throws<TargetInvocationException>(() => InvokeVolume("ValidateFiniteStoragePhaseOriginal", fixture.Continuation, true, (nint)92));
        Assert.Equal(0, waits); Assert.False((bool)PrecommitGet(fixture.Receipt, "Encoded")!);
        Assert.False((bool)PrecommitGet(fixture.Receipt, "SuccessfullyCompleted")!);
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
    }

    private sealed class ReadbackScalarFixture : IDisposable
    {
        internal readonly PreencodeScalarFixture Phase = new();
        private readonly string[] names = ["_finiteStorageBeforeReadbackValidationForControl", "_finiteStorageReadbackBusyForControl"];
        private readonly object?[] saved;
        internal ReadbackScalarFixture()
        {
            saved = names.Select(name => Field(name).GetValue(null)).ToArray();
            foreach (var name in names) Field(name).SetValue(null, null);
            InvokeVolume("SealFiniteStorageComputeEncoding", Phase.Continuation, Phase.Dispatch, (nint)91, (nint)93);
        }
        internal void Validate() => InvokeVolume("ValidateFiniteStorageBeforeReadbackAllocation", Phase.Continuation, Phase.Receipt, (nint)91);
        public void Dispose()
        {
            try { Phase.Dispose(); }
            finally { for (var i = 0; i < names.Length; i++) Field(names[i]).SetValue(null, saved[i]); }
        }
    }
}
