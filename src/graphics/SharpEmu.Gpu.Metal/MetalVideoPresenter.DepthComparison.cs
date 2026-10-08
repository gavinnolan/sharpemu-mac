// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using static SharpEmu.Libs.Agc.AgcBackendServices;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Conversion stays on the consuming command buffer. Point sampling converts
    // the source red component before Metal comparison filtering; a buffer blit
    // into depth storage preserves float values without raster depth clamping.
    private const string DepthComparisonConversionSource = """
        #include <metal_stdlib>
        using namespace metal;
        struct DepthConversion { ulong row_floats, image_floats, mip; };
        constexpr sampler point_texel(coord::normalized, address::clamp_to_edge,
                                       filter::nearest, mip_filter::nearest);
        kernel void depth_red_2d(texture2d<float> source [[texture(0)]],
            device float* output [[buffer(0)]], constant DepthConversion& p [[buffer(1)]],
            uint2 tid [[thread_position_in_grid]]) {
            uint2 size(source.get_width(uint(p.mip)), source.get_height(uint(p.mip)));
            if (any(tid >= size)) return;
            float2 uv = (float2(tid) + 0.5f) / float2(size);
            output[ulong(tid.y) * p.row_floats + tid.x] =
                source.sample(point_texel, uv, level(float(p.mip))).x;
        }
        kernel void depth_red_array(texture2d_array<float> source [[texture(0)]],
            device float* output [[buffer(0)]], constant DepthConversion& p [[buffer(1)]],
            uint3 tid [[thread_position_in_grid]]) {
            uint2 size(source.get_width(uint(p.mip)), source.get_height(uint(p.mip)));
            if (any(tid.xy >= size) || tid.z >= source.get_array_size()) return;
            float2 uv = (float2(tid.xy) + 0.5f) / float2(size);
            output[ulong(tid.z) * p.image_floats + ulong(tid.y) * p.row_floats + tid.x] =
                source.sample(point_texel, uv, tid.z, level(float(p.mip))).x;
        }
        """;

    // Presenter device/process lifetime, like the existing compute pipeline cache.
    private static readonly Dictionary<(nint Device, bool Arrayed), nint> _depthConversionPipelines = [];

    internal static nint CreateDrawTexture(nint device, nint blitCommandBuffer,
        GuestDrawTexture texture, out bool ownedByCaller)
    {
        if (!texture.Shape.DepthCompare)
            return CreateDrawColorTexture(device, blitCommandBuffer, texture, out ownedByCaller);

        ValidateDepthComparisonTexture(texture, blitCommandBuffer);
        if (!MetalNative.SendBool(blitCommandBuffer, MetalNative.Selector("retainedReferences")))
            throw new NotSupportedException("Metal depth conversion requires command-buffer retained references.");
        var source = CreateDrawColorTexture(device, blitCommandBuffer, texture, out var ownsSource);
        if (source == 0)
            throw new InvalidOperationException("Metal depth comparison requires a materialized source texture.");
        nint depth = 0;
        Exception? failure = null;
        try { depth = EncodeDepthComparisonTexture(device, blitCommandBuffer, source); }
        catch (Exception ex) { failure = ex; }
        // Ordinary command buffers retain encoded inputs. Only this call's
        // source retain is released; cache/live/pool ownership remains intact.
        ReleaseDepthObjectsPreservingFailure(failure, ownsSource ? source : 0);
        ownedByCaller = false; // Snapshot pool retains through consuming completion.
        return depth;
    }

    internal static void ValidateDepthComparisonTexture(GuestDrawTexture texture, nint commandBuffer)
    {
        var shape = GuestTextureShape(texture);
        if (!texture.Shape.DepthCompare || texture.Shape.NumericClass != TextureNumericClass.Float ||
            texture.IsStorage || texture.Shape.Storage || texture.Shape.Volume || texture.Shape.OneDimensional ||
            texture.Shape.Arrayed != texture.ArrayedView || texture.Shape.Cube != (texture.Type == Gen5TextureTypeCube) ||
            texture.Type is not (Gen5TextureType2D or Gen5TextureType2DArray or Gen5TextureTypeCube) ||
            texture.ArrayLayers == 0 || texture.Depth != 1 ||
            (texture.Type == Gen5TextureTypeCube && (!texture.ArrayedView || texture.ArrayLayers % 6 != 0)) ||
            texture.Shape.Multisampled || texture.Shape.Atomic || texture.Shape.R128 ||
            shape.TextureType is not (MtlTextureType2D or MtlTextureType2DArray) || shape.Depth != 1 ||
            !((texture.Format == 4 && texture.NumberType == 7) || (texture.Format == 2 && texture.NumberType == 0)))
            throw DepthComparisonDescriptorRejected(texture);
        if (commandBuffer == 0)
            throw new InvalidOperationException("Metal native depth conversion requires the consuming command buffer.");
    }

    // Only the existing rejection formats metadata. A diagnostic failure cannot
    // replace the original refusal or trigger native queries/materialization.
    private static NotSupportedException DepthComparisonDescriptorRejected(GuestDrawTexture texture)
    {
        var failure = new NotSupportedException($"Metal native depth comparison requires a Float R32 or Unorm R16 2D/array source; descriptor={texture.Format}/{texture.NumberType} type={texture.Type}.");
        try
        {
            return new NotSupportedException(failure.Message +
                $" addr=0x{texture.Address:X} size={texture.Width}x{texture.Height} pitch={texture.Pitch}" +
                $" depth={texture.Depth} arrayed={texture.ArrayedView} layers={texture.ArrayLayers}" +
                $" tile={texture.TileMode} mip={texture.MipLevel} base_mip={texture.BaseMipLevel}" +
                $" mips={texture.MipLevels} resource_mips={texture.ResourceMipLevels}" +
                $" shape={texture.Shape} access={texture.Access} fallback={texture.IsFallback}" +
                $" cpu_stable={texture.CpuSnapshotStable} source_bytes={texture.SourceByteCount}" +
                $" linear_bytes={texture.RgbaPixels.LongLength} tiled_bytes={texture.TiledSource?.LongLength ?? 0}" +
                $" sampler=[{texture.Sampler.Word0:X8},{texture.Sampler.Word1:X8},{texture.Sampler.Word2:X8},{texture.Sampler.Word3:X8}]" +
                $" raw=[{string.Join(",", texture.Descriptor?.Select(static word => $"{word:X8}") ?? [])}].");
        }
        catch (Exception) { return failure; }
    }

    // The new CPU face path cannot reconstruct any known native depth/color
    // producer from guest bytes. A layered native producer route is separate.
    internal static void RejectDepthCubeCpuSourceGpuOverlap(ulong address, ulong bytes)
    {
        lock (_gate)
        {
            foreach (var pendingQueue in _pendingGuestWorkByQueue.Values)
                foreach (var pending in pendingQueue)
                    RejectDepthCubeAcceptedWriterLocked(address, bytes, pending.Work);
            if (_takenGuestWorkSequence != 0)
            {
                if (_takenDepthCubeProducerWork is null)
                    throw new NotSupportedException("Metal depth cube CPU snapshot has an unknown taken producer extent.");
                RejectDepthCubeAcceptedWriterLocked(address, bytes, _takenDepthCubeProducerWork);
            }
            foreach (var entry in _guestDepthImages)
            {
                var image = entry.Value;
                var extent = checked((ulong)image.Width * image.Height * 4);
                if (DepthCubeOverlap(address, bytes, entry.Key, extent) ||
                    _guestDepthReadAliases.Any(alias => alias.Value == entry.Key && DepthCubeOverlap(address, bytes, alias.Key, extent)))
                    throw new NotSupportedException("Metal depth cube CPU snapshot overlaps a native depth producer; an exact ordered layered producer source is required.");
            }
            foreach (var entry in _guestImages)
            {
                if (!entry.Value.GpuWritten) continue;
                if (!_guestImageExtents.TryGetValue(entry.Key, out var extent) || extent.ByteCount == 0 ||
                    DepthCubeOverlap(address, bytes, entry.Key, extent.ByteCount))
                    throw new NotSupportedException("Metal depth cube CPU snapshot cannot replace overlapping or unknown native image contents with guest bytes.");
            }
        }
    }

    // Publication repeats the overlap veto after the complete CPU copy, under
    // the same admission gate as accepted/taken ownership. This does not invent
    // a native source or certify retired/all-alias CPU ownership.
    internal static void RegisterDepthCubeCpuSnapshot(GuestDrawTexture texture,
        in CpuTextureSnapshotObservation observation)
    {
        lock (_gate)
        {
            RejectDepthCubeCpuSourceGpuOverlap(texture.Address, texture.SourceByteCount);
            RegisterCpuTextureSnapshotProvenance(texture, observation);
        }
    }

    private static void RejectDepthCubeAcceptedWriterLocked(ulong address, ulong bytes, object work)
    {
        void Range(ulong writer, ulong count)
        {
            if (writer == 0) return;
            if (count == 0 || count > ulong.MaxValue - writer || DepthCubeOverlap(address, bytes, writer, count))
                throw new NotSupportedException("Metal depth cube CPU snapshot overlaps an accepted pending/taken writer or its extent is unknown.");
        }
        void Buffers(IReadOnlyList<GuestMemoryBuffer> buffers)
        {
            foreach (var buffer in buffers)
                if (buffer.Writable && buffer.WriteBackToGuest && buffer.Length != 0)
                    Range(buffer.BaseAddress, buffer.Length > 0 ? checked((ulong)buffer.Length) : 0);
        }
        void Storage(IReadOnlyList<GuestDrawTexture> textures)
        {
            foreach (var texture in textures)
                if (StorageImageWrites(texture)) Range(texture.Address, texture.SourceByteCount);
        }
        void Image(ulong writer)
        {
            if (writer != 0)
                Range(writer, _guestImageExtents.TryGetValue(writer, out var extent) ? extent.ByteCount : 0);
        }
        switch (work)
        {
            case OffscreenGuestDraw draw:
                foreach (var target in draw.Targets)
                {
                    if (target.Address == 0) continue;
                    if (MetalColorMipPlacement.TryGet(target, out var placement))
                        Range(placement.Registers.BaseAddress, placement.BackingByteCount);
                    else if (target.TileMode == 0 && target.MipLevels == 1 &&
                        MetalGuestFormats.TryDecodeRenderTargetFormat(target.Format, target.NumberType, target.ComponentSwap, out var format))
                        Range(target.Address, checked((ulong)target.Width * target.Height * MetalRenderTargetFormat.GetBytesPerPixel(format.Format)));
                    else Range(target.Address, 0); // no tiled footprint inferred from logical dimensions
                }
                if (draw.DepthTarget is { } depth)
                {
                    // Native first-use clear may write even a read-only bound
                    // target. Raw tiled/stencil/metadata extents need a separate
                    // normalized footprint; do not call them disjoint here.
                    var width = Math.Max(depth.Width, draw.Targets.FirstOrDefault()?.Width ?? 0);
                    var height = Math.Max(depth.Height, draw.Targets.FirstOrDefault()?.Height ?? 0);
                    var count = depth.SwizzleMode == 0 && !depth.HasStencil && !depth.HtileAcceleration &&
                        !depth.MetadataClear ? checked((ulong)width * height * 4) : 0;
                    Range(depth.Address, count);
                    if (depth.ReadAddress != depth.Address) Range(depth.ReadAddress, count);
                }
                Storage(draw.Draw.Textures); Buffers(draw.Draw.GlobalMemoryBuffers);
                break;
            case ComputeGuestDispatch dispatch:
                Storage(dispatch.Textures); Buffers(dispatch.GlobalMemoryBuffers);
                break;
            case GuestImageWrite write: Image(write.Address); break;
            case GuestImageBlit blit: Image(blit.DestinationAddress); break;
            case GlobalDataShareCopyToGuest copy: Range(copy.GuestAddress, copy.Size); break;
        }
    }

    private static bool DepthCubeOverlap(ulong a, ulong bytesA, ulong b, ulong bytesB) =>
        (UInt128)a < (UInt128)b + bytesB && (UInt128)b < (UInt128)a + bytesA;

    private static nint EncodeDepthComparisonTexture(nint device, nint commandBuffer, nint source)
    {
        var type = checked((uint)MetalNative.Send(source, MetalNative.Selector("textureType")));
        var format = checked((uint)MetalNative.Send(source, MetalNative.Selector("pixelFormat")));
        var width = checked((uint)MetalNative.Send(source, MetalNative.Selector("width")));
        var height = checked((uint)MetalNative.Send(source, MetalNative.Selector("height")));
        var layers = checked((uint)MetalNative.Send(source, MetalNative.Selector("arrayLength")));
        var mips = checked((uint)MetalNative.Send(source, MetalNative.Selector("mipmapLevelCount")));
        if (type is not (MtlTextureType2D or MtlTextureType2DArray) ||
            format is not ((uint)MtlPixelFormat.R32Float or (uint)MtlPixelFormat.R16Unorm) ||
            width == 0 || height == 0 || layers == 0 || mips == 0)
            throw new NotSupportedException($"Metal depth conversion rejected materialized format={format} type={type} shape={width}x{height} layers={layers} mips={mips}.");
        var pipeline = GetDepthConversionPipeline(device, type == MtlTextureType2DArray);
        var rowBytes = checked(((nuint)width * 4 + 255) & ~(nuint)255);
        var imageBytes = checked(rowBytes * height);
        var staging = AcquireSnapshotBuffer(device, checked(imageBytes * layers));
        var depth = AcquireSnapshotTexture(device, MtlPixelFormat.Depth32Float, width, height,
            (nint)UsageShaderRead, type, layers, mips, privateStorage: true);
        if (staging == 0 || depth == 0)
            throw new InvalidOperationException("Metal depth conversion allocation failed.");

        for (uint mip = 0; mip < mips; mip++)
        {
            var mipWidth = Math.Max(width >> (int)mip, 1u);
            var mipHeight = Math.Max(height >> (int)mip, 1u);
            var mipRowBytes = checked(((nuint)mipWidth * 4 + 255) & ~(nuint)255);
            var mipImageBytes = checked(mipRowBytes * mipHeight);
            var parameters = AllocateUpload(device, 24, out var parametersBuffer, out var parametersOffset);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(parameters, (ulong)(mipRowBytes / 4));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(parameters[8..], (ulong)(mipImageBytes / 4));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(parameters[16..], mip);
            var encoder = MetalNative.Send(commandBuffer, MetalNative.Selector("computeCommandEncoder"));
            if (encoder == 0) throw new InvalidOperationException("Metal depth conversion encoder creation failed.");
            try
            {
                MetalNative.SendVoid(encoder, MetalNative.Selector("setComputePipelineState:"), pipeline);
                MetalNative.SendSetAtIndex(encoder, MetalNative.Selector("setTexture:atIndex:"), source, 0);
                MetalNative.SendSetBuffer(encoder, MetalNative.Selector("setBuffer:offset:atIndex:"), staging, 0, 0);
                MetalNative.SendSetBuffer(encoder, MetalNative.Selector("setBuffer:offset:atIndex:"), parametersBuffer, checked((nuint)parametersOffset), 1);
                MetalNative.SendDispatch(encoder, MetalNative.Selector("dispatchThreadgroups:threadsPerThreadgroup:"),
                    new MtlSize { Width = (mipWidth + 7u) / 8u, Height = (mipHeight + 7u) / 8u, Depth = layers },
                    new MtlSize { Width = 8, Height = 8, Depth = 1 });
            }
            finally { MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding")); }
            var blit = MetalNative.Send(commandBuffer, MetalNative.Selector("blitCommandEncoder"));
            if (blit == 0) throw new InvalidOperationException("Metal depth conversion blit encoder creation failed.");
            try
            {
                for (uint layer = 0; layer < layers; layer++)
                    MetalNative.SendCopyBufferToTexture(blit, MetalNative.Selector(
                        "copyFromBuffer:sourceOffset:sourceBytesPerRow:sourceBytesPerImage:sourceSize:" +
                        "toTexture:destinationSlice:destinationLevel:destinationOrigin:"),
                        staging, checked(mipImageBytes * layer), mipRowBytes, mipImageBytes,
                        new MtlSize { Width = mipWidth, Height = mipHeight, Depth = 1 }, depth, layer, mip, default);
            }
            finally { MetalNative.SendVoid(blit, MetalNative.Selector("endEncoding")); }
        }
        return depth;
    }

    private static nint GetDepthConversionPipeline(nint device, bool arrayed)
    {
        var key = (device, arrayed);
        lock (_depthConversionPipelines)
        {
            if (_depthConversionPipelines.TryGetValue(key, out var cached)) return cached;
            nint library = 0, function = 0, options = 0, pipeline = 0;
            Exception? failure = null;
            try
            {
                options = MetalNative.Send(MetalNative.Send(MetalNative.Class("MTLCompileOptions"), MetalNative.Selector("alloc")), MetalNative.Selector("init"));
                if (options == 0) throw new InvalidOperationException("Metal depth conversion compile options creation failed.");
                MetalNative.SendVoidBool(options, MetalNative.Selector("setFastMathEnabled:"), false);
                nint error = 0;
                library = MetalNative.Send(device, MetalNative.Selector("newLibraryWithSource:options:error:"),
                    MetalNative.NsString(DepthComparisonConversionSource), options, ref error);
                if (library == 0) throw new InvalidOperationException("Metal depth conversion library: " + MetalNative.DescribeError(error));
                function = MetalNative.Send(library, MetalNative.Selector("newFunctionWithName:"),
                    MetalNative.NsString(arrayed ? "depth_red_array" : "depth_red_2d"));
                if (function == 0) throw new InvalidOperationException("Metal depth conversion function creation failed.");
                pipeline = MetalNative.Send(device, MetalNative.Selector("newComputePipelineStateWithFunction:error:"), function, ref error);
                if (pipeline == 0) throw new InvalidOperationException("Metal depth conversion pipeline: " + MetalNative.DescribeError(error));
            }
            catch (Exception ex) { failure = ex; }
            try { ReleaseDepthObjectsPreservingFailure(failure, function, library, options); }
            catch (Exception ex) { ReleaseDepthObjectsPreservingFailure(ex, pipeline); throw; }
            // Transfer only after temporary owned objects have all been released.
            try { _depthConversionPipelines.Add(key, pipeline); }
            catch (Exception ex) { ReleaseDepthObjectsPreservingFailure(ex, pipeline); throw; }
            return pipeline;
        }
    }

    private static void ReleaseDepthObjectsPreservingFailure(Exception? failure, nint first, nint second = 0, nint third = 0)
    {
        Span<nint> owned = stackalloc nint[] { first, second, third };
        for (var index = 0; index < owned.Length; index++)
        {
            if (owned[index] == 0) continue;
            try { MetalNative.SendVoid(owned[index], MetalNative.Selector("release")); }
            catch (Exception ex)
            {
                if (failure is null) failure = ex;
                else
                {
                    try { failure.Data[$"Metal depth cleanup {index}"] = ex; }
                    catch (Exception) { } // Diagnostics cannot suppress remaining releases.
                }
            }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
