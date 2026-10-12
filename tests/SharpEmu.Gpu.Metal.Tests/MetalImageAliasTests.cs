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
/// An image that takes GPU ownership of bytes it shares with another cached image leaves
/// that image stale over the shared bytes and nowhere else. Marking the rest stale too
/// costs a refresh of bytes guest memory and the texture already agree on - a whole 4K
/// target re-uploaded because a smaller surface overlapping its tail was drawn to.
/// Device work is behind the exclusive device gate.
/// </summary>
public sealed unsafe class MetalImageAliasTests(ITestOutputHelper output)
{
    private const ulong GuestBase = 0x2_0000_0000;
    private const ulong ImageAddress = GuestBase + 0x10000;

    // 64x64 RGBA8, linear: 16 KiB.
    private const uint LargeSide = 64;
    private const ulong LargeSize = LargeSide * LargeSide * 4;

    // 32x32 RGBA8, linear: 4 KiB.
    private const uint SmallSide = 32;
    private const ulong SmallSize = SmallSide * SmallSide * 4;

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
        public const ulong Size = 1024 * 1024;
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
        public nint Device => harness.Device;
        public ulong CurrentSerial => harness.SubmissionSerial;
        public ulong CompletedSerial => harness.CompletedSerial;
        public IMetalSubmissionContext BeginTransfer(MetalTransferCause cause) => harness;
        public bool HasPendingGpuWrite(ulong address, ulong size) => false;
        public void FillGuestRange(ulong address, ulong size, uint value) { }
        public void NoteGuestWrite(ulong address, ulong size) { }
        public void WaitForGpu() { }
        public void ClearTextureColor(nint texture, ulong pixelFormat, in SubresourceRange range, uint width, uint height, in GpuClearColor color) { }

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

        public Rig(nint device)
        {
            Harness = new MetalTransferHarness(device);
            Harness.Begin();
            _pages = new PageGuard(_space);
            _memory = new MetalGuestMemory(device, _backing, new NoMemory());
            Cache = new MetalImageCache(new Owner(Harness), Harness.Transfer, _memory, _pages, _backing);
        }

        public MetalTransferHarness Harness { get; }
        public MetalImageCache Cache { get; }

        // A linear RGBA8 square, which every byte offset of is its own row-aligned run.
        public static ImageRequest Request(ulong address, uint side, ImageRole role)
        {
            var size = (ulong)side * side * 4;
            var description = ImageDescription.Create();
            description.Data = new GuestSpan(address, size);
            description.PixelFormat = GpuFormat.R8G8B8A8Unorm;
            description.GuestFormat = GuestPixelFormat.Bits8_8_8_8UNorm;
            description.Type = GuestImageType.Color2D;
            description.Extent = new GpuExtent3D(side, side, 1);
            description.Resources = new SubresourceCount(1, 1);
            description.Pitch = side;
            description.BytesPerBlock = 4;
            description.Samples = 1;
            description.TileMode = GuestTileMode.Linear;
            description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = size, Pitch = side, Height = side };
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

        public ResourceSlotIdentifier Acquire(ulong address, uint side, ImageRole role)
        {
            var request = Request(address, side, role);
            var identifier = Cache.FindImage(ref request);
            if (role == ImageRole.ColorTarget)
            {
                Cache.AcquireColorTargetView(identifier, request);
            }
            else
            {
                Cache.AcquireTextureView(identifier, request);
            }

            return identifier;
        }

        public void Dispose()
        {
            Cache.StopWatching();
            Cache.Dispose();
            _pages.Dispose();
            Harness.Dispose();
            _backing.Dispose();
        }
    }

    // The small surface sits at the large one's tail, in its middle, and straddling its
    // end; the large image goes stale over exactly the bytes the two share.
    [Theory]
    [InlineData(LargeSize - SmallSize, "tail")]
    [InlineData(0x2000UL, "middle")]
    [InlineData(LargeSize - (SmallSize / 2), "straddling the end")]
    public void ATargetWriteDirtiesOnlyTheSharedBytesOfAnAlias(ulong offset, string name)
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var large = rig.Acquire(ImageAddress, LargeSide, ImageRole.Texture);
        var image = rig.Cache.GetImage(large);
        Assert.False(image.IsCpuDirty);

        rig.Acquire(ImageAddress + offset, SmallSide, ImageRole.ColorTarget);

        Assert.True(image.IsCpuDirty);
        Assert.Equal(offset, image.DirtyStart);
        Assert.Equal(Math.Min(LargeSize, offset + SmallSize), image.DirtyEnd);
        output.WriteLine($"{name}: dirty 0x{image.DirtyStart:x}..0x{image.DirtyEnd:x} of 0x{LargeSize:x}");
    }

    // An alias whose texture still holds bytes guest memory does not - written by the GPU
    // and only partly published - cannot be refreshed over the shared run alone, so it is
    // still marked stale as a whole.
    [Fact]
    public void AnAliasThatStillOwesGuestMemoryIsDirtiedWhole()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var large = rig.Acquire(ImageAddress, LargeSide, ImageRole.ColorTarget);
        var image = rig.Cache.GetImage(large);
        Assert.True(image.IsGpuModified);

        rig.Acquire(ImageAddress + 0x2000, SmallSide, ImageRole.ColorTarget);

        Assert.True(image.IsCpuDirty);
        Assert.False(image.IsGpuModified);
        Assert.Equal(0UL, image.DirtyStart);
        Assert.Equal(LargeSize, image.DirtyEnd);
    }

    // Without a page watch a CPU store into the rest of the alias goes unseen, and the
    // whole refresh this marks is what used to pick it up.
    [Fact]
    public void AnUnwatchedAliasIsDirtiedWhole()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var large = rig.Acquire(ImageAddress, LargeSide, ImageRole.Texture);
        var image = rig.Cache.GetImage(large);
        Assert.True(image.IsWatched);
        rig.Cache.StopWatching();
        Assert.False(image.IsWatched);

        rig.Acquire(ImageAddress + 0x2000, SmallSide, ImageRole.ColorTarget);

        Assert.Equal(0UL, image.DirtyStart);
        Assert.Equal(LargeSize, image.DirtyEnd);
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Image alias synchronization runs on the device only behind the exclusive device gate.");
            return false;
        }

        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        return true;
    }

    private static readonly Lazy<nint> Device = new(MetalNative.MTLCreateSystemDefaultDevice);
}
