// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Collections;
using System.Runtime.CompilerServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalCanonicalDirtyRetryTests
{
    [Fact]
    public void RefusedDirtyDrainLeavesActualQueuedLabelAndCompletedSequenceUnchanged()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        var label = 0; var busy = true;
        using var boundary = new RefusalBoundary(() => !busy);
        var sequence = MetalVideoPresenter.SubmitOrderedGuestAction(() => label = 7, "retry label");
        Assert.False(MetalVideoPresenter.TryDrainGuestWork(0, 0));
        Assert.Equal(0, label); Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
        Assert.Null(Field("_guestWorkFailure").GetValue(null));
        busy = false;
        Assert.True(MetalVideoPresenter.TryDrainGuestWork(0, 0));
        Assert.Equal(7, label); Assert.True(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
    }

    [Fact]
    public void RepeatedRefusedTicksDoNotExecuteOrAcknowledgeFutureQueues()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        using var boundary = new RefusalBoundary(() => false);
        var executed = 0;
        long first, second;
        using (MetalVideoPresenter.EnterGuestQueue("dirty.first", 11))
            first = MetalVideoPresenter.SubmitOrderedGuestAction(() => executed++, "first");
        using (MetalVideoPresenter.EnterGuestQueue("dirty.second", 12))
            second = MetalVideoPresenter.SubmitOrderedGuestAction(() => executed++, "second");
        for (var i = 0; i < 3; i++) Assert.False(MetalVideoPresenter.TryDrainGuestWork(0, 0));
        Assert.Equal(0, executed);
        Assert.False(MetalVideoPresenter.WaitForGuestWork(first, 0));
        Assert.False(MetalVideoPresenter.WaitForGuestWork(second, 0));
        Assert.Equal(2, Field("_pendingGuestWorkCount").GetValue(null));
    }

    [Fact]
    public void RefusedFrameDrainCannotReachLaterFrameOrQueueWork()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        using var boundary = new RefusalBoundary(() => false);
        var frameWork = 0; var label = 0;
        MetalVideoPresenter.SubmitOrderedGuestAction(() => label = 1, "frame-blocked label");
        // The production RenderFrame uses this same explicit result BEFORE
        // materialization, drawable acquisition, encoding and presentation.
        if (MetalVideoPresenter.TryDrainGuestWork(0, 0)) frameWork++;
        Assert.Equal(0, frameWork); Assert.Equal(0, label);
    }

    [Fact]
    public void RetryBoundaryFailureCannotAcknowledgeQueuedLabel()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        var cause = new IOException("original dirty owner stale");
        using var boundary = new RefusalBoundary(() => throw cause);
        var label = 0;
        var sequence = MetalVideoPresenter.SubmitOrderedGuestAction(() => label = 9, "stale label");
        Assert.Same(cause, Record.Exception(() => MetalVideoPresenter.TryDrainGuestWork(0, 0)));
        Assert.Equal(0, label); Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualRetryCannotBorrowReplacementOrChangedVersionWithNoPendingWork(bool replacement)
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        const ulong address = 0x74100000;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var presenter = typeof(MetalVideoPresenter);
        var imageType = presenter.GetNestedType("GuestImage", BindingFlags.NonPublic)!;
        var originType = presenter.GetNestedType("StorageCpuSeedOrigin", BindingFlags.NonPublic)!;
        var debtType = presenter.GetNestedType("FiniteStorageDebt", BindingFlags.NonPublic)!;
        var receiptType = presenter.GetNestedType("DeferredCanonicalDirty", BindingFlags.NonPublic)!;
        var image = Activator.CreateInstance(imageType, nonPublic: true)!;
        // Deliberately lacks content/provider authority. The actual retry must
        // reject identity/version before touching any origin or provider field.
        var origin = RuntimeHelpers.GetUninitializedObject(originType);
        var debt = Activator.CreateInstance(debtType, nonPublic: true)!;
        imageType.GetField("StorageCpuOrigin", flags)!.SetValue(image, origin);
        imageType.GetField("ContentVersion", flags)!.SetValue(image, 7);
        var receipt = Activator.CreateInstance(receiptType, flags, null,
            new object[] { address, image, debt, origin, 7, (nint)0 }, null)!;
        var images = (IDictionary)Field("_guestImages").GetValue(null)!;
        Assert.False(images.Contains(address));
        var saved = Field("_deferredCanonicalDirty").GetValue(null);
        try
        {
            images.Add(address, replacement ? Activator.CreateInstance(imageType, nonPublic: true)! : image);
            if (!replacement) imageType.GetField("ContentVersion", flags)!.SetValue(image, 8);
            Field("_deferredCanonicalDirty").SetValue(null, receipt);
            Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null));
            var call = presenter.GetMethod("RetryDeferredCanonicalDirty", BindingFlags.Static | BindingFlags.NonPublic)!;
            var failure = Assert.Throws<TargetInvocationException>(() => call.Invoke(null, null));
            Assert.Contains("ORIGINAL receipt owner or version changed", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
            Assert.Same(receipt, Field("_deferredCanonicalDirty").GetValue(null));
            Assert.Null(Field("_guestWorkFailure").GetValue(null));
        }
        finally { images.Remove(address); Field("_deferredCanonicalDirty").SetValue(null, saved); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ClosedRetiredLifetimeDropsOnlyRetryBookkeepingAndNeverAcknowledgesOldLabels(bool cleanupFailure)
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        var presenter = typeof(MetalVideoPresenter);
        var instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var originType = presenter.GetNestedType("StorageCpuSeedOrigin", BindingFlags.NonPublic)!;
        var debtType = presenter.GetNestedType("FiniteStorageDebt", BindingFlags.NonPublic)!;
        var imageType = presenter.GetNestedType("GuestImage", BindingFlags.NonPublic)!;
        var receiptType = presenter.GetNestedType("DeferredCanonicalDirty", BindingFlags.NonPublic)!;
        var contentType = originType.GetProperty("Observation")!.PropertyType;
        var origin = Activator.CreateInstance(originType, instance, null, new object?[]
            { null, null, null, null, Activator.CreateInstance(contentType), MetalVideoPresenter.CurrentStorageCpuSeedSession }, null)!;
        var debt = Activator.CreateInstance(debtType, nonPublic: true)!;
        debtType.GetField("CanonicalVersion", instance)!.SetValue(debt, 7);
        debtType.GetField("CanonicalOrigin", instance)!.SetValue(debt, origin);
        var image = Activator.CreateInstance(imageType, nonPublic: true)!;
        var receipt = Activator.CreateInstance(receiptType, instance, null,
            new object[] { 0x74110000UL, image, debt, origin, 7, (nint)0 }, null)!;
        var names = new[] { "_deferredCanonicalDirty", "_renderOwner", "_selectedStorageMipSession", "_typedCpuSyncCleanupFailure",
            "_guestPresentationShutdownAttempt", "_guestPresentationNativeOwnersDrained" };
        var saved = names.ToDictionary(name => name, name => Field(name).GetValue(null));
        object? Call(string method) => presenter.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        try
        {
            // These controls have zero native owners: actual retirement methods
            // must not dereference fabricated native handles or query status.
            foreach (var name in new[] { "_singletonVolumeOriginalWatches", "_selectedStorageMipImages", "_nativeDepthBatches",
                "_selectedStorageMipOwners", "_selectedStorageMipPending", "_selectedStorageMipNativeQueues", "_selectedNativeOriginalProofs" })
                Assert.Empty((IEnumerable)Field(name).GetValue(null)!);
            Field("_renderOwner").SetValue(null, Thread.CurrentThread);
            Field("_deferredCanonicalDirty").SetValue(null, receipt);
            Assert.False((bool)Call("DropDeferredCanonicalDirtyAfterShutdown")!);
            var label = 0;
            var old = MetalVideoPresenter.SubmitOrderedGuestAction(() => label = 1, "old deferred label");
            Call("CompletePresenterShutdown");
            Assert.False((bool)Call("DropDeferredCanonicalDirtyAfterShutdown")!); // no retirement/session transition yet
            Call("ReleaseSingletonVolumeNativeOriginals");
            Call("ReleaseSelectedStorageMipImages");
            var failure = new IOException("retirement outcome unknown");
            if (cleanupFailure) Field("_typedCpuSyncCleanupFailure").SetValue(null, failure);
            Assert.Equal(!cleanupFailure, Call("DropDeferredCanonicalDirtyAfterShutdown"));
            Assert.Equal(0, label); Assert.False(MetalVideoPresenter.WaitForGuestWork(old, 0));
            Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
            if (cleanupFailure)
            {
                Assert.Same(receipt, Field("_deferredCanonicalDirty").GetValue(null));
                Assert.Same(failure, Field("_typedCpuSyncCleanupFailure").GetValue(null));
            }
            else
            {
                Assert.Null(Field("_deferredCanonicalDirty").GetValue(null));
                // There is no production reopen API. This fresh scheduling
                // lifetime verifies only that old retry metadata cannot block
                // or adopt a new session; canceled old work is never reused.
                using var restarted = new MetalGuestFailureTests.PresenterState();
                var next = MetalVideoPresenter.SubmitOrderedGuestAction(() => label = 2, "new lifetime label");
                Assert.True(MetalVideoPresenter.TryDrainGuestWork(0, 0));
                Assert.Equal(2, label); Assert.True(MetalVideoPresenter.WaitForGuestWork(next, 0));
            }
        }
        finally { foreach (var entry in saved) Field(entry.Key).SetValue(null, entry.Value); }
    }

    private static FieldInfo Field(string name) => typeof(MetalVideoPresenter)
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    private sealed class RefusalBoundary : IDisposable
    {
        private readonly FieldInfo field = Field("_canonicalDirtyDrainReadyForControl");
        private readonly object? original;
        internal RefusalBoundary(Func<bool> callback) { original = field.GetValue(null); field.SetValue(null, callback); }
        public void Dispose() => field.SetValue(null, original);
    }
}
