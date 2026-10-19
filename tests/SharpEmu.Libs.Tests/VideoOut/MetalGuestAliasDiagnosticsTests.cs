// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalGuestAliasDiagnosticsTests
{
    private const ulong Address = 0x7FEE_0000_1234;

    [Fact]
    public void AdvertisedButPendingAliasStillRejectsTiledRecoveryWithProducerContext()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        using var memory = new GuestMemoryState();
        MetalVideoPresenter.RegisterKnownDisplayBuffer(Address, 1);
        long producer;
        using (MetalVideoPresenter.EnterGuestQueue("alias.producer", 73))
            producer = MetalVideoPresenter.SubmitOrderedGuestAction(() => { }, "pending producer");
        Dictionary("_guestImageWorkSequences")[Address] = producer;
        var texture = Texture();
        var error = Reject(texture);
        Assert.IsType<NotSupportedException>(error.InnerException);
        Assert.Contains("tiled or mip-range snapshot required", error.Message);
        Assert.Contains("current_advertised=True/format=0x1", error.Message);
        Assert.Contains("current_active=[absent] current_selected=[absent]", error.Message);
        Assert.Contains($"current_producer_sequence={producer}", error.Message);
        Assert.Contains("current_producer_completed=False", error.Message);
        Assert.Contains("current_producer_queue='alias.producer' current_producer_submission=73", error.Message);
        Assert.Contains("type=9", error.Message);
        Assert.Contains("size=64x32", error.Message);
        Assert.Contains("format=10/0 tile=24 pitch=64", error.Message);
        Assert.Contains("mips=2 mip=1 base_mip=1 resource_mips=4", error.Message);
        Assert.Contains("captured_alias_provenance=unavailable", error.Message);
        Assert.False(MetalVideoPresenter.WaitForGuestWork(producer, 0));

        // The executing item has been removed from the pending queue. Its queue
        // context is still available through the real ordered-drain scope.
        MetalVideoPresenter.DrainGuestWork(0, 0);
        string? executingDiagnostic = null;
        long executing;
        using (MetalVideoPresenter.EnterGuestQueue("alias.executing", 74))
            executing = MetalVideoPresenter.SubmitOrderedGuestAction(
                () => executingDiagnostic = Reject(texture).Message, "inspect executing alias");
        Dictionary("_guestImageWorkSequences")[Address] = executing;
        MetalVideoPresenter.DrainGuestWork(0, 0);
        Assert.NotNull(executingDiagnostic);
        Assert.Contains("current_producer_executing=True", executingDiagnostic);
        Assert.Contains("current_producer_queue='alias.executing' current_producer_submission=74", executingDiagnostic);
    }

    [Theory]
    [InlineData(64, false, true)]
    [InlineData(16, true, false)]
    public void FailureDistinguishesUninitializedAndIncompatibleActiveImage(
        int width, bool initialized, bool selected)
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        using var memory = new GuestMemoryState();
        var images = Dictionary("_guestImages");
        var hadPrevious = images.Contains(Address);
        var previous = images[Address];
        var imageType = typeof(MetalVideoPresenter).GetNestedType("GuestImage", BindingFlags.NonPublic)!;
        var image = Activator.CreateInstance(imageType, nonPublic: true)!;
        imageType.GetField("Width")!.SetValue(image, (uint)width);
        imageType.GetField("Height")!.SetValue(image, 32u);
        imageType.GetField("Initialized")!.SetValue(image, initialized);
        images[Address] = image; // No native handle; failure inspection never queries Metal.
        try
        {
            var error = Reject(Texture());
            Assert.Contains($"current_active=[{width}x32/", error.Message);
            Assert.Contains($"initialized={initialized}", error.Message);
            Assert.Contains(selected ? "current_selected=[64x32/" : "current_selected=[absent]", error.Message);
            Assert.Contains("current_producer_known=False", error.Message);
        }
        finally
        {
            if (hadPrevious) images[Address] = previous;
            else images.Remove(Address);
        }
    }

    private static GuestDrawTexture Texture() => new(Address, 64, 32, 10, 0, [], false, false,
        MipLevels: 2, MipLevel: 1, BaseMipLevel: 1, ResourceMipLevels: 4, Pitch: 64, TileMode: 24);

    [Fact]
    public void RecoveryDoesNotWrapFatalOrAllocationFailure()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        foreach (var expected in new Exception[] { new GpuFatalException("existing fatal"), new OutOfMemoryException("allocation failure") })
        {
            using var memory = new GuestMemoryState(new ThrowingMemory(expected));
            var linear = Texture() with { TileMode = 0, MipLevel = 0, BaseMipLevel = 0 };
            var actual = Record.Exception(() => MetalVideoPresenter.CreateDrawTexture(0, 0, linear, out _));
            Assert.Same(expected, actual);
        }
    }

    [Theory]
    [InlineData(false, 3u, 4u, 2u, "native-type")]
    [InlineData(true, 3u, 4u, 2u, "native-type")]
    [InlineData(true, 2u, 4u, 2u, "native-depth")]
    [InlineData(true, 2u, 1u, 2u, "native-layers")]
    public void ActualSampledRefusalKeepsFirstShapePredicateAndDefaultOffPolicy(
        bool enabled, uint type, uint depth, uint layers, string reason)
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        using var diagnostic = new AliasDiagnosticState(enabled);
        var memory = new NoReadMemory();
        using var attached = new GuestMemoryState(memory);
        using var image = new AliasImage(1, 1, type, depth, layers);
        var error = Reject(SingletonTexture());
        Assert.Contains("tiled or mip-range snapshot required", error.Message);
        Assert.Contains("current_selected=[absent]", error.Message);
        if (enabled)
        {
            Assert.Contains("sampled_alias_first_refusal=" + reason, error.Message);
            Assert.Contains($"candidate_native_type={type} candidate_depth={depth} candidate_layers={layers}", error.Message);
            Assert.Contains("candidate_original_present=False", error.Message);
        }
        else
        {
            Assert.DoesNotContain("sampled_alias_first_refusal", error.Message);
            Assert.Equal(0, diagnostic.Count);
        }
        Assert.Equal(0, memory.Reads);
    }

    [Theory]
    [InlineData(2u, 1u, "width-outside-owner")]
    [InlineData(1u, 2u, "height-outside-owner")]
    public void ActualSampledRefusalKeepsFirstTiledExtentPredicate(uint width, uint height, string reason)
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        using var diagnostic = new AliasDiagnosticState(true);
        using var attached = new GuestMemoryState(new NoReadMemory());
        using var image = new AliasImage(1, 1, 2, 1, 1);
        var error = Reject(SingletonTexture() with { Width = width, Height = height, Pitch = width });
        Assert.Contains("sampled_alias_first_refusal=" + reason, error.Message);
        Assert.Contains("current_selected=[absent]", error.Message);
    }

    [Fact]
    public void FirstMatcherSnapshotIncludesActualCachedCreationContextWithoutReadingProducerBytes()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        using var diagnostic = new AliasDiagnosticState(true);
        var memory = new NoReadMemory();
        using var attached = new GuestMemoryState(memory);
        using var image = new AliasImage(1, 1, 3, 1, 1);
        uint[] words = [0x80115200, 0x03800000, 0, 0xD1B00F2E, 0, 0x00700000, 0, 0];
        var producer = SingletonTexture() with { IsStorage = true, Type = 13, SourceByteCount = 65536, Descriptor = words };
        long sequence;
        using (MetalVideoPresenter.EnterGuestQueue("oregon.original.producer", 6))
            sequence = MetalVideoPresenter.SubmitOrderedGuestAction(() =>
            {
                var cached = typeof(MetalVideoPresenter).GetMethod("CaptureStorageImageOrigin", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [producer]);
                image.Set("DiagnosticOrigin", cached);
            }, "capture ordinary diagnostic context");
        MetalVideoPresenter.DrainGuestWork(0, 0);
        var error = Reject(SingletonTexture());
        Assert.Contains($"candidate_creation_kind=storage candidate_creation_sequence={sequence}", error.Message);
        Assert.Contains("candidate_creation_queue='oregon.original.producer' candidate_creation_submission=6", error.Message);
        Assert.Contains("candidate_creation_type=13", error.Message);
        Assert.Contains("candidate_creation_source_bytes=65536", error.Message);
        Assert.Contains("candidate_creation_raw=[80115200,03800000,00000000,D1B00F2E,00000000,00700000,00000000,00000000]", error.Message);
        Assert.Contains("candidate_metadata_observation_only=True", error.Message);
        Assert.Equal(0, memory.Reads);
    }

    [Fact]
    public void FirstMatcherSnapshotSaturatesBeforeOptionalFormatting()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        using var diagnostic = new AliasDiagnosticState(true, 63);
        using var attached = new GuestMemoryState(new NoReadMemory());
        using var image = new AliasImage(1, 1, 3, 1, 1);
        Assert.Contains("sampled_alias_first_refusal=native-type", Reject(SingletonTexture()).Message);
        Assert.Equal(64, diagnostic.Count);
        Assert.DoesNotContain("sampled_alias_first_refusal", Reject(SingletonTexture()).Message);
        Assert.Equal(64, diagnostic.Count);
    }

    private static GuestDrawTexture SingletonTexture() => new(Address, 1, 1, 10, 0, [], false, false,
        Type: 9, Pitch: 1, TileMode: 27, ResourceMipLevels: 1);

    private sealed class AliasImage : IDisposable
    {
        private readonly IDictionary images = Dictionary("_guestImages");
        private readonly object? previous;
        private readonly bool hadPrevious;
        private readonly object image;
        internal AliasImage(uint width, uint height, uint type, uint depth, uint layers)
        {
            hadPrevious = images.Contains(Address); previous = images[Address];
            image = Activator.CreateInstance(typeof(MetalVideoPresenter).GetNestedType("GuestImage", BindingFlags.NonPublic)!, true)!;
            Set("Width", width); Set("Height", height); Set("TextureType", type); Set("Depth", depth);
            Set("ArrayLength", layers); Set("Initialized", true); Set("GpuWritten", true); Set("ContentVersion", 2);
            images[Address] = image; // Scalar failure metadata only; no native handle or ownership claim.
        }
        internal void Set(string name, object? value) => image.GetType().GetField(name)!.SetValue(image, value);
        public void Dispose() { if (hadPrevious) images[Address] = previous; else images.Remove(Address); }
    }
    private sealed class AliasDiagnosticState : IDisposable
    {
        private readonly Dictionary<FieldInfo, object?> saved = new();
        internal AliasDiagnosticState(bool enabled, int count = 0)
        {
            Set("_sampledAliasDiagnosticsEnabled", enabled); Set("_sampledAliasDiagnosticCount", count);
            Set("_imageProvenanceEnabled", true);
        }
        private void Set(string name, object value)
        {
            var field = typeof(MetalVideoPresenter).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
            saved.Add(field, field.GetValue(null)); field.SetValue(null, value);
        }
        internal int Count => (int)typeof(MetalVideoPresenter).GetField("_sampledAliasDiagnosticCount", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        public void Dispose() { foreach (var entry in saved) entry.Key.SetValue(null, entry.Value); }
    }
    private sealed class NoReadMemory : ICpuMemory
    {
        internal int Reads;
        public bool TryRead(ulong address, Span<byte> destination) { Reads++; throw new InvalidOperationException("Alias failure must not read producer bytes."); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => throw new InvalidOperationException("No alias write allowed.");
    }

    private static NotSupportedException Reject(GuestDrawTexture texture)
    {
        return Assert.Throws<NotSupportedException>(() =>
            MetalVideoPresenter.CreateDrawTexture(0, 0, texture, out _));
    }

    private static IDictionary Dictionary(string name) => (IDictionary)typeof(MetalVideoPresenter)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private sealed class GuestMemoryState : IDisposable
    {
        private readonly FieldInfo field = typeof(MetalVideoPresenter)
            .GetField("_guestMemory", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? previous;
        public GuestMemoryState(ICpuMemory? memory = null)
        {
            previous = field.GetValue(null);
            MetalVideoPresenter.AttachGuestMemory(memory ?? new FakeCpuMemory(Address, 8192));
        }
        public void Dispose() => field.SetValue(null, previous);
    }

    private sealed class ThrowingMemory(Exception exception) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination) => throw exception;
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => throw exception;
    }
}
