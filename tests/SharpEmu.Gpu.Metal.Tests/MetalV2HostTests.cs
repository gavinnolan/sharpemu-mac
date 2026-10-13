// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// The device-free parts of the v2 host: the slot table the shared engine holds
// identifiers into, the page owner index a CPU write fault consults, and the
// translations between the neutral vocabulary and Metal.
public sealed class MetalV2HostTests
{
    // Every complete group of three is one rectangle (one patch); a trailing incomplete
    // group draws nothing.
    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 0u)]
    [InlineData(2u, 0u)]
    [InlineData(3u, 1u)]
    [InlineData(4u, 1u)]
    [InlineData(5u, 1u)]
    [InlineData(6u, 2u)]
    [InlineData(300u, 100u)]
    public void ARectangleListDrawsOnePatchPerCompleteRectangle(uint count, uint patches)
    {
        Assert.Equal(patches, MetalV2Formats.RectanglePatchCount(count));
    }

    [Fact]
    public void ARectangleListRasterizesTriangles()
    {
        Assert.Equal(Mtl.PrimitiveTriangle, MetalV2Formats.Topology(GpuPrimitiveTopology.PatchList));
        Assert.Equal(3u, MetalV2Formats.TopologyClass(GpuPrimitiveTopology.PatchList));
    }

    [Fact]
    public void ErasedImageSlotStopsResolvingAndItsIndexIsReused()
    {
        var slots = new MetalImageSlots();
        var first = slots.Insert(CreateImage());
        var second = slots.Insert(CreateImage());
        Assert.NotNull(slots.TryGet(first));
        Assert.NotNull(slots.TryGet(second));
        Assert.Equal(2, slots.Count);

        slots.Erase(first);
        Assert.Null(slots.TryGet(first));
        Assert.NotNull(slots.TryGet(second));

        var reused = slots.Insert(CreateImage());
        Assert.Equal(first.Index, reused.Index);
        Assert.NotEqual(first.Generation, reused.Generation);
        // The stale identifier must not resolve to the new occupant of its slot.
        Assert.Null(slots.TryGet(first));
        Assert.NotNull(slots.TryGet(reused));
    }

    [Fact]
    public void DefaultImageIdentifierNeverResolves()
    {
        var slots = new MetalImageSlots();
        _ = slots.Insert(CreateImage());
        Assert.Null(slots.TryGet(ResourceSlotIdentifier.Invalid));
    }

    [Fact]
    public void PageOwnersAnswerOnlyForPagesAnImageHasEverCovered()
    {
        var owners = new MetalImagePageOwners();
        const ulong page = 1UL << MetalImagePageOwners.PageBits;
        Assert.False(owners.MayHaveOwners(page, 0x1000));

        var identifier = new ResourceSlotIdentifier(7, 3);
        owners.GetOrCreate(page >> MetalImagePageOwners.PageBits).Add(identifier);
        Assert.True(owners.MayHaveOwners(page, 0x1000));
        Assert.False(owners.MayHaveOwners(page + (2 * page), 0x1000));

        var list = owners.Find(page >> MetalImagePageOwners.PageBits);
        Assert.NotNull(list);
        Assert.Contains(identifier, list!);
    }

    [Theory]
    [InlineData(0UL, 0UL, false)]
    [InlineData(0x1000UL, 0x1000UL, true)]
    [InlineData(1UL << 40, 0x1000UL, false)]
    public void PageRangesOutsideTheAddressSpaceAreRejected(ulong address, ulong size, bool expected)
    {
        Assert.Equal(expected, MetalImagePageOwners.TryGetPageRange(address, size, out var first, out var last));
        if (expected)
        {
            Assert.True(last > first);
        }
    }

    [Fact]
    public void AnUnmappedImageFormatSubstitutesTheSizeEquivalentOne()
    {
        // An unknown format must never stop a title: the substitute keeps the guest
        // footprint, so the tiler's element size still matches.
        Assert.Equal(GpuFormat.R8Unorm, MetalV2Formats.SubstituteFor(1));
        Assert.Equal(GpuFormat.R16Unorm, MetalV2Formats.SubstituteFor(2));
        Assert.Equal(GpuFormat.R8G8B8A8Unorm, MetalV2Formats.SubstituteFor(4));
        Assert.Equal(GpuFormat.R16G16B16A16Unorm, MetalV2Formats.SubstituteFor(8));
        Assert.Equal(GpuFormat.R32G32B32A32Sfloat, MetalV2Formats.SubstituteFor(16));
        Assert.Equal(GpuFormat.R8G8B8A8Unorm, MetalV2Formats.SubstituteFor(3));
    }

    [Fact]
    public void UnmappedFormatsFallBackToRgba8RatherThanFailing()
    {
        Assert.False(MetalV2Formats.IsSupported(GpuFormat.R64G64B64A64Sfloat));
        Assert.Equal(MtlFormat.Rgba8Unorm, MetalV2Formats.PixelFormat(GpuFormat.R64G64B64A64Sfloat));
        Assert.True(MetalV2Formats.IsSupported(GpuFormat.R8G8B8A8Unorm));
    }

    [Fact]
    public void ColorWriteMaskIsReversedForMetal()
    {
        // Guest CB bits are R=1,G=2,B=4,A=8; MTLColorWriteMask orders them alpha-first.
        Assert.Equal(0, MetalV2Formats.ColorWriteMask(0));
        Assert.Equal(Mtl.ColorWriteMaskRed, MetalV2Formats.ColorWriteMask(1));
        Assert.Equal(Mtl.ColorWriteMaskAlpha, MetalV2Formats.ColorWriteMask(8));
        Assert.Equal(
            Mtl.ColorWriteMaskRed | Mtl.ColorWriteMaskGreen | Mtl.ColorWriteMaskBlue | Mtl.ColorWriteMaskAlpha,
            MetalV2Formats.ColorWriteMask(0xF));
    }

    [Fact]
    public void BlendCodesMapToMetalsReorderedFactors()
    {
        // The guest orders destination alpha before destination colour; Metal does not.
        Assert.Equal(8, MetalV2Formats.BlendFactor(6));
        Assert.Equal(6, MetalV2Formats.BlendFactor(8));
        Assert.Equal(1, MetalV2Formats.BlendFactor(0xFF));
        Assert.Equal(Mtl.BlendOperationMin, MetalV2Formats.BlendOperation(2));
        Assert.Equal(Mtl.BlendOperationReverseSubtract, MetalV2Formats.BlendOperation(4));
    }

    [Theory]
    [InlineData(GpuPrimitiveTopology.PointList, 1u)]
    [InlineData(GpuPrimitiveTopology.LineList, 2u)]
    [InlineData(GpuPrimitiveTopology.LineStrip, 2u)]
    [InlineData(GpuPrimitiveTopology.TriangleList, 3u)]
    [InlineData(GpuPrimitiveTopology.TriangleStrip, 3u)]
    [InlineData(GpuPrimitiveTopology.TriangleFan, 3u)]
    public void LayeredPipelinesDeclareTheClassOfTheDrawnPrimitive(GpuPrimitiveTopology topology, uint expected)
    {
        // A layered pipeline's input topology class has to match every draw's primitive type.
        Assert.Equal((nuint)expected, MetalV2Formats.TopologyClass(topology));
    }

    [Fact]
    public void VertexFormatsNarrowToTheComponentsTheProgramFetches()
    {
        // Buffer format 77 is a float4; a program that fetches two components must
        // get float2 so Metal's fetch matches the shader's declaration.
        var float4 = new BufferDescriptorWords(0, 0, 0, 77u << 12);
        Assert.Equal(MtlVertexFormat.Float4, MetalV2Formats.VertexFormat(float4, 4));
        Assert.Equal(MtlVertexFormat.Float2, MetalV2Formats.VertexFormat(float4, 2));
        Assert.Equal(MtlVertexFormat.Float, MetalV2Formats.VertexFormat(float4, 1));

        // An unknown format falls back to the float vector of the fetched width.
        var unknown = new BufferDescriptorWords(0, 0, 0, 126u << 12);
        Assert.Equal(MtlVertexFormat.Float3, MetalV2Formats.VertexFormat(unknown, 3));
    }

    [Fact]
    public void TextureTypeFollowsTheGuestImageShape()
    {
        Assert.Equal(Mtl.TextureType2D, MetalV2Formats.TextureType(GpuImageType.Type2D, 1, 1));
        Assert.Equal(Mtl.TextureType2DArray, MetalV2Formats.TextureType(GpuImageType.Type2D, 6, 1));
        Assert.Equal(Mtl.TextureType3D, MetalV2Formats.TextureType(GpuImageType.Type3D, 1, 1));
        Assert.Equal(Mtl.TextureType1D, MetalV2Formats.TextureType(GpuImageType.Type1D, 1, 1));
        Assert.Equal(Mtl.TextureType2DMultisample, MetalV2Formats.TextureType(GpuImageType.Type2D, 1, 4));
        // A guest cube binds as a 2D array: the shader resolves the face itself.
        Assert.Equal(
            Mtl.TextureType2DArray,
            MetalV2Formats.ViewTextureType(GpuImageViewType.TypeCube, 1));
    }

    [Fact]
    public void DepthFormatsAreRecognisedWithAndWithoutStencil()
    {
        Assert.True(MetalV2Formats.IsDepthFormat(MetalV2Formats.PixelFormat(GpuFormat.D32Sfloat)));
        Assert.False(MetalV2Formats.HasStencil(MetalV2Formats.PixelFormat(GpuFormat.D32Sfloat)));
        Assert.True(MetalV2Formats.HasStencil(MetalV2Formats.PixelFormat(GpuFormat.D32SfloatS8Uint)));
        Assert.False(MetalV2Formats.IsDepthFormat(MetalV2Formats.PixelFormat(GpuFormat.R8G8B8A8Unorm)));
    }

    [Fact]
    public void CpuWriteInsideAnImageDirtiesAllOfIt()
    {
        var image = CreateImage(0x10000, 0x10000);
        image.RefreshComplete();

        image.InvalidateCpuWrite(0x10100, 8);

        Assert.True(image.IsCpuDirty);
        Assert.Equal(0UL, image.DirtyStart);
        Assert.Equal(0x10000UL, image.DirtyEnd);
    }

    [Fact]
    public void CpuWriteClearsGpuOwnership()
    {
        var image = CreateImage(0x10000, 0x10000);
        image.MarkGpuModified();

        image.InvalidateCpuWrite(0x10100, 8);

        Assert.False(image.IsGpuModified);
        Assert.True(image.IsCpuDirty);
        Assert.Equal(0UL, image.DirtyStart);
        Assert.Equal(0x10000UL, image.DirtyEnd);
    }

    // Two published runs with a gap between them never claim the gap: a run that starts
    // past the image's first byte made disjoint runs possible, and a widened interval
    // would skip publishing bytes guest memory never got.
    [Fact]
    public void DisjointPublishedRunsNeverClaimTheGapBetweenThem()
    {
        var image = CreateImage(0x10000, 0x10000);
        image.MarkGpuModified();

        image.MarkPublished(0x0000, 0x2000);
        image.MarkPublished(0x8000, 0xC000);

        Assert.False(image.IsPublished(0x2000, 0x8000));
        Assert.False(image.IsPublished(0x0000, 0xC000));
        Assert.True(image.IsPublished(0x8000, 0xC000));

        image.MarkPublished(0x6000, 0x8000);
        Assert.True(image.IsPublished(0x6000, 0xC000));
    }

    // A cached image with no texture: enough for the slot table, which never looks
    // at the native handle.
    private static MetalCachedImage CreateImage(ulong address = 0, ulong size = 0)
    {
        var description = SharpEmu.Libs.Gpu.Images.ImageDescription.Create();
        description.PixelFormat = GpuFormat.R8G8B8A8Unorm;
        description.BytesPerBlock = 4;
        description.Pitch = 1;
        description.Data = new SharpEmu.HLE.GpuMemory.GuestSpan(address, size);
        var shape = new MetalTextureShape(
            MtlFormat.Rgba8Unorm, Mtl.TextureType2D, 1, 1, 1, 1, 1, 1, Mtl.TextureUsageShaderRead);
        return new MetalCachedImage(in description, in shape, 0);
    }
}
