// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// A layered depth target with HTILE bound a slice range at a time: DB_HTILE_DATA_BASE
// names slice 0 like DB_Z_*_BASE does, so a request for slices 0..N carries N + 1 HTILE
// slices.
public sealed unsafe partial class MetalDccMaterializationTests
{
    private const ulong CubeHtileAddress = DepthAddress + 0x200000;

    private static ImageRequest CubeHtileRequest(uint baseLayer, uint lastLayer)
    {
        Assert.True(TileGeometry.TryGetDepthSize(AliasWidth, AliasHeight, 0, GuestDepthFormat.Z32Float,
            GuestStencilFormat.Stencil8UInt, true, out _, out var htileSize, out _));
        var request = CubeDepthRequest(baseLayer, lastLayer);
        request.Description.Metadata.Range = new GuestSpan(CubeHtileAddress, (ulong)htileSize.Size * (lastLayer + 1));
        request.Description.Metadata.Kind = MetadataKind.Htile;
        return request;
    }

    // Binding one face, then four faces, of the cube views the cached six-layer image; its
    // HTILE range still spans all six HTILE slices from slice 0.
    [Fact]
    public void ASliceOfALayeredDepthTargetKeepsTheHtileOfEveryLayer()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var cubeRequest = CubeHtileRequest(0, CubeLayers - 1);
        var cube = rig.Cache.FindImage(ref cubeRequest);
        rig.Cache.AcquireDepthTargetView(cube, in cubeRequest);
        var htile = rig.Cache.GetImage(cube).Description.Metadata.Range;
        Assert.Equal(new GuestSpan(CubeHtileAddress, cubeRequest.Description.Metadata.Range.Size), htile);

        var faceRequest = CubeHtileRequest(0, 0);
        Assert.Equal(cube, rig.Cache.FindImage(ref faceRequest));
        rig.Cache.AcquireDepthTargetView(cube, in faceRequest);
        Assert.Equal(htile, rig.Cache.GetImage(cube).Description.Metadata.Range);

        var facesRequest = CubeHtileRequest(0, 3);
        Assert.Equal(cube, rig.Cache.FindImage(ref facesRequest));
        rig.Cache.AcquireDepthTargetView(cube, in facesRequest);
        Assert.Equal(htile, rig.Cache.GetImage(cube).Description.Metadata.Range);
        Assert.Equal(MetadataKind.Htile, rig.Cache.GetImage(cube).Description.Metadata.Kind);

        rig.Cache.FlushAllGpuWrites();
        Assert.False(rig.Cache.GetImage(cube).IsGpuModified);
        rig.Harness.Submit();
    }

    // The range is the request's HTILE stride over every image layer, rebased to the
    // image's slice 0; a request over every layer keeps its own range.
    [Fact]
    public void TheImageHtileRangeIsTheRequestStrideOverEveryImageLayer()
    {
        var cube = CubeHtileRequest(0, CubeLayers - 1).Description;
        Assert.Equal(cube.Metadata.Range, MetalImageCache.ImageHtileRange(in cube, in cube));

        var face = CubeHtileRequest(0, 0).Description;
        var slice = face.Metadata.Range.Size;
        Assert.Equal(new GuestSpan(CubeHtileAddress, slice * CubeLayers),
            MetalImageCache.ImageHtileRange(in cube, in face));

        // A request whose data starts at the image's second slice carries that slice's
        // HTILE, so the range starts one HTILE slice earlier.
        var second = face;
        second.Data = new GuestSpan(DepthAddress + (cube.Data.Size / CubeLayers), face.Data.Size);
        second.Metadata.Range = new GuestSpan(CubeHtileAddress + slice, slice);
        Assert.Equal(new GuestSpan(CubeHtileAddress, slice * CubeLayers),
            MetalImageCache.ImageHtileRange(in cube, in second));
    }
}
