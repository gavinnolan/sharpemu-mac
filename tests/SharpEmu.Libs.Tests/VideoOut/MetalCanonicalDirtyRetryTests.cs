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
