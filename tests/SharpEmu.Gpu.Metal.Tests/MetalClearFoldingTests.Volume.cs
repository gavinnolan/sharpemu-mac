// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed unsafe partial class MetalClearFoldingTests
{
    private const uint VolumeSide = 4;
    private const uint VolumeDepth = 8;

    // A clear of a run of a volume's depth planes is one layered pass, and it reaches
    // exactly those planes: the run starts at its own plane, not at plane zero.
    [Theory]
    [InlineData(0u, 8u)]
    [InlineData(2u, 4u)]
    [InlineData(7u, 1u)]
    public void AClearOfARunOfVolumePlanesClearsExactlyThosePlanes(uint basePlane, uint planes)
    {
        if (!RequireDevice()) return;
        using var rig = new Rig(Device.Value);
        var host = rig.Host;
        var texture = NewVolume(Device.Value);
        try
        {
            using (host.BeginPreparation())
            {
                var color = default(GpuClearColor);
                color.Float32_0 = 1f;
                color.Float32_3 = 1f;
                ((IMetalImageCacheOwner)host).ClearTextureColor(
                    texture, (ulong)PixelFormatRgba8Unorm, new SubresourceRange(0, 1, basePlane, planes),
                    VolumeSide, VolumeSide, in color);
            }

            host.FlushAndWaitForGpu();
            var bytes = ReadVolume(texture);
            var planeBytes = (int)(VolumeSide * VolumeSide * 4);
            for (var plane = 0u; plane < VolumeDepth; plane++)
            {
                var span = bytes.AsSpan((int)plane * planeBytes, planeBytes);
                var cleared = plane >= basePlane && plane < basePlane + planes;
                for (var offset = 0; offset < span.Length; offset += 4)
                {
                    Assert.Equal(cleared ? ClearedPixel : new byte[4], span.Slice(offset, 4).ToArray());
                }
            }
        }
        finally
        {
            MetalNative.SendVoid(texture, MetalNative.Selector("release"));
        }
    }

    private static nint NewVolume(nint device)
    {
        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLTextureDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setTextureType:"), 7); // MTLTextureType3D
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setPixelFormat:"), PixelFormatRgba8Unorm);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setWidth:"), (nint)VolumeSide);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setHeight:"), (nint)VolumeSide);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setDepth:"), (nint)VolumeDepth);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setUsage:"), 1 | 4); // ShaderRead | RenderTarget
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setStorageMode:"), 0); // Shared
        var texture = MetalNative.Send(device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("release"));
        Assert.NotEqual(0, texture);
        var zeros = new byte[VolumeSide * VolumeSide * VolumeDepth * 4];
        fixed (byte* pointer = zeros)
        {
            ReplaceRegion(texture, MetalNative.Selector("replaceRegion:mipmapLevel:slice:withBytes:bytesPerRow:bytesPerImage:"),
                VolumeRegion, 0, 0, (nint)pointer, VolumeSide * 4, VolumeSide * VolumeSide * 4);
        }

        return texture;
    }

    private static byte[] ReadVolume(nint texture)
    {
        var bytes = new byte[VolumeSide * VolumeSide * VolumeDepth * 4];
        fixed (byte* pointer = bytes)
        {
            GetVolumeBytes(texture, MetalNative.Selector("getBytes:bytesPerRow:bytesPerImage:fromRegion:mipmapLevel:slice:"),
                (nint)pointer, VolumeSide * 4, VolumeSide * VolumeSide * 4, VolumeRegion, 0, 0);
        }

        return bytes;
    }

    private static VolumeRegionValue VolumeRegion => new() { Width = VolumeSide, Height = VolumeSide, Depth = VolumeDepth };

    [StructLayout(LayoutKind.Sequential)]
    private struct VolumeRegionValue
    {
        public nuint X, Y, Z, Width, Height, Depth;
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void ReplaceRegion(nint receiver, nint selector, VolumeRegionValue region, nuint level,
        nuint slice, nint bytes, nuint bytesPerRow, nuint bytesPerImage);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void GetVolumeBytes(nint receiver, nint selector, nint bytes, nuint bytesPerRow,
        nuint bytesPerImage, VolumeRegionValue region, nuint level, nuint slice);
}
