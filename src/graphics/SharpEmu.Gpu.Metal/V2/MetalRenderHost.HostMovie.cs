// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Media;
using SharpEmu.ShaderCompiler.Resources;
using MslImageResourceClass = SharpEmu.ShaderCompiler.Resources.ImageResourceClass;

namespace SharpEmu.Gpu.Metal.V2;

// Host-decoded movie planes bound in place of the guest decoder's output surfaces:
// the v2 twin of the Vulkan presenter's host movie path. The selection rules live in
// MetalHostMovieSelector; this part owns the plane textures and their lifetime.
//
// Each decoded frame gets textures of its own, filled from the CPU before the draw
// that first samples them is committed. Rewriting one texture in place would race
// the submissions still sampling the previous frame, and copying through a blit
// would end the open render pass; a replaced plane instead waits on a retire list
// until the GPU has finished every submission that could have bound it.
internal sealed partial class MetalRenderHost
{
    private static readonly nint SelReplaceRegion =
        MetalNative.Selector("replaceRegion:mipmapLevel:withBytes:bytesPerRow:");
    private static readonly nint SelTexture2DDescriptor =
        MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:");
    private static readonly nint SelMovieNewTexture = MetalNative.Selector("newTextureWithDescriptor:");
    private static readonly nint SelMovieSetUsage = MetalNative.Selector("setUsage:");
    private static readonly nint SelMovieSwizzledView =
        MetalNative.Selector("newTextureViewWithPixelFormat:textureType:levels:slices:swizzle:");

    private readonly MetalHostMovieSelector _hostMovie = new();
    private readonly MovieTexture?[] _moviePlanes = new MovieTexture?[2];
    private readonly List<MovieTexture> _retiredMoviePlanes = new();

    // One uploaded plane of one decoded frame, and the swizzled views drawn from it.
    private sealed class MovieTexture
    {
        public nint Texture;
        public ulong PixelFormat;
        public uint Width;
        public uint Height;
        public long FrameSerial;
        public long PathEpoch;
        public ulong RetireSerial;
        public readonly Dictionary<uint, nint> Views = new();

        public void Release()
        {
            foreach (var view in Views.Values)
            {
                if (view != Texture)
                {
                    MetalNative.SendVoid(view, SelRelease);
                }
            }

            Views.Clear();
            if (Texture != 0)
            {
                MetalNative.SendVoid(Texture, SelRelease);
                Texture = 0;
            }
        }
    }

    // Before any stage of the next draw or dispatch selects its movie planes: once the
    // bridge owns a movie its clock advances on every call, so texture discovery that
    // lags the first decoded frame cannot leave the guest stuck on an attract movie.
    private void PumpHostMovieFrame()
    {
        var decoded = MoviePresentationServices.TryDecodeNextFrame(
            advanceClock: true,
            out var pixels,
            out var width,
            out var height,
            out var advanced,
            out var frameSerial,
            out var hostPath);
        _hostMovie.Accept(decoded, pixels, width, height, advanced, frameSerial, hostPath);
        if (_retiredMoviePlanes.Count != 0)
        {
            ReleaseRetiredMoviePlanes(CompletedSerial);
        }
    }

    // The luma and chroma indices of this stage's images, built only while a frame is held.
    private MetalHostMovieSelector.Bindings FindHostMovieBindings(ShaderResourceInfo info, ResourceSnapshot snapshot)
    {
        if (!_hostMovie.HasFrame)
        {
            return MetalHostMovieSelector.Bindings.None;
        }

        var textures = new List<GuestDrawTexture>(info.Images.Count);
        for (var index = 0; index < info.Images.Count; index++)
        {
            var storage = info.Images[index].ResourceClass == MslImageResourceClass.Storage;
            textures.Add(AgcBackendServices.TryDecodeTextureDescriptor(snapshot.Images[index], out var descriptor)
                ? new GuestDrawTexture(
                    descriptor.Address, descriptor.Width, descriptor.Height, descriptor.Format, descriptor.NumberType,
                    [], false, storage, DstSelect: descriptor.DstSelect)
                : new GuestDrawTexture(0, 1, 1, 0, 0, [], true, storage));
        }

        return _hostMovie.Find(textures);
    }

    // A movie plane takes no cache image: the request is kept only for the sampler's
    // numeric class, and the texture is bound when the stage's resources are.
    private static void PrepareHostMovieBinding(MetalStageBindings prepared, ImageResource image, uint[] words, int index)
    {
        prepared.Images[index] = ResourceSlotIdentifier.Invalid;
        prepared.Requests[index] = ImageRequestBuilders.Texture(words, ShapeOf(image)).Request;
        prepared.IsStorage[index] = false;
        prepared.Textures[index] = 0;
    }

    // The view of one plane of the current frame, uploaded the first time it is bound.
    private nint AcquireHostMovieView(int plane)
    {
        var frame = _hostMovie.FramePixels ??
            throw Fatal("A host movie plane was bound without a decoded frame.");
        var frameWidth = _hostMovie.FrameWidth;
        var frameHeight = _hostMovie.FrameHeight;
        var luma = plane == 0;
        var width = luma ? frameWidth : (frameWidth + 1) / 2;
        var height = luma ? frameHeight : (frameHeight + 1) / 2;
        var pixelFormat = luma ? MtlFormat.R8Unorm : MtlFormat.Rg8Unorm;
        var current = _moviePlanes[plane];
        if (current is null ||
            current.FrameSerial != _hostMovie.FrameSerial ||
            current.PathEpoch != _hostMovie.PathEpoch ||
            current.Width != width ||
            current.Height != height)
        {
            var lumaLength = MoviePresentationServices.LumaLength(frameWidth, frameHeight);
            var bytes = luma
                ? frame.AsSpan(0, lumaLength)
                : frame.AsSpan(lumaLength, MoviePresentationServices.ChromaLength(frameWidth, frameHeight));
            var replacement = CreateMoviePlane(pixelFormat, width, height, bytes, luma ? "luma" : "chroma");
            replacement.FrameSerial = _hostMovie.FrameSerial;
            replacement.PathEpoch = _hostMovie.PathEpoch;
            if (current is not null)
            {
                // Every draw that bound the old plane was encoded before this one, so
                // none of them is in a submission later than the one being recorded.
                current.RetireSerial = SubmissionSerial;
                _retiredMoviePlanes.Add(current);
            }

            _moviePlanes[plane] = current = replacement;
        }

        var dstSelect = luma ? _hostMovie.LumaDstSelect : _hostMovie.ChromaDstSelect;
        if (current.Views.TryGetValue(dstSelect, out var view))
        {
            return view;
        }

        var swizzle = MetalCachedImage.SwizzleOf(ViewFormatRules.ComponentMapping(dstSelect), out var identity);
        view = identity
            ? current.Texture
            : MetalV2Native.SendNewSwizzledTextureView(
                current.Texture,
                SelMovieSwizzledView,
                (nuint)current.PixelFormat,
                (nuint)Mtl.TextureType2D,
                new NsRange { Location = 0, Length = 1 },
                new NsRange { Location = 0, Length = 1 },
                swizzle);
        if (view == 0)
        {
            throw Fatal(
                $"A host movie plane view could not be created: plane={plane} dst=0x{dstSelect:X} {width}x{height}.");
        }

        current.Views[dstSelect] = view;
        return view;
    }

    private MovieTexture CreateMoviePlane(
        ulong pixelFormat,
        uint width,
        uint height,
        ReadOnlySpan<byte> bytes,
        string planeName)
    {
        // The class constructor hands back an autoreleased descriptor.
        var pool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            return CreateMoviePlaneInPool(pixelFormat, width, height, bytes, planeName);
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(pool);
        }
    }

    private unsafe MovieTexture CreateMoviePlaneInPool(
        ulong pixelFormat,
        uint width,
        uint height,
        ReadOnlySpan<byte> bytes,
        string planeName)
    {
        var descriptor = MetalNative.SendTextureDescriptor(
            MetalNative.Class("MTLTextureDescriptor"),
            SelTexture2DDescriptor,
            (nuint)pixelFormat,
            width,
            height,
            mipmapped: false);
        if (descriptor == 0)
        {
            throw Fatal($"A host movie {planeName} texture descriptor could not be created: {width}x{height}.");
        }

        MetalV2Native.SendVoidValue(
            descriptor, SelMovieSetUsage, (nuint)(Mtl.TextureUsageShaderRead | Mtl.TextureUsagePixelFormatView));
        var texture = MetalNative.Send(_device, SelMovieNewTexture, descriptor);
        if (texture == 0)
        {
            throw Fatal($"A host movie {planeName} texture could not be created: {width}x{height}.");
        }

        var bytesPerPixel = pixelFormat == MtlFormat.R8Unorm ? 1u : 2u;
        fixed (byte* source = bytes)
        {
            MetalNative.SendReplaceRegion(
                texture,
                SelReplaceRegion,
                new MtlRegion { Width = width, Height = height, Depth = 1 },
                0,
                (nint)source,
                width * bytesPerPixel);
        }

        return new MovieTexture
        {
            Texture = texture,
            PixelFormat = pixelFormat,
            Width = width,
            Height = height,
        };
    }

    private void ReleaseRetiredMoviePlanes(ulong completedSerial)
    {
        for (var index = _retiredMoviePlanes.Count - 1; index >= 0; index--)
        {
            var retired = _retiredMoviePlanes[index];
            if (retired.RetireSerial > completedSerial)
            {
                continue;
            }

            retired.Release();
            _retiredMoviePlanes.RemoveAt(index);
        }
    }

    // Shutdown only, once every submission has drained.
    private void DisposeHostMovie()
    {
        ReleaseRetiredMoviePlanes(ulong.MaxValue);
        for (var plane = 0; plane < _moviePlanes.Length; plane++)
        {
            _moviePlanes[plane]?.Release();
            _moviePlanes[plane] = null;
        }
    }
}
