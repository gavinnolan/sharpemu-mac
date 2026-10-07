// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// Native execution remains disabled. These tests temporarily enable only the
// real admission seam and inspect actual queue/owner publication, never inject
// successful native encoding or assert device qualification.
[Collection(SchedulingStateCollection.Name)]
public sealed class MetalTextureSourceAdmissionTests
{
    private const ulong Address = 0x71F0_0000_00;
    private static readonly MetalCompiledGuestShader Shader = new(new Gen5MslShader("", "admission", Gen5MslStage.Compute, 0));

    [Fact]
    public void ActualAdmissionPublishesOwnedProducerAndRetainsQueuedInputsOnce()
    {
        using var state = new State();
        var seed = Seed();
        var producer = Submit(seed);
        var read = seed with { IsStorage = false, RgbaPixels = [], Shape = seed.Shape with { Storage = false }, Access = GuestImageAccess.Read };
        Assert.True(MetalVideoPresenter.TryCaptureTextureSource(read, 64, false));
        state.Captures.Add(read);
        var consumer = Submit(read);
        Assert.True(consumer > producer);
        Assert.Equal(2, MetalVideoPresenter.TextureSourceState(read).References);
        Assert.Equal(128UL, state.NativeBytes);
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        Assert.Equal(1, MetalVideoPresenter.TextureSourceState(read).References);
        state.ReleaseWork(consumer);
        state.ReleaseWork(consumer);
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(read).References);
        Assert.Equal(0UL, MetalVideoPresenter.TextureSourceState(read).ReservedBytes);
    }

    [Fact]
    public void RejectedSecondOutputCannotPublishFirstOutputOrAdvanceQueueState()
    {
        using var state = new State();
        var seed = Seed();
        var invalid = seed with { Address = Address + 0x1000, Shape = seed.Shape with { NumericClass = TextureNumericClass.Uint }, NumberType = 4 };
        var before = state.Summary;
        Assert.Throws<NotSupportedException>(() => Submit(seed, invalid));
        Assert.Equal(before, state.Summary);
        Assert.Equal(0UL, state.NativeBytes);
        Assert.False(state.HasLatest(Address));
        Assert.False(state.HasLatest(Address + 0x1000));
    }

    [Fact]
    public void ReleasedInputRollsBackRegisteredWorkOwnerWithoutOrphanOrAdvertisement()
    {
        using var state = new State();
        var seed = Seed();
        Submit(seed);
        var read = seed with { IsStorage = false, RgbaPixels = [], Shape = seed.Shape with { Storage = false }, Access = GuestImageAccess.Read };
        Assert.True(MetalVideoPresenter.TryCaptureTextureSource(read, 64, false));
        MetalVideoPresenter.ReleasePreparedTextureSources([read]);
        var before = state.Summary;
        var ownerCount = state.OwnerCount;
        var failure = Assert.Throws<InvalidOperationException>(() => Submit(read));
        Assert.Contains("preparation has already been released", failure.Message);
        Assert.Equal(before, state.Summary);
        Assert.Equal(ownerCount, state.OwnerCount);
        Assert.Equal(0, MetalVideoPresenter.TextureSourceState(read).References);
    }

    [Fact]
    public void OwnedCpuSeedPreparedBeforeInterveningGpuProducerCannotOverwriteIt()
    {
        using var state = new State();
        var first = Seed(); var preparedEarlier = Seed();
        var producer = Submit(first);
        var latest = state.LatestSource(Address);
        var before = state.Summary;
        var failure = Assert.Throws<InvalidOperationException>(() => Submit(preparedEarlier));
        Assert.Contains("cannot overwrite a current GPU producer", failure.Message);
        Assert.Equal(before, state.Summary);
        Assert.Same(latest, state.LatestSource(Address));
        Assert.Equal(producer, state.Summary.Sequence);
    }

    [Fact]
    public void ConflictingSeedsForSameWritableViewAreRejectedBeforeAdmission()
    {
        using var state = new State();
        var first = Seed(); var second = Seed();
        var before = state.Summary;
        var failure = Assert.Throws<NotSupportedException>(() => Submit(first, second));
        Assert.Contains("initialization source", failure.Message);
        Assert.Equal(before, state.Summary);
        Assert.False(state.HasLatest(Address));
    }

    [Fact]
    public void NativeBudgetIncludesLatestProducerWithNoReadLease()
    {
        using var state = new State();
        var seed = Seed();
        state.NativeBytes = 256UL * 1024 * 1024 - 127;
        var before = state.Summary;
        var failure = Assert.Throws<InvalidOperationException>(() => Submit(seed));
        Assert.Contains("native texture version budget", failure.Message);
        Assert.Equal(before, state.Summary);
        Assert.False(state.HasLatest(Address));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealColorRegistersAreRequiredAndMismatchedExtentsCannotBeFabricated(bool mismatch)
    {
        using var state = new State();
        var target = ColorTarget();
        target = mismatch ? target with { Width = 3 } : target with { Registers = null };
        var before = state.Summary;
        Assert.Throws<NotSupportedException>(() => SubmitColor(target));
        Assert.Equal(before, state.Summary);
        Assert.False(state.HasLatest(Address));
    }

    [Fact]
    public void RealColorRegistersPublishTheirOwnViewAndClearInitialization()
    {
        using var state = new State();
        SubmitColor(ColorTarget());
        var source = state.LatestSource(Address)!;
        Assert.True((bool)source.GetType().GetField("ClearsOutput")!.GetValue(source)!);
        var descriptor = (GuestDrawTexture)source.GetType().GetField("Descriptor")!.GetValue(source)!;
        Assert.Equal(4u, descriptor.Width); Assert.Equal(4u, descriptor.Height);
        Assert.Equal(4u, descriptor.Pitch); Assert.Equal(10u, descriptor.Format);
        Assert.Equal(9u, descriptor.Type);
        Assert.Equal(128UL, state.NativeBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    public void NativeSourceCertificationRejectsMissingCompleteResourceWords(int length)
    {
        using var state = new State();
        var seed = Seed();
        var invalid = seed with { Descriptor = seed.Descriptor!.Take(length).ToArray() };
        var before = state.Summary;
        var failure = Assert.Throws<NotSupportedException>(() => Submit(invalid));
        Assert.Contains("eight owned resource descriptor words", failure.Message);
        Assert.Equal(before, state.Summary);
        Assert.False(state.HasLatest(Address));
    }

    private static GuestRenderTarget ColorTarget() => new(Address, 4, 4, 10, 0,
        Registers: new ColorTargetWords(Address, 0, 10u << 2, 0, 3u | 3u << 14, 1u << 24, 0, 0, 0, 0, 0));
    private static void SubmitColor(GuestRenderTarget target) => MetalVideoPresenter.SubmitOffscreenTranslatedDraw(
        new MetalCompiledGuestShader(new Gen5MslShader("", "color", Gen5MslStage.Pixel, 0)), [], [], 0, [target],
        null, 3, 1, 4, null, null, null, null, 0);
    private static long Submit(params GuestDrawTexture[] textures) =>
        MetalVideoPresenter.SubmitComputeDispatch(0, Shader, textures, [], 1, 1, 1, 0, 0, 0, true, null);
    private static GuestDrawTexture Seed()
    {
        var memory = new FakeCpuMemory(Address, 4096);
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(MetalTextureSourceLeaseTests.ResourceWords(), out var descriptor));
        var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [], false,
            ResourceDescriptor: MetalTextureSourceLeaseTests.ResourceWords(),
            Shape: new ShaderImageShape(false, false, true, false, TextureNumericClass.Float), Access: GuestImageAccess.Write);
        return Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
    }
    private static FieldInfo Field(string name) => typeof(MetalVideoPresenter).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static void Call(string name, object work) => typeof(MetalVideoPresenter)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [work]);
    private sealed class State : IDisposable
    {
        private readonly MetalTextureSourceLeaseTests.State sourceState = new();
        private readonly object gate = Field("_gate").GetValue(null)!;
        private readonly bool oldEnabled = (bool)Field("_textureSourceGraphEnabled").GetValue(null)!;
        private readonly ulong oldNativeBytes = (ulong)Field("_immutableTextureSourceBytes").GetValue(null)!;
        private readonly FieldInfo backend = typeof(GuestGpu).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? oldBackend;
        private readonly IDictionary available = (IDictionary)Field("_availableGuestImages").GetValue(null)!;
        private readonly bool hadAvailable;
        private readonly object? oldAvailable;
        public readonly List<GuestDrawTexture> Captures = [];
        private IDictionary Latest => (IDictionary)Field("_latestTextureSources").GetValue(null)!;
        public bool HasLatest(ulong address) { lock (gate) return Latest.Contains(address); }
        public object? LatestSource(ulong address) { lock (gate) return Latest[address]; }
        public State()
        {
            GuestImageWriteTracker.TrackManagedWriter(Address, 4096);
            oldBackend = backend.GetValue(null);
            backend.SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(MetalGuestGpuBackend)));
            hadAvailable = available.Contains(Address);
            oldAvailable = available[Address];
            available.Remove(Address);
            Field("_textureSourceGraphEnabled").SetValue(null, true);
        }
        public ulong NativeBytes
        {
            get { lock (gate) return (ulong)Field("_immutableTextureSourceBytes").GetValue(null)!; }
            set { lock (gate) Field("_immutableTextureSourceBytes").SetValue(null, value); }
        }
        public (long Sequence, int Pending, ulong Bytes) Summary
        {
            get { lock (gate) return ((long)Field("_enqueuedGuestWorkSequence").GetValue(null)!,
                (int)Field("_pendingGuestWorkCount").GetValue(null)!, (ulong)Field("_pendingGuestWorkBytes").GetValue(null)!); }
        }
        private object[] Owners => ((IEnumerable)Field("_textureSourceWorkOwners").GetValue(null)!).Cast<object>().ToArray();
        public int OwnerCount { get { lock (gate) return Owners.Length; } }
        public void ReleaseWork(long sequence)
        {
            lock (gate)
            {
                var queues = ((IDictionary)Field("_pendingGuestWorkByQueue").GetValue(null)!).Values.Cast<IEnumerable>();
                var pending = queues.SelectMany(queue => queue.Cast<object>()).Single(item =>
                    (long)item.GetType().GetProperty("Sequence")!.GetValue(item)! == sequence);
                Call("ReleaseTextureSourceWorkOwnerLocked", pending.GetType().GetProperty("Work")!.GetValue(pending)!);
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                MetalVideoPresenter.ReleasePreparedTextureSources(Captures);
                foreach (var pair in Owners)
                {
                    var work = pair.GetType().GetProperty("Key")!.GetValue(pair)!;
                    Call("ReleaseTextureSourceWorkOwnerLocked", work);
                }
                Field("_textureSourceGraphEnabled").SetValue(null, oldEnabled);
                NativeBytes = oldNativeBytes;
                backend.SetValue(null, oldBackend);
                if (hadAvailable) available[Address] = oldAvailable;
                else available.Remove(Address);
            }
            sourceState.Dispose();
        }
    }
}
