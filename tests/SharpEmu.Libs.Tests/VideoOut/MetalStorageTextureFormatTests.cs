// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalStorageTextureFormatTests
{
    [Theory]
    [InlineData(2u, 7u, (uint)MtlPixelFormat.R16Float, 2u)]
    [InlineData(2u, 4u, (uint)MtlPixelFormat.R16Uint, 2u)]
    [InlineData(11u, 4u, (uint)MtlPixelFormat.Rg32Uint, 8u)]
    [InlineData(11u, 5u, (uint)MtlPixelFormat.Rg32Sint, 8u)]
    [InlineData(14u, 4u, (uint)MtlPixelFormat.Rgba32Uint, 16u)]
    [InlineData(14u, 5u, (uint)MtlPixelFormat.Rgba32Sint, 16u)]
    public void ProductionStorageLayoutUsesTextureDescriptorTexelWidth(uint format, uint number, uint expected, uint bytes)
    {
        // Width3, pitch5, height2: both native rows must be present; the
        // unused padding after the final row need not be copied.
        var descriptor = Texture(format, number, checked((int)(8 * bytes)));
        var native = MetalVideoPresenter.StorageTextureFormat(descriptor);
        Assert.Equal(expected, (uint)native.Format);
        Assert.Equal(bytes, native.BytesPerPixel);
        Assert.Equal((5u, (ulong)(8 * bytes)), MetalVideoPresenter.StorageTextureSeedLayout(descriptor, native));
        Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.StorageTextureSeedLayout(
            descriptor with { RgbaPixels = new byte[8 * bytes - 1] }, native));
    }

    [Theory]
    [InlineData(13u, 7u)] // RGB32 requires 12-to-16 byte repack; never reinterpret.
    [InlineData(169u, 0u)] // BC1 is sampled-only, not a storage image.
    [InlineData(999u, 0u)]
    public void UnsupportedStorageFormatsNeverSubstituteRgba8(uint format, uint number)
    {
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(Texture(format, number, 64)));
        var failure = Assert.Throws<TargetInvocationException>(() => Ensure(Texture(format, number, 64)));
        Assert.IsType<NotSupportedException>(failure.InnerException);
    }

    [Fact]
    public void PartialOrUnstableStorageSeedCannotMarkAnImageInitialized()
    {
        var descriptor = Texture(10, 0, 32);
        var native = MetalVideoPresenter.StorageTextureFormat(descriptor);
        Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.StorageTextureSeedLayout(
            descriptor with { CpuSnapshotStable = false }, native));
        // All-zero complete data has the same row ownership as any other pixels.
        Assert.Equal((5u, 32UL), MetalVideoPresenter.StorageTextureSeedLayout(descriptor, native));
    }

    [Fact]
    public void TypedResourcesCannotBeMaterializedAsAnUnrelatedTwoDimensionalImage()
    {
        var descriptor = Texture(10, 0, 32);
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(descriptor with { Type = 10, Depth = 2 }));
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(descriptor with { ArrayedView = true }));
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(descriptor with { MipLevels = 2 }));
    }

    [Fact]
    public void ProductionMaterializerRejectsMissingOrPartialSeedBeforeNativeAllocation()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        foreach (var descriptor in new[] { Texture(10, 0, 0), Texture(2, 7, 15) })
        {
            var failure = Assert.Throws<TargetInvocationException>(() => Ensure(descriptor));
            Assert.IsType<InvalidOperationException>(failure.InnerException);
        }
    }

    [Theory]
    [InlineData(0u, 2u)]
    [InlineData(3u, 0u)]
    [InlineData(16385u, 2u)]
    public void RealInvalidExtentThrowsInsteadOfBindingANullNativeImage(uint width, uint height)
    {
        var failure = Assert.Throws<TargetInvocationException>(() => Ensure(Texture(10, 0, 0) with { Width = width, Height = height }));
        Assert.IsType<NotSupportedException>(failure.InnerException);
        Assert.Contains($"{width}x{height}", failure.InnerException!.Message);
    }

    [Fact]
    public void LiveStorageViewMustMatchActualNativeFormatExtentAndInitializedState()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        var images = (IDictionary)typeof(MetalVideoPresenter).GetField("_guestImages", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var imageType = typeof(MetalVideoPresenter).GetNestedType("GuestImage", BindingFlags.NonPublic)!;
        var image = Activator.CreateInstance(imageType, nonPublic: true)!;
        var descriptor = Texture(2, 7, 0);
        imageType.GetField("Width")!.SetValue(image, 3u);
        imageType.GetField("Height")!.SetValue(image, 2u);
        imageType.GetField("Format")!.SetValue(image, MtlPixelFormat.R16Float);
        images.Add(descriptor.Address, image);
        try
        {
            Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => Ensure(descriptor)).InnerException);
            imageType.GetField("Initialized")!.SetValue(image, true);
            Assert.Same(image, Ensure(descriptor));
            Assert.IsType<NotSupportedException>(Assert.Throws<TargetInvocationException>(() => Ensure(descriptor with { Format = 10, NumberType = 0 })).InnerException);
            Assert.IsType<NotSupportedException>(Assert.Throws<TargetInvocationException>(() => Ensure(descriptor with { Width = 4 })).InnerException);
        }
        finally { images.Remove(descriptor.Address); }
    }

    [Fact]
    public void ActualStorageSnapshotKeepsOnlySelectedBaseMipWhileResourceHasMoreLevels()
    {
        const ulong address = 0x71F2_1000_00;
        var backendField = typeof(GuestGpu).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = backendField.GetValue(null);
        using var state = new MetalGuestFailureTests.PresenterState();
        backendField.SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(MetalGuestGpuBackend)));
        try
        {
            var bytes = Enumerable.Range(0, 512).Select(static index => (byte)(index * 7 + 17)).ToArray();
            var descriptor = new AgcExports.TextureDescriptor(address, 3, 2, 2, 7, 0, 9, 0, 0, 3, 0xFAC,
                MaxMip: 1, HasExtendedDescriptor: true);
            var shape = new ShaderImageShape(false, false, true, false, TextureNumericClass.Float);
            var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [], Shape: shape, Access: GuestImageAccess.Read | GuestImageAccess.Write);
            var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
                new CpuContext(new SourceMemory(address, bytes), Generation.Gen5), [binding], out _));
            Assert.Equal(2u, snapshot.ResourceMipLevels);
            Assert.Equal(1u, snapshot.MipLevels);
            Assert.Equal(0u, snapshot.BaseMipLevel);
            Assert.Equal(0u, snapshot.MipLevel);
            Assert.Equal(128u, snapshot.Pitch);
            Assert.Equal(bytes, snapshot.RgbaPixels);
            var native = MetalVideoPresenter.StorageTextureFormat(snapshot);
            Assert.Equal(MtlPixelFormat.R16Float, native.Format);
            Assert.Equal((128u, 262UL), MetalVideoPresenter.StorageTextureSeedLayout(snapshot, native));
            Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(snapshot with { BaseMipLevel = 1 }));
            Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(snapshot with { MipLevel = 1 }));
            Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(snapshot with { Shape = shape with { DynamicMip = true } }));
        }
        finally { GuestImageWriteTracker.Untrack(address); backendField.SetValue(null, previous); }
    }

    [Fact]
    public void UnknownOrMismatchedCompiledNumericShapeCannotClaimTypedStorageSupport()
    {
        var descriptor = Texture(2, 4, 16);
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(descriptor with { Shape = default }));
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(descriptor with
        { Shape = descriptor.Shape with { NumericClass = TextureNumericClass.Float } }));
    }

    private sealed class SourceMemory(ulong address, byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong target, Span<byte> destination)
        {
            if (target < address || target - address > (ulong)bytes.Length || (ulong)destination.Length > (ulong)bytes.Length - (target - address)) return false;
            bytes.AsSpan(checked((int)(target - address)), destination.Length).CopyTo(destination);
            return true;
        }
        public bool TryWrite(ulong target, ReadOnlySpan<byte> source) => false;
    }

    private static object? Ensure(GuestDrawTexture descriptor) => typeof(MetalVideoPresenter)
        .GetMethod("EnsureStorageImage", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [(nint)0, descriptor]);

    private static GuestDrawTexture Texture(uint format, uint number, int bytes) =>
        new(0x71F2_0000_00, 3, 2, format, number, new byte[bytes], false, true, Pitch: 5, Access: GuestImageAccess.Write,
            Shape: new ShaderImageShape(false, false, true, false,
                number == 4 ? TextureNumericClass.Uint : number == 5 ? TextureNumericClass.Sint : TextureNumericClass.Float));
}
