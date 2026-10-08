// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Tests.Fixtures;
using Xunit;
namespace SharpEmu.Libs.Tests.VideoOut;
public sealed partial class MetalSingletonArrayStorageTests
{
    private static uint[] R8FourKRaw(uint address = 0x801A8000) =>
        [address, 0xC0500000, 0x021BC3BF, 0xD1800204, 0, 0x00700000, 0, 0];
    [Fact]
    public void R8FourKAllProductionOffsetsAndBothDetilersMatchIndependentPal()
    {
        var expected = R8FourKOracle.Offsets(); var observed = new byte[expected.Length];
        var p = SharpEmu.Libs.Agc.GnmTiling.GetDetileParams(24, 1, 3840, 2160);
        Assert.Equal((256, 256, 65536, 15), (p.BlockWidth, p.BlockHeight, p.BlockBytes, p.BlocksPerRow));
        for (int y = 0; y < 2160; y++) for (int x = 0; x < 3840; x++)
        {
            var offset = ((y / 256) * 15 + x / 256) * 65536 + (p.XByteTerm[x & p.XMask] ^ p.YByteTerm[y & p.YMask]);
            BinaryPrimitives.WriteUInt32LittleEndian(observed.AsSpan((y * 3840 + x) * 4), (uint)offset);
        }
        Assert.Equal(R8FourKOracle.OffsetSha256, Convert.ToHexString(SHA256.HashData(observed))); Assert.Equal(expected, observed);
        var physical = R8FourKOracle.PhysicalSeed(); var before = SHA256.HashData(physical);
        var linear = new byte[R8FourKOracle.LogicalBytes]; var other = new byte[linear.Length];
        Assert.True(SharpEmu.Libs.Agc.GnmTiling.TryDetile(physical, linear, 24, 3840, 2160, 1));
        Assert.True(SharpEmu.Libs.Agc.GnmTiling.DetileWithParams(p, physical, other));
        Assert.Equal(R8FourKOracle.LinearSeed(), linear); Assert.Equal(linear, other); Assert.Equal(before, SHA256.HashData(physical));
    }
    [Theory]
    [InlineData(0x801A8000u)]
    [InlineData(0x805B4500u)]
    public void CapturedR8FourKArrayOwnsEveryLogicalByteAndFullPaddedPhysicalFootprint(uint address)
    {
        using var state = new State(); var raw = R8FourKRaw(address); var binding = UintBinding(raw);
        var source = R8FourKOracle.PhysicalSeed(); var memory = new Memory(source, binding.Descriptor.Address);
        try
        {
            var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out var fallback));
            Assert.Equal(0, fallback); Assert.Equal(1, memory.Reads); Assert.Equal(8847360, memory.LastReadBytes);
            Assert.Equal(R8FourKOracle.LinearSeed(), snapshot.RgbaPixels); Assert.Equal(8847360UL, snapshot.SourceByteCount);
            Assert.Equal(raw, snapshot.Descriptor); Assert.NotSame(raw, snapshot.Descriptor);
            Assert.Equal((3u, 1u, 1u), MetalVideoPresenter.GuestTextureShape(snapshot));
            var format = MetalVideoPresenter.StorageTextureFormat(snapshot); Assert.Equal(MtlPixelFormat.R8Uint, format.Format); Assert.Equal(1u, format.BytesPerPixel);
            Assert.Equal((3840u, 8294400UL), MetalVideoPresenter.StorageTextureSeedLayout(snapshot, format));
            source.AsSpan().Fill(0x33); raw[5] ^= 1; Assert.Equal(R8FourKOracle.LinearSeed(), snapshot.RgbaPixels); Assert.Equal(0x00700000u, snapshot.Descriptor![5]);
            Assert.Throws<InvalidOperationException>(() => Ensure(snapshot with { RgbaPixels = snapshot.RgbaPixels[..^1] }));
            Assert.Throws<InvalidOperationException>(() => Ensure(snapshot with { CpuSnapshotStable = false }));
            Assert.Throws<NotSupportedException>(() => Ensure(snapshot with { ArrayLayers = 2 }));
            Assert.Throws<NotSupportedException>(() => Ensure(snapshot with { SourceByteCount = 8294400 }));
            Assert.Throws<NotSupportedException>(() => Ensure(snapshot with { Type = 9, ArrayedView = false }));
        }
        finally { GuestImageWriteTracker.Untrack(binding.Descriptor.Address); }
    }
    [Fact]
    public void R8FourKShortPhysicalReadCannotUploadTruncatedOrZeroFabricatedSeed()
    {
        using var state = new State(); var binding = UintBinding(R8FourKRaw()); var memory = new Memory(new byte[8847359], binding.Descriptor.Address);
        try
        {
            var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
            Assert.Empty(snapshot.RgbaPixels); Assert.Equal(1, memory.Reads); Assert.Equal(8847360, memory.LastReadBytes);
            Assert.Throws<InvalidOperationException>(() => Ensure(snapshot));
        }
        finally { GuestImageWriteTracker.Untrack(binding.Descriptor.Address); }
    }
    [Fact]
    public void R8FourKSuccessfulZeroSeedStillOwnsEveryNativeByte()
    {
        using var state = new State(); var binding = UintBinding(R8FourKRaw()); var memory = new Memory(new byte[8847360], binding.Descriptor.Address);
        try
        {
            var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out var fallback));
            Assert.Equal(0, fallback); Assert.Equal(1, memory.Reads); Assert.Equal(8847360, memory.LastReadBytes);
            Assert.Equal(8294400, snapshot.RgbaPixels.Length); Assert.Equal(-1, snapshot.RgbaPixels.AsSpan().IndexOfAnyExcept((byte)0));
            Assert.Equal((3840u, 8294400UL), MetalVideoPresenter.StorageTextureSeedLayout(snapshot, MetalVideoPresenter.StorageTextureFormat(snapshot)));
        }
        finally { GuestImageWriteTracker.Untrack(binding.Descriptor.Address); }
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)]
    public void R8FourKNearMissesRejectBeforeGuestRead(int kind)
    {
        using var state = new State(); var raw = R8FourKRaw(); var binding = UintBinding(raw);
        binding = kind switch
        {
            0 => binding with { Access = GuestImageAccess.Read },
            1 => binding with { Access = GuestImageAccess.Read | GuestImageAccess.Write },
            2 => binding with { Shape = binding.Shape with { NumericClass = TextureNumericClass.Float } },
            3 => binding with { Descriptor = binding.Descriptor with { Width = 3839 } },
            4 => binding with { Descriptor = binding.Descriptor with { Height = 2161 } },
            5 => binding with { IsArrayed = false },
            6 => binding with { MipLevel = 1 },
            7 => binding with { Shape = binding.Shape with { Atomic = true } },
            10 => binding with { Shape = binding.Shape with { DynamicMip = true } },
            11 => binding with { ResourceDescriptor = raw.Concat(new uint[] { 0 }).ToArray() },
            _ => binding,
        };
        if (kind == 8) raw[5] ^= 1;
        if (kind == 9) raw[6] = 1;
        if (kind == 12) { raw[0] |= 1; binding = UintBinding(raw); }
        if (kind == 13) { raw[4] = 1; binding = UintBinding(raw); }
        var memory = new Memory([], binding.Descriptor.Address);
        Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
        Assert.Equal(0, memory.Reads);
    }
}
