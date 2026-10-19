// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>How faithfully a texture gives its guest bytes back through a retile.</summary>
internal enum MetalRoundTripFidelity
{
    /// <summary>
    /// The texture is not a copy of the guest bytes, or not one this transfer is known to
    /// retile exactly. The guest copy must be kept.
    /// </summary>
    None,

    /// <summary>
    /// Every byte a held element occupies comes back bit for bit, whatever it held, but the
    /// run also has bytes no held element maps to: padding inside edge blocks, mip-tail and
    /// layer slack, row pitch, and for a texture held from a later level the bytes of the
    /// levels it left out. A retile writes none of those, so a restore into released
    /// memory leaves them zero; a page holding any of them is releasable only once a verify
    /// has seen the guest bytes there match what the retile writes into zeroed staging.
    /// </summary>
    ElementsExact,

    /// <summary>
    /// Every byte of the run is an element byte of the held levels and comes back bit for
    /// bit, whatever it held. A restore reproduces the run without a verify.
    /// </summary>
    Exact,
}

/// <param name="RunStart">Where the run the texture's levels occupy starts in description.Data.</param>
/// <param name="RunEnd">Where that run ends.</param>
/// <param name="ElementBytes">Bytes of the run the held elements occupy.</param>
/// <param name="StencilElementBytes">Bytes of description.Stencil the stencil elements occupy, all of it when exact.</param>
/// <param name="Reason">Why the fidelity is not <see cref="MetalRoundTripFidelity.Exact"/>; empty when it is.</param>
internal readonly record struct MetalRoundTripVerdict(
    MetalRoundTripFidelity Fidelity,
    ulong RunStart,
    ulong RunEnd,
    ulong ElementBytes,
    ulong StencilElementBytes,
    string Reason);

// The release side of single-copy textures (artifacts/memopt/single-copy-textures.md, L3):
// which images give their guest bytes back exactly when the guest copy is rebuilt from the
// texture with EncodeTextureToGuest. Grounded in the device matrix of
// MetalImageTransferTests.RoundTripFidelity, which fills every guest byte of each
// description with random data, uploads, retiles over zeroes and over a sentinel, and
// splits the run where AlignRunEnd allows: every element byte came back for every tile
// mode, element size, block format, depth and stencil plane, array, volume and
// BASE_LEVEL cut it covers, and no byte outside an element was ever written.
internal sealed partial class MetalImageTransfer
{
    private static readonly nint SelCompressionType = MetalNative.Selector("compressionType");

    // MTLTextureCompressionTypeLossy.
    private const nuint LossyCompression = 1;

    /// <summary>
    /// True when the texture of an image the cache holds from <paramref name="firstLevel"/>
    /// reproduces every byte of its run exactly: the guest copy of that run may be dropped
    /// and rebuilt from the texture with no verify. See <see cref="ClassifyRoundTrip"/>.
    /// </summary>
    public bool IsRoundTripExact(in ImageDescription description, uint firstLevel = 0, nint texture = 0) =>
        ClassifyRoundTrip(description, firstLevel, texture).Fidelity == MetalRoundTripFidelity.Exact;

    /// <summary>
    /// How exactly a retile of the image's texture gives back its guest bytes. The
    /// description is one the cache holds (its upload was planned); <paramref name="texture"/>,
    /// when given, is checked too, because a lossily compressed texture keeps no copy of
    /// anything. The verdict says nothing about whether the bytes are still current - the
    /// image must also be clean and the only image over its bytes (L1).
    /// </summary>
    public MetalRoundTripVerdict ClassifyRoundTrip(in ImageDescription description, uint firstLevel = 0, nint texture = 0)
    {
        if (ImageDescription.IsEmptyRange(description.Data) || description.Data.Size == 0)
        {
            return Refuse("the image has no guest bytes");
        }

        if (description.Samples != 1)
        {
            // Ready() skips a multisampled transfer in both directions.
            return Refuse("a multisampled image is never uploaded or retiled");
        }

        if (description.HasMetadata)
        {
            // A DCC or HTILE surface's guest bytes mean nothing without its metadata,
            // which no texture holds, and a sampled DCC image can hold a clear colour the
            // cache materialised from that metadata instead of what the guest bytes say.
            return Refuse($"the image carries {description.Metadata.Kind} metadata the texture does not hold");
        }

        if (!IsCoveredFormatPair(description))
        {
            return Refuse(
                $"the format pair is not one the round-trip matrix covers: guest={description.GuestFormat} " +
                $"host={description.PixelFormat} bgra16={description.Bgra16}");
        }

        var levels = description.Resources.Levels;
        if (levels == 0 || firstLevel >= levels || (firstLevel != 0 && (description.IsDepth || description.IsVolume)))
        {
            return Refuse($"no texture holds this image from level {firstLevel}");
        }

        if (texture != 0 && MetalV2Native.SendValue(texture, SelCompressionType) == LossyCompression)
        {
            return Refuse("the texture is lossily compressed");
        }

        var (start, end) = description.IsDepth ? (0UL, description.Data.Size) : LevelBytes(description, firstLevel);
        var (elements, stencilElements) = ElementBytes(description, firstLevel);
        var run = end - start;
        if (elements > run || (description.HasStencil && stencilElements > description.Stencil.Size))
        {
            return Refuse("the image's elements reach past the bytes it claims", start, end);
        }

        if (elements == run && (!description.HasStencil || stencilElements == description.Stencil.Size))
        {
            return new MetalRoundTripVerdict(MetalRoundTripFidelity.Exact, start, end, elements, stencilElements, string.Empty);
        }

        var reason = firstLevel != 0
            ? $"the run also holds levels below {firstLevel} and padding no held element maps to"
            : "the run also holds padding no element maps to";
        return new MetalRoundTripVerdict(MetalRoundTripFidelity.ElementsExact, start, end, elements, stencilElements,
            $"{reason}: 0x{run - elements:x} of 0x{run:x} bytes" +
            (description.HasStencil ? $", stencil 0x{description.Stencil.Size - stencilElements:x}" : string.Empty));
    }

    private static MetalRoundTripVerdict Refuse(string reason, ulong start = 0, ulong end = 0) =>
        new(MetalRoundTripFidelity.None, start, end, 0, 0, reason);

    // The pairs the round-trip matrix moves: every guest format under the host format the
    // engine samples it as (each a bit-exact blit or a same-width unsigned view of it),
    // the depth formats under their attachment formats - which includes the 16-bit plane
    // the tiler widens to 32-bit float and narrows back - and the BGRA16 display swap. A
    // pair outside these may still be exact, but nothing has shown it.
    private static bool IsCoveredFormatPair(in ImageDescription description)
    {
        if (!MetalGuestFormats.TryDecodeHostFormat(description.PixelFormat, out _) ||
            GuestPixelFormats.IsFmaskFormat(description.GuestFormat) ||
            !TileGeometry.TryGetElementLayout(description.GuestFormat, out _))
        {
            return false;
        }

        if (description.IsDepth)
        {
            var rule = DepthFormatRule.FindByGuestFormat(description.GuestFormat);
            return !description.Bgra16 && rule is not null && rule.BytesPerElement == description.BytesPerBlock &&
                   (description.PixelFormat == rule.DepthAttachmentFormat || rule.IsStencilAttachmentFormat(description.PixelFormat));
        }

        if (description.Bgra16)
        {
            return description.GuestFormat == GuestPixelFormat.Bits16_16_16_16Float &&
                   description.PixelFormat == GpuFormat.R16G16B16A16Sfloat;
        }

        return GuestPixelFormats.HostFormat(description.GuestFormat) == description.PixelFormat;
    }

    // The bytes the held elements occupy, counted from the extents alone: one element per
    // texel (or 4x4 block) of every held level and layer, or depth slice of a volume. The
    // device matrix checks this equals the bytes the tile layout maps elements to.
    internal static (ulong Data, ulong Stencil) ElementBytes(in ImageDescription description, uint firstLevel)
    {
        var extent = description.Extent;
        if (description.IsDepth)
        {
            var plane = (ulong)extent.Width * extent.Height * description.Resources.Layers;
            return (plane * description.BytesPerBlock, description.HasStencil ? plane : 0);
        }

        var texel = description.IsBlock ? 4u : 1u;
        ulong bytes = 0;
        for (var level = firstLevel; level < description.Resources.Levels; level++)
        {
            var width = Math.Max(extent.Width >> (int)level, 1);
            var height = Math.Max(extent.Height >> (int)level, 1);
            var depth = description.IsVolume ? Math.Max(extent.Depth >> (int)level, 1) : description.Resources.Layers;
            bytes += (ulong)((width + texel - 1) / texel) * ((height + texel - 1) / texel) * depth *
                     description.BytesPerBlock;
        }

        return (bytes, 0);
    }
}
