// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal.V2;

// SHARPEMU_METAL_V2_LOSSY_TEXTURES=1: sampled-only textures created with Apple GPU lossy
// compression.
//
// An Apple8+ GPU (M2 and later) can keep a texture lossily compressed, which halves what an
// RGBA8 or RGBA16F mip chain takes (measured on an M4: an 8192x8192 mipped RGBA8 texture is
// 173 MiB lossy against 344 MiB lossless). Texels come back approximated, so this is a memory
// saver the user asks for and never what the guest would see.
//
// Metal refuses a lossy texture that can be written through a shader or viewed in another
// format, and asserts - the process dies - on a pixel format that has no lossy encoding, so
// the table below is exactly the formats creation was tried on, and an image gets one only
// when nothing is known to need more than ShaderRead:
//   - it is a sampled image (role Texture), not a window view over guest memory;
//   - its first view needs no pixel-format-view usage (a swizzled or reinterpreted view
//     would take the texture back at once);
//   - its format is in the table, and it is a plain, arrayed or volume colour texture;
//   - nothing has made an image over the same bytes need more usage before.
// Everything that later writes the image, binds it as a target or storage, or needs a view
// the usage does not give goes through PromoteLinearView or EnsureTextureUsageLocked, which
// make a lossless texture with the full usage of the format and refill it from guest memory
// (a sampled-only image is never the only copy of its bytes).
internal sealed partial class MetalImageCache
{
    // MTLGPUFamilyApple8, the first with lossy compression.
    private const nint GpuFamilyApple8 = 1008;
    private const nuint CompressionTypeLossy = 1;

    // Formats an M4 (Apple9) created lossy, as tested. Integer formats are left out on
    // purpose: they hold indices and identifiers a lossy encoding would corrupt. 16-bit
    // packed formats, RG11B10 float, RGB9E5 and every depth, stencil and block format have
    // no lossy encoding (or, for block formats, are compressed already).
    internal static readonly HashSet<ulong> LossyPixelFormats = new(
    [
        (ulong)MtlPixelFormat.A8Unorm,
        (ulong)MtlPixelFormat.R8Unorm, (ulong)MtlPixelFormat.R8UnormSrgb, (ulong)MtlPixelFormat.R8Snorm,
        (ulong)MtlPixelFormat.R16Unorm, (ulong)MtlPixelFormat.R16Snorm, (ulong)MtlPixelFormat.R16Float,
        (ulong)MtlPixelFormat.Rg8Unorm, (ulong)MtlPixelFormat.Rg8UnormSrgb, (ulong)MtlPixelFormat.Rg8Snorm,
        (ulong)MtlPixelFormat.R32Float,
        (ulong)MtlPixelFormat.Rg16Unorm, (ulong)MtlPixelFormat.Rg16Snorm, (ulong)MtlPixelFormat.Rg16Float,
        (ulong)MtlPixelFormat.Rgba8Unorm, (ulong)MtlPixelFormat.Rgba8UnormSrgb, (ulong)MtlPixelFormat.Rgba8Snorm,
        (ulong)MtlPixelFormat.Bgra8Unorm, (ulong)MtlPixelFormat.Bgra8UnormSrgb,
        (ulong)MtlPixelFormat.Rgb10A2Unorm, (ulong)MtlPixelFormat.Bgr10A2Unorm,
        (ulong)MtlPixelFormat.Rg32Float,
        (ulong)MtlPixelFormat.Rgba16Unorm, (ulong)MtlPixelFormat.Rgba16Snorm, (ulong)MtlPixelFormat.Rgba16Float,
        (ulong)MtlPixelFormat.Rgba32Float,
    ]);

    private bool _lossySupported;
    private bool _lossyTextures;
    private long _lossyPromotions;

    /// <summary>
    /// Whether new sampled-only images may be created lossy: the flag is on and the device
    /// can. Setting it true on a device that cannot leaves it false.
    /// </summary>
    internal bool LossyTextures
    {
        get => _lossyTextures;
        set => _lossyTextures = value && _lossySupported;
    }

    /// <summary>Lossy textures replaced by lossless ones so far.</summary>
    internal long LossyPromotions => Volatile.Read(ref _lossyPromotions);

    private void InitializeLossy()
    {
        _lossySupported = DeviceSupportsLossy(_owner.Device);
        _lossyTextures = MetalV2Diagnostics.LossyTextures && _lossySupported;
    }

    private static bool DeviceSupportsLossy(nint device)
    {
        if (device == 0 || !OperatingSystem.IsMacOSVersionAtLeast(12, 5))
        {
            return false;
        }

        var supportsFamily = MetalNative.Selector("supportsFamily:");
        return MetalNative.SendBool(device, MetalNative.Selector("respondsToSelector:"), supportsFamily) &&
            MetalNative.SendBool(device, supportsFamily, GpuFamilyApple8);
    }

    /// <summary>
    /// The shape with lossy compression and the ShaderRead usage that allows it, when the
    /// image qualifies; the shape unchanged otherwise.
    /// </summary>
    private MetalTextureShape LossyShapeFor(in ImageDescription description, in MetalTextureShape shape)
    {
        if (!_lossyTextures || description.IsBlock || description.IsDepth || description.Samples != 1 ||
            shape.SampleCount != 1 || !LossyPixelFormats.Contains(shape.PixelFormat) ||
            shape.TextureType is not (Mtl.TextureType2D or Mtl.TextureType2DArray or Mtl.TextureType3D) ||
            _fullUsageImages.Contains((description.Data.Address, description.PixelFormat)))
        {
            return shape;
        }

        return shape with { Usage = Mtl.TextureUsageShaderRead, Lossy = true };
    }

    // A view of the texture's own format (or its sRGB twin) in the order it stores the
    // components needs no usage beyond ShaderRead; any other would end a lossy texture on
    // its first bind (the same rule as MetalCachedImage.UsageNeededFor).
    private bool ViewKeepsLossy(in ImageRequest request)
    {
        if (!_lossyTextures)
        {
            return false;
        }

        MetalCachedImage.SwizzleOf(request.View.Mapping, out var identity);
        var viewFormat = MetalV2Formats.PixelFormat(request.View.Format);
        var imageFormat = MetalV2Formats.PixelFormat(request.Description.PixelFormat);
        return identity && (viewFormat == imageFormat ||
            MetalGuestFormats.LinearOf((MtlPixelFormat)(uint)viewFormat) ==
            MetalGuestFormats.LinearOf((MtlPixelFormat)(uint)imageFormat));
    }

    private void ForgetLossy(MetalCachedImage image)
    {
        _fullUsageImages.Add((image.Description.Data.Address, image.Description.PixelFormat));
        Interlocked.Increment(ref _lossyPromotions);
    }
}
