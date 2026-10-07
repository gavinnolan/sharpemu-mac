// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalTextureSourceRuntimeDrainTests
{
    private const ulong Address = 0x71F0_0000_00;
    private static FieldInfo Field(string name) => typeof(MetalVideoPresenter).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static object? Call(string name, params object?[] args) => typeof(MetalVideoPresenter).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
    private static object Owner(object work)
    {
        var table = Field("_textureSourceWorkOwners").GetValue(null)!;
        object?[] args = [work, null]; Assert.True((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!); return args[1]!;
    }
    private static object? Get(object value, string name) => value.GetType().GetField(name)!.GetValue(value);

    [Fact]
    public void ActualCloseDropsAdmittedPendingSourceOwnersWithoutNativeWork()
    {
        using var state = new State();
        var read = state.AdmitPendingSource();
        state.AdmitConsumer(read);
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        Assert.Equal(1, MetalVideoPresenter.TextureSourceState(read).References);
        Call("CompletePresenterShutdown");
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(read).References);
        Assert.Equal(0UL, MetalVideoPresenter.TextureSourceState(read).ReservedBytes);
        Assert.Equal(0UL, Field("_immutableTextureSourceBytes").GetValue(null));
        Assert.True((bool)Field("_closed").GetValue(null)!);
        Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null));
        Assert.Equal(0UL, Field("_pendingGuestWorkBytes").GetValue(null));
        Assert.Null(Field("_thread").GetValue(null));
        Assert.Equal(0, MetalVideoPresenter.SubmitOrderedGuestAction(static () => { }, "closed"));
    }

    [Fact]
    public void ActualFatalOrderedActionCancelsFollowingPendingProducerAndConsumer()
    {
        using var state = new State();
        var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
        var cause = new NotSupportedException("controlled ordered fatal before native work");
        var failed = MetalVideoPresenter.SubmitOrderedGuestAction(() => throw cause, "fatal source cancellation");
        var read = state.AdmitPendingSource();
        var consumer = state.AdmitConsumer(read);
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        var fatal = Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
        Assert.Same(cause, fatal.InnerException);
        Assert.Single(reports);
        Assert.False(MetalVideoPresenter.WaitForGuestWork(failed, 0));
        var sequence = state.SequenceOf(consumer);
        Assert.False(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(read).References);
        Assert.Equal(0UL, Field("_immutableTextureSourceBytes").GetValue(null));
        Assert.True(((HashSet<ulong>)Field("_gpuTextureSourceAddresses").GetValue(null)!).Contains(Address));
        Assert.False((bool)Field("_batchOpen").GetValue(null)!);
        Assert.Equal(0, Field("_pendingGuestWorkCount").GetValue(null));
        Assert.Equal(0UL, Field("_pendingGuestWorkBytes").GetValue(null));
    }

    [Fact]
    public void NativeShutdownRejectsCallerHoldingPresenterGateBeforeAnyMutation()
    {
        using var state = new State();
        var read = state.AdmitPendingSource();
        var before = MetalVideoPresenter.TextureSourceState(read);
        lock (Field("_gate").GetValue(null)!)
        {
            var failure = Assert.Throws<TargetInvocationException>(() => Call("ShutdownTextureSourceNativeOwners"));
            Assert.Contains("holding the presenter gate", Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
        }
        Assert.Equal(before, MetalVideoPresenter.TextureSourceState(read));
    }

    [Fact]
    public void ActualDrainRejectsUnwiredProducerBeforeNativeMaterialization()
    {
        using var state = new State();
        var reports = new List<string>(); GpuFatal.OnFatal = reports.Add;
        var read = state.AdmitPendingSource();
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        var error = Assert.Throws<GpuFatalException>(() => MetalVideoPresenter.DrainGuestWork(0, 0));
        Assert.Contains("producer encoding", Assert.IsType<NotSupportedException>(error.InnerException).Message);
        Assert.Single(reports);
        Assert.Equal(0UL, Field("_immutableTextureSourceBytes").GetValue(null));
        Assert.False((bool)Field("_batchOpen").GetValue(null)!);
    }

    [Fact]
    public void ReceiptCannotAttributePeersUncommittedNativeUseToUnencodedWork()
    {
        using var state = new State();
        var read = state.AdmitPendingSource();
        var first = state.AdmitConsumer(read);
        var peer = state.AdmitConsumer(read);
        var batchType = typeof(MetalVideoPresenter).GetNestedType("TextureSourceNativeBatch", BindingFlags.NonPublic)!;
        // Metadata-only negative qualification. No fake native completion or
        // valid hardware ownership is claimed by this isolated batch record.
        var batch = RuntimeHelpers.GetUninitializedObject(batchType);
        batchType.GetField("Identity")!.SetValue(batch, 7001L);
        var batches = (IDictionary)Field("_textureSourceNativeBatches").GetValue(null)!;
        var source = ((IDictionary)Field("_latestTextureSources").GetValue(null)!)[Address]!;
        try
        {
            source.GetType().GetField("NativePins")!.SetValue(source, 1);
            batches.Add((nint)7001, batch);
            Field("_executingTextureSourceWork").SetValue(null, first);
            Call("RecordTextureSourceNativeWorkUseLocked", batch);
            Call("ReleaseTextureSourceWorkOwnerLocked", peer);
            Assert.Equal(0L, Get(Owner(peer), "NativeUseBatchIdentity"));
            Assert.False((bool)Get(Owner(peer), "ReleasedWithUncommittedNativeUse")!);
            Call("ReleaseTextureSourceWorkOwnerLocked", first);
            Assert.Equal(7001L, Get(Owner(first), "NativeUseBatchIdentity"));
            Assert.True((bool)Get(Owner(first), "ReleasedWithUncommittedNativeUse")!);
        }
        finally
        {
            Field("_executingTextureSourceWork").SetValue(null, null);
            batches.Remove((nint)7001);
            source.GetType().GetField("NativePins")!.SetValue(source, 0);
        }
    }

    private sealed class State : IDisposable
    {
        private readonly MetalTextureSourceLeaseTests.State sourceState = new();
        private readonly bool oldGraph = (bool)Field("_textureSourceGraphEnabled").GetValue(null)!;
        private readonly List<GuestDrawTexture> captures = [];
        private readonly Dictionary<object, long> sequences = new(ReferenceEqualityComparer.Instance);
        public long SequenceOf(object work) => sequences[work];
        public State() => Field("_textureSourceGraphEnabled").SetValue(null, true);
        public GuestDrawTexture AdmitPendingSource()
        {
            var target = new GuestRenderTarget(Address, 4, 4, 10, 0,
                Registers: new ColorTargetWords(Address, 0, 10u << 2, 0, 3u | 3u << 14, 1u << 24, 0, 0, 0, 0, 0));
            MetalVideoPresenter.SubmitOffscreenTranslatedDraw(new(new Gen5MslShader("", "pending never encoded", Gen5MslStage.Pixel, 0)), [], [], 0, [target], null, 3, 1, 4, null, null, null, null, 0);
            var read = new GuestDrawTexture(Address, 4, 4, 10, 0, [], false, false, Pitch: 4,
                Descriptor: MetalTextureSourceLeaseTests.ResourceWords(), Shape: new ShaderImageShape(false, false, false, false, TextureNumericClass.Float), Access: GuestImageAccess.Read);
            Assert.True(MetalVideoPresenter.TryCaptureTextureSource(read, 64, false)); captures.Add(read); return read;
        }
        public object AdmitConsumer(GuestDrawTexture read)
        {
            var shader = new MetalCompiledGuestShader(new Gen5MslShader("", "pending consumer never encoded", Gen5MslStage.Compute, 0));
            var sequence = MetalVideoPresenter.SubmitComputeDispatch(0, shader, [read], [], 1, 1, 1, 0, 0, 0, true, null);
            var pending = ((IDictionary)Field("_pendingGuestWorkByQueue").GetValue(null)!).Values.Cast<IEnumerable>()
                .SelectMany(static queue => queue.Cast<object>()).Single(item => (long)item.GetType().GetProperty("Sequence")!.GetValue(item)! == sequence);
            var work = pending.GetType().GetProperty("Work")!.GetValue(pending)!;
            sequences.Add(work, sequence);
            return work;
        }
        public void Dispose()
        {
            MetalVideoPresenter.ReleasePreparedTextureSources(captures);
            Call("CompletePresenterShutdown");
            Field("_textureSourceGraphEnabled").SetValue(null, oldGraph);
            sourceState.Dispose();
        }
    }
}
