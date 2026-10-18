// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal.V2;

// A depth surface that a shader samples as its single-channel colour twin (D32 as R32
// float, D16 as R16 unorm) binds the depth image's own texture, rather than a second
// texture of the colour format kept coherent by a copy on every hand-off. Depth formats
// are their own view class, so Metal has no R32F view of a Depth32Float texture; but a
// depth texture binds to a texture2d<float> argument, which is what the translator emits
// for every non-comparison sample, and a same-format view of it may carry a swizzle
// without the pixel-format-view usage (both checked on the device,
// MetalDepthTwinTests). Unswizzled, an Apple GPU reads a depth texel as (d, d, d, 1); the
// colour twin reads (r, 0, 0, 1), so the view gets the swizzle that turns the first into
// the second.
//
// Only sampling goes this way. A colour twin the guest renders to or stores into is still
// its own texture, handed over by SynchronizeAliases as before; and a sampled-only colour
// twin made before the depth target existed is dropped when the depth target arrives,
// since guest memory holds everything it held.
internal sealed partial class MetalImageCache
{
    // SHARPEMU_METAL_V2_DEPTH_TWIN=0 gives a sampled colour twin its own texture again.
    private static readonly bool DepthTwinsDefault =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DEPTH_TWIN") != "0";

    private long _depthTwinBinds;
    private long _depthTwinReleases;

    /// <summary>Whether a sampled colour twin of a depth surface binds the depth texture.</summary>
    public bool DepthTwins { get; set; } = DepthTwinsDefault;

    /// <summary>
    /// Lookups a depth texture served as its colour twin, and sampled-only colour twins
    /// released for the depth target over their bytes, since the last read.
    /// </summary>
    public (long Binds, long Releases) TakeDepthTwinCounters() =>
        (Interlocked.Exchange(ref _depthTwinBinds, 0), Interlocked.Exchange(ref _depthTwinReleases, 0));

    // A sampled request for the colour twin of a cached depth image, which the depth
    // texture can serve. Caller holds the lock.
    private bool ServesAsDepthTwin(MetalCachedImage cached, in ImageDescription requested, in ImageRequest request) =>
        DepthTwins && request.Role == ImageRole.Texture && cached.Texture != 0 && cached.FirstLevel == 0 &&
        !cached.IsWindowLinearView && request.View.Format == requested.PixelFormat &&
        ViewFormatRules.IsSupportedSampledDepthView(
            cached.Description.PixelFormat, request.View.Format, PackedSwizzle(request.View.Mapping)) &&
        _transfer.SamplesAsDepthTwin(in cached.Description, in requested);

    // A cached colour twin that only ever served samples, under a depth target request over
    // the same bytes: guest memory holds all it holds, and the depth texture serves its
    // samples from here on. Caller holds the lock.
    private bool IsReleasableColorTwin(MetalCachedImage cached, in ImageDescription requested, ImageRole role) =>
        DepthTwins && role == ImageRole.DepthTarget && !cached.IsGpuModified &&
        !cached.Uses.RenderTarget && !cached.Uses.Storage && !cached.Uses.DepthTarget && !cached.Uses.VideoOut &&
        _transfer.SamplesAsDepthTwin(in requested, in cached.Description);

    // The view of a depth texture that samples like the colour twin view requested of it:
    // the depth format, and the requested swizzle with green and blue read as zero and alpha
    // as one, as a single-channel colour view reads them. False for anything else.
    private static bool TryDepthTwinView(MetalCachedImage image, in ImageViewDescription requested, out ImageViewDescription view)
    {
        view = requested;
        var depthFormat = image.Description.PixelFormat;
        var twin = depthFormat is GpuFormat.D32Sfloat or GpuFormat.D32SfloatS8Uint
            ? GpuFormat.R32Sfloat
            : depthFormat == GpuFormat.D16Unorm ? GpuFormat.R16Unorm : GpuFormat.Undefined;
        if (twin == GpuFormat.Undefined || requested.Format != twin ||
            !ViewFormatRules.IsSupportedSampledDepthView(depthFormat, twin, PackedSwizzle(requested.Mapping)))
        {
            return false;
        }

        var mapping = requested.Mapping;
        view = requested with
        {
            Format = depthFormat,
            Aspect = GpuImageAspect.DepthBit,
            Mapping = new GpuComponentMapping(
                SingleChannel(mapping.R, GpuComponentSwizzle.R),
                SingleChannel(mapping.G, GpuComponentSwizzle.G),
                SingleChannel(mapping.B, GpuComponentSwizzle.B),
                SingleChannel(mapping.A, GpuComponentSwizzle.A)),
        };
        return true;
    }

    // What one output channel of a single-channel colour view reads, spelled out for a view
    // whose texture reads its one channel into all three colour components.
    private static GpuComponentSwizzle SingleChannel(GpuComponentSwizzle component, GpuComponentSwizzle identity) =>
        (component == GpuComponentSwizzle.Identity ? identity : component) switch
        {
            GpuComponentSwizzle.R => GpuComponentSwizzle.R,
            GpuComponentSwizzle.G or GpuComponentSwizzle.B or GpuComponentSwizzle.Zero => GpuComponentSwizzle.Zero,
            _ => GpuComponentSwizzle.One,
        };

    // The guest DST_SEL of a component mapping, which the sampled view rules take.
    private static uint PackedSwizzle(in GpuComponentMapping mapping) => ViewFormatRules.PackDestinationSelect(
        Select(mapping.R, 4), Select(mapping.G, 5), Select(mapping.B, 6), Select(mapping.A, 7));

    private static uint Select(GpuComponentSwizzle component, uint identity) => component switch
    {
        GpuComponentSwizzle.Zero => 0,
        GpuComponentSwizzle.One => 1,
        GpuComponentSwizzle.R => 4,
        GpuComponentSwizzle.G => 5,
        GpuComponentSwizzle.B => 6,
        GpuComponentSwizzle.A => 7,
        _ => identity,
    };
}
