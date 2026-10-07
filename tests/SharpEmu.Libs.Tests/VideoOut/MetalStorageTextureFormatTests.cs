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

    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    public void VolumeSeedRequiresEveryPaddedDepthSliceAndRetainsNativeType(uint depth)
    {
        var descriptor = Volume(depth, checked((int)(5 * 2 * depth * 4)));
        var native = MetalVideoPresenter.StorageTextureFormat(descriptor);
        Assert.Equal(MtlPixelFormat.Rgba8Uint, native.Format);
        Assert.Equal((7u, 1u, depth), MetalVideoPresenter.GuestTextureShape(descriptor));
        var layout = MetalVideoPresenter.StorageTextureSeedLayout(descriptor, native);
        Assert.Equal((5u, 40UL * depth), layout);
        Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.StorageTextureSeedLayout(
            descriptor with { RgbaPixels = new byte[checked((int)layout.RequiredBytes - 1)] }, native));
        Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.StorageTextureSeedLayout(
            descriptor with { CpuSnapshotStable = false }, native));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2049u)]
    public void VolumeNativeExtentIsBoundedBeforeAllocation(uint depth)
    {
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(Volume(depth, 0)));
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(Volume(1, 0) with { Width = 2049 }));
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(Volume(1, 0) with { Height = 2049 }));
    }

    [Fact]
    public void TiledVolumeDepthAndUnknownEquationNeverClaimTwoDimensionalSliceSupport()
    {
        Assert.Equal(MtlPixelFormat.Rgba8Uint,
            MetalVideoPresenter.StorageTextureFormat(Volume(1, 4) with { Width = 1, Height = 1, Pitch = 1, TileMode = 27, Descriptor = VolumeRaw(0x71F2000000, 1, 1, 1, 27) }).Format);
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(Volume(1, 40) with { TileMode = 27, Descriptor = VolumeRaw(0x71F2000000, 3, 2, 1, 27) })); // WxHx1 is not a 3D-equation proof
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(Volume(3, 120) with { TileMode = 27, Descriptor = VolumeRaw(0x71F2000000, 3, 2, 3, 27) }));
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(Volume(1, 4) with
        { Width = 1, Height = 1, Pitch = 1, TileMode = 27, Address = 0x71F2000100, Descriptor = VolumeRaw(0x71F2000100, 1, 1, 1, 27) })); // possible embedded bank/pipe XOR
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(Volume(1, 40) with { TileMode = 6, Descriptor = VolumeRaw(0x71F2000000, 3, 2, 1, 6) }));
        Assert.Throws<OverflowException>(() => MetalVideoPresenter.StorageTextureSeedLayout(
            Volume(uint.MaxValue, 0) with { Height = uint.MaxValue, Pitch = uint.MaxValue },
            MetalVideoPresenter.StorageTextureFormat(Volume(1, 40))));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(0x100UL)]
    [InlineData(0xAB00UL)]
    public void VolumeDescriptorDecoderRetainsEmbeddedPipeBankXorAddressBits(ulong xorBits)
    {
        const ulong aligned = 0x90073C0000;
        var address = aligned | xorBits;
        uint[] words = [(uint)(address >> 8), (uint)(address >> 40) | (60u << 20), 0, (10u << 28) | (27u << 20) | 0xFAC, 0, 0, 0, 0];
        object?[] args = [words, null];
        Assert.True((bool)typeof(AgcExports).GetMethod("TryDecodeTextureDescriptor", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!);
        var descriptor = Assert.IsType<AgcExports.TextureDescriptor>(args[1]);
        Assert.Equal(address, descriptor.Address);
        Assert.Equal(xorBits, descriptor.Address & 0xFFFFUL);
        Assert.Equal(27u, descriptor.TileMode);
        Assert.Equal(1u, descriptor.ResourceMipLevels);
        Assert.Equal(0UL, descriptor.MetadataAddress);
        Assert.Equal(0u, descriptor.DescriptorFlags);
    }

    [Fact]
    public void VolumeStorageRejectsUnknownMetadataAndIncompleteRawDescriptors()
    {
        foreach (var raw in new uint[][] { [], new uint[7] })
            Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(Volume(1, 40) with { Descriptor = raw }));
        foreach (var (word, flag) in new[] { (6, 1u), (6, 1u << 24), (7, 1u), (3, 1u << 25) })
        {
            var raw = VolumeRaw(0x71F2000000, 3, 2, 1, 0);
            raw[word] |= flag;
            Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out _)); // reach the metadata check
            Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(Volume(1, 40) with { Descriptor = raw }));
        }
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(Volume(1, 40) with { Descriptor = null }));
        Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(Volume(1, 40) with { TiledSource = new byte[40] }));
    }

    [Fact]
    public void LiveVolumeViewCannotReuseTwoDimensionalOrDifferentDepthImages()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        var images = (IDictionary)typeof(MetalVideoPresenter).GetField("_guestImages", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var imageType = typeof(MetalVideoPresenter).GetNestedType("GuestImage", BindingFlags.NonPublic)!;
        var image = Activator.CreateInstance(imageType, nonPublic: true)!;
        var descriptor = Volume(3, 0);
        imageType.GetField("Width")!.SetValue(image, descriptor.Width);
        imageType.GetField("Height")!.SetValue(image, descriptor.Height);
        imageType.GetField("Format")!.SetValue(image, MtlPixelFormat.Rgba8Uint);
        imageType.GetField("Initialized")!.SetValue(image, true);
        images.Add(descriptor.Address, image);
        try
        {
            Assert.IsType<NotSupportedException>(Assert.Throws<TargetInvocationException>(() => Ensure(descriptor)).InnerException);
            imageType.GetField("TextureType")!.SetValue(image, 7u);
            imageType.GetField("Depth")!.SetValue(image, 3u);
            Assert.Same(image, Ensure(descriptor));
            Assert.IsType<NotSupportedException>(Assert.Throws<TargetInvocationException>(() => Ensure(descriptor with { Depth = 1, Descriptor = VolumeRaw(descriptor.Address, 3, 2, 1, 0) })).InnerException);
            var guard = typeof(MetalVideoPresenter).GetMethod("Require2DGuestImage", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.IsType<NotSupportedException>(Assert.Throws<TargetInvocationException>(() => guard.Invoke(null, [image, "fixture"])).InnerException);
        }
        finally { images.Remove(descriptor.Address); }
    }

    [Theory]
    [InlineData(1u, 6u)] // unknown/untrusted tiled equation cannot become raw seed
    [InlineData(2u, 27u)] // no Z-aware equation: reject before reading slices
    public void StorageSnapshotRejectsUnqualifiedVolumeTiling(uint depth, uint tile)
    {
        const ulong address = 0x71F2_2000_00;
        var backendField = typeof(GuestGpu).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = backendField.GetValue(null);
        using var state = new MetalGuestFailureTests.PresenterState();
        backendField.SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(MetalGuestGpuBackend)));
        try
        {
            var descriptor = new AgcExports.TextureDescriptor(address, 1, 1, 10, 4, tile, 10, 0, 0, 1, 0xFAC, Depth: depth, HasExtendedDescriptor: true);
            var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [],
                Shape: new ShaderImageShape(true, false, true, false, TextureNumericClass.Uint), Access: GuestImageAccess.Write);
            Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
                new CpuContext(new SourceMemory(address, new byte[131072]), Generation.Gen5), [binding], out _));
        }
        finally { GuestImageWriteTracker.Untrack(address); backendField.SetValue(null, previous); }
    }

    [Fact]
    public void VolumeSnapshotRejectsMetadataUnknownFlagsAndBcSwizzleBeforeReadingMemory()
    {
        const ulong address = 0x71F2_2000_00;
        var baseline = new AgcExports.TextureDescriptor(address, 1, 1, 10, 4, 27, 10, 0, 0, 1, 0xFAC, Depth: 1, HasExtendedDescriptor: true);
        var memory = new SourceMemory(address, new byte[65536]);
        foreach (var descriptor in new[] { baseline with { MetadataAddress = 0x10000 }, baseline with { DescriptorFlags = 1 },
            baseline with { BcSwizzle = 1 }, baseline with { HasExtendedDescriptor = false } })
        {
            var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [], ResourceDescriptor: VolumeRaw(address, 1, 1, 1, 27),
                Shape: new ShaderImageShape(true, false, true, false, TextureNumericClass.Uint), Access: GuestImageAccess.Write);
            Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
                new CpuContext(memory, Generation.Gen5), [binding], out _));
        }
        Assert.Equal(0, memory.Reads);
    }

    [Fact]
    public void ExactMode5VolumeMatchesEveryPrimaryAddrLibCoordinateAndCompleteSeed()
    {
        var raw = SharpEmu.Tests.Fixtures.Tile5VolumeOracle.Raw;
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        Assert.Equal((2u, 4u, 16u, 16u, 16u, 5u, 4u),
            (descriptor.Format, descriptor.NumberType, descriptor.Width, descriptor.Height, descriptor.Depth, descriptor.TileMode, descriptor.DstSelect));
        Assert.True(Gfx10StandardVolumeLayout.IsSupported(16, 16, 16, 16, 5, 1));
        Assert.False(Gfx10StandardVolumeLayout.IsSupported(16, 16, 15, 16, 5, 1));
        Assert.False(Gfx10StandardVolumeLayout.IsSupported(16, 16, 16, 32, 5, 1));
        Assert.False(Gfx10StandardVolumeLayout.IsSupported(16, 16, 16, 16, 9, 1));
        Assert.False(Gfx10StandardVolumeLayout.IsSupported(16, 16, 16, 16, 5, 2));
        var offsets = SharpEmu.Tests.Fixtures.Tile5VolumeOracle.Offsets();
        for (var z = 0; z < 16; z++)
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
            Assert.Equal(offsets[(z * 16 + y) * 16 + x], Gfx10StandardVolumeLayout.TexelOffset16Cube(x, y, z));
        Assert.Equal(4096, offsets.Distinct().Count());
        var (tiled, linear) = SharpEmu.Tests.Fixtures.Tile5VolumeOracle.Seed();
        Assert.Equal(linear, Gfx10StandardVolumeLayout.Decode16Cube(tiled));
        Assert.Throws<ArgumentException>(() => Gfx10StandardVolumeLayout.Decode16Cube(tiled.AsSpan(0, 8191)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gfx10StandardVolumeLayout.TexelOffset16Cube(16, 0, 0));
        var memory = new SourceMemory(descriptor.Address, tiled);
        var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [], ResourceDescriptor: raw,
            Shape: new ShaderImageShape(true, false, true, false, TextureNumericClass.Uint), Access: GuestImageAccess.Write);
        var backendField = typeof(GuestGpu).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = backendField.GetValue(null);
        using var state = new MetalGuestFailureTests.PresenterState();
        backendField.SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(MetalGuestGpuBackend)));
        try
        {
            var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
                new CpuContext(memory, Generation.Gen5), [binding], out _));
            Assert.Equal(1, memory.Reads);
            Assert.Equal(linear, snapshot.RgbaPixels);
            Assert.Equal(16u, snapshot.Pitch);
            var native = MetalVideoPresenter.StorageTextureFormat(snapshot);
            Assert.Equal(MtlPixelFormat.R16Uint, native.Format);
            Assert.Equal((16u, 8192UL), MetalVideoPresenter.StorageTextureSeedLayout(snapshot, native));
            Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.StorageTextureFormat(snapshot with { Pitch = 17 }));
            Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.StorageTextureSeedLayout(snapshot with { RgbaPixels = new byte[8191] }, native));
        }
        finally { GuestImageWriteTracker.Untrack(descriptor.Address); backendField.SetValue(null, previous); }
    }

    [Fact]
    public void NearMissMode5VolumesRejectBeforeAnyGuestRead()
    {
        var baseline = SharpEmu.Tests.Fixtures.Tile5VolumeOracle.Raw;
        var memory = new SourceMemory(SharpEmu.Tests.Fixtures.Tile5VolumeOracle.Address, new byte[131072]);
        var variants = new List<uint[]>();
        foreach (var (word, mask) in new[] { (0, 1u), (1, 1u << 20), (2, 1u), (2, 1u << 14), (3, 1u),
            (3, 1u << 12), (3, 1u << 16), (3, 1u << 25), (4, 1u), (4, 1u << 16), (5, 1u << 4), (5, 1u << 20), (6, 1u), (6, 1u << 24), (7, 1u) })
        {
            var raw = (uint[])baseline.Clone(); raw[word] ^= mask; variants.Add(raw);
        }
        foreach (var raw in variants)
        {
            Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
            var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [], ResourceDescriptor: raw,
                Shape: new ShaderImageShape(true, false, true, false, TextureNumericClass.Uint), Access: GuestImageAccess.Write);
            Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
                new CpuContext(memory, Generation.Gen5), [binding], out _));
        }
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(baseline, out var valid));
        foreach (IReadOnlyList<uint>? raw in new IReadOnlyList<uint>?[] { null, new uint[7], variants[0] })
        {
            var binding = new AgcExports.TranslatedImageBinding(valid, true, 0, [], ResourceDescriptor: raw,
                Shape: new ShaderImageShape(true, false, true, false, TextureNumericClass.Uint), Access: GuestImageAccess.Write);
            Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
                new CpuContext(memory, Generation.Gen5), [binding], out _));
        }
        var acceptedBinding = new AgcExports.TranslatedImageBinding(valid, true, 0, [], ResourceDescriptor: baseline,
            Shape: new ShaderImageShape(true, false, true, false, TextureNumericClass.Uint), Access: GuestImageAccess.Write);
        foreach (var binding in new[] { acceptedBinding with { MipLevel = 1 }, acceptedBinding with { IsArrayed = true },
            acceptedBinding with { Shape = acceptedBinding.Shape with { NumericClass = TextureNumericClass.Float } },
            acceptedBinding with { Shape = acceptedBinding.Shape with { Atomic = true } } })
            Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
                new CpuContext(memory, Generation.Gen5), [binding], out _));
        Assert.Equal(0, memory.Reads);
    }

    private static GuestDrawTexture Volume(uint depth, int bytes) => Texture(10, 4, bytes) with
    { Type = 10, Depth = depth, Descriptor = VolumeRaw(0x71F2000000, 3, 2, depth, 0), Shape = new ShaderImageShape(true, false, true, false, TextureNumericClass.Uint) };

    private static uint[] VolumeRaw(ulong address, uint width, uint height, uint depth, uint tile) =>
        [(uint)(address >> 8), (uint)(address >> 40) | (60u << 20) | (((width - 1) & 3) << 30),
         ((width - 1) >> 2) | ((height - 1) << 14), (10u << 28) | (tile << 20) | 0xFAC, depth - 1, 0, 0, 0];

    private sealed class SourceMemory(ulong address, byte[] bytes) : ICpuMemory
    {
        public int Reads { get; private set; }
        public bool TryRead(ulong target, Span<byte> destination)
        {
            Reads++;
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
