// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Managed receipt/refusal seams only. Numeric handles are NEVER sent to
    // Metal, waited, read or released; only scalar helpers are invoked here.
    [Theory]
    [InlineData("replacement")] [InlineData("version")] [InlineData("session")]
    [InlineData("staging")] [InlineData("command-buffer")] [InlineData("closed")]
    [InlineData("published")] [InlineData("retired")]
    public void PrecommitContinuationRefusesChangedOriginalBeforeAnyProviderOrNativeUse(string change)
    {
        ActualArchitecture(); using var fixture = new PrecommitScalarFixture();
        var queries = fixture.Memory.MappingQueries;
        switch (change)
        {
            case "replacement": Images[Address] = Activator.CreateInstance(fixture.Image.GetType(), nonPublic: true); break;
            case "version": Set(fixture.Image, "ContentVersion", 2); break;
            case "session": Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentStorageCpuSeedSession + 1); break;
            case "staging": PrecommitSet(fixture.Receipt, "Staging", (nint)93); break;
            case "command-buffer": PrecommitSet(fixture.Receipt, "CommandBuffer", (nint)94); break;
            case "closed": Field("_closed").SetValue(null, true); break;
            case "published": PrecommitSet(fixture.Receipt, "Published", true); break;
            case "retired": Field("_finiteStorageRetiredOriginCount").SetValue(null, 1); break;
        }
        var publisher = new PrecommitRefusingPublisher();
        var failure = Assert.Throws<TargetInvocationException>(() =>
            InvokeVolume("PrepareFiniteStoragePrecommit", fixture.Continuation, publisher, Array.Empty<CpuMappedWriteRun>()));
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Equal(0, publisher.Preparations); Assert.Equal(queries, fixture.Memory.MappingQueries);
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        Assert.Equal(change == "published", (bool)PrecommitGet(fixture.Receipt, "Published")!);
    }

    [Fact]
    public void PrecommitOriginalBusyWaitRunsGateFreeAndCannotPublishOrAcknowledge()
    {
        ActualArchitecture(); using var fixture = new PrecommitScalarFixture();
        fixture.Memory.WatchCurrent = false;
        fixture.Memory.ReadStatus = () => CpuMappedReadWatchStatus.Busy;
        var publisher = new PrecommitRefusingPublisher(); var waits = 0;
        Field("_finiteStoragePrecommitBusyForControl").SetValue(null, (Action)(() =>
        {
            Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!)); waits++;
            Assert.Equal(0, publisher.Preparations); Assert.True((bool)PrecommitGet(fixture.Debt, "Pending")!);
            Assert.False((bool)PrecommitGet(fixture.Receipt, "Published")!);
            Assert.Same(fixture.Origin, fixture.Image.GetType().GetField("StorageCpuOrigin")!.GetValue(fixture.Image));
            fixture.Memory.WatchCurrent = true; fixture.Memory.ReadStatus = () => CpuMappedReadWatchStatus.Current;
        }));
        var failure = Assert.Throws<TargetInvocationException>(() =>
            InvokeVolume("PrepareFiniteStoragePrecommit", fixture.Continuation, publisher, Array.Empty<CpuMappedWriteRun>()));
        Assert.Contains("preparation was refused", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
        Assert.Equal(1, waits); Assert.Equal(1, publisher.Preparations);
        Assert.False((bool)PrecommitGet(fixture.Receipt, "Published")!);
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        Assert.Equal(1, fixture.Memory.Copies); Assert.Equal(0, fixture.Memory.Reads);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PrecommitBusyCancelsUnstartedExactTicketOnceAndUnknownCancellationNeverRetries(bool unknownCancel)
    {
        ActualArchitecture(); using var fixture = new PrecommitScalarFixture();
        var cause = new IOException("uncertain exact precommit cancellation");
        var ticket = new PrecommitUnstartedTransaction(unknownCancel ? cause : null);
        var publisher = new PrecommitRefusingPublisher(ticket); var waits = 0;
        Field("_finiteStorageBeforePrecommitValidationForControl").SetValue(null, (Action<bool>)(prepared =>
        {
            if (!prepared) return;
            Assert.Equal(1, publisher.Preparations);
            fixture.Memory.WatchCurrent = false; fixture.Memory.ReadStatus = () => CpuMappedReadWatchStatus.Busy;
        }));
        Field("_finiteStoragePrecommitBusyForControl").SetValue(null, (Action)(() =>
        {
            Assert.False(Monitor.IsEntered(Field("_gate").GetValue(null)!));
            Assert.Equal(1, ticket.Cancellations); Assert.Equal(0, ticket.Commits); waits++;
            fixture.Memory.WatchCurrent = true; fixture.Memory.ReadStatus = () => CpuMappedReadWatchStatus.Current;
        }));
        var failure = Assert.Throws<TargetInvocationException>(() =>
            InvokeVolume("PrepareFiniteStoragePrecommit", fixture.Continuation, publisher, Array.Empty<CpuMappedWriteRun>()));
        if (unknownCancel)
        {
            Assert.Equal("SingletonVolumeOriginalBusy", failure.InnerException!.GetType().Name);
            Assert.Same(cause, failure.InnerException.Data["FiniteStoragePrecommitCancellation"]);
            Assert.Same(cause, Field("_textureSourceCleanupFailure").GetValue(null));
            Assert.True((bool)PrecommitGet(fixture.Debt, "Failed")!);
            Assert.Equal(0, waits); Assert.Equal(1, publisher.Preparations);
        }
        else
        {
            Assert.Contains("preparation was refused", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
            Assert.Equal(1, waits); Assert.Equal(2, publisher.Preparations);
        }
        Assert.Equal(1, ticket.Cancellations); Assert.Equal(0, ticket.Commits);
        Assert.False((bool)PrecommitGet(fixture.Receipt, "Published")!);
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
    }

    [Theory]
    [InlineData("stale")] [InlineData("token")] [InlineData("unknown")]
    public void PrecommitOrdinaryBusyCannotMaskOtherOriginalFailure(string change)
    {
        ActualArchitecture(); using var fixture = new PrecommitScalarFixture();
        fixture.Memory.WatchCurrent = false;
        fixture.Memory.ReadStatus = () => change == "stale" ? CpuMappedReadWatchStatus.Stale : CpuMappedReadWatchStatus.Busy;
        if (change == "token") fixture.Protection.Generation(1);
        if (change == "unknown") PrecommitSet(VolumeWatch(fixture.Origin), "UnknownProducer", true);
        var publisher = new PrecommitRefusingPublisher(); var waits = 0;
        Field("_finiteStoragePrecommitBusyForControl").SetValue(null, (Action)(() => waits++));
        var failure = Assert.Throws<TargetInvocationException>(() =>
            InvokeVolume("PrepareFiniteStoragePrecommit", fixture.Continuation, publisher, Array.Empty<CpuMappedWriteRun>()));
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Equal(0, waits); Assert.Equal(0, publisher.Preparations);
    }

    [Fact]
    public void PrecommitBusyBoundPreservesFirstRefusalWithoutAdvancingGuestCompletion()
    {
        ActualArchitecture(); using var fixture = new PrecommitScalarFixture();
        fixture.Memory.WatchCurrent = false; fixture.Memory.ReadStatus = () => CpuMappedReadWatchStatus.Busy;
        var first = Assert.Throws<TargetInvocationException>(() => InvokeVolume("ValidateFiniteStoragePrecommit",
            fixture.Continuation, Activator.CreateInstance(Presenter.GetNestedType("StorageWatchReservation", BindingFlags.NonPublic)!)!)).InnerException!;
        PrecommitSet(fixture.Continuation, "FirstBusy", first);
        PrecommitSet(fixture.Continuation, "BusyStarted", Stopwatch.GetTimestamp() - Stopwatch.Frequency);
        var publisher = new PrecommitRefusingPublisher();
        var failure = Assert.Throws<TargetInvocationException>(() =>
            InvokeVolume("PrepareFiniteStoragePrecommit", fixture.Continuation, publisher, Array.Empty<CpuMappedWriteRun>()));
        Assert.Same(first, failure.InnerException); Assert.Contains("TryCommit not called", (string)first.Data["FiniteStoragePrecommitWait"]!);
        Assert.Equal(0, publisher.Preparations); Assert.True((bool)PrecommitGet(fixture.Debt, "Pending")!);
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
    }

    private static object? PrecommitGet(object owner, string name) => owner.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(owner);
    private static void PrecommitSet(object owner, string name, object value) => owner.GetType().GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(owner, value);

    private sealed class PrecommitScalarFixture : IDisposable
    {
        private readonly VolumeState state = new();
        internal readonly VolumeProtectedScalar Protection = new();
        internal readonly VolumeMemory Memory = new(VolumePhysical());
        internal readonly GuestDrawTexture Snapshot;
        internal readonly object Origin, Image, Debt, Receipt, Continuation;
        private readonly string[] names = ["_finiteStorageBeforePrecommitValidationForControl", "_finiteStoragePrecommitBusyForControl", "_textureSourceCleanupFailure", "_closed"];
        private readonly object?[] saved;
        internal PrecommitScalarFixture()
        {
            saved = names.Select(name => Field(name).GetValue(null)).ToArray();
            Field("_finiteStorageBeforePrecommitValidationForControl").SetValue(null, null);
            Field("_finiteStoragePrecommitBusyForControl").SetValue(null, null);
            Field("_textureSourceCleanupFailure").SetValue(null, null);
            Snapshot = VolumeSnapshot(Memory); Origin = GetOrigin(Snapshot); Image = VolumeImage(Origin, Memory);
            PublishVolume(Snapshot, Image);
            Debt = Activator.CreateInstance(Presenter.GetNestedType("FiniteStorageDebt", BindingFlags.NonPublic)!, nonPublic: true)!;
            PrecommitSet(Debt, "Pending", true);
            Receipt = Activator.CreateInstance(Presenter.GetNestedType("FiniteStorageWriteback", BindingFlags.NonPublic)!,
                BindingFlags.Instance | BindingFlags.NonPublic, null, [Snapshot, Image, Origin, Debt, 4], null)!;
            PrecommitSet(Debt, "Work", PrecommitGet(Receipt, "Work")!);
            PrecommitSet(Receipt, "Encoded", true); PrecommitSet(Receipt, "SuccessfullyCompleted", true);
            PrecommitSet(Receipt, "CommandBuffer", (nint)91); PrecommitSet(Receipt, "Staging", (nint)92);
            PrecommitSet(Receipt, "Texture", (nint)123);
            Continuation = InvokeVolume("CaptureFiniteStoragePrecommit", Receipt)!;
        }
        public void Dispose()
        {
            // No FiniteStorageWriteback.Dispose: those numeric native handles
            // were only scalar sentinels and never acquired real retains.
            Field("_closed").SetValue(null, false);
            var watch = VolumeWatch(Origin);
            watch.GetType().GetMethod("Release", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(watch, [false, null]);
            Images.Remove(Address); MetalVideoPresenter.ReleasePreparedTextureSources([Snapshot]);
            for (var i = 0; i < names.Length; i++) Field(names[i]).SetValue(null, saved[i]);
            Protection.Dispose(); state.Dispose();
        }
    }

    private sealed class PrecommitRefusingPublisher(PrecommitUnstartedTransaction? first = null) : ICpuMappedPublicationSource
    {
        internal int Preparations;
        public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(CpuMappedCopyObservation original,
            IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication)
        {
            Preparations++;
            publication = Preparations == 1 && first is not null ? new CpuMappedPublication(first) : null;
            return publication is null ? CpuMappedPublicationPreparationResult.Rejected : CpuMappedPublicationPreparationResult.Prepared;
        }
    }
    private sealed class PrecommitUnstartedTransaction(Exception? cancelFailure) : CpuMappedPublicationTransaction
    {
        internal int Cancellations, Commits;
        internal override CpuMappedCopyObservation? SuccessfulObservation => null;
        internal override CpuMappedPublicationResult TryCommit() { Commits++; throw new InvalidOperationException("managed precommit seam must never commit"); }
        internal override void Cancel() { Cancellations++; if (cancelFailure is not null) throw cancelFailure; }
    }
}
