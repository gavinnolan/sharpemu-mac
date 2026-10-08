// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalComputeReadinessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedPrefixRejectsCachedNilPipelineBeforeNativeCallsOrCompletion(bool rendererFollowup)
    {
        using var state = new State();
        state.RejectPipeline();
        state.Set("_computeBeginCommandsOverride", (Func<nint, nint>)(_ => throw new Exception("must not create commands")));
        state.Set("_computeTextureCreationOverride", (Func<nint, nint, GuestDrawTexture, (nint, bool)>)((_, _, _) => throw new Exception("must not resolve textures")));
        var fatal = state.Fail([], capturePrefix: true, rendererFollowup: rendererFollowup);
        Assert.Contains("compute pipeline creation failed", Assert.IsType<InvalidOperationException>(fatal.InnerException).Message);
        Assert.Empty(state.Released);
    }

    [Fact]
    public void NilCommandBufferFailsBeforeAnyTextureOrEncoderAndNeverCompletes()
    {
        using var state = new State();
        state.Set("_computeBeginCommandsOverride", (Func<nint, nint>)(_ => 0));
        state.Set("_computeTextureCreationOverride", (Func<nint, nint, GuestDrawTexture, (nint, bool)>)((_, _, _) => throw new Exception("must not resolve")));
        state.Set("_computeEncoderCreationOverride", (Func<nint, nint>)(_ => throw new Exception("must not create encoder")));
        var fatal = state.Fail([Sample(1)]);
        Assert.Contains("command buffer", Assert.IsType<InvalidOperationException>(fatal.InnerException).Message);
        Assert.Empty(state.Released);
    }

    [Fact]
    public void NilEncoderReleasesTemporaryRetainsAndPreservesRegistryStorageVersion()
    {
        using var state = new State();
        var image = state.InstallStorageImage();
        state.Set("_computeTextureCreationOverride", (Func<nint, nint, GuestDrawTexture, (nint, bool)>)((_, _, texture) => ((nint)texture.Address, texture.Address != 20)));
        var fatal = state.Fail([Sample(11), Sample(20), Storage(), Sample(12)]);
        Assert.Contains("compute encoder", Assert.IsType<InvalidOperationException>(fatal.InnerException).Message);
        Assert.Equal(new nint[] { 11, 12 }, state.Released);
        Assert.Equal(7, image.GetType().GetField("ContentVersion")!.GetValue(image));
        Assert.DoesNotContain((nint)900, state.Released);
    }

    [Fact]
    public void LaterTextureFailureReleasesOnlyEarlierOwnedSlotsAndKeepsOriginalCause()
    {
        using var state = new State();
        var original = new NotSupportedException("second source cannot materialize");
        state.Set("_computeTextureCreationOverride", (Func<nint, nint, GuestDrawTexture, (nint, bool)>)((_, _, texture) => texture.Address == 12 ? throw original : (11, true)));
        var fatal = state.Fail([Sample(11), Sample(12), Sample(13)]);
        Assert.Same(original, fatal.InnerException);
        Assert.Equal(new nint[] { 11 }, state.Released);
    }

    [Fact]
    public void ThrowingEncoderCreationKeepsCauseAndReleasesEachTemporaryRetain()
    {
        using var state = new State();
        var original = new InvalidOperationException("encoder factory failed");
        state.Set("_computeEncoderCreationOverride", (Func<nint, nint>)(_ => throw original));
        var fatal = state.Fail([Sample(11), Sample(12)]);
        Assert.Same(original, fatal.InnerException);
        Assert.Equal(new nint[] { 11, 12 }, state.Released);
    }

    [Fact]
    public void CleanupFailureCannotReplaceNilEncoderOrStopRemainingReleases()
    {
        using var state = new State();
        var cleanup = new InvalidOperationException("release failed");
        state.Set("_computeTextureReleaseOverride", (Action<nint>)(handle => { state.Released.Add(handle); if (handle == 11) throw cleanup; }));
        var fatal = state.Fail([Sample(11), Sample(12)]);
        Assert.Contains("compute encoder", Assert.IsType<InvalidOperationException>(fatal.InnerException).Message);
        Assert.Same(cleanup, Field("_textureSourceCleanupFailure").GetValue(null));
        Assert.Equal(new nint[] { 11, 12 }, state.Released);
    }

    [Fact]
    public void EqualHandlesWithIndependentRetainsEachReceiveOneRelease()
    {
        using var state = new State();
        state.Set("_computeTextureCreationOverride", (Func<nint, nint, GuestDrawTexture, (nint, bool)>)((_, _, _) => (11, true)));
        state.Fail([Sample(1), Sample(2)]);
        Assert.Equal(new nint[] { 11, 11 }, state.Released);
    }

    private static GuestDrawTexture Sample(ulong address) => new(address, 1, 1, 10, 0, [1, 2, 3, 4], IsFallback: false, IsStorage: false);
    private static GuestDrawTexture Storage() => new(0xA000, 1, 1, 10, 0, [], IsFallback: false, IsStorage: true, Type: 9,
        Shape: new ShaderImageShape(false, false, false, true, false, TextureNumericClass.Float), Access: GuestImageAccess.Write);
    private static FieldInfo Field(string name) => typeof(MetalVideoPresenter).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!;

    // All native-producing calls are default-null private controls. These tests
    // execute actual queue take/dispatch/fatal paths, not a Metal device or ISA.
    private sealed class State : IDisposable
    {
        private readonly MetalGuestFailureTests.PresenterState presenter = new();
        private readonly Dictionary<FieldInfo, object?> saved = new();
        private readonly MetalCompiledGuestShader shader = new(new Gen5MslShader("", "managed readiness", Gen5MslStage.Compute, 0));
        private readonly byte[] payload = GuestDataPool.Shared.Rent(64);
        private readonly List<string> reports = [];
        private bool imageInstalled;
        internal readonly List<nint> Released = [];
        public State()
        {
            Set("_textureSourceGraphEnabled", false);
            Set("_guestPrefixCapture", null);
            Set("_guestPrefixExecution", false);
            Set("_takenGuestWorkSequence", 0L);
            Set("_takenGuestPrefixProducerSequence", 0L);
            Set("_guestPrefixProducerSequence", 0L);
            Set("_guestPrefixProducerQueue", null);
            Set("_textureSourceCleanupFailure", null);
            Set("_computeBeginCommandsOverride", (Func<nint, nint>)(_ => { Field("_batchOpen").SetValue(null, true); Field("_batchCommandBuffer").SetValue(null, (nint)101); return 101; }));
            Set("_computeEncoderCreationOverride", (Func<nint, nint>)(_ => 0));
            Set("_computeTextureCreationOverride", (Func<nint, nint, GuestDrawTexture, (nint, bool)>)((_, _, texture) => ((nint)texture.Address, true)));
            Set("_computeTextureReleaseOverride", (Action<nint>)Released.Add);
            ((IDictionary)Field("_computePipelineCache").GetValue(null)!).Add(shader, (nint)102);
            GpuFatal.OnFatal = reports.Add;
        }
        internal void Set(string name, object? value)
        {
            var field = Field(name);
            saved.TryAdd(field, field.GetValue(null)); field.SetValue(null, value);
        }
        internal object InstallStorageImage()
        {
            var type = typeof(MetalVideoPresenter).GetNestedType("GuestImage", BindingFlags.NonPublic)!;
            var image = Activator.CreateInstance(type, nonPublic: true)!;
            foreach (var pair in new Dictionary<string, object> { ["Texture"] = (nint)900, ["Width"] = 1u, ["Height"] = 1u,
                ["Depth"] = 1u, ["ArrayLength"] = 1u, ["TextureType"] = 2u, ["Format"] = Enum.ToObject(type.GetField("Format")!.FieldType, 70), ["Initialized"] = true, ["ContentVersion"] = 7 })
                type.GetField(pair.Key)!.SetValue(image, pair.Value);
            ((IDictionary)Field("_guestImages").GetValue(null)!).Add(0xA000UL, image);
            imageInstalled = true; return image;
        }
        internal void RejectPipeline() => ((IDictionary)Field("_computePipelineCache").GetValue(null)!)[shader] = (nint)0;
        internal GpuFatalException Fail(GuestDrawTexture[] textures, bool capturePrefix = false, bool rendererFollowup = false)
        {
            var leases = GuestDataPool.DiagnosticStats().LeaseCount;
            using var queue = MetalVideoPresenter.EnterGuestQueue(capturePrefix ? "dcb.graphics" : "compute.readiness", 91);
            if (capturePrefix) MetalVideoPresenter.BeginGuestSubmissionPrefixCapture(0, 91);
            long SubmitDispatch() => MetalVideoPresenter.SubmitComputeDispatch(0, shader, textures,
                [new GuestMemoryBuffer(0, payload, 64, 64, true)], 1, 1, 1, 0, 0, 0, true, null);
            long failed = 0;
            if (rendererFollowup)
                Assert.True(MetalVideoPresenter.SubmitOrderedGuestAction(() => failed = SubmitDispatch(), "captured renderer followup") > 0);
            else
            { failed = SubmitDispatch(); Assert.True(failed > 0); }
            var followed = false;
            var next = MetalVideoPresenter.SubmitOrderedGuestAction(() => followed = true, "must not follow failed encoder");
            var completion = capturePrefix ? MetalVideoPresenter.EndGuestSubmissionPrefixCapture(true) : null;
            var fatal = Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
            Assert.True(failed > 0);
            if (capturePrefix)
            {
                Assert.NotNull(completion);
                Assert.Same(fatal.InnerException, Assert.Throws<InvalidOperationException>(completion.WaitUntilCompleted));
            }
            Assert.Contains("kind=ComputeGuestDispatch", fatal.Message);
            Assert.False(followed);
            Assert.False(MetalVideoPresenter.WaitForGuestWork(failed, 0));
            Assert.False(MetalVideoPresenter.WaitForGuestWork(next, 0));
            Assert.False((bool)Field("_batchOpen").GetValue(null)!);
            Assert.Equal((nint)0, Field("_batchCommandBuffer").GetValue(null));
            Assert.Equal(leases, GuestDataPool.DiagnosticStats().LeaseCount); // no new early pooled return
            Assert.Single(reports);
            Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
            Assert.Single(reports);
            return fatal;
        }
        public void Dispose()
        {
            if (imageInstalled) ((IDictionary)Field("_guestImages").GetValue(null)!).Remove(0xA000UL);
            ((IDictionary)Field("_computePipelineCache").GetValue(null)!).Remove(shader);
            foreach (var pair in saved) pair.Key.SetValue(null, pair.Value);
            GuestDataPool.Shared.Return(payload); // fixture-owned failure payload, not a completion receipt
            presenter.Dispose();
        }
    }
}
