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
/// A DCC fast clear is applied to the image once per guest clear, and the guest's own
/// metadata bytes are never written to remember it: transient aliasing reuses that range
/// as live data. Device work is behind the exclusive device gate.
/// </summary>
public sealed unsafe class MetalDccMaterializationTests(ITestOutputHelper output)
{
    private const ulong GuestBase = 0x2_0000_0000;
    private const ulong ImageAddress = GuestBase + 0x1000;
    private const ulong MetadataAddress = GuestBase + 0x10000;
    private const ulong SliceSize = 0x1000;

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

    // One page-aligned native block seen as guest memory at GuestBase.
    private sealed class Backing : IGuestBackedSpace, IDisposable
    {
        public const ulong Size = 4 * 1024 * 1024;
        private readonly void* _block = NativeMemory.AlignedAlloc((nuint)Size, 16384);

        public Backing() => NativeMemory.Clear(_block, (nuint)Size);

        public void Dispose() => NativeMemory.AlignedFree(_block);

        private bool Contains(ulong address, ulong length) => address >= GuestBase && address - GuestBase + length <= Size;

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

    private sealed class Owner(MetalTransferHarness harness) : IMetalImageCacheOwner
    {
        public int Clears;
        public int Fills;
        public bool Pending;

        public nint Device => harness.Device;
        public ulong CurrentSerial => harness.SubmissionSerial;
        public ulong CompletedSerial => harness.CompletedSerial;
        public IMetalSubmissionContext BeginTransfer(MetalTransferCause cause) => harness;
        public bool HasPendingGpuWrite(ulong address, ulong size) => Pending;
        public void FillGuestRange(ulong address, ulong size, uint value) => Fills++;
        public void NoteGuestWrite(ulong address, ulong size) { }
        public void WaitForGpu() { }

        public void ClearTextureColor(nint texture, ulong pixelFormat, in SubresourceRange range, uint width, uint height, in GpuClearColor color) => Clears++;

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

        public Rig(nint device)
        {
            Harness = new MetalTransferHarness(device);
            Harness.Begin();
            Backing = new Backing();
            Owner = new Owner(Harness);
            _pages = new PageGuard(_space);
            _memory = new MetalGuestMemory(device, Backing, new NoMemory());
            Cache = new MetalImageCache(Owner, Harness.Transfer, _memory, _pages, Backing);
            // Not a clear code, so the target lookups in a test do not materialize.
            FillMetadata(0x55);
        }

        public MetalTransferHarness Harness { get; }
        public Backing Backing { get; }
        public Owner Owner { get; }
        public MetalImageCache Cache { get; }

        public void FillMetadata(byte value) => Backing.TryWriteBacking(MetadataAddress, Enumerable.Repeat(value, (int)SliceSize).ToArray());

        public byte[] ReadMetadata()
        {
            var bytes = new byte[SliceSize];
            Backing.TryReadBacking(MetadataAddress, bytes);
            return bytes;
        }

        public ImageRequest Request(ImageRole role)
        {
            var description = ImageDescription.Create();
            description.Data = new GuestSpan(ImageAddress, 4 * 4 * 4);
            description.PixelFormat = GpuFormat.R8G8B8A8Unorm;
            description.GuestFormat = GuestPixelFormat.Bits8_8_8_8UNorm;
            description.Type = GuestImageType.Color2D;
            description.Extent = new GpuExtent3D(4, 4, 1);
            description.Resources = new SubresourceCount(1, 1);
            description.Pitch = 4;
            description.BytesPerBlock = 4;
            description.Samples = 1;
            description.TileMode = GuestTileMode.Linear;
            description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = 64, Pitch = 4, Height = 4 };
            description.Metadata.Kind = MetadataKind.Dcc;
            description.Metadata.Range = new GuestSpan(MetadataAddress, SliceSize);
            description.Metadata.DccAlphaMsb = true;
            var view = ImageViewDescription.Default with
            {
                Format = GpuFormat.R8G8B8A8Unorm,
                Type = GpuImageViewType.Type2D,
                Aspect = GpuImageAspect.ColorBit,
                LayerCount = 1,
                Usage = role == ImageRole.ColorTarget ? GpuImageUsage.ColorAttachmentBit : GpuImageUsage.SampledBit,
            };
            return new ImageRequest(description, view, role);
        }

        public ResourceSlotIdentifier Lookup(ImageRole role = ImageRole.Texture)
        {
            var request = Request(role);
            return Cache.FindImage(ref request);
        }

        public void Dispose()
        {
            Cache.StopWatching();
            Cache.Dispose();
            _pages.Dispose();
            Harness.Dispose();
            Backing.Dispose();
        }
    }

    [Fact]
    public void MaterializeDcc_NeverWritesGuestMemory()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        rig.Lookup(ImageRole.ColorTarget);
        rig.FillMetadata(0x00);

        rig.Lookup();

        Assert.Equal(1, rig.Owner.Clears);
        Assert.All(rig.ReadMetadata(), static value => Assert.Equal(0x00, value));
        Assert.Equal(0, rig.Owner.Fills);
    }

    [Fact]
    public void MaterializeDcc_RepeatedLookupClearsOnce()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        rig.Lookup(ImageRole.ColorTarget);
        rig.FillMetadata(0x00);

        rig.Lookup();
        rig.Lookup();

        Assert.Equal(1, rig.Owner.Clears);
        Assert.Equal(1, rig.Cache.TakeDccRepeatsSkipped());
    }

    public enum Rewrite
    {
        DmaFill,
        GpuWrite,
        ClearMetadata,
        CpuWrite,
    }

    [Theory]
    [InlineData(Rewrite.DmaFill)]
    [InlineData(Rewrite.GpuWrite)]
    [InlineData(Rewrite.ClearMetadata)]
    [InlineData(Rewrite.CpuWrite)]
    public void MaterializeDcc_GuestRefillRearms(Rewrite rewrite)
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        rig.Lookup(ImageRole.ColorTarget);
        rig.FillMetadata(0x00);
        rig.Lookup();
        Assert.Equal(1, rig.Owner.Clears);

        switch (rewrite)
        {
            case Rewrite.DmaFill:
                rig.Cache.TryAbsorbDccFill(MetadataAddress, SliceSize, 0);
                break;
            case Rewrite.GpuWrite:
                rig.Cache.InvalidateMemoryFromGpu(MetadataAddress, SliceSize);
                break;
            case Rewrite.ClearMetadata:
                rig.Cache.ClearMetadata(MetadataAddress);
                break;
            case Rewrite.CpuWrite:
                Assert.True(((IGuestImageStore)rig.Cache).MarkCpuWrite(MetadataAddress + 0x40, 4));
                break;
        }

        rig.Lookup();
        Assert.Equal(2, rig.Owner.Clears);
        rig.Lookup();
        Assert.Equal(2, rig.Owner.Clears);
    }

    [Fact]
    public void MaterializeDcc_SkipsWhilePendingGpuWrite()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        rig.Lookup(ImageRole.ColorTarget);
        rig.FillMetadata(0x00);
        rig.Owner.Pending = true;

        rig.Lookup();
        rig.Lookup();
        Assert.Equal(0, rig.Owner.Clears);

        rig.Owner.Pending = false;
        rig.Lookup();
        rig.Lookup();
        Assert.Equal(1, rig.Owner.Clears);
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("DCC materialization runs on the device only behind the exclusive device gate.");
            return false;
        }

        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        return true;
    }

    private static readonly Lazy<nint> Device = new(MetalNative.MTLCreateSystemDefaultDevice);
}
