// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// A DCC fast-cleared target over memory another target of the same frame drew into, the
// way Demon's Souls' 4K HUD layer shares its bytes with the 1440p scene's transients. The
// clear is the layer's newest write: the alias hands its bytes over to guest memory, and
// nothing it drew is reloaded over the cleared texture when the layer binds.
public sealed unsafe partial class MetalDccMaterializationTests
{
    // A 64x64 linear RGBA8 layer, and an alias over its last 32 rows.
    private const uint LayerSide = 64;
    private const ulong LayerSize = LayerSide * LayerSide * 4;
    private const ulong TailAliasOffset = LayerSize / 2;
    private const ulong TailAliasSize = LayerSize / 2;

    private static ImageRequest LayerRequest(Rig rig, ulong offset, uint rows, bool dcc)
    {
        var request = rig.Request(ImageRole.ColorTarget);
        var description = request.Description;
        var size = (ulong)LayerSide * rows * 4;
        description.Data = new GuestSpan(ImageAddress + offset, size);
        description.Extent = new GpuExtent3D(LayerSide, rows, 1);
        description.Pitch = LayerSide;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = size, Pitch = LayerSide, Height = rows };
        if (!dcc)
        {
            description.Metadata = default;
        }

        return new ImageRequest(description, request.View, ImageRole.ColorTarget);
    }

    [Fact]
    public void ADccClearTakesTheImageFromAnAliasDrawnBeforeIt()
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var layerRequest = LayerRequest(rig, 0, LayerSide, dcc: true);
        var layer = rig.Cache.FindImage(ref layerRequest);
        rig.Cache.AcquireColorTargetView(layer, in layerRequest);
        var image = rig.Cache.GetImage(layer);

        var aliasRequest = LayerRequest(rig, TailAliasOffset, LayerSide / 2, dcc: false);
        var aliasIdentifier = rig.Cache.FindImage(ref aliasRequest);
        Assert.NotEqual(layer, aliasIdentifier);
        rig.Cache.AcquireColorTargetView(aliasIdentifier, in aliasRequest);
        var alias = rig.Cache.GetImage(aliasIdentifier);
        var drawn = MetalTileOracle.Pattern((int)TailAliasSize, 0x77);
        rig.Harness.Transfer.EncodeGuestToTexture(
            rig.Harness, alias.Description, new SubresourceRange(0, 1, 0, 1),
            new MetalGuestRange(rig.Harness.Buffer(drawn), 0, TailAliasSize, 0), default, alias.Texture, 0, TailAliasSize);
        rig.Harness.Submit();
        rig.Harness.Begin();
        Assert.True(alias.IsGpuModified);

        // The guest fast-clears the layer; its next lookup applies the clear.
        rig.FillMetadata(0x00);
        var lookup = LayerRequest(rig, 0, LayerSide, dcc: true);
        Assert.Equal(layer, rig.Cache.FindImage(ref lookup));
        Assert.Equal(1, rig.Owner.Clears);
        Assert.False(alias.IsGpuModified);
        Assert.True(alias.IsCpuDirty);
        var uploads = rig.Cache.Counters.Uploads;

        rig.Cache.AcquireColorTargetView(layer, in layerRequest);
        rig.Harness.Submit();
        rig.Harness.Begin();

        Assert.Equal(uploads, rig.Cache.Counters.Uploads);
        Assert.False(image.IsCpuDirty);
        Assert.True(image.IsGpuModified);
        // What the alias drew reached guest memory before the clear took the bytes, so its
        // own next use reloads it.
        var guest = new byte[TailAliasSize];
        Assert.True(rig.Backing.TryReadBacking(ImageAddress + TailAliasOffset, guest));
        Assert.Equal(drawn, guest);
        output.WriteLine($"uploads after the clear: {rig.Cache.Counters.Uploads - uploads}");
    }
}
