// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    private static AgcExports.TranslatedImageBinding VolumeBinding()
    {
        uint[] raw = [(uint)(Address >> 8), 0x03C00000, 0, 0xA1B00FAC, 0, 0x00700000, 0, 0];
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        return new(descriptor, true, 0, [], IsArrayed: false, ResourceDescriptor: raw,
            Shape: new ShaderImageShape(true, false, true, false, TextureNumericClass.Uint), Access: GuestImageAccess.Write);
    }
    private static byte[] VolumePhysical()
    {
        var bytes = Enumerable.Range(0, 65536).Select(index => (byte)(index * 37 + 11)).ToArray();
        new byte[] { 3, 127, 239, 251 }.CopyTo(bytes, 0);
        return bytes;
    }
    private static GuestDrawTexture VolumeSnapshot(VolumeMemory memory) => Assert.Single(
        MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [VolumeBinding()], out _));
    private static object VolumeWatch(object origin) => origin.GetType().GetProperty("VolumeWatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(origin)!;
    private static object? InvokeVolume(string name, params object?[] args) => Presenter.GetMethod(name, Private)!.Invoke(null, args);
    private static object VolumeImage(object origin, ICpuMemory memory)
    {
        var image = InstallImage(origin, memory); Set(image, "TextureType", 7u); return image;
    }
    private static void PublishVolume(GuestDrawTexture snapshot, object image)
    {
        Images.Remove(Address);
        lock (Field("_gate").GetValue(null)!) InvokeVolume("PublishStorageImageLocked", snapshot, null, image, (1u, 1u, 4UL));
    }

    [Fact]
    public void SingletonVolumeActualProducerRetainsFullOriginalWatchAndFourUintOriginBytes()
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot);
        try
        {
            Assert.Equal(memory.Bytes[..4], snapshot.RgbaPixels); Assert.Equal(65536UL, snapshot.SourceByteCount);
            Assert.Equal((7u, 1u, 1u), MetalVideoPresenter.GuestTextureShape(snapshot));
            var layout = Assert.IsType<MetalStorageCpuLayout>(origin.GetType().GetProperty("Layout")!.GetValue(origin));
            Assert.Equal(MtlPixelFormat.Rgba8Uint, layout.NativeFormat.Format); Assert.Equal(4, layout.LinearBytes);
            Assert.Same(memory.LastMapping, origin.GetType().GetProperty("Mapping")!.GetValue(origin));
            Assert.Equal(MetalVideoPresenter.CurrentStorageCpuSeedSession, origin.GetType().GetProperty("Session")!.GetValue(origin));
            var watch = VolumeWatch(origin);
            Assert.Same(memory.LastMapping, watch.GetType().GetField("Mapping", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watch));
            Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads); Assert.Equal(0, memory.Releases);
            var copy = snapshot with { Sampler = new(1, 2, 3, 4) };
            MetalVideoPresenter.CopyCapturedTextureSource(snapshot, copy);
            Assert.Same(origin, GetOrigin(copy)); // same immutable original, never a lookup recapture
            Assert.Throws<NotSupportedException>(() => layout.Encode(new byte[4])); // no volume publisher expansion
            MetalVideoPresenter.ReleasePreparedTextureSources([snapshot, copy]);
            MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]);
            Assert.Equal(1, memory.Releases);
        }
        finally { MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
    }

    [Theory]
    [InlineData("raw-perf")] [InlineData("raw-flags")] [InlineData("shape")] [InlineData("unknown-access")]
    public void SingletonVolumeNearCapturedClassRefusesBeforeAnyMappedRead(string change)
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        var binding = VolumeBinding(); var raw = binding.ResourceDescriptor!.ToArray();
        if (change == "raw-perf") raw[5] ^= 1;
        if (change == "raw-flags") raw[6] = 1;
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        binding = binding with { Descriptor = descriptor, ResourceDescriptor = raw };
        if (change == "shape") binding = binding with { Shape = binding.Shape with { Volume = false } };
        if (change == "unknown-access") binding = binding with { Access = GuestImageAccess.Unknown };
        Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(memory, Generation.Gen5), [binding], out _));
        Assert.Equal(0, memory.Copies); Assert.Equal(0, memory.Reads); Assert.Equal(0, memory.Releases);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SingletonVolumeLegacyUnavailableOrDisabledDoesNotManufactureOriginalOwner(bool disabled)
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical()) { Result = CpuMappedCopyResult.Unavailable };
        if (disabled) Field("_finiteStorageWritebackEnabled").SetValue(null, false);
        var snapshot = VolumeSnapshot(memory);
        Assert.Null(InvokeVolume("StorageCpuOrigin", snapshot));
        Assert.Equal(disabled ? 0 : 1, memory.Copies); Assert.Equal(1, memory.Reads); Assert.Equal(0, memory.Releases);
        Assert.Equal(memory.Bytes[..4], snapshot.RgbaPixels);
    }

    [Theory]
    [InlineData("rejected")] [InlineData("stale-mapping")] [InlineData("stale-watch")]
    public void SingletonVolumeRejectedOrStaleOriginalNeverFallsBackToOrdinaryMemory(string reason)
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        if (reason == "rejected") memory.Result = CpuMappedCopyResult.Rejected;
        if (reason == "stale-mapping") memory.Current = false;
        if (reason == "stale-watch") memory.WatchCurrent = false;
        Assert.Throws<InvalidOperationException>(() => VolumeSnapshot(memory));
        Assert.Equal(3, memory.Copies); Assert.Equal(0, memory.Reads);
        Assert.Equal(reason == "rejected" ? 0 : 3, memory.Releases);
    }

    [Fact]
    public void SingletonVolumeResetDuringCopyPreservesPrimaryAndConsumesUnknownCleanupOnce()
    {
        ActualArchitecture(); using var state = new VolumeState();
        var cleanup = new InvalidOperationException("watch release unknown");
        var memory = new VolumeMemory(VolumePhysical()) { ReleaseFailure = cleanup,
            AfterCopy = () => Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentStorageCpuSeedSession + 1) };
        var error = Assert.Throws<InvalidOperationException>(() => VolumeSnapshot(memory));
        Assert.Contains("session changed during the original copy", error.Message);
        Assert.Same(cleanup, error.Data["MetalSingletonVolumeUnregisteredCleanup"]);
        Assert.Equal(1, memory.Copies); Assert.Equal(1, memory.Releases); Assert.Equal(0, memory.Reads);
        Assert.False(Images.Contains(Address));
    }

    [Theory]
    [InlineData("watch")] [InlineData("token")]
    public void SingletonVolumeOriginalVetoRejectsEnsureBeforeAnyNativeAllocation(string change)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory);
        MetalVideoPresenter.AttachGuestMemory(memory);
        try
        {
            if (change == "watch") memory.WatchCurrent = false;
            else protection.Generation(1);
            var error = Assert.Throws<TargetInvocationException>(() => InvokeVolume("EnsureStorageImage", (nint)0, snapshot));
            Assert.Contains("ORIGINAL", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
            Assert.False(Images.Contains(Address)); Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads);
        }
        finally { MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
    }

    [Fact]
    public void SingletonVolumeOriginalPublicationTransfersOnlyExactNativeOwnerAndRetirementConsumesOnce()
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot);
        var image = VolumeImage(origin, memory);
        try
        {
            PublishVolume(snapshot, image);
            MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); Assert.Equal(0, memory.Releases);
            lock (Field("_gate").GetValue(null)!) Assert.True((bool)InvokeVolume("IsSingletonVolumeNativeOwnerCurrentLocked", image, origin)!);
            Field("_executingGuestWorkSequence").SetValue(null, 24L);
            MetalVideoPresenter.RecordStorageImageWrites([snapshot]);
            Assert.Equal(2, image.GetType().GetField("ContentVersion")!.GetValue(image));
            lock (Field("_gate").GetValue(null)!) Assert.True((bool)InvokeVolume("IsSingletonVolumeNativeOwnerCurrentLocked", image, origin)!);
            // Actual retirement hook, synthetic native pointer never dispatched/released.
            lock (Field("_gate").GetValue(null)!)
            {
                InvokeVolume("NoteFiniteStorageOwnerRetirementLocked", image);
                Images.Remove(Address);
                InvokeVolume("NoteFiniteStorageOwnerRetirementLocked", image);
            }
            Assert.Equal(1, memory.Releases); Assert.Equal((nint)123, image.GetType().GetField("Texture")!.GetValue(image));
        }
        finally { Images.Remove(Address); MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
    }

    [Theory]
    [InlineData("session")] [InlineData("watch")]
    public void SingletonVolumePublicationCallbackCannotTransferResetOrStaleOriginal(string change)
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        var snapshot = VolumeSnapshot(memory); var image = VolumeImage(GetOrigin(snapshot), memory);
        var hook = Field("_storageImageBeforeMetadataPublication"); var old = hook.GetValue(null);
        try
        {
            hook.SetValue(null, (Action<nint>)(_ =>
            {
                if (change == "watch") memory.WatchCurrent = false;
                else Field("_selectedStorageMipSession").SetValue(null, MetalVideoPresenter.CurrentStorageCpuSeedSession + 1);
            }));
            Assert.Throws<TargetInvocationException>(() => PublishVolume(snapshot, image));
            Assert.False(Images.Contains(Address));
            Assert.Equal((nint)123, image.GetType().GetField("Texture")!.GetValue(image)); // unpublished constructor owns native retain
        }
        finally { hook.SetValue(null, old); Images.Remove(Address); MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
        Assert.Equal(1, memory.Releases);
    }

    private static long SubmitVolume(IReadOnlyList<GuestDrawTexture> textures, bool draw = false)
    {
        var shader = new MetalCompiledGuestShader(new Gen5MslShader("", "volume_queue_ownership", Gen5MslStage.Compute, 0));
        if (!draw) return MetalVideoPresenter.SubmitComputeDispatch(0, shader, textures, [], 1, 1, 1, 0, 0, 0, false, null);
        MetalVideoPresenter.SubmitOffscreenTranslatedDraw(shader, textures, [], 0,
            [new GuestRenderTarget(Address + 0x20000, 4, 4, 10, 0)], null, 3, 1, 4, null, null, null, null, 0);
        return (long)Field("_enqueuedGuestWorkSequence").GetValue(null)!;
    }
    private static object TakeVolumeWork()
    {
        object?[] arguments = [null];
        Assert.True((bool)Presenter.GetMethod("TryTakeGuestWork", Private)!.Invoke(null, arguments)!);
        return arguments[0]!;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SingletonVolumeActualSubmissionRetainsPreparationThroughTakeAndCompletion(bool draw)
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        var snapshot = VolumeSnapshot(memory); var watch = VolumeWatch(GetOrigin(snapshot));
        var sequence = SubmitVolume([snapshot, snapshot], draw);
        Assert.True(sequence > 0); Assert.False(MetalVideoPresenter.TextureSourceGraphEnabled);
        Assert.Equal(1, watch.GetType().GetField("QueuedReferences", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watch));
        // Actual frontend cleanup after Submit, not direct invocation of the retain helper.
        MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); Assert.Equal(0, memory.Releases);
        var pending = TakeVolumeWork();
        Assert.Equal(sequence, pending.GetType().GetProperty("Sequence")!.GetValue(pending));
        Assert.Equal(0, memory.Releases);
        InvokeVolume("CompleteGuestWork", pending); // queue bookkeeping only; no encoded/GPU success claim
        Assert.Equal(1, memory.Releases); Assert.Equal(sequence, Field("_completedGuestWorkSequence").GetValue(null));
        Assert.Equal(0, watch.GetType().GetField("QueuedReferences", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watch));
        Assert.Empty((IEnumerable)Field("_pendingSingletonVolumeOriginalWorks").GetValue(null)!);
        MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); Assert.Equal(1, memory.Releases);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SingletonVolumeActualShutdownCancelsQueuedOrTakenPreparationWithoutCompletion(bool taken)
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        var snapshot = VolumeSnapshot(memory); Assert.True(SubmitVolume([snapshot]) > 0);
        MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]);
        if (taken) _ = TakeVolumeWork();
        Assert.Equal(0, memory.Releases);
        InvokeVolume("CompletePresenterShutdown"); InvokeVolume("CompletePresenterShutdown");
        Assert.Equal(1, memory.Releases); Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        Assert.Empty((IEnumerable)Field("_pendingSingletonVolumeOriginalWorks").GetValue(null)!);
    }

    [Fact]
    public void SingletonVolumeActualAdmissionFailureRollsBackQueueAndPreservesPrimary()
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        var snapshot = VolumeSnapshot(memory); var primary = new InvalidOperationException("inserted work cannot publish");
        var hook = Field("_singletonVolumeAfterOrdinaryQueueInsert"); var old = hook.GetValue(null);
        try
        {
            hook.SetValue(null, (Action)(() => throw primary));
            Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => SubmitVolume([snapshot])));
            Assert.Same(primary, Field("_guestWorkFailure").GetValue(null));
            Assert.Equal(0L, Field("_enqueuedGuestWorkSequence").GetValue(null));
            Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null));
            Assert.Equal(0UL, Field("_pendingGuestWorkBytes").GetValue(null));
            Assert.Empty((IEnumerable)Field("_pendingGuestWorkByQueue").GetValue(null)!);
            Assert.Empty((IEnumerable)Field("_pendingSingletonVolumeOriginalWorks").GetValue(null)!);
            Assert.Equal(0, memory.Releases); // original preparation is still caller-owned
            MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); Assert.Equal(1, memory.Releases);
        }
        finally { hook.SetValue(null, old); MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
    }

    [Fact]
    public void SingletonVolumeActualDrainFailureCancelsPendingWatchAndRetainsOriginalCause()
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        var primary = new InvalidOperationException("earlier ordinary action failed");
        GpuFatal.OnFatal = _ => { }; // actual fatal exception, never process FailFast in this control
        Assert.True(MetalVideoPresenter.SubmitOrderedGuestAction(() => throw primary, "before volume") > 0);
        var snapshot = VolumeSnapshot(memory); var sequence = SubmitVolume([snapshot]);
        Assert.True(sequence > 0); MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]);
        Assert.Equal(0, memory.Releases);
        var error = Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
        Assert.Same(primary, error.InnerException); Assert.Same(primary, Field("_guestWorkFailure").GetValue(null));
        Assert.Equal(1, memory.Releases); Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
        Assert.Empty((IEnumerable)Field("_pendingSingletonVolumeOriginalWorks").GetValue(null)!);
        InvokeVolume("CompletePresenterShutdown"); Assert.Equal(1, memory.Releases);
    }

    [Fact]
    public void SingletonVolumeActualFollowupAdmissionHasFinite65WorkOwnershipAndNo66thRetain()
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        var snapshot = VolumeSnapshot(memory); var watch = VolumeWatch(GetOrigin(snapshot));
        Field("_enqueueAsImmediateQueueFollowup").SetValue(null, true); // real consumer follow-up bypasses ordinary backpressure
        for (var index = 0; index < 65; index++) Assert.True(SubmitVolume([snapshot]) > 0);
        var error = Assert.Throws<InvalidOperationException>(() => SubmitVolume([snapshot]));
        Assert.Contains("finite bound", error.Message);
        Assert.Equal(65L, Field("_enqueuedGuestWorkSequence").GetValue(null));
        Assert.Equal(65, Field("_pendingGuestWorkCount").GetValue(null));
        Assert.Equal(65, watch.GetType().GetField("QueuedReferences", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(watch));
        MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); Assert.Equal(0, memory.Releases);
        InvokeVolume("CompletePresenterShutdown"); Assert.Equal(1, memory.Releases);
        Assert.Empty((IEnumerable)Field("_pendingSingletonVolumeOriginalWorks").GetValue(null)!);
    }

    [Fact]
    public void SingletonVolumePriorPreparationCleanupFailureLatchesOrdinaryCompletionAndCancelsOtherOwners()
    {
        ActualArchitecture(); using var state = new VolumeState();
        var first = new VolumeMemory(VolumePhysical()); var second = new VolumeMemory(VolumePhysical());
        var firstSnapshot = VolumeSnapshot(first); var secondSnapshot = VolumeSnapshot(second);
        var primary = new InvalidOperationException("a different preparation release is unknown");
        var failing = new VolumeMemory(VolumePhysical()) { ReleaseFailure = primary };
        var failedPreparation = VolumeSnapshot(failing); var executed = 0;
        var action = MetalVideoPresenter.SubmitOrderedGuestAction(() => executed++, "before sticky cleanup");
        Assert.True(action > 0); Assert.True(SubmitVolume([firstSnapshot]) > 0); Assert.True(SubmitVolume([secondSnapshot]) > 0);
        MetalVideoPresenter.ReleasePreparedTextureSources([firstSnapshot, secondSnapshot]);
        Assert.Same(primary, Assert.Throws<InvalidOperationException>(() =>
            MetalVideoPresenter.ReleasePreparedTextureSources([failedPreparation])));
        Assert.Null(Field("_guestWorkFailure").GetValue(null)); // optional cleanup is sticky, not yet a drain failure
        Assert.Same(primary, Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.DrainGuestWork(0, 0)));
        Assert.Equal(1, executed); Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        Assert.Same(primary, Field("_guestWorkFailure").GetValue(null));
        Assert.False(MetalVideoPresenter.WaitForGuestWork(action, 0));
        Assert.Equal(1, first.Releases); Assert.Equal(1, second.Releases); Assert.Equal(1, failing.Releases);
        Assert.Empty((IEnumerable)Field("_pendingSingletonVolumeOriginalWorks").GetValue(null)!);
        InvokeVolume("CompletePresenterShutdown"); Assert.Equal(1, failing.Releases); // no uncertain cleanup retry
    }

    [Fact]
    public void SingletonVolumeUnknownNativeProducerCannotRefreshOriginalVersionProof()
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot);
        var image = VolumeImage(origin, memory);
        try
        {
            PublishVolume(snapshot, image);
            Field("_executingGuestWorkSequence").SetValue(null, 24L);
            image.GetType().GetMethod("MarkContentChangedWithDiagnostic")!.Invoke(image, ["guest-blit"]);
            lock (Field("_gate").GetValue(null)!)
                Assert.False((bool)InvokeVolume("IsSingletonVolumeNativeOwnerCurrentLocked", image, origin)!);
            Assert.Same(memory.LastMapping, origin.GetType().GetProperty("Mapping")!.GetValue(origin));
            InvokeVolume("ReleaseSingletonVolumeNativeOriginals");
            Assert.Equal(1, memory.Releases);
        }
        finally { Images.Remove(Address); MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
    }

    [Fact]
    public void SingletonVolumePostPublicationChangedTokenStillRejectsOriginalOutsidePresenterGate()
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var snapshot = VolumeSnapshot(memory); var origin = GetOrigin(snapshot);
        var image = VolumeImage(origin, memory); var hook = Field("_storageImageBeforeMetadataPublication"); var old = hook.GetValue(null);
        try
        {
            hook.SetValue(null, (Action<nint>)(_ => GuestImageWriteTracker.NotifyManagedWrite(Address + 31, 1)));
            PublishVolume(snapshot, image); // scalar bookkeeping cannot sample the tracker while this gate is held
            var error = Assert.Throws<TargetInvocationException>(() => InvokeVolume("ValidateSingletonVolumeOriginalContent", origin));
            Assert.Contains("ORIGINAL", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
            // No positive native execution/ack is asserted by this synthetic construction control.
            Assert.Same(memory.LastMapping, origin.GetType().GetProperty("Mapping")!.GetValue(origin));
            InvokeVolume("ReleaseSingletonVolumeNativeOriginals"); Assert.Equal(1, memory.Releases);
        }
        finally { hook.SetValue(null, old); Images.Remove(Address); MetalVideoPresenter.ReleasePreparedTextureSources([snapshot]); }
    }

    [Fact]
    public void SingletonVolumeCanceledQueuedWatchCleanupAttemptsAllAndNeverRetriesUnknownRelease()
    {
        ActualArchitecture(); using var state = new VolumeState();
        var first = new VolumeMemory(VolumePhysical()) { ReleaseFailure = new InvalidOperationException("first exact release unknown") };
        var second = new VolumeMemory(VolumePhysical());
        var snapshots = new[] { VolumeSnapshot(first), VolumeSnapshot(second) };
        Assert.True(SubmitVolume(snapshots) > 0);
        MetalVideoPresenter.ReleasePreparedTextureSources(snapshots);
        var pending = TakeVolumeWork();
        var error = Assert.Throws<TargetInvocationException>(() => InvokeVolume("CompleteGuestWork", pending));
        Assert.Same(first.ReleaseFailure, error.InnerException);
        Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
        Assert.Same(first.ReleaseFailure, Field("_guestWorkFailure").GetValue(null));
        var repeat = Assert.Throws<TargetInvocationException>(() => InvokeVolume("CompleteGuestWork", pending));
        Assert.Same(first.ReleaseFailure, repeat.InnerException); // cannot acknowledge after uncertain cleanup

        Assert.Equal(1, first.Releases); Assert.Equal(1, second.Releases);
        Assert.Same(first.ReleaseFailure, Field("_singletonVolumeOriginalCleanupFailure").GetValue(null));
        MetalVideoPresenter.ReleasePreparedTextureSources(snapshots);
        Assert.Equal(1, first.Releases); Assert.Equal(1, second.Releases);
    }

    [Fact]
    public void SingletonVolumeInventoryBoundRejects129thOriginalAndCloseReleasesUntakenPreparations()
    {
        ActualArchitecture(); using var state = new VolumeState(); var memory = new VolumeMemory(VolumePhysical());
        var snapshots = new List<GuestDrawTexture>();
        for (var index = 0; index < 128; index++) snapshots.Add(VolumeSnapshot(memory));
        var failure = Assert.Throws<InvalidOperationException>(() => VolumeSnapshot(memory));
        Assert.Contains("inventory", failure.Message);
        Assert.Equal(129, memory.Copies); Assert.Equal(1, memory.Releases); // exact failed129th watch, no positive origin
        InvokeVolume("ReleaseSingletonVolumeNativeOriginals"); // includes all private, never-queued originals
        Assert.Equal(129, memory.Releases);
        MetalVideoPresenter.ReleasePreparedTextureSources(snapshots);
        Assert.Equal(129, memory.Releases); // close/caller cleanup cannot replay an exact release
    }

    [Theory]
    [InlineData("legacy")] [InlineData("different-original")]
    public void SingletonVolumeLiveLookupCannotAdoptAnotherOwnersNewlyCapturedOrigin(string kind)
    {
        ActualArchitecture(); using var state = new VolumeState(); using var protection = new VolumeProtectedScalar();
        var memory = new VolumeMemory(VolumePhysical()); var incoming = VolumeSnapshot(memory);
        var existingSeed = VolumeSnapshot(memory); var image = VolumeImage(GetOrigin(existingSeed), memory);
        try
        {
            if (kind == "legacy") { Set(image, "StorageCpuOrigin", null!); Set(image, "StorageCpuLayout", null!); }
            else PublishVolume(existingSeed, image);
            var error = Assert.Throws<TargetInvocationException>(() => InvokeVolume("EnsureStorageImage", (nint)0, incoming));
            Assert.Contains("cannot adopt", Assert.IsType<InvalidOperationException>(error.InnerException).Message);
            Assert.Equal((nint)123, image.GetType().GetField("Texture")!.GetValue(image));
            Assert.Equal(2, memory.Copies); Assert.Equal(0, memory.Reads); // lookup performs no new mapping/token capture
            InvokeVolume("ReleaseSingletonVolumeNativeOriginals"); Assert.Equal(2, memory.Releases);
        }
        finally { Images.Remove(Address); MetalVideoPresenter.ReleasePreparedTextureSources([incoming, existingSeed]); }
    }

    private sealed class VolumeMemory(byte[] bytes) : Memory(bytes), ICpuMappedReadWatchSource
    {
        internal CpuMappedCopyResult Result = CpuMappedCopyResult.Copied;
        internal bool Current = true, WatchCurrent = true;
        internal int Copies, Releases;
        internal CpuMappedCopyObservation? LastMapping;
        internal Exception? ReleaseFailure; internal Action? AfterCopy;
        public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong footprintAddress, ulong footprintByteCount,
            ulong sourceAddress, Span<byte> destination, out CpuMappedCopyObservation? observation)
        {
            Copies++; observation = null; if (Result != CpuMappedCopyResult.Copied) return Result;
            Assert.Equal(Address, footprintAddress); Assert.Equal(65536UL, footprintByteCount);
            Assert.Equal(Address, sourceAddress); Assert.Equal(65536, destination.Length);
            Bytes.CopyTo(destination); AfterCopy?.Invoke();
            observation = LastMapping = new(this, Address, 65536, Address, 65536,
                [new(Address, 65536, Address, 65536, 1, 1, 65536, 0, 16384, GuestPageProtection.Read | GuestPageProtection.Write)]);
            return Result;
        }
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprintAddress, ulong footprintByteCount, ulong sourceAddress,
            Span<byte> destination, out CpuMappedCopyObservation? observation) => throw new InvalidOperationException("Volume original must use watched copy.");
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation) => Current;
        public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation observation) => WatchCurrent;
        public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation observation)
        { Releases++; if (ReleaseFailure is not null) throw ReleaseFailure; }
    }
    private sealed class VolumeState : IDisposable
    {
        private readonly State state = new();
        private readonly string[] names = ["_finiteStorageWritebackEnabled", "_singletonVolumeOriginalCleanupFailure", "_finiteStorageRetiredOrigins", "_finiteStorageRetiredOriginCount", "_finiteStorageRetiredUnknown"];
        private readonly object?[] values;
        private readonly object[] oldWatches;
        private readonly HashSet<object> pendingWorks = (HashSet<object>)Field("_pendingSingletonVolumeOriginalWorks").GetValue(null)!;
        private readonly object[] oldPendingWorks;
        internal VolumeState()
        {
            oldPendingWorks = pendingWorks.ToArray(); pendingWorks.Clear();
            var watches = (IEnumerable)Field("_singletonVolumeOriginalWatches").GetValue(null)!;
            oldWatches = watches.Cast<object>().ToArray();
            watches.GetType().GetMethod("Clear")!.Invoke(watches, []);
            values = names.Select(name => Field(name).GetValue(null)).ToArray();
            Field("_finiteStorageWritebackEnabled").SetValue(null, true);
            Field("_singletonVolumeOriginalCleanupFailure").SetValue(null, null);
            Field("_finiteStorageRetiredOrigins").SetValue(null, null);
            Field("_finiteStorageRetiredOriginCount").SetValue(null, 0);
            Field("_finiteStorageRetiredUnknown").SetValue(null, false);
        }
        public void Dispose()
        {
            Images.Remove(Address);
            pendingWorks.Clear(); pendingWorks.UnionWith(oldPendingWorks);
            var watches = Field("_singletonVolumeOriginalWatches").GetValue(null)!;
            watches.GetType().GetMethod("Clear")!.Invoke(watches, []);
            foreach (var watch in oldWatches) watches.GetType().GetMethod("Add")!.Invoke(watches, [watch]);
            for (var index = 0; index < names.Length; index++) Field(names[index]).SetValue(null, values[index]);
            state.Dispose();
        }
    }
    // Owner metadata only: no syscall, signal, native write completion or permission claim.
    private sealed class VolumeProtectedScalar : IDisposable
    {
        private readonly IDictionary ranges = (IDictionary)typeof(GuestImageWriteTracker).GetField("_rangesByAddress", Private)!.GetValue(null)!;
        private readonly object gate = typeof(GuestImageWriteTracker).GetField("_gate", Private)!.GetValue(null)!;
        private readonly object owner;
        private readonly object? previous;
        internal VolumeProtectedScalar()
        {
            owner = Activator.CreateInstance(typeof(GuestImageWriteTracker).GetNestedType("TrackedRange", BindingFlags.NonPublic)!, true)!;
            void SetRange(string name, object value) => owner.GetType().GetField(name)!.SetValue(owner, value);
            SetRange("Address", Address); SetRange("ByteCount", 65536UL); SetRange("Start", Address); SetRange("End", Address + 65536);
            SetRange("Protect", true); SetRange("Armed", 1); SetRange("WriteGeneration", 0L);
            lock (gate) { previous = ranges[Address]; ranges[Address] = owner; }
        }
        internal void Generation(long value) => owner.GetType().GetField("WriteGeneration")!.SetValue(owner, value);
        public void Dispose() { lock (gate) { if (previous is null) ranges.Remove(Address); else ranges[Address] = previous; } }
    }
}
