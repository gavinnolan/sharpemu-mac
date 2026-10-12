// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// The same rig drives the cache's alias hand-off: a depth target and the R32 texture a
// shader samples it through, over the same guest bytes.
public sealed unsafe partial class MetalDccMaterializationTests
{
    private const ulong DepthAddress = GuestBase + 0x100000;
    private const uint AliasWidth = 128;
    private const uint AliasHeight = 64;

    private static ImageRequest DepthAliasRequest(ImageRole role, bool stencil = false)
    {
        var depthRole = role == ImageRole.DepthTarget;
        Assert.True(TileGeometry.TryGetDepthSize(AliasWidth, AliasHeight, 0, GuestDepthFormat.Z32Float,
            stencil ? GuestStencilFormat.Stencil8UInt : GuestStencilFormat.Invalid, false,
            out var stencilSize, out _, out var depthSize));
        var pitch = TileGeometry.DepthPitch(AliasWidth, 4);
        var description = ImageDescription.Create();
        description.Data = new GuestSpan(DepthAddress, depthSize.Size);
        if (depthRole && stencil)
        {
            description.Stencil = new GuestSpan(DepthAddress + 0x100000, stencilSize.Size);
        }

        var format = depthRole
            ? stencil ? GpuFormat.D32SfloatS8Uint : GpuFormat.D32Sfloat
            : GpuFormat.R32Sfloat;
        description.PixelFormat = format;
        description.GuestFormat = GuestPixelFormat.Bits32Float;
        description.Type = GuestImageType.Color2D;
        description.Extent = new GpuExtent3D(AliasWidth, AliasHeight, 1);
        description.Resources = new SubresourceCount(1, 1);
        description.Pitch = pitch;
        description.BytesPerBlock = 4;
        description.Samples = 1;
        description.TileMode = GuestTileMode.Depth;
        description.MipLayout[0] = new MipLevelLayout { Offset = 0, Size = depthSize.Size, Pitch = pitch, Height = AliasHeight };
        var view = ImageViewDescription.Default with
        {
            Format = format,
            Type = GpuImageViewType.Type2D,
            Aspect = depthRole ? GpuImageAspect.DepthBit : GpuImageAspect.ColorBit,
            LayerCount = 1,
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
    [InlineData(false)]
    [InlineData(true)]
    public void ADepthTargetSampledThroughItsR32AliasIsHandedOverOnTheDevice(bool stencil)
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var depthRequest = DepthAliasRequest(ImageRole.DepthTarget, stencil);
        var depth = rig.Cache.FindImage(ref depthRequest);
        rig.Cache.AcquireDepthTargetView(depth, in depthRequest);
        Assert.True(rig.Cache.GetImage(depth).IsGpuModified);

        var textureRequest = DepthAliasRequest(ImageRole.Texture);
        var alias = rig.Cache.FindImage(ref textureRequest);
        Assert.NotEqual(depth, alias);
        rig.Cache.AcquireTextureView(alias, in textureRequest);

        // Served on the device: the alias owes guest memory no upload, and the depth
        // target still owns the bytes, so nothing was written back for it.
        Assert.Equal(1, rig.Cache.TakeDeviceAliasPublishes());
        Assert.False(rig.Cache.GetImage(alias).IsCpuDirty);
        Assert.True(rig.Cache.GetImage(depth).IsGpuModified);

        // A write through the alias is handed the depth target's contents first and then
        // takes the bytes from it, exactly as it does through guest memory.
        var storageRequest = DepthAliasRequest(ImageRole.StorageImage);
        Assert.Equal(alias, rig.Cache.FindImage(ref storageRequest));
        rig.Cache.AcquireTextureView(alias, in storageRequest);
        Assert.Equal(1, rig.Cache.TakeDeviceAliasPublishes());
        Assert.False(rig.Cache.GetImage(depth).IsGpuModified);
        Assert.True(rig.Cache.GetImage(depth).IsCpuDirty);
        Assert.True(rig.Cache.GetImage(alias).IsGpuModified);

        // The way back is a converting upload into a depth texture, which stays on the
        // guest-memory path.
        rig.Cache.AcquireDepthTargetView(depth, in depthRequest);
        Assert.Equal(0, rig.Cache.TakeDeviceAliasPublishes());
        rig.Harness.Submit();
    }
}
