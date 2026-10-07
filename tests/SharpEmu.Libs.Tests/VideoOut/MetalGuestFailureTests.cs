// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalGuestFailureTests
{
    [Fact]
    public void FailedOrderedWorkIsFatalAndNeverAcknowledgedOrFollowed()
    {
        using var state = new PresenterState();
        var reports = new List<string>();
        GpuFatal.OnFatal = reports.Add;
        var original = new NotSupportedException("Layered snapshot is incomplete.");
        var executed = new List<string>();
        using var queue = MetalVideoPresenter.EnterGuestQueue("compute.test", 42);
        var good = MetalVideoPresenter.SubmitOrderedGuestAction(() => executed.Add("good"), "good");
        var bad = MetalVideoPresenter.SubmitOrderedGuestAction(() => throw original, "broken upload");
        var following = MetalVideoPresenter.SubmitOrderedGuestAction(() => executed.Add("following"), "following");

        var fatal = Assert.Throws<GpuFatalException>(() =>
        {
            // The real consumer may yield after the successful item if its
            // frame budget expires; the next drain must still stop at failure.
            MetalVideoPresenter.DrainGuestWork(0, 0);
            MetalVideoPresenter.DrainGuestWork(0, 0);
        });
        Assert.Same(original, fatal.InnerException);
        Assert.Equal(new[] { "good" }, executed);
        Assert.True(MetalVideoPresenter.WaitForGuestWork(good, 0));
        // Even an indefinite wait exits unsuccessfully when execution failed.
        Assert.False(MetalVideoPresenter.WaitForGuestWork(bad, Timeout.Infinite));
        Assert.False(MetalVideoPresenter.WaitForGuestWork(following, Timeout.Infinite));
        Assert.Equal(0, MetalVideoPresenter.SubmitOrderedGuestAction(() => executed.Add("later"), "later"));
        Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
        Assert.Equal(new[] { "good" }, executed);
        var report = Assert.Single(reports);
        Assert.Contains("kind=OrderedGuestAction", report);
        Assert.Contains($"sequence={bad}", report);
        Assert.Contains("queue='compute.test' submission=42 action='broken upload'", report);
        Assert.Contains(original.ToString(), report);
    }

    [Fact]
    public void ThrowingFatalHookIsNotReportedAgainByTheFrameBoundary()
    {
        using var state = new PresenterState();
        using var fatal = new FatalScope();
        MetalVideoPresenter.SubmitOrderedGuestAction(() => throw new NotSupportedException("bad dispatch"), "dispatch");
        Assert.Throws<SchedulerFatalException>(() => MetalVideoPresenter.RenderFrameSafely(
            () => MetalVideoPresenter.DrainGuestWork(0, 0)));
        Assert.Single(fatal.Messages);
    }

    [Fact]
    public void UnexpectedFrameFailuresKeepTheirCauseAndBecomeFatal()
    {
        using var state = new PresenterState();
        var reports = new List<string>();
        GpuFatal.OnFatal = reports.Add;
        var original = new InvalidOperationException("Device command failed.");
        var fatal = Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.RenderFrameSafely(() => throw original));
        Assert.Same(original, fatal.InnerException);
        Assert.Contains(original.ToString(), Assert.Single(reports));
    }

    [Fact]
    public void NormalFrameSkipsAndAlreadyReportedFailuresDoNotReportAgain()
    {
        using var state = new PresenterState();
        var reports = new List<string>();
        GpuFatal.OnFatal = reports.Add;
        MetalVideoPresenter.RenderFrameSafely(() => { });
        Assert.Empty(reports);
        Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.RenderFrameSafely(
            () => throw GpuFatal.Fatal("Already reported.")));
        Assert.Single(reports);
    }

    // The real consumer runs ordered managed actions with no device. Static
    // scheduling state is isolated and restored under the existing solo collection.
    internal sealed class PresenterState : IDisposable
    {
        private readonly Dictionary<FieldInfo, object?> saved = new();
        private readonly List<Action> restoreCollections = new();
        private readonly Action<string> previousFatal = GpuFatal.OnFatal;

        public PresenterState()
        {
            Set("_thread", Thread.CurrentThread);
            Set("_closed", false);
            Set("_guestWorkFailure", null);
            Set("_enqueuedGuestWorkSequence", 0L);
            Set("_completedGuestWorkSequence", 0L);
            Set("_executingGuestWorkSequence", 0L);
            Set("_pendingGuestWorkCount", 0);
            Set("_pendingGuestWorkBytes", 0UL);
            Set("_pendingGuestQueueCursor", 0);
            Set("_enqueueAsImmediateQueueFollowup", false);
            Set("_immediateFollowupTail", null);
            Set("_cpuWrittenGuestImageSyncRequested", 0);
            Assert.False((bool)Field("_batchOpen").GetValue(null)!);
            Set("_batchOpen", false);
            Set("_batchCommandBuffer", (nint)0);
            ClearDictionary("_pendingGuestWorkByQueue");
            ClearDictionary("_lastEnqueuedGuestWorkByQueue");
            ClearDictionary("_guestImageExtents");
            ClearDictionary("_availableGuestImages");
            ClearDictionary("_guestImageWorkSequences");
            ClearList("_pendingGuestQueueSchedule");
            foreach (var name in new[] { "_retiredUploadPages", "_retiredSnapshotResources", "_pendingFaultScans" })
                Assert.Empty((IEnumerable)Field(name).GetValue(null)!);
            var outOfOrder = (HashSet<long>)Field("_completedGuestWorkOutOfOrder").GetValue(null)!;
            var entries = outOfOrder.ToArray();
            outOfOrder.Clear();
            restoreCollections.Add(() => { outOfOrder.Clear(); outOfOrder.UnionWith(entries); });
        }

        private static FieldInfo Field(string name) => typeof(MetalVideoPresenter)
            .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;

        private void Set(string name, object? value)
        {
            var field = Field(name);
            saved.Add(field, field.GetValue(null));
            field.SetValue(null, value);
        }

        private void ClearDictionary(string name)
        {
            var dictionary = (IDictionary)Field(name).GetValue(null)!;
            var entries = new List<DictionaryEntry>();
            foreach (DictionaryEntry entry in dictionary) entries.Add(entry);
            dictionary.Clear();
            restoreCollections.Add(() =>
            {
                dictionary.Clear();
                foreach (var entry in entries) dictionary.Add(entry.Key, entry.Value);
            });
        }

        private void ClearList(string name)
        {
            var list = (IList)Field(name).GetValue(null)!;
            var entries = list.Cast<object>().ToArray();
            list.Clear();
            restoreCollections.Add(() =>
            {
                list.Clear();
                foreach (var entry in entries) list.Add(entry);
            });
        }

        public void Dispose()
        {
            foreach (var restore in restoreCollections) restore();
            foreach (var entry in saved) entry.Key.SetValue(null, entry.Value);
            GpuFatal.OnFatal = previousFatal;
        }
    }
}
