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
