// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalQueuedTextureSnapshotTests
{
    private const ulong Address = 0x7FEE_0000_0000;
    private const int AllocationBytes = 1024 * 1024;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CacheHitThenWriteAndEvictionCannotDiscardTheQueuedTiledOrMipSnapshot(bool mipArray, bool writeBeforeCapture)
    {
        using var state = new SnapshotState();
        var memory = new CountingMemory();
        var expected = writeBeforeCapture ? (byte)0x55 : (byte)0x37;
        memory.Fill(0x37);
        var descriptor = Descriptor(mipArray);
        var lookup = Lookup(descriptor);
        // A synthetic advertised native entry exercises the real producer's
        // old cache-hit path without allocating any device resource.
        state.Cache[lookup] = 0;
        state.Cache[lookup with { Sampler = new GuestSampler(1, 2, 3, 4) }] = 0;
        state.Cache[lookup with { Sampler = new GuestSampler(5, 6, 7, 8) }] = 0;
        Assert.True(MetalVideoPresenter.IsTextureContentCached(lookup));
        Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(Address, out var cachedGeneration));
        if (writeBeforeCapture)
        {
            memory.Fill(expected);
            GuestImageWriteTracker.NotifyManagedWrite(Address, AllocationBytes);
        }
        Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(Address, out var captureGeneration));
        var firstSampler = new uint[] { 1, 2, 3, 4 };
        var secondSampler = new uint[] { 5, 6, 7, 8 };
        var snapshots = MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5),
            [Binding(descriptor, firstSampler, mipArray), Binding(descriptor, secondSampler, mipArray)], out var fallbacks);
        Assert.Equal(0, fallbacks);
        Assert.Equal(2, snapshots.Count);
        Assert.Equal(mipArray ? 2 : 1, memory.Reads);
        var snapshot = snapshots[0];
        Assert.Equal(captureGeneration, snapshot.WriteGeneration);
        Assert.True(snapshot.CpuSnapshotStable);
        if (writeBeforeCapture) Assert.NotEqual(cachedGeneration, snapshot.WriteGeneration);
        Assert.Equal(descriptor.Type, snapshot.Type);
        Assert.Equal(descriptor.TileMode, snapshot.TileMode);
        Assert.NotEqual(snapshot.Sampler, snapshots[1].Sampler);
        if (mipArray)
        {
            Assert.NotEmpty(snapshot.RgbaPixels);
            Assert.NotNull(snapshot.MipUploads);
            Assert.Equal(2, snapshot.MipUploads!.Length);
            Assert.Same(snapshot.RgbaPixels, snapshots[1].RgbaPixels);
        }
        else
        {
            Assert.NotNull(snapshot.TiledSource);
            Assert.NotEmpty(snapshot.TiledSource!);
            Assert.Same(snapshot.TiledSource, snapshots[1].TiledSource);
        }

        GuestDrawTexture? consumed = null;
        var sequence = MetalVideoPresenter.SubmitOrderedGuestAction(
            () => consumed = MetalVideoPresenter.PrepareDrawTextureUpload(snapshot), "consume retained snapshot");
        // Both dirty eviction and a full cache flush remove this entry. The
        // queued action keeps its producer bytes, descriptor and generation.
        memory.Fill(0xA5);
        GuestImageWriteTracker.NotifyManagedWrite(Address, AllocationBytes);
        Assert.True(GuestImageWriteTracker.ConsumeDirty(Address));
        state.Cache.Clear();
        Assert.False(MetalVideoPresenter.IsTextureContentCached(lookup));
        var readsAtEnqueue = memory.Reads;
        MetalVideoPresenter.DrainGuestWork(0, 0);
        Assert.True(MetalVideoPresenter.WaitForGuestWork(sequence, 0));
        Assert.NotNull(consumed);
        Assert.NotEmpty(consumed!.RgbaPixels);
        Assert.All(consumed.RgbaPixels, value => Assert.Equal(expected, value));
        Assert.Equal(captureGeneration, consumed.WriteGeneration);
        Assert.Equal(readsAtEnqueue, memory.Reads); // Consumption never re-reads the newer guest bytes.
    }

    [Fact]
    public void DifferentDescriptorViewsDoNotShareProducerPayloads()
    {
        using var state = new SnapshotState();
        var memory = new CountingMemory();
        memory.Fill(0x37);
        var descriptor = Descriptor(false);
        var changed = descriptor with { DstSelect = 0x123 };
        var snapshots = MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5),
            [Binding(descriptor, [], false), Binding(changed, [], false)], out _);
        Assert.Equal(2, memory.Reads);
        Assert.NotSame(snapshots[0].TiledSource, snapshots[1].TiledSource);
        Assert.Equal(0x123u, snapshots[1].DstSelect);
    }

    [Fact]
    public void RepeatedWritesDuringCaptureRejectInsteadOfReturningAnEmptyCacheDependency()
    {
        using var state = new SnapshotState();
        var memory = new CountingMemory { AfterRead = () =>
        {
            // The tracker counts one write generation per dirty epoch. Model
            // the other cache owner consuming each epoch before the next write.
            _ = GuestImageWriteTracker.ConsumeDirty(Address);
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(Address, out var before));
            GuestImageWriteTracker.NotifyManagedWrite(Address, AllocationBytes);
            Assert.True(GuestImageWriteTracker.TryGetWriteGeneration(Address, out var after));
            Assert.True(after > before);
        } };
        var descriptor = Descriptor(false);
        var error = Assert.Throws<InvalidOperationException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(memory, Generation.Gen5), [Binding(descriptor, [], false)], out _));
        Assert.Equal(3, memory.Reads);
        Assert.Contains("write generation changed across 3 attempts", error.Message);
    }

    [Fact]
    public void UnstableSnapshotsAreRejectedBeforeNativeAliasOrCacheRecovery()
    {
        var texture = new GuestDrawTexture(Address, 128, 128, 10, 0, [], false, false,
            TileMode: 24, CpuSnapshotStable: false);
        Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.CreateDrawTexture(0, 0, texture, out _));
    }

    [Fact]
    public void SnapshotBudgetFailureReportsRequiredBytesAndTheUnchangedLimit()
    {
        using var state = new SnapshotState();
        var descriptor = Descriptor(false) with { Width = 8192, Height = 8192, Pitch = 8192, Format = 4, NumberType = 7 };
        var error = Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(new CountingMemory(), Generation.Gen5), [Binding(descriptor, [], false)], out _));
        Assert.Contains("required_bytes=268435456", error.Message);
        Assert.Contains("maximum_snapshot_bytes=134217728", error.Message);
        Assert.Contains("format=4/7 tile=24", error.Message);
    }

    [Theory]
    [InlineData(9u, 0u, 1u, 169u, 0u)]
    [InlineData(10u, 0u, 1u, 176u, 1u)]
    [InlineData(10u, 27u, 1u, 176u, 1u)]
    [InlineData(10u, 5u, 16u, 176u, 1u)]
    public void CompressedStorageRejectsBeforeReadingGuestBacking(uint type, uint tile, uint depth, uint format, uint number)
    {
        using var state = new SnapshotState();
        var memory = new CountingMemory();
        var descriptor = new AgcExports.TextureDescriptor(Address, 16, 16, format, number, tile,
            type, 0, 0, 16, 0xFAC, Depth: depth, HasExtendedDescriptor: true);
        var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, []);
        var error = Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(memory, Generation.Gen5), [binding], out _));
        Assert.Contains("compressed storage images require explicit guest block-write semantics", error.Message);
        Assert.Equal(0, memory.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DrawAndDispatchBudgetsCountBackingAllocationsOncePerWork(bool compute)
    {
        using var state = new SnapshotState();
        var pixels = new byte[256];
        var tiled = new byte[4096];
        var descriptor = new uint[8];
        var mips = new GuestTextureMipUpload[2];
        var texture = new GuestDrawTexture(Address, 4, 4, 10, 0, pixels, false, compute,
            TiledSource: tiled, Descriptor: descriptor, MipUploads: mips,
            Access: compute ? GuestImageAccess.Write : GuestImageAccess.Read);
        var bufferBytes = new byte[1024]; // Logical Length deliberately smaller than the owned allocation.
        var buffer = new GuestMemoryBuffer(Address, bufferBytes, 4, 4, false);
        var shader = new MetalCompiledGuestShader(new Gen5MslShader("", "snapshot_test",
            compute ? Gen5MslStage.Compute : Gen5MslStage.Pixel, AttributeCount: 0));
        if (compute)
        {
            Assert.True(MetalVideoPresenter.SubmitComputeDispatch(0, shader, [texture, texture], [buffer, buffer],
                1, 1, 1, 0, 0, 0, false, null) > 0);
        }
        else
        {
            MetalVideoPresenter.SubmitOffscreenTranslatedDraw(shader, [texture, texture], [buffer, buffer], 0,
                [new GuestRenderTarget(Address, 4, 4, 10, 0)], null, 3, 1, 4,
                new GuestIndexBuffer(bufferBytes, 4, false, false),
                [new GuestVertexBuffer(0, 1, 0, 0, Address, 4, 0, bufferBytes, 4, false)],
                null, null, 0);
        }
        var queues = (IDictionary)PresenterField("_pendingGuestWorkByQueue").GetValue(null)!;
        var queue = queues.Values.Cast<object>().Single();
        var first = queue.GetType().GetProperty("First")!.GetValue(queue)!;
        var pending = first.GetType().GetProperty("Value")!.GetValue(first)!;
        var work = pending.GetType().GetProperty("Work")!.GetValue(pending)!;
        var expected = 256UL + 4096 + 8 * 4 + 2 * 24 + 1024;
        Assert.Equal(expected, MetalVideoPresenter.GetGuestWorkPayloadBytes(work));
        Assert.Equal(expected, (ulong)PresenterField("_pendingGuestWorkBytes").GetValue(null)!);
    }

    private static AgcExports.TextureDescriptor Descriptor(bool mipArray) =>
        new(Address, 128, 128, 10, 0, 24, mipArray ? 13u : 9u, 0, mipArray ? 1u : 0u,
            128, 0xFAC, Depth: mipArray ? 2u : 1u, MaxMip: mipArray ? 1u : 0u, HasExtendedDescriptor: true);
    private static AgcExports.TranslatedImageBinding Binding(AgcExports.TextureDescriptor descriptor, uint[] sampler, bool array) =>
        new(descriptor, false, 0, sampler, array);
    private static TextureCacheLookupIdentity Lookup(AgcExports.TextureDescriptor descriptor) =>
        new(new(Address, descriptor.Width, descriptor.Height, descriptor.Format, descriptor.NumberType,
            descriptor.DstSelect, descriptor.TileMode, descriptor.Pitch, descriptor.Type == 13, descriptor.Depth,
            descriptor.Type, 1, descriptor.ResourceMipLevels), default);
    private static FieldInfo PresenterField(string name) => typeof(MetalVideoPresenter)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private sealed class CountingMemory : ICpuMemory
    {
        private readonly FakeCpuMemory memory = new(Address, AllocationBytes);
        public int Reads { get; private set; }
        public Action? AfterRead { get; init; }
        public void Fill(byte value) => Assert.True(memory.TryWrite(Address, Enumerable.Repeat(value, AllocationBytes).ToArray()));
        public bool TryRead(ulong address, Span<byte> destination)
        {
            Reads++;
            var success = memory.TryRead(address, destination);
            AfterRead?.Invoke();
            return success;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => memory.TryWrite(address, source);
    }

    private sealed class SnapshotState : IDisposable
    {
        private readonly MetalGuestFailureTests.PresenterState presenter = new();
        private readonly FieldInfo instance = typeof(GuestGpu).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly FieldInfo enabled = typeof(GuestImageWriteTracker).GetField("_enabled", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? previousInstance;
        private readonly object? previousEnabled;
        private readonly KeyValuePair<TextureCacheLookupIdentity, byte>[] previousCache;
        public ConcurrentDictionary<TextureCacheLookupIdentity, byte> Cache { get; } =
            (ConcurrentDictionary<TextureCacheLookupIdentity, byte>)PresenterField("_cachedDrawTextureIdentities").GetValue(null)!;

        public SnapshotState()
        {
            previousInstance = instance.GetValue(null);
            previousEnabled = enabled.GetValue(null);
            // Backend methods use the real presenter index; its constructor's
            // page-protection warm-up is unrelated to this no-device fixture.
            instance.SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(MetalGuestGpuBackend)));
            enabled.SetValue(null, true);
            GuestImageWriteTracker.TrackManagedWriter(Address, AllocationBytes, source: "queued-snapshot-test");
            previousCache = Cache.ToArray();
            Cache.Clear();
        }
        public void Dispose()
        {
            Cache.Clear();
            foreach (var entry in previousCache) Cache[entry.Key] = entry.Value;
            GuestImageWriteTracker.Untrack(Address);
            enabled.SetValue(null, previousEnabled);
            instance.SetValue(null, previousInstance);
            presenter.Dispose();
        }
    }
}
