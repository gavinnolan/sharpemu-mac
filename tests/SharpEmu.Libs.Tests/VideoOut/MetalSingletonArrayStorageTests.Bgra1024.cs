// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Tests.Fixtures;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalSingletonArrayStorageTests
{
    private static uint[] Bgra1024Raw(uint address = 0x80102E00) =>
        [address, 0xC3800000, 0x00FFC0FF, 0xD1B00F2E, 0, 0x00700000, 0, 0];

    [Fact]
    public void Bgra1024AllProductionOffsetsAndDetiledBytesMatchIndependentPalTopology()
    {
        var expected = Bgra1024Oracle.Offsets(); var observed = new byte[expected.Length];
        var parameters = GnmTiling.GetDetileParams(27, 4, 1024, 1024);
        Assert.Equal((128, 128, 65536, 8), (parameters.BlockWidth, parameters.BlockHeight, parameters.BlockBytes, parameters.BlocksPerRow));
        for (var y = 0; y < 1024; y++) for (var x = 0; x < 1024; x++)
        {
            var offset = ((y / 128) * 8 + x / 128) * 65536 +
                (parameters.XByteTerm[x & parameters.XMask] ^ parameters.YByteTerm[y & parameters.YMask]);
            BinaryPrimitives.WriteUInt32LittleEndian(observed.AsSpan((y * 1024 + x) * 4), (uint)offset);
        }
        Assert.Equal(Bgra1024Oracle.OffsetSha256, Convert.ToHexString(SHA256.HashData(observed)));
        Assert.Equal(expected, observed);
        var source = Bgra1024Oracle.PhysicalSeed(); var linear = new byte[source.Length]; var parameterLinear = new byte[source.Length];
        Assert.True(GnmTiling.TryDetile(source, linear, 27, 1024, 1024, 4));
        Assert.True(GnmTiling.DetileWithParams(parameters, source, parameterLinear));
        Assert.Equal(Bgra1024Oracle.LinearSeed(), linear); Assert.Equal(linear, parameterLinear);
    }

    [Theory]
    [InlineData(0x80102E00u)] // SH captured words
    [InlineData(0x80303000u)] // Octopath
    [InlineData(0x801C6F00u)] // Oregon
    public void CapturedBgra1024ArrayOwnsFullIndependentFourMiBSeed(uint addressWord)
    {
        using var state = new State(); var raw = Bgra1024Raw(addressWord); var binding = Binding(raw);
        var source = Bgra1024Oracle.PhysicalSeed(); var memory = new Memory(source, binding.Descriptor.Address);
        try
        {
            var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out var fallbacks));
            Assert.Equal(0, fallbacks); Assert.Equal(1, memory.Reads); Assert.Equal(Bgra1024Oracle.ByteCount, memory.LastReadBytes);
            Assert.Equal(Bgra1024Oracle.LinearSeed(), snapshot.RgbaPixels); Assert.Equal(4194304UL, snapshot.SourceByteCount);
            Assert.Equal(raw, snapshot.Descriptor); Assert.NotSame(raw, snapshot.Descriptor);
            Assert.Equal((3u, 1u, 1u), MetalVideoPresenter.GuestTextureShape(snapshot));
            var format = MetalVideoPresenter.StorageTextureFormat(snapshot);
            Assert.Equal(MtlPixelFormat.Rgba8Unorm, format.Format); Assert.Equal(4u, format.BytesPerPixel);
            Assert.Equal((1024u, 4194304UL), MetalVideoPresenter.StorageTextureSeedLayout(snapshot, format));
            source.AsSpan().Fill(0x33); raw[5] ^= 1;
            Assert.Equal(Bgra1024Oracle.LinearSeed(), snapshot.RgbaPixels); Assert.Equal(0x00700000u, snapshot.Descriptor![5]);
            Assert.Throws<InvalidOperationException>(() => Ensure(snapshot with { RgbaPixels = snapshot.RgbaPixels[..^4] }));
            Assert.Throws<InvalidOperationException>(() => Ensure(snapshot with { CpuSnapshotStable = false }));
            Assert.Throws<NotSupportedException>(() => Ensure(snapshot with { ArrayLayers = 2 }));
            Assert.Throws<NotSupportedException>(() => Ensure(snapshot with { SourceByteCount = 65536 }));
            Assert.Throws<NotSupportedException>(() => Ensure(snapshot with { Type = 9, ArrayedView = false }));
        }
        finally { GuestImageWriteTracker.Untrack(binding.Descriptor.Address); }
    }

    [Fact]
    public void Bgra1024ShortPhysicalReadCannotBecomeAZeroInitializedNativeImage()
    {
        using var state = new State(); var binding = Binding(Bgra1024Raw());
        var memory = new Memory(new byte[Bgra1024Oracle.ByteCount - 4], binding.Descriptor.Address);
        try
        {
            var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
            Assert.Empty(snapshot.RgbaPixels); Assert.Equal(1, memory.Reads); Assert.Equal(Bgra1024Oracle.ByteCount, memory.LastReadBytes);
            Assert.Throws<InvalidOperationException>(() => Ensure(snapshot));
        }
        finally { GuestImageWriteTracker.Untrack(binding.Descriptor.Address); }
    }

    [Fact]
    public void Bgra1024SuccessfulAllZeroPhysicalReadStillOwnsEveryTexel()
    {
        using var state = new State(); var binding = Binding(Bgra1024Raw());
        var memory = new Memory(new byte[Bgra1024Oracle.ByteCount], binding.Descriptor.Address);
        try
        {
            var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out var fallbacks));
            Assert.Equal(0, fallbacks); Assert.Equal(1, memory.Reads);
            Assert.Equal(Bgra1024Oracle.ByteCount, snapshot.RgbaPixels.Length);
            Assert.Equal(-1, snapshot.RgbaPixels.AsSpan().IndexOfAnyExcept((byte)0));
            Assert.Equal((1024u, 4194304UL), MetalVideoPresenter.StorageTextureSeedLayout(snapshot, MetalVideoPresenter.StorageTextureFormat(snapshot)));
        }
        finally { GuestImageWriteTracker.Untrack(binding.Descriptor.Address); }
    }

    [Fact]
    public void Bgra1024CannotReuseAnInitializedDifferentExtentOrNativeArrayShape()
    {
        using var state = new State(); var binding = Binding(Bgra1024Raw());
        var images = (System.Collections.IDictionary)typeof(MetalVideoPresenter).GetField("_guestImages", Private)!.GetValue(null)!;
        var type = typeof(MetalVideoPresenter).GetNestedType("GuestImage", System.Reflection.BindingFlags.NonPublic)!;
        var image = Activator.CreateInstance(type, nonPublic: true)!;
        var memory = new Memory(new byte[Bgra1024Oracle.ByteCount], binding.Descriptor.Address);
        try
        {
            var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
            foreach (var (field, value) in new (string, object)[] { ("Width", 512u), ("Height", 1024u),
                ("Format", MtlPixelFormat.Rgba8Unorm), ("Initialized", true), ("TextureType", 3u), ("ArrayLength", 1u), ("Depth", 1u) })
                type.GetField(field)!.SetValue(image, value);
            images.Add(binding.Descriptor.Address, image);
            Assert.Throws<NotSupportedException>(() => Ensure(snapshot));
            type.GetField("Width")!.SetValue(image, 1024u); type.GetField("TextureType")!.SetValue(image, 2u);
            Assert.Throws<NotSupportedException>(() => Ensure(snapshot));
            type.GetField("TextureType")!.SetValue(image, 3u); type.GetField("ArrayLength")!.SetValue(image, 2u);
            Assert.Throws<NotSupportedException>(() => Ensure(snapshot));
            type.GetField("ArrayLength")!.SetValue(image, 1u); type.GetField("Initialized")!.SetValue(image, false);
            Assert.Throws<InvalidOperationException>(() => Ensure(snapshot));
            type.GetField("Initialized")!.SetValue(image, true); Assert.Same(image, Ensure(snapshot));
        }
        finally { images.Remove(binding.Descriptor.Address); GuestImageWriteTracker.Untrack(binding.Descriptor.Address); }
    }

    [Theory]
    [InlineData(0, 1u)] [InlineData(1, 1u << 8)] [InlineData(1, 1u << 20)]
    [InlineData(2, 1u)] [InlineData(2, 1u << 14)] [InlineData(2, 1u << 28)]
    [InlineData(3, 1u)] [InlineData(3, 1u << 12)] [InlineData(3, 1u << 16)]
    [InlineData(3, 1u << 20)]
    [InlineData(4, 1u)] [InlineData(4, 1u << 16)] [InlineData(5, 1u)]
    [InlineData(5, 1u << 4)] [InlineData(5, 1u << 20)] [InlineData(5, 1u << 23)]
    [InlineData(6, 1u << 21)] [InlineData(7, 1u)]
    public void Bgra1024RawNearMissRejectsBeforeReading(int word, uint mask)
    {
        using var state = new State(); var raw = Bgra1024Raw(); raw[word] ^= mask;
        var binding = Binding(raw); var memory = new Memory(new byte[Bgra1024Oracle.ByteCount], binding.Descriptor.Address);
        Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
        Assert.Equal(0, memory.Reads);
    }

    [Fact]
    public void Bgra1024CompiledClassAndDescriptorCopyStayStrictBeforeReads()
    {
        using var state = new State(); var binding = Binding(Bgra1024Raw()); var memory = new Memory(new byte[Bgra1024Oracle.ByteCount], binding.Descriptor.Address);
        foreach (var invalid in new[] {
            binding with { ResourceDescriptor = binding.ResourceDescriptor!.Take(4).ToArray() }, binding with { ResourceDescriptor = null },
            binding with { ResourceDescriptor = Bgra1024Raw().Select((word, index) => index == 3 ? word ^ (1u << 28) : word).ToArray() },
            binding with { Access = GuestImageAccess.Read }, binding with { Access = GuestImageAccess.Read | GuestImageAccess.Write },
            binding with { Access = GuestImageAccess.Atomic }, binding with { Shape = binding.Shape with { Arrayed = false } },
            binding with { Shape = binding.Shape with { NumericClass = SharpEmu.Libs.Gpu.Images.TextureNumericClass.Uint } },
            binding with { Shape = binding.Shape with { Volume = true } }, binding with { Shape = binding.Shape with { Atomic = true } },
            binding with { MipLevel = 1 }, binding with { Descriptor = binding.Descriptor with { Pitch = 1023 } } })
            Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [invalid], out _));
        Assert.Equal(0, memory.Reads);
    }
}
