// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalSingletonArrayStorageTests
{
    private sealed class Rgba16FloatTrackerCleanup(ulong address) : IDisposable
    {
        public void Dispose() => GuestImageWriteTracker.Untrack(address);
    }

    private static uint[] Rgba16FloatRaw(uint addressWord = 0x80107500) =>
        [addressWord, 0x04700000, 0, 0xD1B00FAC, 0, 0x00700000, 0, 0];
    private static AgcExports.TranslatedImageBinding Rgba16FloatBinding(uint[]? raw = null)
    {
        raw ??= Rgba16FloatRaw();
        Assert.True(AgcExports.TryDecodeTextureDescriptor(raw, out var descriptor));
        const TextureNumericClass numeric = TextureNumericClass.Float;
        return new(descriptor, true, 0, [], IsArrayed: true, ResourceDescriptor: raw,
            Shape: new ShaderImageShape(false, true, true, false, numeric), Access: GuestImageAccess.Write);
    }

    [Theory]
    [InlineData(0x80107500u)] // Silent Hill
    [InlineData(0x8030B700u)] // Octopath
    [InlineData(0x80115800u)] // Oregon
    public void CanonicalRgba16FloatOwnExactDetachedBitsAndArrayLayout(uint addressWord)
    {
        using var state = new State(); var raw = Rgba16FloatRaw(addressWord); var binding = Rgba16FloatBinding(raw);
        Assert.True(Gfx10UnifiedFormat.TryDecode(71, out var formatCode, out var numberCode));
        Assert.Equal((12u, 7u), (formatCode, numberCode));
        Assert.Equal(12u, binding.Descriptor.Format); Assert.Equal(7u, binding.Descriptor.NumberType);
        var source = Enumerable.Repeat((byte)0xD3, 65536).ToArray(); byte[] seed = [0x00, 0xC0, 0x00, 0x38, 0x00, 0x42, 0x00, 0xB4]; seed.CopyTo(source, 0);
        var original = source.ToArray(); var memory = new Memory(source, binding.Descriptor.Address);
        using var tracking = new Rgba16FloatTrackerCleanup(binding.Descriptor.Address);
        var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out var fallbacks));
        Assert.Equal(0, fallbacks); Assert.Equal(1, memory.Reads); Assert.Equal(65536, memory.LastReadBytes);
        Assert.Equal(original, source); Assert.Equal(original.AsSpan(0, 8).ToArray(), snapshot.RgbaPixels);
        Assert.Equal(65536UL, snapshot.SourceByteCount); Assert.Equal(raw, snapshot.Descriptor); Assert.NotSame(raw, snapshot.Descriptor);
        Assert.Equal((3u, 1u, 1u), MetalVideoPresenter.GuestTextureShape(snapshot));
        var format = MetalVideoPresenter.StorageTextureFormat(snapshot);
        Assert.Equal(MtlPixelFormat.Rgba16Float, format.Format);
        Assert.Equal(8u, format.BytesPerPixel); Assert.Equal((1u, 8UL), MetalVideoPresenter.StorageTextureSeedLayout(snapshot, format));
        source[0] ^= 0xFF; raw[1] ^= 1u << 20;
        Assert.Equal(seed, snapshot.RgbaPixels); Assert.Equal(Rgba16FloatRaw(addressWord), snapshot.Descriptor);
        foreach (var invalid in new[] { snapshot with { RgbaPixels = [] }, snapshot with { RgbaPixels = new byte[7] },
            snapshot with { CpuSnapshotStable = false } })
            Assert.Throws<InvalidOperationException>(() => Ensure(invalid));
        Assert.Throws<NotSupportedException>(() => Ensure(snapshot with { Shape = snapshot.Shape with { NumericClass = TextureNumericClass.Uint } }));
    }

    [Fact]
    public void UnqualifiedCanonicalRgba16FloatMetadataRejectsBeforeReadingIncludingTypedAndRawContradictions()
    {
        using var state = new State();
        var binding = Rgba16FloatBinding(); var memory = new Memory(new byte[65536]);
        var invalidBindings = new List<AgcExports.TranslatedImageBinding>
        {
            binding with { Shape = binding.Shape with { NumericClass = TextureNumericClass.Uint } },
            binding with { Shape = binding.Shape with { NumericClass = TextureNumericClass.Sint } },
            binding with { Shape = binding.Shape with { Arrayed = false } },
            binding with { Shape = binding.Shape with { Atomic = true } },
            binding with { IsArrayed = false }, binding with { MipLevel = 1 },
            binding with { ResourceDescriptor = Rgba16FloatRaw()[..4] }, binding with { ResourceDescriptor = null },
            binding with { Descriptor = binding.Descriptor with { NumberType = 5u } },
            binding with { Access = GuestImageAccess.Unknown }, binding with { Access = GuestImageAccess.Atomic },
        };
        foreach (var (word, bit) in new[] { (0, 1u), (1, 1u << 8), (2, 1u), (3, 1u << 12),
            (3, 1u << 16), (4, 1u), (4, 1u << 16), (5, 1u), (5, 1u << 4), (5, 1u << 23),
            (6, 1u << 21), (7, 1u) })
        {
            var raw = Rgba16FloatRaw(); raw[word] ^= bit; invalidBindings.Add(Rgba16FloatBinding(raw));
        }
        var noPerf = Rgba16FloatRaw(); noPerf[5] = 0; invalidBindings.Add(Rgba16FloatBinding(noPerf));
        var wrongSelect = Rgba16FloatRaw(); wrongSelect[3] = 0xD1B00F2E; invalidBindings.Add(Rgba16FloatBinding(wrongSelect));
        foreach (var invalid in invalidBindings)
            Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [invalid], out _));
        Assert.Equal(0, memory.Reads);
    }

    [Fact]
    public void CanonicalRgba16FloatRegistryReuseCannotReinterpretAnotherNumericClass()
    {
        using var state = new State(); var binding = Rgba16FloatBinding();
        using var tracking = new Rgba16FloatTrackerCleanup(binding.Descriptor.Address);
        var memory = new Memory(new byte[65536], binding.Descriptor.Address);
        var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
        var images = (IDictionary)typeof(MetalVideoPresenter).GetField("_guestImages", Private)!.GetValue(null)!;
        var type = typeof(MetalVideoPresenter).GetNestedType("GuestImage", System.Reflection.BindingFlags.NonPublic)!;
        var image = Activator.CreateInstance(type, nonPublic: true)!;
        foreach (var (field, value) in new (string, object)[] { ("Width", 1u), ("Height", 1u), ("TextureType", 3u),
            ("ArrayLength", 1u), ("Depth", 1u), ("Format", MtlPixelFormat.Rgba8Unorm), ("Initialized", true) })
            type.GetField(field)!.SetValue(image, value);
        images.Add(snapshot.Address, image);
        try { Assert.Throws<NotSupportedException>(() => Ensure(snapshot)); }
        finally { images.Remove(snapshot.Address); }
    }
}
