// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// SILENT HILL's shadow map through the cache: a D16 target, sampled through its R16 alias,
// written through the alias by a shader, and rendered into as depth again. The same steps
// run once with the 16-bit hand-off on the device and once through guest memory, and every
// texture and the guest bytes left at the end have to come out identical.
public sealed unsafe partial class MetalDccMaterializationTests
{
    private static readonly nint SelPlaneToBuffer = MetalNative.Selector(
        "copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:" +
        "toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:options:");

    private static readonly nint SelBufferToPlane = MetalNative.Selector(
        "copyFromBuffer:sourceOffset:sourceBytesPerRow:sourceBytesPerImage:sourceSize:" +
        "toTexture:destinationSlice:destinationLevel:destinationOrigin:options:");

    private static ImageRequest Depth16AliasRequest(ImageRole role, uint width, uint height, uint layers)
    {
        Assert.True(TileGeometry.TryGetDepthSize(width, height, 0, GuestDepthFormat.Z16,
            GuestStencilFormat.Invalid, false, out _, out _, out var depthSize));
        var size = (ulong)depthSize.Size * layers;
        var pitch = TileGeometry.DepthPitch(width, sizeof(ushort));
        var depthRole = role == ImageRole.DepthTarget;
        var format = depthRole ? GpuFormat.D16Unorm : GpuFormat.R16Unorm;
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(DepthAddress, size);
        description.PixelFormat = format;
        description.GuestFormat = DepthFormatRule.Find(GuestDepthFormat.Z16)!.GuestFormat;
        description.Type = GuestImageType.Color2D;
        description.Extent = new GpuExtent3D(width, height, 1);
        description.Resources = new SubresourceCount(1, layers);
        description.Pitch = pitch;
        description.BytesPerBlock = sizeof(ushort);
        description.Samples = 1;
        description.TileMode = GuestTileMode.Depth;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = size, Pitch = pitch, Height = height };
        var view = ImageViewDescription.Default with
        {
            Format = format,
            Type = layers == 1 ? GpuImageViewType.Type2D : GpuImageViewType.Type2DArray,
            Aspect = depthRole ? GpuImageAspect.DepthBit : GpuImageAspect.ColorBit,
            LayerCount = layers,
            Usage = role switch
            {
                ImageRole.DepthTarget => GpuImageUsage.DepthStencilAttachmentBit,
                ImageRole.StorageImage => GpuImageUsage.StorageBit,
                _ => GpuImageUsage.SampledBit,
            },
        };
        return new ImageRequest(description, view, role);
    }

    [Theory]
    [InlineData(256u, 128u, 1u)]
    [InlineData(97u, 33u, 2u)]
    [InlineData(512u, 512u, 6u)]
    [InlineData(32u, 32u, 6u)]
    public void AD16ShadowMapAndItsR16AliasEndTheSameOnTheDeviceAsThroughGuestMemory(uint width, uint height, uint layers)
    {
        if (!RequireDevice()) return;
        var device = RunDepth16AliasSequence(onDevice: true, width, height, layers);
        var guest = RunDepth16AliasSequence(onDevice: false, width, height, layers);

        Assert.Equal(guest.Sampled, device.Sampled);
        Assert.Equal(guest.Rendered, device.Rendered);
        Assert.Equal(guest.Guest, device.Guest);
        // And what they agree on is what each pass left, not something stale both kept.
        var texelBytes = (int)((ulong)width * height * sizeof(ushort) * layers);
        Assert.Equal(MetalTileOracle.Pattern(texelBytes, 172), device.Sampled);
        Assert.Equal(MetalTileOracle.Pattern(texelBytes, 173), device.Rendered);
        // Both hand-offs went over the device (the alias write reused the first copy), and
        // none of them through guest memory; the other run never used the device.
        Assert.Equal((2L, 1L, 0L), (device.Publishes, device.Reuses, device.WholePublishes));
        Assert.Equal((0L, 0L), (guest.Publishes, guest.Reuses));
        Assert.NotEqual(0L, guest.WholePublishes);
        output.WriteLine($"{width}x{height}x{layers}: D16 -> R16 -> D16 and 0x{device.Guest.Length:x} guest bytes " +
                         "matched the guest-memory hand-off");
    }

    private static (byte[] Sampled, byte[] Rendered, byte[] Guest, long Publishes, long Reuses, long WholePublishes)
        RunDepth16AliasSequence(bool onDevice, uint width, uint height, uint layers)
    {
        using var rig = new Rig(Device.Value);
        rig.Harness.Transfer.Depth16AliasOnDevice = onDevice;
        var depthRequest = Depth16AliasRequest(ImageRole.DepthTarget, width, height, layers);
        var size = depthRequest.Description.Data.Size;
        var texelBytes = (int)((ulong)width * height * sizeof(ushort) * layers);
        Assert.True(DepthAddress - GuestBase + size <= Backing.Size);
        Assert.True(rig.Backing.TryWriteBacking(DepthAddress, MetalTileOracle.Pattern((int)size, 171)));

        // The shadow pass: the target loads the guest bytes and its draws replace them.
        var depth = rig.Cache.FindImage(ref depthRequest);
        rig.Cache.AcquireDepthTargetView(depth, in depthRequest);
        var depthTexture = rig.Cache.GetImage(depth).Texture;
        FillPlane(rig.Harness, depthRequest.Description, depthTexture, MetalTileOracle.Pattern(texelBytes, 172));

        // The lighting pass samples it through the alias.
        var textureRequest = Depth16AliasRequest(ImageRole.Texture, width, height, layers);
        var alias = rig.Cache.FindImage(ref textureRequest);
        Assert.NotEqual(depth, alias);
        rig.Cache.AcquireTextureView(alias, in textureRequest);
        var sampled = ReadPlane(rig.Harness, textureRequest.Description, rig.Cache.GetImage(alias).Texture);

        // A shader stores into the map through the alias...
        var storageRequest = Depth16AliasRequest(ImageRole.StorageImage, width, height, layers);
        Assert.Equal(alias, rig.Cache.FindImage(ref storageRequest));
        rig.Cache.AcquireTextureView(alias, in storageRequest);
        FillPlane(rig.Harness, storageRequest.Description, rig.Cache.GetImage(alias).Texture,
            MetalTileOracle.Pattern(texelBytes, 173));

        // ...and the next shadow pass renders on top of what it stored.
        Assert.Equal(depth, rig.Cache.FindImage(ref depthRequest));
        rig.Cache.AcquireDepthTargetView(depth, in depthRequest);
        var rendered = ReadPlane(rig.Harness, depthRequest.Description, rig.Cache.GetImage(depth).Texture);

        var publishes = rig.Cache.TakeDeviceAliasPublishes();
        var reuses = rig.Cache.TakeDeviceAliasReuses();
        var whole = rig.Cache.AliasCounters.Published - publishes - reuses;
        rig.Cache.FlushAllGpuWrites();
        rig.Harness.Submit();
        var guest = new byte[size];
        Assert.True(rig.Backing.TryReadBacking(DepthAddress, guest));
        return (sampled, rendered, guest, publishes, reuses, whole);
    }

    // Every texel of every layer straight into the texture, recorded into the open command
    // buffer the way a draw would be.
    private static void FillPlane(MetalTransferHarness harness, in ImageDescription description, nint texture, byte[] texels)
    {
        var rowBytes = (ulong)description.Extent.Width * description.BytesPerBlock;
        var sliceBytes = rowBytes * description.Extent.Height;
        var buffer = harness.Buffer(texels);
        var blit = harness.Tiler.BeginBlit(harness);
        for (uint layer = 0; layer < description.TransferLayers; layer++)
        {
            MetalNative.SendCopyBufferToTextureOptions(blit, SelBufferToPlane,
                buffer, (nuint)(sliceBytes * layer), (nuint)rowBytes, (nuint)sliceBytes,
                new MtlSize { Width = description.Extent.Width, Height = description.Extent.Height, Depth = 1 },
                texture, layer, 0, default, (nuint)MtlBlitOption.None);
        }

        MetalTiler.EndEncoding(blit);
    }

    // Every texel of every layer as the texture holds it once the open command buffer has
    // run; a new one is left open for the cache.
    private static byte[] ReadPlane(MetalTransferHarness harness, in ImageDescription description, nint texture)
    {
        var rowBytes = (ulong)description.Extent.Width * description.BytesPerBlock;
        var sliceBytes = rowBytes * description.Extent.Height;
        var buffer = harness.Buffer(sliceBytes * description.TransferLayers);
        var blit = harness.Tiler.BeginBlit(harness);
        for (uint layer = 0; layer < description.TransferLayers; layer++)
        {
            MetalNative.SendCopyTextureToBufferOptions(blit, SelPlaneToBuffer,
                texture, layer, 0, default,
                new MtlSize { Width = description.Extent.Width, Height = description.Extent.Height, Depth = 1 },
                buffer, (nuint)(sliceBytes * layer), (nuint)rowBytes, (nuint)sliceBytes, (nuint)MtlBlitOption.None);
        }

        MetalTiler.EndEncoding(blit);
        harness.Submit();
        harness.Begin();
        return harness.Read(buffer, 0, sliceBytes * description.TransferLayers);
    }
}
