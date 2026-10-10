// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// TEMPORARY image transfer. Linear surfaces move entirely on the GPU, as a blit
/// between the guest window and the texture. Tiled surfaces go through the existing
/// CPU detiler (<see cref="GnmTiling"/>) into a staging slice of the upload ring and
/// are then blitted, and a tiled writeback retiles on the CPU with the same
/// addressing formula after waiting for the GPU.
///
/// This whole file is replaced by the real <c>MetalImageTransfer</c> (the tiler
/// workstream: MSL detile/retile kernels for every TileBlockKind). Nothing outside
/// this file knows it exists: the cache only sees <see cref="IMetalImageTransfer"/>.
/// </summary>
internal sealed class MetalImageTransferStub : IMetalImageTransfer
{
    // MTLBlitOption.
    private const nuint BlitOptionNone = 0;
    private const nuint BlitOptionDepthFromDepthStencil = 1 << 1;
    private const nuint BlitOptionStencilFromDepthStencil = 1 << 2;

    private static readonly nint SelBlitEncoder = MetalNative.Selector("blitCommandEncoder");
    private static readonly nint SelEndEncoding = MetalNative.Selector("endEncoding");
    private static readonly nint SelCopyBufferToTexture = MetalNative.Selector(
        "copyFromBuffer:sourceOffset:sourceBytesPerRow:sourceBytesPerImage:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:options:");
    private static readonly nint SelCopyTextureToBuffer = MetalNative.Selector(
        "copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:options:");

    private readonly MetalGuestMemory _guestMemory;
    private readonly IGuestBackedSpace _backing;
    private readonly Action _waitForGpu;

    public MetalImageTransferStub(MetalGuestMemory guestMemory, IGuestBackedSpace backing, Action waitForGpu)
    {
        _guestMemory = guestMemory;
        _backing = backing;
        _waitForGpu = waitForGpu;
    }

    public MetalTextureShape ShapeFor(in ImageDescription description)
    {
        var format = MetalV2Formats.PixelFormat(description.PixelFormat);
        var layers = description.IsVolume ? 1 : Math.Max(description.Resources.Layers, 1);
        var samples = Math.Max(description.Samples, 1);
        var type = MetalV2Formats.TextureType(HostImageType(description.Type), layers, samples);
        var depth = description.IsVolume ? Math.Max(description.Extent.Depth, 1) : 1;
        var usage = Mtl.TextureUsageShaderRead;
        var isBlock = description.IsBlock;
        var isDepth = MetalV2Formats.IsDepthFormat(format) || MetalV2Formats.HasStencil(format);
        if (!isBlock)
        {
            // Every non-compressed image can be an attachment: the metadata clears and
            // the format-reinterpreting clears both go through a render pass.
            usage |= Mtl.TextureUsageRenderTarget;
        }

        if (!isDepth)
        {
            usage |= Mtl.TextureUsagePixelFormatView;
        }

        if (!isBlock && !isDepth && samples == 1 && !IsSrgb(format))
        {
            usage |= Mtl.TextureUsageShaderWrite;
        }

        return new MetalTextureShape(
            format,
            type,
            Math.Max(description.Extent.Width, 1),
            Math.Max(description.Extent.Height, 1),
            depth,
            Math.Max(description.Resources.Levels, 1),
            layers,
            samples,
            usage);
    }

    private static GpuImageType HostImageType(GuestImageType type) => type switch
    {
        GuestImageType.Color1D => GpuImageType.Type1D,
        GuestImageType.Color3D => GpuImageType.Type3D,
        _ => GpuImageType.Type2D,
    };

    private static bool IsSrgb(ulong format) => format is MtlFormat.R8UnormSrgb or MtlFormat.Rg8UnormSrgb or
        MtlFormat.Rgba8UnormSrgb or MtlFormat.Bgra8UnormSrgb or MtlFormat.Bc1RgbaSrgb or MtlFormat.Bc2RgbaSrgb or
        MtlFormat.Bc3RgbaSrgb or MtlFormat.Bc7RgbaUnormSrgb;

    // One subresource of a guest surface: where its tiled bytes are and how big its element grid is.
    private readonly record struct Subresource(
        uint Level,
        uint Layer,
        ulong GuestOffset,
        uint ElementsWide,
        uint ElementsHigh,
        uint TexelsWide,
        uint TexelsHigh,
        uint BytesPerElement);

    public void EncodeGuestToTexture(
        IMetalSubmissionContext context,
        in ImageDescription description,
        SubresourceRange range,
        MetalGuestRange source,
        MetalGuestRange stencilSource,
        nint texture)
    {
        if (texture == 0 || !source.IsValid)
        {
            return;
        }

        var encoder = MetalNative.Send(context.CommandBuffer, SelBlitEncoder);
        if (encoder == 0)
        {
            throw GpuFatal.Fatal("A Metal blit encoder could not be created for an image upload.");
        }

        try
        {
            var depthFormat = MetalV2Formats.PixelFormat(description.PixelFormat);
            var hasStencilPlane = MetalV2Formats.HasStencil(depthFormat) && stencilSource.IsValid;
            var depthOption = MetalV2Formats.HasStencil(depthFormat) ? BlitOptionDepthFromDepthStencil : BlitOptionNone;
            var depthBytes = DepthPlaneBytes(description, depthFormat);
            foreach (var subresource in Subresources(description, range))
            {
                var plane = subresource with { BytesPerElement = depthBytes != 0 ? depthBytes : subresource.BytesPerElement };
                var bytesPerRow = (ulong)plane.ElementsWide * plane.BytesPerElement;
                var planeBytes = bytesPerRow * plane.ElementsHigh;
                var staged = StageUpload(in description, in subresource, in source, in plane, depthBytes);
                if (!staged.IsValid)
                {
                    continue;
                }

                CopyBufferToTexture(encoder, staged, bytesPerRow, planeBytes, in plane, texture, description, depthOption);
            }

            if (hasStencilPlane)
            {
                foreach (var subresource in Subresources(description, range))
                {
                    var plane = subresource with { BytesPerElement = 1 };
                    var bytesPerRow = (ulong)plane.ElementsWide;
                    var planeBytes = bytesPerRow * plane.ElementsHigh;
                    var staged = StageStencilUpload(in description, in subresource, in stencilSource, in plane);
                    if (!staged.IsValid)
                    {
                        continue;
                    }

                    CopyBufferToTexture(
                        encoder, staged, bytesPerRow, planeBytes, in plane, texture, description,
                        BlitOptionStencilFromDepthStencil);
                }
            }
        }
        finally
        {
            MetalNative.SendVoid(encoder, SelEndEncoding);
        }
    }

    public void EncodeTextureToGuest(
        IMetalSubmissionContext context,
        in ImageDescription description,
        SubresourceRange range,
        nint texture,
        MetalGuestRange destination,
        MetalGuestRange stencilDestination)
    {
        if (texture == 0 || !destination.IsValid)
        {
            return;
        }

        var format = MetalV2Formats.PixelFormat(description.PixelFormat);
        if (MetalV2Formats.HasStencil(format) || description.Samples > 1)
        {
            // A combined depth/stencil or multisampled readback is the real tiler's job.
            MetalV2Diagnostics.WarnOnce(
                "writeback-depth-stencil",
                $"A depth/stencil or multisampled image is not written back by the temporary transfer: " +
                $"address=0x{description.Data.Address:X16} format={(int)description.PixelFormat} samples={description.Samples}.");
            return;
        }

        var depthBytes = DepthPlaneBytes(description, format);
        var linear = description.TileMode == GuestTileMode.Linear;
        var encoder = MetalNative.Send(context.CommandBuffer, SelBlitEncoder);
        if (encoder == 0)
        {
            throw GpuFatal.Fatal("A Metal blit encoder could not be created for an image writeback.");
        }

        var retiles = linear ? null : new List<(Subresource Plane, MetalGuestRange Staging)>();
        try
        {
            foreach (var subresource in Subresources(description, range))
            {
                var plane = subresource with { BytesPerElement = depthBytes != 0 ? depthBytes : subresource.BytesPerElement };
                var bytesPerRow = (ulong)plane.ElementsWide * plane.BytesPerElement;
                var planeBytes = bytesPerRow * plane.ElementsHigh;
                if (planeBytes == 0)
                {
                    continue;
                }

                MetalGuestRange target;
                if (linear)
                {
                    if (plane.GuestOffset + planeBytes > destination.Size)
                    {
                        continue;
                    }

                    target = new MetalGuestRange(
                        destination.Buffer,
                        destination.Offset + plane.GuestOffset,
                        planeBytes,
                        destination.GpuAddress + plane.GuestOffset);
                }
                else
                {
                    target = _guestMemory.Allocate(planeBytes, 256, out var staging);
                    staging.Clear();
                    retiles!.Add((plane, target));
                }

                MetalV2Native.SendCopyTextureToBufferOptions(
                    encoder,
                    SelCopyTextureToBuffer,
                    texture,
                    plane.Layer,
                    plane.Level,
                    default,
                    new MtlSize
                    {
                        Width = Math.Max(plane.TexelsWide, 1),
                        Height = Math.Max(plane.TexelsHigh, 1),
                        Depth = 1,
                    },
                    target.Buffer,
                    (nuint)target.Offset,
                    (nuint)bytesPerRow,
                    (nuint)planeBytes,
                    BlitOptionNone);
            }
        }
        finally
        {
            MetalNative.SendVoid(encoder, SelEndEncoding);
        }

        if (retiles is null || retiles.Count == 0)
        {
            return;
        }

        // The CPU retile must see the blit's result, so the recorded work runs first.
        _waitForGpu();
        var sliceStride = SliceStride(description);
        foreach (var (plane, staging) in retiles)
        {
            var linearBytes = _guestMemory.RingSpan(in staging);
            if (linearBytes.IsEmpty)
            {
                continue;
            }

            var tiledSize = TiledSubresourceBytes(description, plane);
            var tiled = new byte[tiledSize];
            var guestAddress = description.Data.Address + plane.Layer * sliceStride +
                description.MipLayout[(int)plane.Level].Offset;
            if (!_backing.TryReadBacking(guestAddress, tiled))
            {
                continue;
            }

            if (!Retile(description, plane, linearBytes, tiled))
            {
                continue;
            }

            _ = _backing.TryWriteBacking(guestAddress, tiled);
        }
    }

    // The depth plane's guest byte size, or zero when the image is not a depth image.
    private static uint DepthPlaneBytes(in ImageDescription description, ulong format)
    {
        if (!MetalV2Formats.IsDepthFormat(format))
        {
            return 0;
        }

        return format == MtlFormat.Depth16Unorm ? 2u : 4u;
    }

    private static ulong SliceStride(in ImageDescription description) =>
        description.IsVolume || description.Resources.Layers <= 1 || description.Data.Size == 0
            ? description.Data.Size
            : description.Data.Size / description.Resources.Layers;

    private static ulong TiledSubresourceBytes(in ImageDescription description, in Subresource plane)
    {
        var layout = description.MipLayout[(int)plane.Level];
        var layers = description.IsVolume ? 1u : Math.Max(description.Resources.Layers, 1);
        var size = layout.Size != 0 ? layout.Size : description.Data.Size;
        if (!description.IsVolume && layers > 1 && size % layers == 0)
        {
            size /= layers;
        }

        var minimum = (ulong)plane.ElementsWide * plane.ElementsHigh * plane.BytesPerElement;
        return Math.Max(size, minimum);
    }

    // Every (level, layer) pair of the range, with its guest offset and element grid.
    private static List<Subresource> Subresources(in ImageDescription description, SubresourceRange range)
    {
        var result = new List<Subresource>();
        var blockShift = description.IsBlock ? 2 : 0;
        var bytesPerElement = Math.Max(description.BytesPerBlock, 1);
        var sliceStride = SliceStride(description);
        var lastLevel = Math.Min(range.BaseLevel + range.LevelCount, Math.Max(description.Resources.Levels, 1));
        var lastLayer = Math.Min(range.BaseLayer + range.LayerCount, Math.Max(description.TransferLayers, 1));
        for (var level = range.BaseLevel; level < lastLevel; level++)
        {
            var layout = description.MipLayout[(int)Math.Min(level, ImageDescription.MaxLevels - 1)];
            var pitch = layout.Pitch != 0 ? layout.Pitch : Math.Max(description.Pitch >> (int)level, 1);
            var height = layout.Height != 0 ? layout.Height : Math.Max(description.Extent.Height >> (int)level, 1);
            var texelsWide = Math.Max(description.Extent.Width >> (int)level, 1);
            var texelsHigh = Math.Max(description.Extent.Height >> (int)level, 1);
            var elementsWide = Math.Max(pitch >> blockShift, 1u);
            var elementsHigh = Math.Max(height >> blockShift, 1u);
            for (var layer = range.BaseLayer; layer < lastLayer; layer++)
            {
                result.Add(new Subresource(
                    level,
                    layer,
                    layer * sliceStride + layout.Offset,
                    elementsWide,
                    elementsHigh,
                    texelsWide,
                    texelsHigh,
                    bytesPerElement));
            }
        }

        return result;
    }

    // Linear guest bytes bind straight from the window; tiled bytes are detiled into
    // a ring slice first. Either way the result is a buffer the blit can read.
    private MetalGuestRange StageUpload(
        in ImageDescription description,
        in Subresource subresource,
        in MetalGuestRange source,
        in Subresource plane,
        uint depthBytes)
    {
        var bytesPerRow = (ulong)plane.ElementsWide * plane.BytesPerElement;
        var planeBytes = bytesPerRow * plane.ElementsHigh;
        if (planeBytes == 0)
        {
            return default;
        }

        var guestBytesPerElement = depthBytes != 0 ? GuestDepthBytes(description) : plane.BytesPerElement;
        var needsWiden = depthBytes != 0 && guestBytesPerElement != depthBytes;
        if (description.TileMode == GuestTileMode.Linear && !needsWiden)
        {
            return plane.GuestOffset + planeBytes <= source.Size
                ? new MetalGuestRange(
                    source.Buffer,
                    source.Offset + plane.GuestOffset,
                    planeBytes,
                    source.GpuAddress + plane.GuestOffset)
                : default;
        }

        var tiledBytes = (ulong)plane.ElementsWide * plane.ElementsHigh * guestBytesPerElement;
        var guestAddress = description.Data.Address + subresource.GuestOffset;
        var tiled = new byte[Math.Max(TiledSubresourceBytes(description, subresource), tiledBytes)];
        if (!_backing.TryReadBacking(guestAddress, tiled))
        {
            return default;
        }

        var staged = _guestMemory.Allocate(planeBytes, 256, out var span);
        span.Clear();
        if (description.TileMode == GuestTileMode.Linear)
        {
            WidenDepth(tiled, span, plane, guestBytesPerElement);
            return staged;
        }

        if (needsWiden)
        {
            var intermediate = new byte[plane.ElementsWide * plane.ElementsHigh * guestBytesPerElement];
            if (!GnmTiling.TryDetile(
                    tiled, intermediate, (uint)description.TileMode,
                    (int)plane.ElementsWide, (int)plane.ElementsHigh, (int)guestBytesPerElement))
            {
                return default;
            }

            WidenDepth(intermediate, span, plane, guestBytesPerElement);
            return staged;
        }

        if (!GnmTiling.TryDetile(
                tiled, span, (uint)description.TileMode,
                (int)plane.ElementsWide, (int)plane.ElementsHigh, (int)plane.BytesPerElement))
        {
            // An unsupported swizzle uploads the raw bytes; the surface looks tiled
            // but nothing is lost, and the real tiler covers the mode later.
            MetalV2Diagnostics.WarnOnce(
                $"detile:{(uint)description.TileMode}:{plane.BytesPerElement}",
                $"The temporary transfer cannot detile this surface; uploading raw bytes: " +
                $"tile={(uint)description.TileMode} bytesPerElement={plane.BytesPerElement}.");
            tiled.AsSpan(0, (int)Math.Min((ulong)tiled.Length, planeBytes)).CopyTo(span);
        }

        return staged;
    }

    private MetalGuestRange StageStencilUpload(
        in ImageDescription description,
        in Subresource subresource,
        in MetalGuestRange stencilSource,
        in Subresource plane)
    {
        var planeBytes = (ulong)plane.ElementsWide * plane.ElementsHigh;
        if (planeBytes == 0)
        {
            return default;
        }

        if (description.TileMode == GuestTileMode.Linear)
        {
            return plane.GuestOffset + planeBytes <= stencilSource.Size
                ? new MetalGuestRange(
                    stencilSource.Buffer,
                    stencilSource.Offset + plane.GuestOffset,
                    planeBytes,
                    stencilSource.GpuAddress + plane.GuestOffset)
                : default;
        }

        var tiled = new byte[description.Stencil.Size];
        if (!_backing.TryReadBacking(description.Stencil.Address, tiled))
        {
            return default;
        }

        var staged = _guestMemory.Allocate(planeBytes, 256, out var span);
        span.Clear();
        if (!GnmTiling.TryDetile(
                tiled, span, (uint)description.TileMode, (int)plane.ElementsWide, (int)plane.ElementsHigh, 1))
        {
            tiled.AsSpan(0, (int)Math.Min((ulong)tiled.Length, planeBytes)).CopyTo(span);
        }

        _ = subresource;
        return staged;
    }

    private static uint GuestDepthBytes(in ImageDescription description) => Math.Max(description.BytesPerBlock, 1);

    // Metal has no 16-bit depth with stencil, so a Z16 guest plane widens to float32.
    private static void WidenDepth(ReadOnlySpan<byte> source, Span<byte> destination, in Subresource plane, uint guestBytes)
    {
        var elements = (int)(plane.ElementsWide * plane.ElementsHigh);
        if (guestBytes == plane.BytesPerElement)
        {
            var count = Math.Min(source.Length, destination.Length);
            source[..count].CopyTo(destination[..count]);
            return;
        }

        for (var index = 0; index < elements; index++)
        {
            var sourceOffset = index * (int)guestBytes;
            var destinationOffset = index * (int)plane.BytesPerElement;
            if (sourceOffset + 2 > source.Length || destinationOffset + 4 > destination.Length)
            {
                break;
            }

            var value = (ushort)(source[sourceOffset] | (source[sourceOffset + 1] << 8));
            var bits = DepthFormatRule.EncodeD16AsD32(value);
            destination[destinationOffset] = (byte)bits;
            destination[destinationOffset + 1] = (byte)(bits >> 8);
            destination[destinationOffset + 2] = (byte)(bits >> 16);
            destination[destinationOffset + 3] = (byte)(bits >> 24);
        }
    }

    private static void CopyBufferToTexture(
        nint encoder,
        in MetalGuestRange source,
        ulong bytesPerRow,
        ulong bytesPerImage,
        in Subresource plane,
        nint texture,
        in ImageDescription description,
        nuint options)
    {
        MetalV2Native.SendCopyBufferToTextureOptions(
            encoder,
            SelCopyBufferToTexture,
            source.Buffer,
            (nuint)source.Offset,
            (nuint)bytesPerRow,
            (nuint)bytesPerImage,
            new MtlSize
            {
                Width = Math.Max(plane.TexelsWide, 1),
                Height = Math.Max(plane.TexelsHigh, 1),
                Depth = 1,
            },
            texture,
            description.IsVolume ? 0 : plane.Layer,
            plane.Level,
            description.IsVolume ? new MtlOrigin { Z = plane.Layer } : default,
            options);
    }

    // The inverse of GnmTiling's detile addressing, which the DetileParams contract
    // documents: tiledByte(x, y) -> linearByte(x, y), copied the other way round.
    private static bool Retile(
        in ImageDescription description,
        in Subresource plane,
        ReadOnlySpan<byte> linear,
        Span<byte> tiled)
    {
        var parameters = GnmTiling.GetDetileParams(
            (uint)description.TileMode,
            (int)plane.BytesPerElement,
            (int)plane.ElementsWide,
            (int)plane.ElementsHigh);
        if (!parameters.IsSupported)
        {
            MetalV2Diagnostics.WarnOnce(
                $"retile:{(uint)description.TileMode}:{plane.BytesPerElement}",
                $"The temporary transfer cannot retile this surface; the writeback is skipped: " +
                $"tile={(uint)description.TileMode} bytesPerElement={plane.BytesPerElement}.");
            return false;
        }

        var bytes = parameters.BytesPerElement;
        var exactXor = parameters.Equation == DetileEquation.ExactXor;
        for (var y = 0; y < parameters.ElementsHigh; y++)
        {
            var blockY = y / parameters.BlockHeight;
            var inY = y % parameters.BlockHeight;
            var yTerm = exactXor ? parameters.YByteTerm[y & parameters.YMask] : 0;
            for (var x = 0; x < parameters.ElementsWide; x++)
            {
                var blockX = x / parameters.BlockWidth;
                var inBlockByte = exactXor
                    ? parameters.XByteTerm[x & parameters.XMask] ^ yTerm
                    : parameters.BlockTable[inY * parameters.BlockWidth + (x % parameters.BlockWidth)] * bytes;
                var tiledByte = ((long)blockY * parameters.BlocksPerRow + blockX) * parameters.BlockBytes + inBlockByte;
                var linearByte = ((long)y * parameters.ElementsWide + x) * bytes;
                if (tiledByte < 0 || tiledByte + bytes > tiled.Length || linearByte + bytes > linear.Length)
                {
                    continue;
                }

                linear.Slice((int)linearByte, bytes).CopyTo(tiled.Slice((int)tiledByte, bytes));
            }
        }

        return true;
    }
}
