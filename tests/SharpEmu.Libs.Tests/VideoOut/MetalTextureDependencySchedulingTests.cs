// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Diagnostics;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalTextureDependencySchedulingTests
{
    [Fact]
    public void BlockedHeadKeepsItsFollowerHiddenWhileOtherQueueAndProducerProgress()
    {
        using var state = new State();
        var producer = Action("producer");
        var read = state.Capture(producer, "producer");
        var consumer = Compute("consumer", read);
        var follower = Action("consumer");
        var unrelated = Action("unrelated");
        CursorAt("consumer");

        Assert.Equal(unrelated, TakeSequence());
        Assert.Equal(producer, TakeSequence());
        state.Stage(read, "Completed", true);
        Assert.Equal(consumer, TakeSequence());
        Assert.Equal(follower, TakeSequence());
        Assert.False(TryTake(out _));
    }

    [Fact]
    public void ActualDrainExecutesProducerBeforeCapturedConsumerAndAcknowledgesOnlyProducer()
    {
        using var state = new State();
        GuestDrawTexture? read = null;
        var executed = new List<string>();
        var producer = Action("producer", () =>
        {
            executed.Add("producer");
            state.Stage(read!, "Completed", true);
            // Expire the real render budget after the managed producer. The
            // consumer stays queued so this no-device fixture never enters a
            // native compute encoder; its next actual selection is checked.
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 30;
            while (Stopwatch.GetTimestamp() < deadline) Thread.SpinWait(100);
        });
        read = state.Capture(producer, "producer");
        var consumer = Compute("consumer", read);
        CursorAt("consumer");

        MetalVideoPresenter.DrainGuestWork(0, 0);
        Assert.Equal(new[] { "producer" }, executed);
        Assert.True(MetalVideoPresenter.WaitForGuestWork(producer, 0));
        Assert.False(MetalVideoPresenter.WaitForGuestWork(consumer, 0));
        Assert.Equal(consumer, TakeSequence());
    }

    [Fact]
    public void EncodedSourceDoesNotWaitForHardwareCompletionOrRequireProducerStillQueued()
    {
        using var state = new State();
        var producer = Action("producer");
        var read = state.Capture(producer, "producer");
        Assert.Equal(producer, TakeSequence());
        // Synthetic registry stage only: this does not assert native execution.
        state.Stage(read, "Completed", true);
        var consumer = Compute("consumer", read);
        Assert.False(MetalVideoPresenter.WaitForGuestWork(producer, 0));
        Assert.Equal(consumer, TakeSequence());
    }

    [Fact]
    public void PendingProducerBehindAnotherReadyHeadInItsOwnQueueCanStillProgress()
    {
        using var state = new State();
        var preceding = Action("producer");
        var producer = Action("producer");
        var read = state.Capture(producer, "producer");
        var consumer = Compute("consumer", read);
        CursorAt("consumer");
        Assert.Equal(preceding, TakeSequence());
        Assert.Equal(producer, TakeSequence());
        state.Stage(read, "Completed", true);
        Assert.Equal(consumer, TakeSequence());
    }

    [Fact]
    public void OneEncodedInputCannotHideAnotherFailedCapturedInput()
    {
        using var state = new State();
        var producer = Action("producer");
        var encoded = state.Capture(producer, "producer");
        var failed = state.Capture(producer, "producer");
        state.Stage(encoded, "Completed", true);
        state.Stage(failed, "Failed", true);
        Compute("consumer", encoded, failed);
        CursorAt("consumer");
        using var fatal = new FatalScope();
        var error = Assert.Throws<SchedulerFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
        Assert.Contains("required source is Failed", error.Message);
        Assert.Contains($"image=0x{failed.Address:X}", error.Message);
    }

    [Theory]
    [InlineData(0, "producer")]
    [InlineData(99, "producer")]
    [InlineData(1, "")]
    public void InvalidCapturedProducerIdentityFailsBeforeDispatchSelection(long producerSequence, string queue)
    {
        using var state = new State();
        var read = state.Capture(Action("producer"), "producer");
        Compute("consumer", read);
        state.Producer(read, producerSequence, queue);
        CursorAt("consumer");
        using var fatal = new FatalScope();
        var error = Assert.Throws<SchedulerFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
        Assert.Contains("invalid producer identity", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualDrainRejectsFailedOrMissingProducerWithoutAcknowledgingConsumer(bool missing)
    {
        using var state = new State();
        var producer = Action("producer");
        var read = state.Capture(producer, "producer");
        if (missing) Assert.Equal(producer, TakeSequence());
        else state.Stage(read, "Failed", true);
        var consumer = Compute("consumer", read);
        CursorAt("consumer");
        using var fatal = new FatalScope();

        var error = Assert.Throws<SchedulerFatalException>(() => MetalVideoPresenter.RenderFrameSafely(
            () => MetalVideoPresenter.DrainGuestWork(0, 0)));
        Assert.Contains(missing ? "no longer queued" : "required source is Failed", error.Message);
        Assert.Contains($"sequence={consumer}", error.Message);
        Assert.Contains("queue='consumer' submission=17", error.Message);
        Assert.False(MetalVideoPresenter.WaitForGuestWork(consumer, Timeout.Infinite));
        Assert.Equal(0, Action("later"));
        Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
        Assert.Single(fatal.Messages);
        Assert.True(MetalVideoPresenter.TextureSourceState(read).Failed);
    }

    [Fact]
    public void FailedSelectionDiscardsBorrowedUncommittedBatchBeforeFatalHookThrows()
    {
        using var state = new State();
        var read = state.Capture(Action("producer"), "producer");
        Compute("consumer", read);
        state.Stage(read, "Failed", true);
        CursorAt("consumer");
        Field("_batchOpen").SetValue(null, true);
        Field("_batchCommandBuffer").SetValue(null, (nint)0x1234);
        using var fatal = new FatalScope();
        // Select directly: Drain's successful finalizer would commit a batch,
        // so the rejection must have latched failure and dropped it already.
        var error = Assert.Throws<TargetInvocationException>(() => TryTake(out _));
        Assert.IsType<SchedulerFatalException>(error.InnerException);
        Assert.False((bool)Field("_batchOpen").GetValue(null)!);
        Assert.Equal((nint)0, Field("_batchCommandBuffer").GetValue(null));
        Assert.IsType<InvalidOperationException>(Field("_guestWorkFailure").GetValue(null));
    }

    [Fact]
    public void SelectionFatalRetainsLatchedCauseAndReportsCapturedSourceContext()
    {
        using var state = new State();
        var producer = Action("producer");
        var read = state.Capture(producer, "producer");
        var consumer = Compute("consumer", read);
        state.Stage(read, "Failed", true);
        CursorAt("consumer");
        var reports = new List<string>();
        GpuFatal.OnFatal = reports.Add;
        var error = Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
        Assert.Same(Field("_guestWorkFailure").GetValue(null), error.InnerException);
        var report = Assert.Single(reports);
        Assert.Contains($"sequence={consumer}", report);
        Assert.Contains($"image=0x{read.Address:X}", report);
        Assert.Contains($"ProducerSequence = {producer}", report);
        Assert.Contains("ProducerQueue = producer", report);
        Assert.Contains(error.InnerException!.ToString(), report);
    }

    [Fact]
    public void ActualDependencyLookupExceptionCannotCommitPriorBatchOrAcknowledgeAnyWork()
    {
        using var state = new State();
        var producer = Action("producer");
        var read = state.Capture(producer, "producer");
        var consumer = Compute("consumer", read);
        CursorAt("consumer");
        Field("_batchOpen").SetValue(null, true);
        Field("_batchCommandBuffer").SetValue(null, (nint)0x1234);
        var reports = new List<string>();
        GpuFatal.OnFatal = reports.Add;
        // Corrupt the real captured owner's version only for the actual
        // dependency callback invocation. No alternate callback/mock bypasses
        // production lookup; restore before ownership teardown.
        var restore = state.BreakVersionLookup(read);
        try
        {
            var error = Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
            Assert.IsType<NullReferenceException>(error.InnerException);
            Assert.Same(error.InnerException, Field("_guestWorkFailure").GetValue(null));
            Assert.Contains("dependency readiness lookup failed", Assert.Single(reports));
            Assert.False((bool)Field("_batchOpen").GetValue(null)!);
            Assert.Equal((nint)0, Field("_batchCommandBuffer").GetValue(null));
            Assert.Equal(2, Field("_pendingGuestWorkCount").GetValue(null));
            Assert.False(MetalVideoPresenter.WaitForGuestWork(producer, 0));
            Assert.False(MetalVideoPresenter.WaitForGuestWork(consumer, 0));
            Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
            Assert.Single(reports);
        }
        finally { restore(); }
        Assert.True(MetalVideoPresenter.TextureSourceState(read).Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImpossibleSelfOrSameQueuePrerequisiteFailsInsteadOfSkippingFifo(bool self)
    {
        using var state = new State();
        var producer = Action("fixture");
        var read = state.Capture(producer, "fixture");
        Assert.Equal(producer, TakeSequence());
        var consumer = Compute("consumer", read);
        var behind = self ? consumer : Action("consumer");
        state.Producer(read, behind, "consumer");
        using var fatal = new FatalScope();
        var error = Assert.Throws<SchedulerFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
        Assert.Contains(self ? "own output" : "behind this head", error.Message);
        Assert.False(MetalVideoPresenter.WaitForGuestWork(consumer, 0));
        Assert.False(MetalVideoPresenter.WaitForGuestWork(behind, 0));
    }

    [Fact]
    public void ActualCrossQueueCycleFailsAfterBoundedHeadScan()
    {
        using var state = new State();
        var first = Action("fixture");
        var a = state.Capture(first, "fixture");
        var b = state.Capture(first, "fixture");
        Assert.Equal(first, TakeSequence());
        var consumerA = Compute("a", a);
        var consumerB = Compute("b", b);
        // Deliberately inject an impossible captured graph into real CWT owners
        // to verify production scheduler rejection, not a native-copy claim.
        state.Producer(a, consumerB, "b");
        state.Producer(b, consumerA, "a");
        using var fatal = new FatalScope();
        var error = Assert.Throws<SchedulerFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
        Assert.Contains("source dependency cycle", error.Message);
        Assert.Equal(2, Field("_pendingGuestWorkCount").GetValue(null));
        Assert.False(MetalVideoPresenter.WaitForGuestWork(consumerA, 0));
        Assert.False(MetalVideoPresenter.WaitForGuestWork(consumerB, 0));
    }

    [Fact]
    public void FatalDependencyWakesRealBackpressuredAdmissionWithFailure()
    {
        using var state = new State();
        var read = state.Capture(Action("producer"), "producer");
        Compute("consumer", read);
        state.Stage(read, "Failed", true);
        for (var index = 2; index < 64; index++) Action("filler");
        Thread? worker = null;
        using var entered = new ManualResetEventSlim();
        var waiting = Task.Run(() =>
        {
            worker = Thread.CurrentThread;
            entered.Set();
            return Action("waiting");
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => (worker!.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(5)));
            lock (Field("_gate").GetValue(null)!)
            {
                Assert.False(waiting.IsCompleted);
                Assert.Equal(64, Field("_pendingGuestWorkCount").GetValue(null));
            }
            CursorAt("consumer");
            using var fatal = new FatalScope();
            Assert.Throws<SchedulerFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
            Assert.True(waiting.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, waiting.Result);
        }
        finally
        {
            if (!waiting.IsCompleted)
            {
                lock (Field("_gate").GetValue(null)!)
                {
                    Field("_closed").SetValue(null, true);
                    Monitor.PulseAll(Field("_gate").GetValue(null)!);
                }
                Assert.True(waiting.Wait(TimeSpan.FromSeconds(5)));
            }
        }
    }

    private static readonly MetalCompiledGuestShader Shader = new(new Gen5MslShader("", "dependency_fixture", Gen5MslStage.Compute, 0));
    private static long Compute(string queue, params GuestDrawTexture[] reads)
    {
        using var scope = MetalVideoPresenter.EnterGuestQueue(queue, 17);
        // The frontend elides a dispatch without any observable writes. Give
        // this sampled-source consumer a global-memory effect so it is really
        // admitted; no native execution is used in these queue fixtures.
        var sequence = MetalVideoPresenter.SubmitComputeDispatch(0, Shader, reads, [], 1, 1, 1, 0, 0, 0, true, null);
        Assert.True(sequence > 0);
        return sequence;
    }
    private static long Action(string queue, Action? body = null)
    {
        using var scope = MetalVideoPresenter.EnterGuestQueue(queue, 17);
        return MetalVideoPresenter.SubmitOrderedGuestAction(body ?? (static () => { }), "dependency fixture");
    }
    private static FieldInfo Field(string name) => typeof(MetalVideoPresenter).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static object? Call(string name, params object?[] arguments) => typeof(MetalVideoPresenter)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments);
    private static object Queue(string name) => Activator.CreateInstance(typeof(MetalVideoPresenter)
        .GetNestedType("GuestQueueIdentity", BindingFlags.NonPublic)!, [name, 17UL])!;
    private static void CursorAt(string name)
    {
        var index = ((IList)Field("_pendingGuestQueueSchedule").GetValue(null)!).IndexOf(name);
        Assert.True(index >= 0);
        Field("_pendingGuestQueueCursor").SetValue(null, index);
    }
    private static bool TryTake(out object? pending)
    {
        object?[] args = [null];
        var found = (bool)Call("TryTakeGuestWork", args)!;
        pending = args[0];
        return found;
    }
    private static long TakeSequence()
    {
        Assert.True(TryTake(out var pending));
        return (long)pending!.GetType().GetProperty("Sequence")!.GetValue(pending)!;
    }

    private sealed class State : IDisposable
    {
        private readonly MetalGuestFailureTests.PresenterState presenter = new();
        private readonly IDictionary latest = (IDictionary)Field("_latestTextureSources").GetValue(null)!;
        private readonly IList versions = (IList)Field("_textureSourceVersions").GetValue(null)!;
        private readonly DictionaryEntry[] oldLatest;
        private readonly object[] oldVersions;
        private readonly ulong oldReserved = (ulong)Field("_reservedTextureSourceBytes").GetValue(null)!;
        private readonly List<GuestDrawTexture> captures = [];
        private ulong nextAddress = 0x71F4_0000_00;
        private readonly bool wasTrackerEnabled = GuestImageWriteTracker.Enabled;
        public State()
        {
            GuestImageWriteTracker.Configure(true);
            oldLatest = latest.Cast<DictionaryEntry>().ToArray();
            oldVersions = versions.Cast<object>().ToArray();
            latest.Clear(); versions.Clear();
            Field("_reservedTextureSourceBytes").SetValue(null, 0UL);
        }
        public GuestDrawTexture Capture(long producer, string queue)
        {
            var address = nextAddress; nextAddress += 0x1000;
            GuestImageWriteTracker.TrackManagedWriter(address, 64);
            var seed = new GuestDrawTexture(address, 4, 4, 10, 0, new byte[64], false, true, Pitch: 4, WriteGeneration: 0,
                Shape: new ShaderImageShape(false, false, true, false, TextureNumericClass.Float), Access: GuestImageAccess.Write);
            MetalVideoPresenter.RegisterCpuTextureSnapshotProvenance(seed,
                MetalVideoPresenter.ObserveCpuTextureSnapshot(address, 64, HostMemory.MappingGeneration));
            Call("PublishAdmittedTextureSourceLocked", seed, producer, Queue(queue), false);
            var read = seed with { RgbaPixels = [], IsStorage = false, Shape = seed.Shape with { Storage = false }, Access = GuestImageAccess.Read };
            Assert.True(MetalVideoPresenter.TryCaptureTextureSource(read, 64, false));
            captures.Add(read);
            return read;
        }
        private static object Lease(GuestDrawTexture texture)
        {
            var table = Field("_textureSourceLeases").GetValue(null)!;
            object?[] args = [texture, null];
            Assert.True((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!);
            return args[1]!;
        }
        private static object Version(GuestDrawTexture texture)
        {
            var lease = Lease(texture);
            return lease.GetType().GetProperty("Version")!.GetValue(lease)!;
        }
        public Action BreakVersionLookup(GuestDrawTexture texture)
        {
            var lease = Lease(texture);
            var field = lease.GetType().GetField("<Version>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var version = field.GetValue(lease);
            field.SetValue(lease, null);
            return () => field.SetValue(lease, version);
        }
        public void Stage(GuestDrawTexture texture, string field, bool value)
        {
            var version = Version(texture);
            version.GetType().GetField(field)!.SetValue(version, value);
        }
        public void Producer(GuestDrawTexture texture, long sequence, string queue)
        {
            var version = Version(texture);
            version.GetType().GetField("ProducerSequence")!.SetValue(version, sequence);
            version.GetType().GetField("ProducerQueue")!.SetValue(version, Queue(queue));
        }
        public void Dispose()
        {
            MetalVideoPresenter.ReleasePreparedTextureSources(captures);
            foreach (var capture in captures) GuestImageWriteTracker.Untrack(capture.Address);
            latest.Clear(); versions.Clear();
            foreach (var item in oldLatest) latest.Add(item.Key, item.Value);
            foreach (var item in oldVersions) versions.Add(item);
            Field("_reservedTextureSourceBytes").SetValue(null, oldReserved);
            GuestImageWriteTracker.Configure(wasTrackerEnabled);
            presenter.Dispose();
        }
    }
}
