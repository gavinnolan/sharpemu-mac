// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Images;

namespace SharpEmu.Gpu.Metal.V2;

// A guest image that is only ever sampled, laid out the way Metal lays out a linear
// texture, needs no copy at all: an MTLTexture created over the guest-window buffer reads
// the bytes where the guest wrote them. A title that rewrites hundreds of small linear
// images a frame (SILENT HILL's video planes) otherwise pays a page-watch fault, a render
// pass split and a blit for each one.
internal static class MetalLinearWindowView
{
    // SHARPEMU_METAL_V2_LINEAR_VIEW=0 gives every image its own texture again.
    internal static bool Enabled { get; set; } =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_LINEAR_VIEW") != "0";

    // The largest width or height of a buffer-backed texture on every Apple GPU family.
    private const uint MaxExtent = 16384;

    /// <summary>
    /// Whether a pixel format can back a linear texture at all, so the device may be asked
    /// its linear alignment: Metal asserts on that question for a compressed, depth, stencil
    /// or packed-chroma format and ends the process. Only the ordinary colour formats
    /// (MTLPixelFormat 1 through 129) qualify.
    /// </summary>
    public static bool SupportsLinearFormat(ulong pixelFormat) =>
        pixelFormat is > 0 and < 130 &&
        !MetalGuestFormats.IsDepthStencil((MtlPixelFormat)pixelFormat);

    /// <summary>
    /// Whether an image can be a texture over the window bytes it sits in.
    /// <paramref name="alignment"/> is the device's minimum linear texture alignment for
    /// the pixel format; the window offset and the row pitch both have to be multiples of it.
    /// </summary>
    public static bool Qualifies(
        in ImageDescription description,
        in MetalTextureShape shape,
        ulong windowOffset,
        ulong alignment,
        out ulong bytesPerRow)
    {
        bytesPerRow = 0;
        if (description.TileMode != GuestTileMode.Linear ||
            description.Type != GuestImageType.Color2D ||
            description.Resources.Levels != 1 || description.Resources.Layers != 1 ||
            description.Samples != 1 || description.Extent.Depth > 1 ||
            description.HasMetadata || description.HasStencil || description.IsDepth ||
            description.IsBlock || description.Bgra16 ||
            !ImageDescription.IsValidRange(description.Data) ||
            shape.TextureType != Mtl.TextureType2D || shape.SampleCount != 1 ||
            shape.MipLevels != 1 || shape.ArrayLength != 1 ||
            shape.Width == 0 || shape.Height == 0 || shape.Width > MaxExtent || shape.Height > MaxExtent ||
            alignment == 0 || description.BytesPerBlock == 0 ||
            MetalGuestFormats.IsDepthStencil((MtlPixelFormat)shape.PixelFormat))
        {
            return false;
        }

        if (!TileGeometry.TryGetElementLayout(description.GuestFormat, out var element) ||
            element.TexelWidth != 1 || element.TexelHeight != 1 || element.Bytes != description.BytesPerBlock)
        {
            return false;
        }

        var (spans, padded) = MipLayoutScratch.Rent();
        if (!TileGeometry.TryGetTextureSize(
                description.GuestFormat, description.Extent.Width, description.Extent.Height, 1,
                GuestTileMode.Linear, out _, spans, padded))
        {
            return false;
        }

        var pitch = padded[0].Width;
        var rowBytes = (ulong)pitch * element.Bytes;
        if (pitch < description.Extent.Width || rowBytes % alignment != 0 || windowOffset % alignment != 0 ||
            rowBytes * description.Extent.Height > description.Data.Size)
        {
            return false;
        }

        bytesPerRow = rowBytes;
        return true;
    }
}
