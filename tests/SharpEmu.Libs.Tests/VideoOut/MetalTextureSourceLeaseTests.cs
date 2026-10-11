// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalTextureSourceLeaseTests
{
    private const ulong Address = 0x71F0_0000_00;

    [Fact]
    public void AdvertisementWithoutAdmittedProducerCannotReserveAnAlias()
    {
        using var state = new State();
        MetalVideoPresenter.RegisterKnownDisplayBuffer(Address, 10);
        var read = Texture(false);
        Assert.False(MetalVideoPresenter.TryCaptureTextureSource(read, 64, false));
        Assert.Equal(default, MetalVideoPresenter.TextureSourceState(read));
    }

    [Fact]
    public void QueuedOwnerOutlivesPreparationButCannotAuthorizeAnotherAdmission()
    {
        using var state = new State();
        var read = PublishAndCapture();
        var shader = new MetalCompiledGuestShader(new Gen5MslShader("", "lease_test", Gen5MslStage.Compute, 0));
        Assert.True(MetalVideoPresenter.SubmitComputeDispatch(0, shader, [read, read], [], 1, 1, 1, 0, 0, 0, true, null) > 0);
        var queue = ((IDictionary)Field("_pendingGuestWorkByQueue").GetValue(null)!).Values.Cast<object>().Single();
        var last = queue.GetType().GetProperty("Last")!.GetValue(queue)!;
        var pending = last.GetType().GetProperty("Value")!.GetValue(last)!;
        var work = pending.GetType().GetProperty("Work")!.GetValue(pending)!;
        Call("RetainQueuedTextureSourcesLocked", work);
        Assert.Equal(2, MetalVideoPresenter.TextureSourceState(read).References);
        MetalVideoPresenter.ReleasePreparedTextureSources([read, read]);
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        Assert.Equal(1, MetalVideoPresenter.TextureSourceState(read).References);
        var failure = Assert.Throws<TargetInvocationException>(() => Call("RetainQueuedTextureSourcesLocked", work));
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Call("ReleaseQueuedTextureSourcesLocked", work);
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(read).References);
        Assert.Equal(0UL, MetalVideoPresenter.TextureSourceState(read).ReservedBytes);
    }

    [Fact]
    public void RecordCopyKeepsOnePreparationOwnerAndCanceledPreparationReleasesIt()
    {
        using var state = new State();
        var read = PublishAndCapture();
        var copy = read with { Sampler = new GuestSampler(1, 2, 3, 4) };
        MetalVideoPresenter.CopyCapturedTextureSource(read, copy);
        Assert.Equal(MetalVideoPresenter.TextureSourceState(read), MetalVideoPresenter.TextureSourceState(copy));
        MetalVideoPresenter.ReleasePreparedTextureSources([copy]);
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(read).References);
        Assert.Equal(0UL, MetalVideoPresenter.TextureSourceState(read).ReservedBytes);
    }

    [Fact]
    public void DuplicateCaptureCannotLeakAReservationOrAnotherReference()
    {
        using var state = new State();
        var read = PublishAndCapture();
        var before = MetalVideoPresenter.TextureSourceState(read);
        Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.TryCaptureTextureSource(read, 64, false));
        Assert.Equal(before, MetalVideoPresenter.TextureSourceState(read));
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        Assert.Equal(0UL, MetalVideoPresenter.TextureSourceState(read).ReservedBytes);
    }

    [Fact]
    public void FailedMultiLeaseRetainDoesNotAcquireEarlierOwners()
    {
        using var state = new State();
        var read = PublishAndCapture();
        var second = Texture(false) with { Sampler = new GuestSampler(1, 2, 3, 4) };
        Assert.True(MetalVideoPresenter.TryCaptureTextureSource(second, 64, false));
        MetalVideoPresenter.ReleasePreparedTextureSources([second]);
        var work = QueuedWork(read, second);
        var failure = Assert.Throws<TargetInvocationException>(() => Call("RetainQueuedTextureSourcesLocked", work));
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Equal(1, MetalVideoPresenter.TextureSourceState(read).References);
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(second).References);
        // No work owner was acquired, so cancellation cannot steal prep ownership.
        Call("ReleaseQueuedTextureSourcesLocked", work);
        Assert.Equal(1, MetalVideoPresenter.TextureSourceState(read).References);
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        Assert.Equal(0UL, MetalVideoPresenter.TextureSourceState(read).ReservedBytes);
    }

    [Fact]
    public void ReleasingOneWorkTwiceCannotStealAnotherAdmittedWorkReference()
    {
        using var state = new State();
        var read = PublishAndCapture();
        var first = QueuedWork(read); var second = QueuedWork(read);
        Call("RetainQueuedTextureSourcesLocked", first); Call("RetainQueuedTextureSourcesLocked", second);
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        Assert.Equal(2, MetalVideoPresenter.TextureSourceState(read).References);
        Call("ReleaseQueuedTextureSourcesLocked", first); Call("ReleaseQueuedTextureSourcesLocked", first);
        Assert.Equal(1, MetalVideoPresenter.TextureSourceState(read).References);
        Call("ReleaseQueuedTextureSourcesLocked", second);
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(read).References);
        Assert.Equal(0UL, MetalVideoPresenter.TextureSourceState(read).ReservedBytes);
    }

    [Fact]
    public void CapturedAssociationCannotBeCopiedToAForgedViewOrReleasedPreparation()
    {
        using var state = new State();
        var read = PublishAndCapture();
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.CopyCapturedTextureSource(read, read with { Width = 2 }));
        Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.CopyCapturedTextureSource(read, read with { Access = GuestImageAccess.Write }));
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.CopyCapturedTextureSource(read, read with { Sampler = new GuestSampler(1, 2, 3, 4) }));
    }

    [Theory]
    [InlineData((GuestImageAccess)8, 10u, 0u)]
    [InlineData(GuestImageAccess.Read, 10u, 4u)]
    public void UndefinedIntentOrIntegerNativeFormatCannotReserveAFloatCompiledSource(GuestImageAccess access, uint format, uint number)
    {
        using var state = new State();
        var read = PublishAndCapture();
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.TryCaptureTextureSource(Texture(false) with
            { Access = access, Format = format, NumberType = number }, 64, false));
        Assert.Equal(1, MetalVideoPresenter.TextureSourceState(read).References);
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
    }

    [Fact]
    public void SelectionFailureInvalidatesEveryCapturedVersionWithoutClaimingNativeCleanup()
    {
        using var state = new State();
        var read = PublishAndCapture();
        Call("InvalidateTextureSourceVersionsLocked");
        Assert.True(MetalVideoPresenter.TextureSourceState(read).Failed);
        Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.TryCaptureTextureSource(Texture(false), 64, false));
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        Assert.Equal(0UL, MetalVideoPresenter.TextureSourceState(read).ReservedBytes);
    }

    [Fact]
    public void CopiedGenerationZeroSeedCannotBeRetimestampedAfterCpuRewrite()
    {
        using var state = new State();
        var wasEnabled = GuestImageWriteTracker.Enabled;
        GuestImageWriteTracker.Configure(true);
        try
        {
            // A managed-only watch makes this a deterministic no-device test:
            // there are no mappings or page-protection calls at this address.
            GuestImageWriteTracker.TrackManagedWriter(Address, 64);
            var seed = Texture(true) with { RgbaPixels = new byte[64], WriteGeneration = 0, Access = GuestImageAccess.Write };
            MetalVideoPresenter.RegisterCpuTextureSnapshotProvenance(seed, MetalVideoPresenter.ObserveCpuTextureSnapshot(Address, 64, HostMemory.MappingGeneration));
            var copy = seed with { Sampler = new GuestSampler(1, 2, 3, 4) };
            MetalVideoPresenter.CopyCapturedTextureSource(seed, copy);
            GuestImageWriteTracker.NotifyManagedWrite(Address, 4);
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(Address, out var generation));
            Assert.Equal(1, generation);
            var failure = Assert.Throws<TargetInvocationException>(() => Publish(copy));
            Assert.IsType<InvalidOperationException>(failure.InnerException);
            Assert.Contains("seed provenance", failure.InnerException!.Message);
            Assert.False(((IDictionary)Field("_latestTextureSources").GetValue(null)!).Contains(Address));
            Assert.Equal(0, copy.WriteGeneration);
        }
        finally { GuestImageWriteTracker.Configure(wasEnabled); }
    }

    [Fact]
    public void SupersededCpuGenerationCannotAuthorizePreviousProducerInitialization()
    {
        using var state = new State();
        var wasEnabled = GuestImageWriteTracker.Enabled;
        GuestImageWriteTracker.Configure(true);
        try
        {
            GuestImageWriteTracker.TrackManagedWriter(Address, 64);
            var seed = Texture(true) with { RgbaPixels = new byte[64], WriteGeneration = 0, Access = GuestImageAccess.Write };
            MetalVideoPresenter.RegisterCpuTextureSnapshotProvenance(seed, MetalVideoPresenter.ObserveCpuTextureSnapshot(Address, 64, HostMemory.MappingGeneration));
            Publish(seed);
            var previous = ((IDictionary)Field("_latestTextureSources").GetValue(null)!)[Address];
            GuestImageWriteTracker.NotifyManagedWrite(Address, 4);
            var failure = Assert.Throws<TargetInvocationException>(() => Publish(Texture(true) with { Access = GuestImageAccess.Write }));
            Assert.Contains("no faithful initialization", failure.InnerException!.Message);
            Assert.Same(previous, ((IDictionary)Field("_latestTextureSources").GetValue(null)!)[Address]);
        }
        finally { GuestImageWriteTracker.Configure(wasEnabled); }
    }

    [Fact]
    public void SeedObservationRejectsTrackerRetirementEvenWhenGenerationRestartsAtZero()
    {
        using var state = new State();
        var observation = MetalVideoPresenter.ObserveCpuTextureSnapshot(Address, 64, HostMemory.MappingGeneration);
        var seed = Texture(true) with { RgbaPixels = new byte[64], WriteGeneration = 0, Access = GuestImageAccess.Write };
        MetalVideoPresenter.RegisterCpuTextureSnapshotProvenance(seed, observation);
        GuestImageWriteTracker.NotifyManagedWrite(Address, 4);
        GuestImageWriteTracker.Untrack(Address);
        GuestImageWriteTracker.TrackManagedWriter(Address, 64);
        Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(Address, out var generation));
        Assert.Equal(0, generation);
        Assert.False(MetalVideoPresenter.IsCpuTextureSnapshotObservationStable(Address, observation));
        var failure = Assert.Throws<TargetInvocationException>(() => Publish(seed));
        Assert.Contains("seed provenance", failure.InnerException!.Message);
        Assert.False(((IDictionary)Field("_latestTextureSources").GetValue(null)!).Contains(Address));
    }

    [Fact]
    public void UnobservedOwnedSeedCannotAuthorizeNativeProducerFreshness()
    {
        using var state = new State();
        GuestImageWriteTracker.Configure(false);
        var seed = Texture(true) with { RgbaPixels = new byte[64], WriteGeneration = 0, Access = GuestImageAccess.Write };
        MetalVideoPresenter.RegisterCpuTextureSnapshotProvenance(seed,
            MetalVideoPresenter.ObserveCpuTextureSnapshot(Address, 64, HostMemory.MappingGeneration));
        var failure = Assert.Throws<TargetInvocationException>(() => Publish(seed));
        Assert.Contains("seed provenance", failure.InnerException!.Message);
    }

    [Fact]
    public void NewCaptureRejectsRetiredTrackerOwnerEvenWhenCounterMatches()
    {
        using var state = new State();
        var existing = PublishAndCapture();
        GuestImageWriteTracker.Untrack(Address);
        GuestImageWriteTracker.TrackManagedWriter(Address, 64);
        Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.TryCaptureTextureSource(Texture(false), 64, false));
        Assert.Equal(1, MetalVideoPresenter.TextureSourceState(existing).References);
        MetalVideoPresenter.ReleasePreparedTextureSources([existing]);
    }

    private static void Publish(GuestDrawTexture seed)
    {
        var sequence = MetalVideoPresenter.SubmitOrderedGuestAction(static () => { }, "managed provenance fixture");
        var queueType = typeof(MetalVideoPresenter).GetNestedType("GuestQueueIdentity", BindingFlags.NonPublic)!;
        var queue = Activator.CreateInstance(queueType, ["host.default", 0UL])!;
        Call("PublishAdmittedTextureSourceLocked", seed, sequence, queue, false);
    }

    private static object QueuedWork(params GuestDrawTexture[] textures)
    {
        var shader = new MetalCompiledGuestShader(new Gen5MslShader("", "owner_test", Gen5MslStage.Compute, 0));
        Assert.True(MetalVideoPresenter.SubmitComputeDispatch(0, shader, textures, [], 1, 1, 1, 0, 0, 0, true, null) > 0);
        var queue = ((IDictionary)Field("_pendingGuestWorkByQueue").GetValue(null)!).Values.Cast<object>().Single();
        var last = queue.GetType().GetProperty("Last")!.GetValue(queue)!;
        var pending = last.GetType().GetProperty("Value")!.GetValue(last)!;
        return pending.GetType().GetProperty("Work")!.GetValue(pending)!;
    }

    internal static GuestDrawTexture PublishAndCapture()
    {
        var seed = Texture(true) with { RgbaPixels = new byte[64], WriteGeneration = 0, Access = GuestImageAccess.Write };
        MetalVideoPresenter.RegisterCpuTextureSnapshotProvenance(seed, MetalVideoPresenter.ObserveCpuTextureSnapshot(Address, 64, HostMemory.MappingGeneration));
        var sequence = MetalVideoPresenter.SubmitOrderedGuestAction(static () => { }, "managed source owner fixture");
        var queueType = typeof(MetalVideoPresenter).GetNestedType("GuestQueueIdentity", BindingFlags.NonPublic)!;
        var queue = Activator.CreateInstance(queueType, ["host.default", 0UL])!;
        Call("PublishAdmittedTextureSourceLocked", seed, sequence, queue, false);
        var read = Texture(false);
        Assert.True(MetalVideoPresenter.TryCaptureTextureSource(read, 64, false));
        Assert.Equal(sequence, MetalVideoPresenter.TextureSourceState(read).Producer);
        Assert.Equal(64UL, MetalVideoPresenter.TextureSourceState(read).ReservedBytes);
        return read;
    }

    [Fact]
    public void OrdinaryWatchIdentityCannotCertifyOwnedCpuSeed()
    {
        using var state = new State();
        GuestImageWriteTracker.Untrack(Address);
        GuestImageWriteTracker.Track(Address, 64, protect: false);
        Assert.True(GuestImageWriteTracker.TryGetContentToken(Address, out var identity));
        Assert.NotEqual(0, identity.WatchIdentity);
        var observation = MetalVideoPresenter.ObserveCpuTextureSnapshot(Address, 64, HostMemory.MappingGeneration);
        Assert.False(observation.Observed);
        var seed = Texture(true) with { RgbaPixels = new byte[64], WriteGeneration = 0, Access = GuestImageAccess.Write };
        MetalVideoPresenter.RegisterCpuTextureSnapshotProvenance(seed, observation);
        var queueType = typeof(MetalVideoPresenter).GetNestedType("GuestQueueIdentity", BindingFlags.NonPublic)!;
        var queue = Activator.CreateInstance(queueType, ["host.default", 0UL])!;
        var exception = Assert.Throws<TargetInvocationException>(() => Call("PublishAdmittedTextureSourceLocked", seed, 1L, queue, false));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("seed provenance", exception.InnerException!.Message);
    }

    [Fact]
    public void FullRawZeroComponentSelectionCannotBeCertifiedAsIdentitySwizzle()
    {
        using var state = new State();
        var seed = Texture(true) with { RgbaPixels = new byte[64], WriteGeneration = 0, Access = GuestImageAccess.Write, DstSelect = 0 };
        var words = seed.Descriptor!.ToArray(); words[3] &= ~0xFFFu;
        seed = seed with { Descriptor = words };
        var queueType = typeof(MetalVideoPresenter).GetNestedType("GuestQueueIdentity", BindingFlags.NonPublic)!;
        var queue = Activator.CreateInstance(queueType, ["host.default", 0UL])!;
        var exception = Assert.Throws<TargetInvocationException>(() => Call("PublishAdmittedTextureSourceLocked", seed, 1L, queue, false));
        Assert.IsType<NotSupportedException>(exception.InnerException);
    }

    private static GuestDrawTexture Texture(bool storage) => new(Address, 4, 4, 10, 0, [], false, storage,
        Pitch: 4, Descriptor: ResourceWords(), Shape: new ShaderImageShape(false, false, storage, false, TextureNumericClass.Float),
        Access: GuestImageAccess.Read);
    // A 4x4 RGBA8 linear color target resolves to a 256-byte-aligned row
    // pitch (64 texels) and four padded rows, the same as a linear texture of
    // that memory. A texture that reads such a target names that pitch.
    internal const uint LinearColorPitch = 64;
    internal const ulong LinearColorFootprint = 1024;
    internal static uint[] ResourceWords(ulong address = Address, uint pitch = 4) =>
        [(uint)(address >> 8), (uint)((address >> 40) & 0xFF) | 56u << 20 | 3u << 30,
         3u << 14, 0xFACu | 9u << 28, pitch - 1, 0, 0, 0];
    private static FieldInfo Field(string name) => typeof(MetalVideoPresenter).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static void Call(string name, params object[] arguments) => typeof(MetalVideoPresenter)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments);

    internal sealed class State : IDisposable
    {
        private readonly MetalGuestFailureTests.PresenterState presenter = new();
        private readonly bool wasEnabled = GuestImageWriteTracker.Enabled;
        public State()
        {
            GuestImageWriteTracker.Configure(true);
            GuestImageWriteTracker.TrackManagedWriter(Address, 64);
        }
        public void Dispose()
        {
            ((IDictionary)Field("_latestTextureSources").GetValue(null)!).Remove(Address);
            ((HashSet<ulong>)Field("_gpuTextureSourceAddresses").GetValue(null)!).Remove(Address);
            var versions = (IList)Field("_textureSourceVersions").GetValue(null)!;
            for (var index = versions.Count - 1; index >= 0; index--)
            {
                var view = versions[index]!.GetType().GetField("View")!.GetValue(versions[index])!;
                if ((ulong)view.GetType().GetProperty("Address")!.GetValue(view)! == Address) versions.RemoveAt(index);
            }
            GuestImageWriteTracker.Untrack(Address);
            GuestImageWriteTracker.Configure(wasEnabled);
            presenter.Dispose();
        }
    }
}
