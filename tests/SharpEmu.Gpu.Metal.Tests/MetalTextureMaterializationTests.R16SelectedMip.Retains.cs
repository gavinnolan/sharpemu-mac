// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    private sealed class R16RetainTestState : IDisposable
    {
        private readonly object? oldRelease = Presenter.GetField("_r16SelectedTextureReleaseOverride", Methods)!.GetValue(null);
        private readonly object? oldFailure = Presenter.GetField("_r16SelectedRetainCleanupFailure", Methods)!.GetValue(null);
        private readonly object? oldHook = Presenter.GetField("_storageImageBeforeMetadataPublication", Methods)!.GetValue(null);
        public R16RetainTestState() => Presenter.GetField("_r16SelectedRetainCleanupFailure", Methods)!.SetValue(null, null);
        public void Dispose()
        {
            // Fake retained handles belong only to this ordinary source-control
            // seam. Never pass them to the real native release on test cleanup.
            ((IDictionary)Presenter.GetField("_selectedStorageMipImages", Methods)!.GetValue(null)!).Clear();
            Presenter.GetField("_r16SelectedTextureReleaseOverride", Methods)!.SetValue(null, oldRelease);
            Presenter.GetField("_r16SelectedRetainCleanupFailure", Methods)!.SetValue(null, oldFailure);
            Presenter.GetField("_storageImageBeforeMetadataPublication", Methods)!.SetValue(null, oldHook);
        }
    }

    private static object R16ConstructionImage(nint handle)
    {
        var image = Activator.CreateInstance(Presenter.GetNestedType("GuestImage", BindingFlags.NonPublic)!, nonPublic: true)!;
        image.GetType().GetField("Texture")!.SetValue(image, handle);
        return image;
    }

    private static void PublishR16Construction(SharpEmu.Libs.Gpu.GuestDrawTexture snapshot, object image)
    {
        lock (Presenter.GetField("_gate", Methods)!.GetValue(null)!)
            Presenter.GetMethod("PublishStorageImageLocked", Methods)!.Invoke(null,
                [snapshot, SelectedMipAssociation(snapshot), image, (snapshot.Width, snapshot.Height, (ulong)snapshot.RgbaPixels.Length)]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void R16SelectedPublicationCannotAcceptOrRollbackReplacementNativeOwner(bool throws)
    {
        using var queue = new SelectedMipQueueScope(); using var observation = new R16SelectedObservationScope(); using var state = new R16RetainTestState();
        var (guest, _) = R16SelectedMipOracle.Seed();
        var snapshot = Assert.Single(R16SelectedSnapshots(new R16SelectedMemory(guest), R16SelectedBinding(3)));
        var key = SelectedMipAssociation(snapshot).GetType().GetProperty("Key")!.GetValue(SelectedMipAssociation(snapshot))!;
        var images = (IDictionary)Presenter.GetField("_selectedStorageMipImages", Methods)!.GetValue(null)!;
        var image = R16ConstructionImage((nint)701); var replacement = R16ConstructionImage((nint)702);
        var cause = new InvalidOperationException("replacement publication failed");
        Presenter.GetField("_storageImageBeforeMetadataPublication", Methods)!.SetValue(null, (Action<nint>)(_ =>
        {
            images[key] = replacement; if (throws) throw cause;
        }));
        var failure = Assert.Throws<TargetInvocationException>(() => PublishR16Construction(snapshot, image));
        if (throws) Assert.Same(cause, failure.InnerException);
        else Assert.Contains("exact construction owner", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
        Assert.Same(replacement, images[key]);
        Assert.False((bool)image.GetType().GetField("R16SelectedOwnsRetain")!.GetValue(image)!);
        Assert.Empty((IDictionary)Presenter.GetField("_selectedStorageMipOwners", Methods)!.GetValue(null)!);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void R16SelectedPostHookMappingSessionOrContentVetoKeepsConstructionRetainUnaccepted(int change)
    {
        using var queue = new SelectedMipQueueScope(); using var observation = new R16SelectedObservationScope(); using var state = new R16RetainTestState();
        var (guest, _) = R16SelectedMipOracle.Seed();
        var snapshot = Assert.Single(R16SelectedSnapshots(new R16SelectedMemory(guest), R16SelectedBinding(3)));
        var image = R16ConstructionImage((nint)801); var released = new List<nint>();
        var epoch = typeof(HostMemory).GetField("_mappingGeneration", Methods)!; var before = HostMemory.MappingGeneration;
        Presenter.GetField("_r16SelectedTextureReleaseOverride", Methods)!.SetValue(null, (Action<nint>)(handle => released.Add(handle)));
        Presenter.GetField("_storageImageBeforeMetadataPublication", Methods)!.SetValue(null, (Action<nint>)(_ =>
        {
            if (change == 1) Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!.Invoke(null, null);
            else if (change == 2) GuestImageWriteTracker.NotifyManagedWrite(R16SelectedAddress + 0x10000, 2);
            else HostMemory.OnMappingChanged();
        }));
        try
        {
            var failure = Assert.Throws<TargetInvocationException>(() => PublishR16Construction(snapshot, image));
            Assert.Contains(change == 1 ? "presenter-session" : change == 2 ? "content-token-changed" : "global-mapping-epoch",
                Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
            Assert.Empty(released); Assert.Equal((nint)801, image.GetType().GetField("Texture")!.GetValue(image));
            Assert.False((bool)image.GetType().GetField("R16SelectedOwnsRetain")!.GetValue(image)!);
            Assert.Empty((IDictionary)Presenter.GetField("_selectedStorageMipImages", Methods)!.GetValue(null)!);
            // Caller still owns this unaccepted construction retain; exercise
            // its actual finally helper without claiming a native refcount.
            image.GetType().GetField("Texture")!.SetValue(image, (nint)0);
            Presenter.GetMethod("ReleaseR16SelectedConstructionRetain", Methods)!.Invoke(null, [(nint)801, failure.InnerException]);
            Assert.Equal(new nint[] { 801 }, released);
        }
        finally { epoch.SetValue(null, before); }
    }

    [Fact]
    public void R16SelectedAcceptedCloseReentryConsumesEachExactRetainOnce()
    {
        using var queue = new SelectedMipQueueScope(); using var observation = new R16SelectedObservationScope(); using var state = new R16RetainTestState();
        var (guest, _) = R16SelectedMipOracle.Seed(); var memory = new R16SelectedMemory(guest);
        var snapshots = R16SelectedSnapshots(memory, R16SelectedBinding(2), R16SelectedBinding(3));
        var a = R16ConstructionImage((nint)901); var b = R16ConstructionImage((nint)902);
        PublishR16Construction(snapshots[0], a); PublishR16Construction(snapshots[1], b);
        var released = new List<nint>();
        Presenter.GetField("_r16SelectedTextureReleaseOverride", Methods)!.SetValue(null, (Action<nint>)(handle =>
        {
            released.Add(handle);
            if (handle == 901)
            {
                Assert.False((bool)a.GetType().GetField("R16SelectedOwnsRetain")!.GetValue(a)!);
                Assert.Equal((nint)0, a.GetType().GetField("Texture")!.GetValue(a));
                Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!.Invoke(null, null);
            }
        }));
        Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!.Invoke(null, null);
        Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!.Invoke(null, null);
        Assert.Equal(new nint[] { 901, 902 }, released);
        Assert.Empty((IDictionary)Presenter.GetField("_selectedStorageMipImages", Methods)!.GetValue(null)!);
    }

    [Fact]
    public void R16SelectedUncertainAcceptedReleaseIsStickyAndCannotRetryOrReplacePrimaryConstructionFailure()
    {
        using var queue = new SelectedMipQueueScope(); using var observation = new R16SelectedObservationScope(); using var state = new R16RetainTestState();
        var (guest, _) = R16SelectedMipOracle.Seed(); var memory = new R16SelectedMemory(guest);
        var snapshot = Assert.Single(R16SelectedSnapshots(memory, R16SelectedBinding(3)));
        var image = R16ConstructionImage((nint)1001); PublishR16Construction(snapshot, image);
        var cleanup = new InvalidOperationException("uncertain native retain release"); var calls = 0;
        Presenter.GetField("_r16SelectedTextureReleaseOverride", Methods)!.SetValue(null, (Action<nint>)(_ => { calls++; throw cleanup; }));
        for (var attempt = 0; attempt < 2; attempt++)
            Assert.Same(cleanup, Assert.Throws<TargetInvocationException>(() =>
                Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!.Invoke(null, null)).InnerException);
        Assert.Equal(1, calls); Assert.False((bool)image.GetType().GetField("R16SelectedOwnsRetain")!.GetValue(image)!);
        Assert.Equal((nint)0, image.GetType().GetField("Texture")!.GetValue(image));
        var primary = new InvalidOperationException("original construction failure");
        Presenter.GetMethod("ReleaseR16SelectedConstructionRetain", Methods)!.Invoke(null, [(nint)1002, primary]);
        Assert.Same(cleanup, primary.Data["MetalR16SelectedSecondaryCleanupFailure"]);
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() =>
            R16SelectedSnapshots(memory, R16SelectedBinding(3))).InnerException);
        Assert.Equal(2, calls); // The second call belonged to a distinct constructor, never a retried accepted retain.
    }
}
