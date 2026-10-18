// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// SHARPEMU_METAL_V2_SPARSE_LARGE: a large one-level 2D-array texture is created on a sparse
/// heap with tiles only for the layers something wrote. Every case runs the same steps on a
/// cache with sparse textures and one without, and compares what the textures hold, what a
/// shader samples and what a writeback leaves in guest memory. Device work is behind the
/// exclusive device gate.
/// </summary>
public sealed unsafe class MetalSparseTextureTests(ITestOutputHelper output)
{
    private const ulong GuestBase = 0x2_0000_0000;
    private const ulong ArrayAddress = GuestBase + 0x100000;

    private const string SampleKernel = """
        #include <metal_stdlib>
        using namespace metal;
        kernel void sample_layers(texture2d_array<float, access::sample> t [[texture(0)]],
                                  device float4* o [[buffer(0)]],
                                  uint2 p [[thread_position_in_grid]])
        {
            constexpr sampler s(filter::nearest);
            float2 uv = (float2(p.x * 37 % t.get_width(), p.x * 11 % t.get_height()) + 0.5) /
                float2(t.get_width(), t.get_height());
            o[p.y * 64 + p.x] = t.sample(s, uv, p.y);
        }
        """;

    // The Demon's Souls shadow pool: D32 1024x1024, 80 layers (320 MiB), four layers with
    // guest bytes and a fifth rendered to, at the default threshold.
    [Fact]
    public void AShadowPoolSizedArrayHoldsTilesOnlyForTheLayersInUse()
    {
        if (!RequireDevice()) return;
        var shape = new ArrayShape(1024, 1024, 80);
        var guest = Guest(shape, filledLayers: [0, 1, 2, 3]);
        // The first run pays the process's one-time Metal costs (pipelines, staging), which
        // would land in whichever footprint and time came first.
        _ = RunPool(shape, guest, sparseTextures: false);
        var sparse = RunPool(shape, guest, sparseTextures: true);
        var full = RunPool(shape, guest, sparseTextures: false);

        Assert.True(sparse.Sparse);
        Assert.False(full.Sparse);
        Assert.Equal(full.Layers, sparse.Layers);
        Assert.Equal(full.Samples, sparse.Samples);
        Assert.Equal(full.Guest, sparse.Guest);
        Assert.True(sparse.Allocated < full.Allocated / 4, $"sparse {sparse.Allocated} vs {full.Allocated}");
        output.WriteLine(
            $"80-layer D32 1024x1024: allocated sparse={sparse.Allocated} full={full.Allocated} " +
            $"saved={full.Allocated - sparse.Allocated} bytes; footprint delta sparse={sparse.Footprint} full={full.Footprint}; " +
            $"create+upload+pass sparse={sparse.Milliseconds:F1} ms full={full.Milliseconds:F1} ms; " +
            $"counters={sparse.Counters}");
    }

    // A pass over more layers than the heap holds moves the image to a heap twice as large
    // with every layer it held; at the full count it becomes an ordinary texture.
    [Fact]
    public void LayersPastTheHeapGrowItAndKeepEveryLayersContents()
    {
        if (!RequireDevice()) return;
        var shape = new ArrayShape(512, 512, 16);
        var guest = Guest(shape, filledLayers: [7]);
        var sparse = RunGrowth(shape, guest, sparseTextures: true);
        var full = RunGrowth(shape, guest, sparseTextures: false);
        Assert.Equal(full.Snapshots.Count, sparse.Snapshots.Count);
        for (var index = 0; index < full.Snapshots.Count; index++)
        {
            Assert.True(full.Snapshots[index].SequenceEqual(sparse.Snapshots[index]), $"snapshot {index}");
        }

        Assert.Equal(full.Guest, sparse.Guest);
        Assert.Equal([true, true, true, false], sparse.SparseAfterStep);
        Assert.True(sparse.Grown >= 2, $"grown {sparse.Grown}");
        output.WriteLine($"growth: allocated after each step {string.Join(", ", sparse.AllocatedAfterStep)}; grown={sparse.Grown}");
    }

    // A layer whose guest bytes become all zero gives its tiles back on the next upload,
    // and still reads as zero.
    [Fact]
    public void AnUploadOfAZeroedLayerUnmapsIt()
    {
        if (!RequireDevice()) return;
        var shape = new ArrayShape(512, 512, 16);
        using var rig = new Rig(Device.Value, shape.Bytes, sparseTextures: true, minimum: 4 * 1024 * 1024);
        rig.Write(ArrayAddress, Guest(shape, filledLayers: [2, 3]));
        var request = Request(shape, ImageRole.Texture, 0, shape.Layers);
        var identifier = rig.Cache.FindImage(ref request);
        rig.Cache.AcquireTextureView(identifier, in request);
        var image = rig.Cache.GetImage(identifier);
        Assert.NotNull(image.Sparse);
        Assert.Equal(2u, image.Sparse!.MappedCount);

        rig.Write(ArrayAddress + (shape.LayerBytes * 3), new byte[shape.LayerBytes]);
        rig.Cache.InvalidateMemory(ArrayAddress + (shape.LayerBytes * 3), shape.LayerBytes);
        rig.Cache.AcquireTextureView(identifier, in request);
        Assert.Equal(1u, image.Sparse.MappedCount);
        Assert.False(image.Sparse.Mapped[3]);
        var counters = rig.Cache.TakeSparseCounters();
        Assert.Equal((1L, 0L, 2L, 1L), counters);
        Assert.All(rig.ReadLayer(image.Texture, shape, 3), value => Assert.Equal(0, value));
        Assert.Contains(rig.ReadLayer(image.Texture, shape, 2), value => value != 0);
    }

    // A clear of the whole image (a guest fill the cache turns into one) and a copy into it
    // both leave it an ordinary texture holding what the sparse one held plus the write.
    [Fact]
    public void AWriteTheCacheCannotNarrowToLayersGivesAnOrdinaryTexture()
    {
        if (!RequireDevice()) return;
        var shape = new ArrayShape(512, 512, 16);
        using var rig = new Rig(Device.Value, shape.Bytes, sparseTextures: true, minimum: 4 * 1024 * 1024);
        var request = Request(shape, ImageRole.DepthTarget, 0, 1);
        var identifier = rig.Cache.FindImage(ref request);
        rig.Cache.AcquireDepthTargetView(identifier, in request);
        var image = rig.Cache.GetImage(identifier);
        Assert.NotNull(image.Sparse);
        Assert.True(rig.Cache.TryClearImageFromBuffer(ArrayAddress, shape.Bytes, 0x3F000000));
        Assert.Null(image.Sparse is { } sparse && sparse.Texture == image.Texture ? sparse : null);
        for (var layer = 0u; layer < shape.Layers; layer += 5)
        {
            Assert.All(rig.ReadLayer(image.Texture, shape, layer), value => Assert.Equal(0.5f, value));
        }
    }

    [Fact]
    public void OnlyLargeOneLevelArraysWithoutShaderWriteAreSparse()
    {
        if (!RequireDevice()) return;
        var shape = new ArrayShape(512, 512, 16);
        using (var rig = new Rig(Device.Value, shape.Bytes, sparseTextures: true, minimum: shape.Bytes + 1))
        {
            var small = Request(shape, ImageRole.DepthTarget, 0, 1);
            Assert.Null(rig.Cache.GetImage(rig.Cache.FindImage(ref small)).Sparse);
        }

        using (var rig = new Rig(Device.Value, shape.Bytes, sparseTextures: false, minimum: 4 * 1024 * 1024))
        {
            var off = Request(shape, ImageRole.DepthTarget, 0, 1);
            Assert.Null(rig.Cache.GetImage(rig.Cache.FindImage(ref off)).Sparse);
        }

        using (var rig = new Rig(Device.Value, shape.Bytes, sparseTextures: true, minimum: 4 * 1024 * 1024))
        {
            var colour = Request(shape, ImageRole.Texture, 0, shape.Layers, GpuFormat.R32Sfloat);
            Assert.Null(rig.Cache.GetImage(rig.Cache.FindImage(ref colour)).Sparse);
            var depth = Request(shape, ImageRole.Texture, 0, shape.Layers);
            Assert.NotNull(rig.Cache.GetImage(rig.Cache.FindImage(ref depth)).Sparse);
        }
    }

    private readonly record struct ArrayShape(uint Width, uint Height, uint Layers)
    {
        public ulong LayerBytes => LayerSize(Width, Height);

        public ulong Bytes => LayerBytes * Layers;
    }

    private sealed record PoolResult(
        bool Sparse, ulong Allocated, long Footprint, double Milliseconds, string Counters,
        List<float[]> Layers, float[] Samples, byte[] Guest);

    // Creates the array over the guest bytes, renders a clear to layer 5, samples every
    // layer and writes the image back.
    private PoolResult RunPool(ArrayShape shape, byte[] guest, bool sparseTextures)
    {
        using var rig = new Rig(Device.Value, shape.Bytes, sparseTextures, minimum: 64UL * 1024 * 1024);
        rig.Write(ArrayAddress, guest);
        var before = MetalHostMemory.Sample().ProcessFootprintBytes;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var target = Request(shape, ImageRole.DepthTarget, 5, 1);
        var identifier = rig.Cache.FindImage(ref target);
        var view = rig.Cache.AcquireDepthTargetView(identifier, in target);
        rig.ClearDepth(view, 0.75f);
        rig.Harness.Submit();
        rig.Harness.Begin();
        clock.Stop();
        var footprint = (long)MetalHostMemory.Sample().ProcessFootprintBytes - (long)before;
        var image = rig.Cache.GetImage(identifier);
        var sparse = image.Sparse is { } residency && residency.Texture == image.Texture;
        var allocated = image.AllocatedBytes;
        var counters = rig.Cache.TakeSparseCounters().ToString();

        var layers = new List<float[]>();
        foreach (var layer in new uint[] { 0, 3, 4, 5, 6, shape.Layers - 1 })
        {
            layers.Add(rig.ReadLayer(image.Texture, shape, layer));
        }

        var sampled = Request(shape, ImageRole.Texture, 0, shape.Layers);
        Assert.Equal(identifier, rig.Cache.FindImage(ref sampled));
        var samples = rig.Sample(rig.Cache.AcquireTextureView(identifier, in sampled), shape.Layers);
        rig.Cache.FlushGpuWrites(ArrayAddress, shape.Bytes, MetalWritebackCause.HostRead);
        rig.Harness.Submit();
        rig.Harness.Begin();
        return new PoolResult(sparse, allocated, footprint, clock.Elapsed.TotalMilliseconds, counters,
            layers, samples, rig.ReadGuest(ArrayAddress, shape.Bytes));
    }

    private sealed record GrowthResult(
        List<float[]> Snapshots, List<bool> SparseAfterStep, List<ulong> AllocatedAfterStep, long Grown, byte[] Guest);

    // Renders a different clear into more and more layers, one pass at a time, and keeps
    // what every rendered layer holds after each step.
    private GrowthResult RunGrowth(ArrayShape shape, byte[] guest, bool sparseTextures)
    {
        using var rig = new Rig(Device.Value, shape.Bytes, sparseTextures, minimum: 4 * 1024 * 1024);
        rig.Write(ArrayAddress, guest);
        var snapshots = new List<float[]>();
        var sparseAfter = new List<bool>();
        var allocatedAfter = new List<ulong>();
        var grown = 0L;
        uint[][] steps = [[0], [1, 2], [3, 4, 5, 6], [8, 9, 10, 11, 12, 13, 14, 15]];
        var written = new List<uint> { 7 };
        foreach (var step in steps)
        {
            foreach (var layer in step)
            {
                var target = Request(shape, ImageRole.DepthTarget, layer, 1);
                var identifier = rig.Cache.FindImage(ref target);
                var view = rig.Cache.AcquireDepthTargetView(identifier, in target);
                rig.ClearDepth(view, 0.125f * (layer % 7 + 1));
                written.Add(layer);
            }

            rig.Harness.Submit();
            rig.Harness.Begin();
            var request = Request(shape, ImageRole.Texture, 0, shape.Layers);
            var image = rig.Cache.GetImage(rig.Cache.FindImage(ref request));
            sparseAfter.Add(image.Sparse is { } sparse && sparse.Texture == image.Texture);
            allocatedAfter.Add(image.AllocatedBytes);
            grown += rig.Cache.TakeSparseCounters().Grown;
            foreach (var layer in written)
            {
                snapshots.Add(rig.ReadLayer(image.Texture, shape, layer));
            }
        }

        rig.Cache.FlushGpuWrites(ArrayAddress, shape.Bytes, MetalWritebackCause.HostRead);
        rig.Harness.Submit();
        rig.Harness.Begin();
        return new GrowthResult(snapshots, sparseAfter, allocatedAfter, grown, rig.ReadGuest(ArrayAddress, shape.Bytes));
    }

    private static ulong LayerSize(uint width, uint height)
    {
        Assert.True(TileGeometry.TryGetDepthSize(width, height, 0, GuestDepthFormat.Z32Float, GuestStencilFormat.Invalid,
            false, out _, out _, out var depthSize));
        return depthSize.Size;
    }

    // Depth values in [0, 1] over the listed layers, zero everywhere else.
    private static byte[] Guest(ArrayShape shape, uint[] filledLayers)
    {
        var bytes = new byte[shape.Bytes];
        var random = new Random(0x5A5E);
        foreach (var layer in filledLayers)
        {
            var words = MemoryMarshal.Cast<byte, float>(bytes.AsSpan((int)(shape.LayerBytes * layer), (int)shape.LayerBytes));
            for (var index = 0; index < words.Length; index++)
            {
                words[index] = (float)random.NextDouble();
            }
        }

        return bytes;
    }

    private static ImageRequest Request(ArrayShape shape, ImageRole role, uint baseLayer, uint layerCount,
        GpuFormat format = GpuFormat.D32Sfloat)
    {
        var pitch = TileGeometry.DepthPitch(shape.Width, 4);
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(ArrayAddress, shape.Bytes);
        description.PixelFormat = format;
        description.GuestFormat = GuestPixelFormat.Bits32Float;
        description.Type = GuestImageType.Color2D;
        description.Extent = new GpuExtent3D(shape.Width, shape.Height, 1);
        description.Resources = new SubresourceCount(1, shape.Layers);
        description.Pitch = pitch;
        description.BytesPerBlock = 4;
        description.Samples = 1;
        description.TileMode = GuestTileMode.Depth;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = shape.Bytes, Pitch = pitch, Height = shape.Height };
        var view = ImageViewDescription.Default with
        {
            Format = format,
            Type = layerCount == 1 ? GpuImageViewType.Type2D : GpuImageViewType.Type2DArray,
            Aspect = format == GpuFormat.D32Sfloat ? GpuImageAspect.DepthBit : GpuImageAspect.ColorBit,
            BaseLayer = baseLayer,
            LayerCount = layerCount,
            Usage = role == ImageRole.DepthTarget ? GpuImageUsage.DepthStencilAttachmentBit : GpuImageUsage.SampledBit,
        };
        return new ImageRequest(description, view, role);
    }

    private sealed class Rig : IDisposable
    {
        private readonly AddressSpace _space = new();
        private readonly PageGuard _pages;
        private readonly MetalGuestMemory _memory;
        private readonly Backing _backing;

        public Rig(nint device, ulong arrayBytes, bool sparseTextures, ulong minimum)
        {
            _backing = new Backing(arrayBytes + (2 * 1024 * 1024));
            Harness = new MetalTransferHarness(device);
            Harness.Begin();
            _pages = new PageGuard(_space);
            _memory = new MetalGuestMemory(device, _backing, new NoMemory());
            Cache = new MetalImageCache(new Owner(Harness), Harness.Transfer, _memory, _pages, _backing)
            {
                SparseLargeTextures = sparseTextures,
                SparseMinimumBytes = minimum,
            };
        }

        public MetalTransferHarness Harness { get; }
        public MetalImageCache Cache { get; }

        public void Write(ulong address, ReadOnlySpan<byte> bytes) => Assert.True(_backing.TryWriteBacking(address, bytes));

        public byte[] ReadGuest(ulong address, ulong size)
        {
            var bytes = new byte[size];
            Assert.True(_backing.TryReadBacking(address, bytes));
            return bytes;
        }

        public void ClearDepth(nint texture, float depth) => Owner.EncodeDepthClear(Harness.CommandBuffer, texture, 0, depth);

        // One layer of a Depth32Float array texture, through a buffer.
        public float[] ReadLayer(nint texture, ArrayShape shape, uint layer)
        {
            var bytes = (ulong)shape.Width * shape.Height * 4;
            var buffer = Harness.Buffer(bytes);
            var blit = MetalNative.Send(Harness.CommandBuffer, MetalNative.Selector("blitCommandEncoder"));
            MetalNative.SendCopyTextureToBufferOptions(
                blit,
                MetalNative.Selector(
                    "copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:" +
                    "toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:options:"),
                texture, layer, 0, default, new MtlSize { Width = shape.Width, Height = shape.Height, Depth = 1 },
                buffer, 0, shape.Width * 4, (nuint)bytes, 0);
            MetalNative.SendVoid(blit, MetalNative.Selector("endEncoding"));
            Harness.Submit();
            Harness.Begin();
            return MemoryMarshal.Cast<byte, float>(Harness.Read(buffer, 0, bytes)).ToArray();
        }

        // 64 samples of every layer through a texture2d_array<float> binding.
        public float[] Sample(nint view, uint layers)
        {
            var count = (int)(64 * layers * 4);
            var buffer = Harness.Buffer((ulong)count * sizeof(float));
            var pipeline = MetalGuestWindowResidencyTests.ComputePipeline(SampleKernel, "sample_layers");
            try
            {
                var encoder = MetalNative.Send(Harness.CommandBuffer, MetalNative.Selector("computeCommandEncoder"));
                MetalNative.SendVoid(encoder, MetalNative.Selector("setComputePipelineState:"), pipeline);
                MetalNative.SendSetAtIndex(encoder, MetalNative.Selector("setTexture:atIndex:"), view, 0);
                MetalNative.SendSetBuffer(encoder, MetalNative.Selector("setBuffer:offset:atIndex:"), buffer, 0, 0);
                MetalNative.SendDispatch(encoder, MetalNative.Selector("dispatchThreads:threadsPerThreadgroup:"),
                    new MtlSize { Width = 64, Height = layers, Depth = 1 },
                    new MtlSize { Width = 64, Height = 1, Depth = 1 });
                MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
                Harness.Submit();
                Harness.Begin();
            }
            finally
            {
                MetalNative.SendVoid(pipeline, MetalNative.Selector("release"));
            }

            return MemoryMarshal.Cast<byte, float>(Harness.Read(buffer, 0, (ulong)count * sizeof(float))).ToArray();
        }

        public void Dispose()
        {
            Cache.StopWatching();
            Cache.Dispose();
            _pages.Dispose();
            Harness.Dispose();
            _backing.Dispose();
        }
    }

    // Clears are real render passes, recorded into the harness command buffer.
    private sealed class Owner(MetalTransferHarness harness) : IMetalImageCacheOwner
    {
        public nint Device => harness.Device;
        public ulong CurrentSerial => harness.SubmissionSerial;
        public ulong CompletedSerial => harness.CompletedSerial;
        public IMetalSubmissionContext BeginTransfer(MetalTransferCause cause) => harness;
        public bool HasPendingGpuWrite(ulong address, ulong size) => false;
        public void FillGuestRange(ulong address, ulong size, uint value) { }
        public void NoteGuestWrite(ulong address, ulong size) { }
        public void WaitForGpu() { }

        public void ClearTextureColor(nint texture, ulong pixelFormat, in SubresourceRange range, uint width, uint height, in GpuClearColor color)
        {
        }

        public void ClearTextureDepthStencil(nint texture, ulong pixelFormat, in SubresourceRange range, uint width, uint height,
            float depth, byte stencil, bool clearDepth, bool clearStencil)
        {
            for (var layer = range.BaseLayer; layer < range.BaseLayer + range.LayerCount; layer++)
            {
                EncodeDepthClear(harness.CommandBuffer, texture, layer, depth);
            }
        }

        public static void EncodeDepthClear(nint commandBuffer, nint texture, uint slice, float depth)
        {
            var descriptor = MetalNative.Send(MetalNative.Class("MTLRenderPassDescriptor"), MetalNative.Selector("renderPassDescriptor"));
            var attachment = MetalNative.Send(descriptor, MetalNative.Selector("depthAttachment"));
            MetalNative.SendVoid(attachment, MetalNative.Selector("setTexture:"), texture);
            MetalNative.SendVoidNUInt(attachment, MetalNative.Selector("setSlice:"), slice);
            MetalNative.SendVoidNUInt(attachment, MetalNative.Selector("setLoadAction:"), 2);
            MetalNative.SendVoidNUInt(attachment, MetalNative.Selector("setStoreAction:"), 1);
            MetalNative.SendVoidDouble(attachment, MetalNative.Selector("setClearDepth:"), depth);
            var encoder = MetalNative.Send(commandBuffer, MetalNative.Selector("renderCommandEncoderWithDescriptor:"), descriptor);
            Assert.NotEqual(0, encoder);
            MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
        }
    }

    private sealed class NoMemory : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;
        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
        public bool CanRead(ulong address, ulong size) => false;
    }

    private sealed class AddressSpace : IGuestAddressSpace
    {
        public ulong ProtectionPageSize => 4096;
        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) => true;
        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true) => throw new NotSupportedException();
        public bool TryBackFixedRange(ulong address, ulong size, bool executable) => throw new NotSupportedException();
        public bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress) => throw new NotSupportedException();
        public bool TryEnsureRangeCommitted(ulong address, ulong size) => throw new NotSupportedException();
        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address) => throw new NotSupportedException();
        public bool TryFreeGuestMemory(ulong address) => throw new NotSupportedException();
    }

    private sealed class Backing : IGuestBackedSpace, IDisposable
    {
        private readonly ulong _size;
        private readonly void* _block;

        public Backing(ulong size)
        {
            _size = (size + 0xFFFFF) & ~0xFFFFFUL;
            _block = NativeMemory.AlignedAlloc((nuint)_size, 16384);
            NativeMemory.Clear(_block, (nuint)_size);
        }

        public void Dispose() => NativeMemory.AlignedFree(_block);

        private bool Contains(ulong address, ulong length) => address >= GuestBase && address - GuestBase + length <= _size;

        public bool TryHoldRange(ulong address, ulong size) => false;
        public bool TryHoldRangeAtOrAbove(ulong searchStart, ulong size, ulong alignment, out ulong address) { address = 0; return false; }
        public bool TryMapBacked(ulong address, ulong size, ulong backingOffset, GuestPageProtection protection, out HostViewFailure failure) { failure = HostViewFailure.None; return false; }
        public bool TryUnmapBacked(ulong address, ulong size) => false;
        public bool TryClearBacking(ulong offset, ulong size) => false;
        public bool IsBackedView(ulong address) => Contains(address, 1);
        public bool IsBackedRange(ulong address, ulong size) => Contains(address, size);
        public (ulong Base, ulong Size) BackingAliasRange => ((ulong)_block, _size);

        public bool TryResolveBackingAlias(ulong address, ulong size, out ulong alias)
        {
            alias = Contains(address, size) ? (ulong)_block + (address - GuestBase) : 0;
            return alias != 0;
        }

        public bool TryResolveBackingAliasSpan(ulong address, ulong size, out ulong alias) =>
            TryResolveBackingAlias(address, size, out alias);

        public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data)
        {
            if (!Contains(address, (ulong)data.Length)) return false;
            data.CopyTo(new Span<byte>((byte*)_block + (address - GuestBase), data.Length));
            return true;
        }

        public bool TryReadBacking(ulong address, Span<byte> data)
        {
            if (!Contains(address, (ulong)data.Length)) return false;
            new ReadOnlySpan<byte>((byte*)_block + (address - GuestBase), data.Length).CopyTo(data);
            return true;
        }
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Sparse textures run on the device only behind the exclusive device gate.");
            return false;
        }

        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        if (!MetalNative.SendBool(Device.Value, MetalNative.Selector("supportsFamily:"), 1006))
        {
            output.WriteLine("This device has no sparse textures (they need Apple6 or later).");
            return false;
        }

        return true;
    }

    private static readonly Lazy<nint> Device = new(MetalNative.MTLCreateSystemDefaultDevice);
}
