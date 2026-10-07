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
        var copy = read with { Descriptor = [0, 1, 2, 3, 4, 5, 6, 7] };
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
        Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.CopyCapturedTextureSource(read, read with { Width = 2 }));
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
        var seed = Texture(true) with { RgbaPixels = new byte[64], Access = GuestImageAccess.Write };
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

    private static GuestDrawTexture Texture(bool storage) => new(Address, 4, 4, 10, 0, [], false, storage,
        Pitch: 4, Shape: new ShaderImageShape(false, false, storage, false, TextureNumericClass.Float),
        Access: GuestImageAccess.Read);
    private static FieldInfo Field(string name) => typeof(MetalVideoPresenter).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static void Call(string name, params object[] arguments) => typeof(MetalVideoPresenter)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments);

    internal sealed class State : IDisposable
    {
        private readonly MetalGuestFailureTests.PresenterState presenter = new();
        public void Dispose()
        {
            ((IDictionary)Field("_latestTextureSources").GetValue(null)!).Remove(Address);
            var versions = (IList)Field("_textureSourceVersions").GetValue(null)!;
            for (var index = versions.Count - 1; index >= 0; index--)
            {
                var view = versions[index]!.GetType().GetField("View")!.GetValue(versions[index])!;
                if ((ulong)view.GetType().GetProperty("Address")!.GetValue(view)! == Address) versions.RemoveAt(index);
            }
            GuestImageWriteTracker.Untrack(Address);
            presenter.Dispose();
        }
    }
}
