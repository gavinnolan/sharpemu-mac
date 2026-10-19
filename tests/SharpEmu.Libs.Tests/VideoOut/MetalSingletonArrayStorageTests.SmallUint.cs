// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
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
    private static uint[] SmallUintRaw(uint unified, uint addressWord) =>
        [addressWord, unified << 20, 0, unified == 5 ? 0xD1800204u : 0xD1B00FACu, 0, 0x00700000, 0, 0];
    private static AgcExports.TranslatedImageBinding SmallUintBinding(uint[] raw)
    {
        Assert.True(AgcExports.TryDecodeTextureDescriptor(raw, out var descriptor));
        return new(descriptor, true, 0, [], IsArrayed: true, ResourceDescriptor: raw,
            Shape: new ShaderImageShape(false, true, true, false, TextureNumericClass.Uint), Access: GuestImageAccess.Write);
    }

    [Theory]
    [InlineData(5u, 0x80107800u, false)] // SH actual raw descriptor
    [InlineData(5u, 0x8030BA00u, false)] // Octopath actual raw descriptor
    [InlineData(60u, 0x80115C00u, false)] // Oregon actual raw descriptor, RGBA8Uint
    [InlineData(5u, 0x80107800u, true)] // successful zero is a complete seed
    [InlineData(60u, 0x80115C00u, true)]
    public void ActualSmallUintArrayOwnsCompletePhysicalBlockAndExactDetachedTypedSeed(uint unified, uint addressWord, bool zero)
    {
        using var state = new State(); var raw = SmallUintRaw(unified, addressWord); var binding = SmallUintBinding(raw);
        var data = unified == 5 ? 1u : 10u; var bytes = unified == 5 ? 1u : 4u;
        Assert.True(Gfx10UnifiedFormat.TryDecode(unified, out var formatCode, out var numberCode));
        Assert.Equal((data, 4u), (formatCode, numberCode));
        var source = Enumerable.Repeat((byte)0xD3, 65536).ToArray();
        byte[] seed = zero ? new byte[bytes] : unified == 5 ? [0xA7] : [0x17, 0x53, 0x95, 0xE3];
        seed.CopyTo(source, 0); var original = source.ToArray(); var memory = new Memory(source, binding.Descriptor.Address);
        try
        {
            var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out var fallbacks));
            Assert.Equal(0, fallbacks); Assert.Equal(1, memory.Reads); Assert.Equal(65536, memory.LastReadBytes);
            Assert.Equal(original, source); Assert.Equal(seed, snapshot.RgbaPixels); Assert.Equal(65536UL, snapshot.SourceByteCount);
            Assert.Equal(raw, snapshot.Descriptor); Assert.NotSame(raw, snapshot.Descriptor);
            Assert.Equal((3u, 1u, 1u), MetalVideoPresenter.GuestTextureShape(snapshot));
            var format = MetalVideoPresenter.StorageTextureFormat(snapshot);
            Assert.Equal(unified == 5 ? MtlPixelFormat.R8Uint : MtlPixelFormat.Rgba8Uint, format.Format);
            Assert.Equal(bytes, format.BytesPerPixel); Assert.Equal((1u, (ulong)bytes), MetalVideoPresenter.StorageTextureSeedLayout(snapshot, format));
            source[0] ^= 0xFF; raw[1] ^= 1u << 20;
            Assert.Equal(seed, snapshot.RgbaPixels); Assert.Equal(SmallUintRaw(unified, addressWord), snapshot.Descriptor);
            foreach (var invalid in new[] { snapshot with { RgbaPixels = [] }, snapshot with { RgbaPixels = new byte[bytes - 1] }, snapshot with { CpuSnapshotStable = false } })
                Assert.Throws<InvalidOperationException>(() => Ensure(invalid));
            Assert.Throws<NotSupportedException>(() => Ensure(snapshot with { Shape = snapshot.Shape with { NumericClass = TextureNumericClass.Float } }));
            Assert.Throws<NotSupportedException>(() => Ensure(snapshot with { TileMode = unified == 5 ? 27u : 24u }));
        }
        finally { GuestImageWriteTracker.Untrack(binding.Descriptor.Address); }
    }

    [Theory]
    [InlineData(5u)]
    [InlineData(60u)]
    public void SmallUintRawShapeAndLayoutNearMissesRejectBeforeAnyGuestRead(uint unified)
    {
        using var state = new State(); var binding = SmallUintBinding(SmallUintRaw(unified, 0x80107800));
        var memory = new Memory(new byte[65536], binding.Descriptor.Address);
        var invalidBindings = new List<AgcExports.TranslatedImageBinding>
        {
            binding with { Shape = binding.Shape with { NumericClass = TextureNumericClass.Float } },
            binding with { Shape = binding.Shape with { NumericClass = TextureNumericClass.Sint } },
            binding with { Shape = binding.Shape with { Arrayed = false } },
            binding with { Shape = binding.Shape with { Atomic = true } },
            binding with { Shape = binding.Shape with { Volume = true } },
            binding with { Shape = binding.Shape with { Multisampled = true } },
            binding with { IsArrayed = false }, binding with { MipLevel = 1 },
            binding with { ResourceDescriptor = SmallUintRaw(unified, 0x80107800)[..4] }, binding with { ResourceDescriptor = null },
            binding with { Descriptor = binding.Descriptor with { NumberType = 5 } },
            binding with { Access = GuestImageAccess.Unknown }, binding with { Access = GuestImageAccess.Atomic },
        };
        foreach (var (word, bit) in new[] { (0, 1u), (1, 1u << 8), (2, 1u), (3, 1u << 12),
            (3, 1u << 16), (4, 1u), (4, 1u << 16), (5, 1u), (5, 1u << 4), (5, 1u << 23), (6, 1u << 21), (7, 1u) })
        {
            var raw = SmallUintRaw(unified, 0x80107800); raw[word] ^= bit; invalidBindings.Add(SmallUintBinding(raw));
        }
        foreach (var replacement in new[] { 0u, 0x00600000u, 0x00700001u, 0x00F00000u })
        {
            var raw = SmallUintRaw(unified, 0x80107800); raw[5] = replacement; invalidBindings.Add(SmallUintBinding(raw));
        }
        var wrongTile = SmallUintRaw(unified, 0x80107800); wrongTile[3] = unified == 5 ? 0xD1B00204u : 0xD1800FACu;
        invalidBindings.Add(SmallUintBinding(wrongTile));
        var wrongSelect = SmallUintRaw(unified, 0x80107800); wrongSelect[3] ^= 1; invalidBindings.Add(SmallUintBinding(wrongSelect));
        var wrongFormat = SmallUintRaw(unified, 0x80107800); wrongFormat[1] = (unified == 5 ? 6u : 69u) << 20;
        invalidBindings.Add(SmallUintBinding(wrongFormat)); // R8Sint / actual RGBA16Uint are not admitted
        foreach (var invalid in invalidBindings)
            Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [invalid], out _));
        Assert.Equal(0, memory.Reads);
    }
}
