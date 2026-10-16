// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed unsafe partial class MetalDccMaterializationTests
{
    private const uint VolumePlanes = 4;

    // Adjacent planes of a volume that take the same DCC clear are cleared by one pass
    // over all of them; a plane that holds no clear splits the run around it.
    [Theory]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 }, new uint[] { 0, 4 })]
    [InlineData(new byte[] { 0x00, 0x00, 0x55, 0x00 }, new uint[] { 0, 2, 3, 1 })]
    public void MaterializeDcc_AdjacentVolumePlanesClearInOnePass(byte[] codes, uint[] expectedRuns)
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        FillVolumeMetadata(rig, [0x55, 0x55, 0x55, 0x55]);
        var request = VolumeRequest();
        rig.Cache.FindImage(ref request);
        Assert.Equal(0, rig.Owner.Clears);

        FillVolumeMetadata(rig, codes);
        request = VolumeRequest();
        rig.Cache.FindImage(ref request);

        var runs = rig.Owner.ClearRanges.SelectMany(static range => new[] { range.BaseLayer, range.LayerCount }).ToArray();
        Assert.Equal(expectedRuns, runs);
        Assert.Equal(VolumePlanes - (uint)(expectedRuns.Length / 2) - (uint)codes.Count(static code => code != 0),
            (uint)rig.Cache.TakeDccSlicesCoalesced());
    }

    private static void FillVolumeMetadata(Rig rig, byte[] codes)
    {
        for (var plane = 0; plane < codes.Length; plane++)
        {
            rig.Backing.TryWriteBacking(
                MetadataAddress + (ulong)plane * SliceSize, Enumerable.Repeat(codes[plane], (int)SliceSize).ToArray());
        }
    }

    private static ImageRequest VolumeRequest()
    {
        const uint Side = 4;
        const ulong PlaneBytes = Side * Side * 4;
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(ImageAddress, PlaneBytes * VolumePlanes);
        description.PixelFormat = GpuFormat.R8G8B8A8Unorm;
        description.GuestFormat = GuestPixelFormat.Bits8_8_8_8UNorm;
        description.Type = GuestImageType.Color3D;
        description.Extent = new GpuExtent3D(Side, Side, VolumePlanes);
        description.Resources = new SubresourceCount(1, 1);
        description.Pitch = Side;
        description.BytesPerBlock = 4;
        description.Samples = 1;
        description.TileMode = GuestTileMode.Linear;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = PlaneBytes * VolumePlanes, Pitch = Side, Height = Side };
        description.Metadata.Kind = MetadataKind.Dcc;
        description.Metadata.Range = new GuestSpan(MetadataAddress, SliceSize * VolumePlanes);
        description.Metadata.DccAlphaMsb = true;
        var view = ImageViewDescription.Default with
        {
            Format = GpuFormat.R8G8B8A8Unorm,
            Type = GpuImageViewType.Type3D,
            Aspect = GpuImageAspect.ColorBit,
            LayerCount = 1,
            Usage = GpuImageUsage.SampledBit,
        };
        return new ImageRequest(description, view, ImageRole.Texture);
    }
}
