// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
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
    private static uint[] R32FloatRaw() =>
        [(uint)(Address >> 8), (uint)(Address >> 40) | (22u << 20), 0, 0xD1B00204, 0, 0x00700000, 0, 0];
    private static AgcExports.TranslatedImageBinding R32FloatBinding(uint[]? raw = null)
    {
        raw ??= R32FloatRaw();
        Assert.True(AgcExports.TryDecodeTextureDescriptor(raw, out var descriptor));
        const TextureNumericClass numeric = TextureNumericClass.Float;
        return new(descriptor, true, 0, [], IsArrayed: true, ResourceDescriptor: raw,
            Shape: new ShaderImageShape(false, true, true, false, numeric), Access: GuestImageAccess.Write);
    }

    [Theory]
    [InlineData(22u, 7u, 0xC19A0000u)] // finite -19.25f
    [InlineData(22u, 7u, 0x40490000u)] // finite 3.140625f
    public void CanonicalR32FloatOwnExactDetachedBitsAndArrayLayout(uint unified, uint number, uint bits)
    {
        using var state = new State(); var raw = R32FloatRaw(); var binding = R32FloatBinding(raw);
        Assert.True(Gfx10UnifiedFormat.TryDecode(unified, out var formatCode, out var numberCode));
        Assert.Equal((4u, number), (formatCode, numberCode));
        Assert.Equal(4u, binding.Descriptor.Format); Assert.Equal(number, binding.Descriptor.NumberType);
        var source = Enumerable.Repeat((byte)0xD3, 65536).ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(source, bits);
        var original = source.ToArray(); var memory = new Memory(source);
        var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out var fallbacks));
        Assert.Equal(0, fallbacks); Assert.Equal(1, memory.Reads); Assert.Equal(65536, memory.LastReadBytes);
        Assert.Equal(original, source); Assert.Equal(original.AsSpan(0, 4).ToArray(), snapshot.RgbaPixels);
        Assert.Equal(65536UL, snapshot.SourceByteCount); Assert.Equal(raw, snapshot.Descriptor); Assert.NotSame(raw, snapshot.Descriptor);
        Assert.Equal((3u, 1u, 1u), MetalVideoPresenter.GuestTextureShape(snapshot));
        var format = MetalVideoPresenter.StorageTextureFormat(snapshot);
        Assert.Equal(MtlPixelFormat.R32Float, format.Format);
        Assert.Equal(4u, format.BytesPerPixel); Assert.Equal((1u, 4UL), MetalVideoPresenter.StorageTextureSeedLayout(snapshot, format));
        source[0] ^= 0xFF; raw[1] ^= 1u << 20;
        Assert.Equal(bits, BinaryPrimitives.ReadUInt32LittleEndian(snapshot.RgbaPixels)); Assert.Equal(R32FloatRaw(), snapshot.Descriptor);
        foreach (var invalid in new[] { snapshot with { RgbaPixels = [] }, snapshot with { RgbaPixels = new byte[3] },
            snapshot with { CpuSnapshotStable = false } })
            Assert.Throws<InvalidOperationException>(() => Ensure(invalid));
        Assert.Throws<NotSupportedException>(() => Ensure(snapshot with { Shape = snapshot.Shape with { NumericClass = TextureNumericClass.Uint } }));
    }

    [Fact]
    public void UnqualifiedCanonicalR32FloatMetadataRejectsBeforeReadingIncludingTypedAndRawContradictions()
    {
        using var state = new State();
        var binding = R32FloatBinding(); var memory = new Memory(new byte[65536]);
        var invalidBindings = new List<AgcExports.TranslatedImageBinding>
        {
            binding with { Shape = binding.Shape with { NumericClass = TextureNumericClass.Uint } },
            binding with { Shape = binding.Shape with { NumericClass = TextureNumericClass.Sint } },
            binding with { Shape = binding.Shape with { Arrayed = false } },
            binding with { Shape = binding.Shape with { Atomic = true } },
            binding with { IsArrayed = false }, binding with { MipLevel = 1 },
            binding with { ResourceDescriptor = R32FloatRaw()[..4] }, binding with { ResourceDescriptor = null },
            binding with { Descriptor = binding.Descriptor with { NumberType = 5u } },
            binding with { Access = GuestImageAccess.Unknown }, binding with { Access = GuestImageAccess.Atomic },
        };
        foreach (var (word, bit) in new[] { (0, 1u), (1, 1u << 8), (2, 1u), (3, 1u << 12),
            (3, 1u << 16), (4, 1u), (4, 1u << 16), (5, 1u), (5, 1u << 4), (5, 1u << 23),
            (6, 1u << 21), (7, 1u) })
        {
            var raw = R32FloatRaw(); raw[word] ^= bit; invalidBindings.Add(R32FloatBinding(raw));
        }
        var noPerf = R32FloatRaw(); noPerf[5] = 0; invalidBindings.Add(R32FloatBinding(noPerf));
        var wrongSelect = R32FloatRaw(); wrongSelect[3] = 0xD1B00FAC; invalidBindings.Add(R32FloatBinding(wrongSelect));
        foreach (var invalid in invalidBindings)
            Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [invalid], out _));
        Assert.Equal(0, memory.Reads);
    }

    [Fact]
    public void CanonicalR32FloatRegistryReuseCannotReinterpretAnotherNumericClass()
    {
        using var state = new State(); var memory = new Memory(new byte[65536]);
        var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [R32FloatBinding()], out _));
        var images = (IDictionary)typeof(MetalVideoPresenter).GetField("_guestImages", Private)!.GetValue(null)!;
        var type = typeof(MetalVideoPresenter).GetNestedType("GuestImage", System.Reflection.BindingFlags.NonPublic)!;
        var image = Activator.CreateInstance(type, nonPublic: true)!;
        foreach (var (field, value) in new (string, object)[] { ("Width", 1u), ("Height", 1u), ("TextureType", 3u),
            ("ArrayLength", 1u), ("Depth", 1u), ("Format", MtlPixelFormat.R32Uint), ("Initialized", true) })
            type.GetField(field)!.SetValue(image, value);
        images.Add(Address, image);
        try { Assert.Throws<NotSupportedException>(() => Ensure(snapshot)); }
        finally { images.Remove(Address); }
    }
}
