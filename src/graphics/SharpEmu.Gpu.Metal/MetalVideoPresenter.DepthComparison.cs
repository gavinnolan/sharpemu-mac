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
