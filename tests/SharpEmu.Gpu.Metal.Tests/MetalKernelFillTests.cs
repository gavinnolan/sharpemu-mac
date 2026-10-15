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
/// A compute fill or copy over exactly one cached image becomes a clear or a texture copy
/// of it: the pattern has to store one texel everywhere, the texel has to survive a clear,
/// and anything short of exact coverage runs as the dispatch it is. Device work is behind
/// the exclusive device gate.
/// </summary>
public sealed unsafe class MetalKernelFillTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(new byte[] { 1, 2, 3, 4 }, 8, new byte[] { 1, 2, 3, 4, 1, 2, 3, 4 })]
    [InlineData(new byte[] { 1, 2, 3, 4, 1, 2, 3, 4 }, 4, new byte[] { 1, 2, 3, 4 })]
    [InlineData(new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 }, 8, new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 })]
    [InlineData(new byte[] { 7, 7, 7, 7 }, 2, new byte[] { 7, 7 })]
    public void APatternWithTheTexelsPeriodResolvesToOneTexel(byte[] pattern, int texelBytes, byte[] expected)
    {
        Span<byte> texel = stackalloc byte[16];
        Assert.True(MetalKernelFill.TryResolveTexel(pattern, texelBytes, texel));
        Assert.Equal(expected, texel[..texelBytes].ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 4)]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, 8)]
    [InlineData(new byte[] { 1, 2, 3, 4 }, 2)]
    public void APatternThatVariesBetweenTexelsIsRefused(byte[] pattern, int texelBytes)
    {
        Span<byte> texel = stackalloc byte[16];
        Assert.False(MetalKernelFill.TryResolveTexel(pattern, texelBytes, texel));
    }

    [Theory]
    [InlineData(GpuFormat.R32Uint)]
    [InlineData(GpuFormat.R32G32Uint)]
    [InlineData(GpuFormat.R16G16B16A16Sfloat)]
    [InlineData(GpuFormat.B10G11R11UfloatPack32)]
    public void ZeroClearsInAnyFormat(GpuFormat format)
    {
        Assert.True(MetalKernelFill.TryDecodeTexel(format, new byte[8], out var color));
        Assert.Equal(default(GpuClearColor).Uint32_0, color.Uint32_0);
        Assert.Equal(0u, color.Uint32_1 | color.Uint32_2 | color.Uint32_3);
    }

    [Fact]
    public void AnIntegerFormatTakesZeroOnly() =>
        Assert.False(MetalKernelFill.TryDecodeTexel(GpuFormat.R32Uint, BitConverter.GetBytes(5u), out _));

    [Fact]
    public void AHalfFloatTexelBecomesItsComponents()
    {
        var texel = new byte[8];
        BitConverter.TryWriteBytes(texel.AsSpan(0, 2), (Half)1.0f);
        BitConverter.TryWriteBytes(texel.AsSpan(2, 2), (Half)0.5f);
        BitConverter.TryWriteBytes(texel.AsSpan(6, 2), (Half)(-2.0f));

        Assert.True(MetalKernelFill.TryDecodeTexel(GpuFormat.R16G16B16A16Sfloat, texel, out var color));
        Assert.Equal(1.0f, color.Float32_0);
        Assert.Equal(0.5f, color.Float32_1);
        Assert.Equal(0.0f, color.Float32_2);
        Assert.Equal(-2.0f, color.Float32_3);
    }

    [Theory]
    [InlineData((ushort)0x7E00)] // NaN
    [InlineData((ushort)0x7C00)] // infinity
    [InlineData((ushort)0x0001)] // subnormal
    public void AHalfThatAClearMayNotKeepIsRefused(ushort bits)
    {
        var texel = new byte[8];
        BitConverter.TryWriteBytes(texel.AsSpan(4, 2), bits);
        Assert.False(MetalKernelFill.TryDecodeTexel(GpuFormat.R16G16B16A16Sfloat, texel, out _));
    }

    [Fact]
    public void APackedSmallFloatTexelBecomesItsComponents()
    {
        // R = 1.0 (exponent 15), G = 0.5 (exponent 14), B = 2.0 (exponent 16, five mantissa bits).
        var packed = (15u << 6) | ((14u << 6) << 11) | ((16u << 5) << 22);
        Assert.True(MetalKernelFill.TryDecodeTexel(GpuFormat.B10G11R11UfloatPack32, BitConverter.GetBytes(packed), out var color));
        Assert.Equal(1.0f, color.Float32_0);
        Assert.Equal(0.5f, color.Float32_1);
        Assert.Equal(2.0f, color.Float32_2);

        Assert.False(MetalKernelFill.TryDecodeTexel(GpuFormat.B10G11R11UfloatPack32, BitConverter.GetBytes(0x1Fu << 6), out _));
    }

    [Fact]
    public void UnormBytesBecomeTheirFractions()
    {
        Assert.True(MetalKernelFill.TryDecodeTexel(GpuFormat.R8G8Unorm, new byte[] { 255, 51 }, out var color));
        Assert.Equal(1.0f, color.Float32_0);
        Assert.Equal(0.2f, color.Float32_1);
    }

    private const ulong GuestBase = 0x2_0000_0000;
    private const ulong ImageAddress = GuestBase + 0x10000;
    private const ulong OtherAddress = GuestBase + 0x40000;
    private const uint Side = 64;
    private const ulong ImageSize = Side * Side * 4;

    [Fact]
    public void AFillOverExactlyOneImageClearsItAndLeavesItGpuModified()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var identifier = rig.Acquire(ImageAddress, GpuFormat.R8G8B8A8Unorm, ImageRole.Texture);

        Assert.True(rig.Cache.TryClearImageFromKernelFill(ImageAddress, ImageSize, new byte[] { 255, 0, 0, 255 }, out var fallback), fallback);

        var clear = Assert.Single(rig.Clears);
        Assert.Equal((1.0f, 0.0f, 0.0f, 1.0f), (clear.Color.Float32_0, clear.Color.Float32_1, clear.Color.Float32_2, clear.Color.Float32_3));
        Assert.Equal((Side, Side), (clear.Width, clear.Height));
        var image = rig.Cache.GetImage(identifier);
        Assert.True(image.IsGpuModified);
        Assert.False(image.IsCpuDirty);
    }

    [Theory]
    [InlineData(ImageAddress, ImageSize / 2, "partial")]
    [InlineData(ImageAddress + 0x1000, ImageSize, "partial")]
    [InlineData(OtherAddress, ImageSize, "no-image")]
    public void AFillThatDoesNotCoverExactlyOneImageRunsAsWritten(ulong address, ulong size, string reason)
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        rig.Acquire(ImageAddress, GpuFormat.R8G8B8A8Unorm, ImageRole.Texture);

        Assert.False(rig.Cache.TryClearImageFromKernelFill(address, size, new byte[4], out var fallback));
        Assert.Equal(reason, fallback);
        Assert.Empty(rig.Clears);
    }

    [Fact]
    public void AFillPatternThatVariesBetweenTexelsRunsAsWritten()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        rig.Acquire(ImageAddress, GpuFormat.R8G8B8A8Unorm, ImageRole.Texture);

        Assert.False(rig.Cache.TryClearImageFromKernelFill(ImageAddress, ImageSize, new byte[] { 0, 0, 0, 0, 1, 1, 1, 1 }, out var fallback));
        Assert.Equal("pattern", fallback);
    }

    [Fact]
    public void ACopyBetweenTwoImagesOfOneLayoutIsATextureCopy()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var source = rig.Acquire(OtherAddress, GpuFormat.R8G8B8A8Unorm, ImageRole.Texture);
        var destination = rig.Acquire(ImageAddress, GpuFormat.R8G8B8A8Unorm, ImageRole.Texture);

        Assert.True(rig.Cache.TryBeginKernelCopy(ImageAddress, OtherAddress, ImageSize, out var from, out var to, out var identifier, out var fallback), fallback);
        Assert.Same(rig.Cache.GetImage(source), from);
        Assert.Same(rig.Cache.GetImage(destination), to);
        rig.Cache.CompleteKernelCopy(identifier);
        Assert.True(to!.IsGpuModified);
        Assert.False(to.IsCpuDirty);
    }

    [Fact]
    public void ACopyBetweenImagesOfDifferentFormatsRunsAsWritten()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        rig.Acquire(OtherAddress, GpuFormat.R32Uint, ImageRole.Texture);
        rig.Acquire(ImageAddress, GpuFormat.R8G8B8A8Unorm, ImageRole.Texture);

        Assert.False(rig.Cache.TryBeginKernelCopy(ImageAddress, OtherAddress, ImageSize, out _, out _, out _, out var fallback));
        Assert.Equal("mismatch", fallback);
        Assert.False(rig.Cache.TryBeginKernelCopy(ImageAddress, ImageAddress + 0x800, ImageSize, out _, out _, out _, out fallback));
        Assert.Equal("overlap", fallback);
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
        public const ulong Size = 1024 * 1024;
        private readonly void* _block = NativeMemory.AlignedAlloc((nuint)Size, 16384);

        public Backing() => NativeMemory.Clear(_block, (nuint)Size);

        public void Dispose() => NativeMemory.AlignedFree(_block);

        private static bool Contains(ulong address, ulong length) => address >= GuestBase && address - GuestBase + length <= Size;

        public bool TryHoldRange(ulong address, ulong size) => false;
        public bool TryHoldRangeAtOrAbove(ulong searchStart, ulong size, ulong alignment, out ulong address) { address = 0; return false; }
        public bool TryMapBacked(ulong address, ulong size, ulong backingOffset, GuestPageProtection protection, out HostViewFailure failure) { failure = HostViewFailure.None; return false; }
        public bool TryUnmapBacked(ulong address, ulong size) => false;
        public bool TryClearBacking(ulong offset, ulong size) => false;
        public bool IsBackedView(ulong address) => Contains(address, 1);
        public bool IsBackedRange(ulong address, ulong size) => Contains(address, size);
        public (ulong Base, ulong Size) BackingAliasRange => ((ulong)_block, Size);

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

    private sealed class NoMemory : ICpuMemory
    {
        public bool TryRead(ulong virtualAddress, Span<byte> destination) => false;
        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
        public bool CanRead(ulong address, ulong size) => false;
    }

    private readonly record struct RecordedClear(nint Texture, uint Width, uint Height, GpuClearColor Color);

    private sealed class Owner(MetalTransferHarness harness, List<RecordedClear> clears) : IMetalImageCacheOwner
    {
        public nint Device => harness.Device;
        public ulong CurrentSerial => harness.SubmissionSerial;
        public ulong CompletedSerial => harness.CompletedSerial;
        public IMetalSubmissionContext BeginTransfer(MetalTransferCause cause) => harness;
        public bool HasPendingGpuWrite(ulong address, ulong size) => false;
        public void FillGuestRange(ulong address, ulong size, uint value) { }
        public void NoteGuestWrite(ulong address, ulong size) { }
        public void WaitForGpu() { }

        public void ClearTextureColor(nint texture, ulong pixelFormat, in SubresourceRange range, uint width, uint height, in GpuClearColor color) =>
            clears.Add(new RecordedClear(texture, width, height, color));

        public void ClearTextureDepthStencil(nint texture, ulong pixelFormat, in SubresourceRange range, uint width, uint height,
            float depth, byte stencil, bool clearDepth, bool clearStencil)
        {
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly AddressSpace _space = new();
        private readonly PageGuard _pages;
        private readonly MetalGuestMemory _memory;
        private readonly Backing _backing = new();
        private readonly bool _savedLinearViews = MetalLinearWindowView.Enabled;

        public Rig(nint device)
        {
            MetalLinearWindowView.Enabled = false;
            Harness = new MetalTransferHarness(device);
            Harness.Begin();
            _pages = new PageGuard(_space);
            _memory = new MetalGuestMemory(device, _backing, new NoMemory());
            Cache = new MetalImageCache(new Owner(Harness, Clears), Harness.Transfer, _memory, _pages, _backing);
        }

        public MetalTransferHarness Harness { get; }
        public MetalImageCache Cache { get; }
        public List<RecordedClear> Clears { get; } = [];

        // A linear 64x64 surface of four-byte texels.
        public ResourceSlotIdentifier Acquire(ulong address, GpuFormat format, ImageRole role)
        {
            var description = ImageDescription.Create();
            description.Data = new GuestSpan(address, ImageSize);
            description.PixelFormat = format;
            description.GuestFormat = format == GpuFormat.R32Uint ? GuestPixelFormat.Bits32UInt : GuestPixelFormat.Bits8_8_8_8UNorm;
            description.Type = GuestImageType.Color2D;
            description.Extent = new GpuExtent3D(Side, Side, 1);
            description.Resources = new SubresourceCount(1, 1);
            description.Pitch = Side;
            description.BytesPerBlock = 4;
            description.Samples = 1;
            description.TileMode = GuestTileMode.Linear;
            description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = ImageSize, Pitch = Side, Height = Side };
            var view = ImageViewDescription.Default with
            {
                Format = format,
                Type = GpuImageViewType.Type2D,
                Aspect = GpuImageAspect.ColorBit,
                LayerCount = 1,
                Usage = GpuImageUsage.SampledBit,
            };
            var request = new ImageRequest(description, view, role);
            var identifier = Cache.FindImage(ref request);
            Cache.AcquireTextureView(identifier, request);
            return identifier;
        }

        public void Dispose()
        {
            Cache.StopWatching();
            Cache.Dispose();
            _pages.Dispose();
            Harness.Dispose();
            _backing.Dispose();
            MetalLinearWindowView.Enabled = _savedLinearViews;
        }
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Kernel fill interception runs on the device only behind the exclusive device gate.");
            return false;
        }

        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        return true;
    }

    private static readonly Lazy<nint> Device = new(MetalNative.MTLCreateSystemDefaultDevice);
}
