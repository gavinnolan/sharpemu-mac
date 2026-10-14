// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Gpu.Metal.V2;

// Image lookups, render-target attachment acquisition, and the metadata operations
// a guest fill or clear packet turns into.
internal sealed partial class MetalRenderHost
{
    private static readonly nint SelCopyTextureSlice = MetalNative.Selector(
        "copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:");

    public ResourceSlotIdentifier FindImage(ref ImageRequest request, bool exactFormat)
    {
        _ = EnsureCommandBuffer();
        return _imageCache.FindImage(ref request, exactFormat);
    }

    public void BindRenderTarget(ResourceSlotIdentifier image)
    {
        // v2 keeps no per-image binding state: an attachment is resolved again on
        // acquisition, and a replaced image is simply a different slot identifier.
        _ = image;
    }

    ColorAttachmentAcquisition IRenderHost.AcquireColorAttachment(in ColorTargetState target)
    {
        var request = target.Resolution.Request;
        var imageIdentifier = target.Image;
        if (!_imageCache.TryGetImage(imageIdentifier, out _))
        {
            imageIdentifier = _imageCache.FindImage(ref request, exactFormat: false);
        }

        var metadataClear = _imageCache.TryConsumeDccAttachmentClear(
            in request, in target.Resolution, out var clearValue);
        var texture = _imageCache.AcquireColorTargetView(imageIdentifier, request with { Role = ImageRole.ColorTarget });
        var image = _imageCache.GetImage(imageIdentifier);
        RecordAttachmentSubresource(
            request.View, image.Shape, image.Description.IsVolume, color: true,
            image.Description.BytesPerBlock, image.Texture);
        return new ColorAttachmentAcquisition(
            imageIdentifier,
            new GpuImageHandle((ulong)texture),
            GpuImageLayout.ColorAttachmentOptimal,
            image.Shape.SampleCount,
            metadataClear,
            clearValue);
    }

    private long _untestedDepthWrites;

    internal long TakeUntestedDepthWrites() => Interlocked.Exchange(ref _untestedDepthWrites, 0);

    // Whether the attachment's texture can change under this draw. The guest state calls a
    // draw with depth writes on and the depth test off read-only, but the Metal state
    // writes depth whenever writes are on (the test becomes Always), so such a draw does
    // change the texture, and whatever copied it - an R32 alias - no longer holds it.
    // A metadata clear loads as a clear, so it writes even when the registers do not.
    private bool DepthAttachmentWrites(in DepthAttachmentState depth, uint layer)
    {
        var resolution = depth.Target.Target;
        var state = depth.Target.State;
        if (WritesAttachment(in state, resolution.Format))
        {
            if (state.IsReadOnly(resolution.Format))
            {
                Interlocked.Increment(ref _untestedDepthWrites);
            }

            return true;
        }

        return resolution.HasHtile && _imageCache.IsMetadataCleared(resolution.HtileAddress, layer);
    }

    // Whether a draw that writes the attachment can change its depth plane, rather than
    // only its stencil: the rules above, for the depth aspect alone. A format without a
    // depth aspect keeps everything in what it calls its data, so it always answers yes.
    private bool DepthPlaneWrites(in DepthAttachmentState depth, uint layer)
    {
        var resolution = depth.Target.Target;
        var state = depth.Target.State;
        if ((ViewFormatRules.DepthAspects(resolution.Format) & GpuImageAspect.DepthBit) == 0 ||
            (state.AttachmentWriteAspects(resolution.Format) & GpuImageAspect.DepthBit) != 0 ||
            state.DepthWriteEnabled)
        {
            return true;
        }

        return resolution.HasHtile && _imageCache.IsMetadataCleared(resolution.HtileAddress, layer);
    }

    internal static bool WritesAttachment(in DepthStencilState state, GpuFormat format) =>
        !state.IsReadOnly(format) ||
        (state.DepthWriteEnabled && (ViewFormatRules.DepthAspects(format) & GpuImageAspect.DepthBit) != 0);

    DepthAttachmentAcquisition IRenderHost.AcquireDepthAttachment(in DepthAttachmentState depth)
    {
        var resolution = depth.Target.Target;
        var request = resolution.Request;
        var imageIdentifier = depth.Image;
        if (!_imageCache.TryGetImage(imageIdentifier, out _))
        {
            imageIdentifier = _imageCache.FindImage(ref request, exactFormat: false);
        }

        var writes = DepthAttachmentWrites(in depth, request.View.BaseLayer);
        var writesDepth = writes && DepthPlaneWrites(in depth, request.View.BaseLayer);
        var texture = _imageCache.AcquireDepthTargetView(
            imageIdentifier, request with { Role = ImageRole.DepthTarget }, writes, writesDepth);
        var layer = request.View.BaseLayer;
        if (resolution.HasHtile && resolution.DepthClearEnabled)
        {
            _ = _imageCache.ClearMetadata(resolution.HtileAddress);
        }

        var metadataClear = resolution.HasHtile && _imageCache.IsMetadataCleared(resolution.HtileAddress, layer);
        if (metadataClear)
        {
            _ = _imageCache.SetMetadataSlice(resolution.HtileAddress, layer, false);
        }

        var image = _imageCache.GetImage(imageIdentifier);
        RecordAttachmentSubresource(
            request.View, image.Shape, image.Description.IsVolume, color: false,
            image.Description.BytesPerBlock, image.Texture);
        return new DepthAttachmentAcquisition(
            new GpuImageHandle((ulong)texture), image.Shape.SampleCount, metadataClear);
    }

    // A created texture view starts at its own level zero; the image texture keeps
    // the real level and slice, so the render pass needs to know which it got.
    private void RecordAttachmentSubresource(
        ImageViewDescription view,
        MetalTextureShape shape,
        bool volume,
        bool color,
        uint texelBytes,
        nint texture)
    {
        // The subresource this attachment really is, which is what a discarded store
        // would throw away: only a pass that covers all of it can discard anything.
        var subresourceWidth = Math.Max(1u, shape.Width >> (int)view.BaseLevel);
        var subresourceHeight = Math.Max(1u, shape.Height >> (int)view.BaseLevel);
        var wholeImage = view.BaseLevel == 0 && view.LevelCount >= shape.MipLevels &&
            view.BaseLayer == 0 && view.LayerCount >= shape.ArrayLength;
        // A volume attachment is always the texture itself - Metal slices it with the
        // depth plane rather than with a view - so its level is the one asked for.
        var level = wholeImage || volume ? view.BaseLevel : 0;
        var slice = wholeImage && !volume ? view.BaseLayer : 0;
        var depthPlane = volume ? view.BaseLayer : 0;
        if (color)
        {
            if (_attachmentCount < MaxColorAttachments)
            {
                _attachmentLevels[_attachmentCount] = level;
                _attachmentSlices[_attachmentCount] = slice;
                _attachmentDepthPlanes[_attachmentCount] = depthPlane;
                _attachmentTexelBytes[_attachmentCount] = texelBytes;
                _attachmentWidths[_attachmentCount] = subresourceWidth;
                _attachmentHeights[_attachmentCount] = subresourceHeight;
                _attachmentTextures[_attachmentCount] = texture;
                _attachmentRealLevels[_attachmentCount] = view.BaseLevel;
                _attachmentRealLayers[_attachmentCount] = view.BaseLayer;
                _attachmentCount++;
            }

            return;
        }

        _depthAttachmentLevel = level;
        _depthAttachmentSlice = slice;
        _depthTexelBytes = texelBytes;
        _depthAttachmentWidth = subresourceWidth;
        _depthAttachmentHeight = subresourceHeight;
        _depthAttachmentTexture = texture;
        _depthAttachmentRealLevel = view.BaseLevel;
        _depthAttachmentRealLayer = view.BaseLayer;
    }

    public void TransitionDepthAttachment(in DepthAttachmentState depth, GpuImageLayout layout, GpuImageAspect writeAspects)
    {
        // Metal has no image layouts; the pass descriptor's load and store actions
        // carry everything the transition would have expressed.
        _ = depth;
        _ = layout;
        _ = writeAspects;
    }

    public void MarkGpuWritten(ResourceSlotIdentifier image) => _imageCache.MarkGpuWritten(image);

    public void ResolveImage(
        ResourceSlotIdentifier source,
        uint sourceMip,
        uint sourceLayer,
        ResourceSlotIdentifier destination,
        uint destinationMip,
        uint destinationLayer)
    {
        if (!_imageCache.TryGetImage(source, out var sourceImage) ||
            !_imageCache.TryGetImage(destination, out var destinationImage) ||
            sourceImage.Texture == 0 || destinationImage.Texture == 0)
        {
            return;
        }

        // A multisample resolve of equal-format images is a blit of sample zero here;
        // the dedicated resolve pass belongs with the real tiler work.
        CopyTextureSubresource(
            sourceImage,
            destinationImage,
            new SubresourceRange(sourceMip, 1, sourceLayer, 1),
            new SubresourceRange(destinationMip, 1, destinationLayer, 1),
            new GpuExtent3D(
                Math.Max(destinationImage.Shape.Width >> (int)destinationMip, 1),
                Math.Max(destinationImage.Shape.Height >> (int)destinationMip, 1),
                1));
    }

    public void CopyDepthStencilImage(
        ResourceSlotIdentifier source,
        ResourceSlotIdentifier destination,
        in SubresourceRange range,
        in GpuExtent3D extent,
        GpuImageAspect aspects)
    {
        _ = aspects;
        _imageCache.RefreshImage(source);
        _imageCache.RefreshImage(destination);
        if (!_imageCache.TryGetImage(source, out var sourceImage) ||
            !_imageCache.TryGetImage(destination, out var destinationImage) ||
            sourceImage.Texture == 0 || destinationImage.Texture == 0)
        {
            return;
        }

        CopyTextureSubresource(sourceImage, destinationImage, in range, in range, in extent);
        _imageCache.MarkGpuWritten(destination);
    }

    private void CopyTextureSubresource(
        MetalCachedImage source,
        MetalCachedImage destination,
        in SubresourceRange sourceRange,
        in SubresourceRange destinationRange,
        in GpuExtent3D extent)
    {
        _imageCache.PrepareImageWrite(destination);
        var context = ((IMetalImageCacheOwner)this).BeginTransfer(
            MetalTransferCause.ImageCopy, source.Texture, destination.Texture);
        // A copy rewrites the destination whether or not its caller marks it written.
        destination.NoteContentChanged();
        var encoder = MetalNative.Send(context.CommandBuffer, SelBlitEncoder);
        if (encoder == 0)
        {
            throw Fatal("A Metal blit encoder could not be created for an image copy.");
        }

        try
        {
            var layers = Math.Min(sourceRange.LayerCount, destinationRange.LayerCount);
            for (var layer = 0u; layer < layers; layer++)
            {
                MetalNative.SendCopyTextureSlice(
                    encoder,
                    SelCopyTextureSlice,
                    source.Texture,
                    sourceRange.BaseLayer + layer,
                    sourceRange.BaseLevel,
                    default,
                    new MtlSize
                    {
                        Width = Math.Max(extent.Width, 1),
                        Height = Math.Max(extent.Height, 1),
                        Depth = Math.Max(extent.Depth, 1),
                    },
                    destination.Texture,
                    destinationRange.BaseLayer + layer,
                    destinationRange.BaseLevel,
                    default);
            }
        }
        finally
        {
            MetalNative.SendVoid(encoder, SelEndEncoding);
        }

        context.ProfileEncoded(
            $"image-copy extent={extent.Width}x{extent.Height}x{extent.Depth} " +
            $"layers={Math.Min(sourceRange.LayerCount, destinationRange.LayerCount)}");
    }

    public bool IsMetadata(ulong address) => _imageCache.IsMetadata(address);

    public bool ClearMetadata(ulong address) => _imageCache.ClearMetadata(address);

    public bool TryClearImageFromBuffer(ulong address, ulong size, uint packedClear)
    {
        _ = EnsureCommandBuffer();
        return _imageCache.TryClearImageFromBuffer(address, size, packedClear);
    }

    public bool TryAbsorbDccFill(ulong address, ulong size, uint fillValue) =>
        _imageCache.TryAbsorbDccFill(address, size, fillValue);

    public bool TryFillDccMetadata(ulong address, ulong size, uint fillValue)
    {
        if (!_imageCache.OverlapsDccMetadata(address, size))
        {
            return false;
        }

        FillGuestMemory(address, size, fillValue);
        return true;
    }

    // Guest memory is the GPU's memory, so a word copy has no host-side shortcut:
    // the GPU copy in CopyGuestMemory already lands where the guest reads it.
    public bool TryCopyWordsOnHost(ulong destination, ulong source, ulong sourceWords, ulong words)
    {
        _ = destination;
        _ = source;
        _ = sourceWords;
        _ = words;
        return false;
    }

    public void PrepareMemoryWritingDraw()
    {
        // A store-only draw needs no special setup on Metal: the open render pass
        // already has the window buffers resident and hazard-tracked. The ranges the
        // draw stores into were recorded when its storage buffers were bound, so a
        // later read of those bytes waits for this submission and a read of anything
        // else does not.
    }
}
