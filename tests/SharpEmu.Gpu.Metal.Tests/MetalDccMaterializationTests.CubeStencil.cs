// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// A layered depth/stencil target bound a slice range at a time, the way GTA V renders
// its shadow cube: DB_Z_*_BASE and DB_STENCIL_*_BASE name slice 0 and DB_DEPTH_VIEW picks
// the slices, so a request for slices 0..N carries N + 1 depth and stencil slices.
public sealed unsafe partial class MetalDccMaterializationTests
{
    private const ulong CubeStencilAddress = DepthAddress + 0x100000;
    private const uint CubeLayers = 6;

    private static ImageRequest CubeDepthRequest(uint baseLayer, uint lastLayer)
    {
        Assert.True(TileGeometry.TryGetDepthSize(AliasWidth, AliasHeight, 0, GuestDepthFormat.Z32Float,
            GuestStencilFormat.Stencil8UInt, false, out var stencilSize, out _, out var depthSize));
        var layers = lastLayer + 1;
        var pitch = TileGeometry.DepthPitch(AliasWidth, 4);
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(DepthAddress, (ulong)depthSize.Size * layers);
        description.Stencil = new GuestSpan(CubeStencilAddress, (ulong)stencilSize.Size * layers);
        description.PixelFormat = GpuFormat.D32SfloatS8Uint;
        description.GuestFormat = GuestPixelFormat.Bits32Float;
        description.Type = GuestImageType.Color2D;
        description.Extent = new GpuExtent3D(AliasWidth, AliasHeight, 1);
        description.Resources = new SubresourceCount(1, layers);
        description.Pitch = pitch;
        description.BytesPerBlock = 4;
        description.Samples = 1;
        description.TileMode = GuestTileMode.Depth;
        description.MipLayout[0] = new MipLevelLayout
        {
            Offset = 0, Size = (ulong)depthSize.Size * layers, Pitch = pitch, Height = AliasHeight,
        };
        var count = lastLayer - baseLayer + 1;
        var view = ImageViewDescription.Default with
        {
            Format = GpuFormat.D32SfloatS8Uint,
            Type = count == 1 ? GpuImageViewType.Type2D : GpuImageViewType.Type2DArray,
            Aspect = GpuImageAspect.DepthBit | GpuImageAspect.StencilBit,
            BaseLayer = baseLayer,
            LayerCount = count,
            Usage = GpuImageUsage.DepthStencilAttachmentBit,
        };
        return new ImageRequest(description, view, ImageRole.DepthTarget);
    }

    // Binding one face of the cube views the cached six-layer image; its stencil plane
    // still spans all six stencil slices, so the image writes back whole and a later bind
    // of slices 0..3 finds it again instead of becoming an alias of it.
    [Fact]
    public void ASliceOfALayeredDepthTargetKeepsTheStencilPlaneOfEveryLayer()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var cubeRequest = CubeDepthRequest(0, CubeLayers - 1);
        var cube = rig.Cache.FindImage(ref cubeRequest);
        rig.Cache.AcquireDepthTargetView(cube, in cubeRequest);
        var plane = rig.Cache.GetImage(cube).Description.Stencil;
        Assert.Equal(new GuestSpan(CubeStencilAddress, cubeRequest.Description.Stencil.Size), plane);

        var faceRequest = CubeDepthRequest(0, 0);
        Assert.Equal(cube, rig.Cache.FindImage(ref faceRequest));
        rig.Cache.AcquireDepthTargetView(cube, in faceRequest);
        Assert.Equal(plane, rig.Cache.GetImage(cube).Description.Stencil);

        var laterRequest = CubeDepthRequest(3, 3);
        Assert.Equal(cube, rig.Cache.FindImage(ref laterRequest));
        rig.Cache.AcquireDepthTargetView(cube, in laterRequest);
        Assert.Equal(plane, rig.Cache.GetImage(cube).Description.Stencil);

        rig.Cache.FlushAllGpuWrites();
        Assert.False(rig.Cache.GetImage(cube).IsGpuModified);
        rig.Harness.Submit();
    }

    // The plane follows a stencil base the guest moves, rebased to the image's slice 0.
    [Fact]
    public void TheImageStencilPlaneIsTheRequestStrideOverEveryImageLayer()
    {
        var cube = CubeDepthRequest(0, CubeLayers - 1).Description;
        var face = CubeDepthRequest(0, 0).Description;
        var slice = face.Stencil.Size;
        face.Stencil = new GuestSpan(CubeStencilAddress + 0x40000, slice);
        Assert.Equal(new GuestSpan(CubeStencilAddress + 0x40000, slice * CubeLayers),
            MetalImageCache.ImageStencilPlane(in cube, in face));

        // A request whose data starts at the image's second slice carries that slice's
        // stencil, so the plane starts one stencil slice earlier.
        var second = face;
        second.Data = new GuestSpan(DepthAddress + (cube.Data.Size / CubeLayers), face.Data.Size);
        second.Stencil = new GuestSpan(CubeStencilAddress + slice, slice);
        Assert.Equal(new GuestSpan(CubeStencilAddress, slice * CubeLayers),
            MetalImageCache.ImageStencilPlane(in cube, in second));
    }
}
