// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal.V2;

// SHARPEMU_METAL_V2_LOSSY_TEXTURES=1: sampled-only textures over bytes no GPU write has
// produced are created with Apple GPU lossy compression.
//
// An Apple8+ GPU (M2 and later) can keep a texture lossily compressed, which halves what an
// RGBA8 or RGBA16F mip chain takes (measured on an M4: an 8192x8192 mipped RGBA8 texture is
// 173 MiB lossy against 344 MiB lossless). Texels come back approximated, so this is a memory
// saver for colour the guest loaded, and never what the guest would see.
//
// Metal refuses a lossy texture that can be written through a shader or viewed in another
// format, and asserts - the process dies - on a pixel format that has no lossy encoding, so
// the table below is exactly the formats creation was tried on, and an image gets one only
// when nothing is known to need more than ShaderRead:
//   - it is a sampled image (role Texture), not a window view over guest memory;
//   - its first view needs no pixel-format-view usage (a swizzled or reinterpreted view
//     would take the texture back at once);
//   - its format is in the table, and it is a plain, arrayed or volume colour texture;
//   - nothing has made an image over the same bytes need more usage before;
//   - no GPU write has ever landed in its bytes.
// Everything that later writes the image, binds it as a target or storage, or needs a view
// the usage does not give goes through PromoteLinearView or EnsureTextureUsageLocked, which
// make a lossless texture with the full usage of the format and refill it from guest memory
// (a sampled-only image is never the only copy of its bytes).
//
// The last rule keeps rendered data out. A sampled image over bytes a render target, a depth
// or stencil plane, a storage image or a shader store produced - the R32 float alias of a
// depth target, the R8 view of a stencil plane, last frame's colour read by a temporal or
// exposure pass - gets them by an alias copy or a reload, and lossy, it holds an
// approximation of data the next pass computes with: a stencil mask or a depth off by a
// step, or a history surface compressed again every frame, until a dark scene renders as
// false-colour noise. Which images were sampled-only first over such bytes depends on the
// order a run creates and evicts them, so the damage came and went between runs of one
// build. Every image taking GPU ownership records its bytes, and so does every write a
// shader or transfer makes to guest memory (64 KiB granules, never forgotten: a granule
// errs towards lossless); a lossy image over them is made lossless there and then, and none
// is created over them again. A kernel copy takes its source as exact bytes, so a lossy
// source is made lossless first too.
internal sealed partial class MetalImageCache
{
    // MTLGPUFamilyApple8, the first with lossy compression.
    private const nint GpuFamilyApple8 = 1008;
    private const nuint CompressionTypeLossy = 1;

    // Colour formats an M4 (Apple9) created lossy, as tested. Integer formats are left out on
    // purpose: they hold indices and identifiers a lossy encoding would corrupt. So are the
    // one- and two-channel 16-bit and every 32-bit float format, which Metal would compress
    // too: they carry data (heights, distances, lookup tables, depth copies) where an
    // approximation moves geometry or skews the arithmetic, not colour. 16-bit packed
    // formats, RG11B10 float, RGB9E5 and every depth, stencil and block format have no lossy
    // encoding (or, for block formats, are compressed already).
    internal static readonly HashSet<ulong> LossyPixelFormats = new(
    [
        (ulong)MtlPixelFormat.A8Unorm,
        (ulong)MtlPixelFormat.R8Unorm, (ulong)MtlPixelFormat.R8UnormSrgb, (ulong)MtlPixelFormat.R8Snorm,
        (ulong)MtlPixelFormat.Rg8Unorm, (ulong)MtlPixelFormat.Rg8UnormSrgb, (ulong)MtlPixelFormat.Rg8Snorm,
        (ulong)MtlPixelFormat.Rgba8Unorm, (ulong)MtlPixelFormat.Rgba8UnormSrgb, (ulong)MtlPixelFormat.Rgba8Snorm,
        (ulong)MtlPixelFormat.Bgra8Unorm, (ulong)MtlPixelFormat.Bgra8UnormSrgb,
        (ulong)MtlPixelFormat.Rgb10A2Unorm, (ulong)MtlPixelFormat.Bgr10A2Unorm,
        (ulong)MtlPixelFormat.Rgba16Unorm, (ulong)MtlPixelFormat.Rgba16Snorm, (ulong)MtlPixelFormat.Rgba16Float,
    ]);

    private bool _lossySupported;
    private bool _lossyTextures;
    private long _lossyPromotions;
    private long _lossyCreated;
    private long _lossyGpuBytePromotions;
    private long _lossyCopySourcePromotions;
    private long _lossyRefusedGpuBytes;
    private readonly MetalGpuWrittenGranules _gpuWrittenBytes = new();

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

    /// <summary>Of <see cref="LossyPromotions"/>, those a GPU write over the image's bytes made.</summary>
    internal long LossyGpuBytePromotions => Volatile.Read(ref _lossyGpuBytePromotions);

    /// <summary>Sampled-only images left lossless because a GPU write had produced their bytes.</summary>
    internal long LossyRefusedGpuBytes => Volatile.Read(ref _lossyRefusedGpuBytes);

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

        if (_gpuWrittenBytes.Overlaps(description.Data.Address, description.Data.Size))
        {
            _lossyRefusedGpuBytes++;
            return shape;
        }

        _lossyCreated++;
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

    /// <summary>
    /// The image is about to take GPU ownership of its bytes: they are recorded as
    /// GPU-written (both planes of a depth-stencil image), and every lossy image over them
    /// becomes lossless before it can be handed what the GPU writes. Once per image.
    /// Caller holds the lock.
    /// </summary>
    private void NoteGpuWrittenImageLocked(MetalCachedImage image)
    {
        if (image.GpuBytesNoted || !_lossyTextures)
        {
            return;
        }

        image.GpuBytesNoted = true;
        ref readonly var description = ref image.Description;
        if (!ImageDescription.IsEmptyRange(description.Data))
        {
            NoteGpuWrittenBytesLocked(description.Data.Address, description.Data.Size, image);
        }

        if (description.HasStencil && !ImageDescription.IsEmptyRange(description.Stencil))
        {
            NoteGpuWrittenBytesLocked(description.Stencil.Address, description.Stencil.Size, image);
        }
    }

    /// <summary>
    /// A GPU write lands in these guest bytes: they are recorded, and every lossy image
    /// over them but <paramref name="writer"/> gets a lossless texture refilled from guest
    /// memory and its aliases on its next use. Caller holds the lock.
    /// </summary>
    private void NoteGpuWrittenBytesLocked(ulong address, ulong size, MetalCachedImage? writer = null)
    {
        if (!_lossyTextures || size == 0)
        {
            return;
        }

        _gpuWrittenBytes.Mark(address, size);
        if (!_pageOwners.MayHaveOwners(address, size))
        {
            return;
        }

        foreach (var otherIdentifier in FindImagesInRange(address, size, pageOverlap: false))
        {
            if (_slots.TryGet(otherIdentifier) is { } other && !ReferenceEquals(other, writer) &&
                other.Shape.Lossy && other.Texture != 0 && other.Overlaps(address, size))
            {
                ReshapeLevelsLocked(other, other.FirstLevel, dropLossy: true);
                _lossyGpuBytePromotions++;
            }
        }
    }

    /// <summary>
    /// A copy is about to read the image's texture as the guest bytes: a lossy one becomes
    /// lossless and reloads them first. Caller holds the lock.
    /// </summary>
    private void DropLossyCopySourceLocked(MetalCachedImage image)
    {
        if (image.Shape.Lossy && image.Texture != 0)
        {
            ReshapeLevelsLocked(image, image.FirstLevel, dropLossy: true);
            _lossyCopySourcePromotions++;
        }
    }

    // [METAL-V2][LOSSY]: the lossy images alive and what they take, then, since the last
    // line, lossy images created, made lossless (all causes, then of those the GPU-written
    // bytes and copy-source ones) and left lossless for GPU-written bytes, and the GPU-written
    // granules recorded so far. Null when the flag is off or nothing changed.
    internal string? TakeLossyCensus()
    {
        if (!_lossyTextures)
        {
            return null;
        }

        using var held = _lock.Hold();
        var now = (_lossyCreated, Volatile.Read(ref _lossyPromotions), _lossyGpuBytePromotions,
            _lossyCopySourcePromotions, _lossyRefusedGpuBytes);
        var last = _lossyReported;
        _lossyReported = now;
        if (now == last)
        {
            return null;
        }

        long images = 0;
        ulong allocated = 0;
        _slots.ForEach((_, image) =>
        {
            if (image.Shape.Lossy && image.Texture != 0)
            {
                images++;
                allocated += image.AllocatedBytes;
            }
        });
        return string.Create(CultureInfo.InvariantCulture,
            $"[METAL-V2][LOSSY] images={images} mb={allocated / 1048576.0:F0} created={now.Item1 - last.Item1} " +
            $"promoted={now.Item2 - last.Item2} promoted_gpu_bytes={now.Item3 - last.Item3} " +
            $"promoted_copy_source={now.Item4 - last.Item4} refused_gpu_bytes={now.Item5 - last.Item5} " +
            $"gpu_written_granules={_gpuWrittenBytes.Count}");
    }

    private (long, long, long, long, long) _lossyReported;
}

/// <summary>
/// Guest bytes some GPU write has produced, in 64 KiB granules, which are never cleared.
/// Marking is lock-free and costs a read per 64 granules when they are marked already,
/// which is what a write repeated every draw finds.
/// </summary>
internal sealed class MetalGpuWrittenGranules
{
    private const int GranuleShift = 16;
    // One bitmap covers 1 GiB: 16384 granules in 256 words.
    private const int RegionShift = 30;
    private const int WordsPerRegion = 1 << (RegionShift - GranuleShift - 6);

    private readonly ConcurrentDictionary<ulong, ulong[]> _regions = new();
    private long _count;

    /// <summary>Granules marked so far.</summary>
    public long Count => Volatile.Read(ref _count);

    public void Mark(ulong address, ulong size)
    {
        if (size == 0 || address > ulong.MaxValue - (size - 1))
        {
            return;
        }

        var first = address >> GranuleShift;
        var last = (address + (size - 1)) >> GranuleShift;
        for (var granule = first; granule <= last;)
        {
            var bits = _regions.GetOrAdd(granule >> (RegionShift - GranuleShift), static _ => new ulong[WordsPerRegion]);
            var index = (int)((granule >> 6) & (WordsPerRegion - 1));
            var bit = (int)(granule & 63);
            var span = Math.Min(64UL - (ulong)bit, last - granule + 1);
            var mask = span == 64 ? ulong.MaxValue : ((1UL << (int)span) - 1) << bit;
            if ((Volatile.Read(ref bits[index]) & mask) != mask)
            {
                var before = Interlocked.Or(ref bits[index], mask);
                Interlocked.Add(ref _count, (long)ulong.PopCount(mask & ~before));
            }

            granule += span;
        }
    }

    public bool Overlaps(ulong address, ulong size)
    {
        if (size == 0 || address > ulong.MaxValue - (size - 1) || _regions.IsEmpty)
        {
            return false;
        }

        var first = address >> GranuleShift;
        var last = (address + (size - 1)) >> GranuleShift;
        for (var granule = first; granule <= last;)
        {
            var bit = (int)(granule & 63);
            var span = Math.Min(64UL - (ulong)bit, last - granule + 1);
            if (_regions.TryGetValue(granule >> (RegionShift - GranuleShift), out var bits))
            {
                var mask = span == 64 ? ulong.MaxValue : ((1UL << (int)span) - 1) << bit;
                if ((Volatile.Read(ref bits[(int)((granule >> 6) & (WordsPerRegion - 1))]) & mask) != 0)
                {
                    return true;
                }
            }

            granule += span;
        }

        return false;
    }
}
