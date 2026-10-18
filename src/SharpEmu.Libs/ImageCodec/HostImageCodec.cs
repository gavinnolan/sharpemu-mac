// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics.CodeAnalysis;

namespace SharpEmu.Libs.ImageCodec;

/// <summary>The still-image containers the guest image libraries hand to the host codec.</summary>
public enum HostImageContainer
{
    Png,
    Jpeg,
}

/// <summary>Layout of the packed (pitch = width * bytes-per-pixel) pixels given to an encode.</summary>
public enum HostImagePixelLayout
{
    /// <summary>R, G, B, A bytes; alpha is straight (not premultiplied) and is written out.</summary>
    Rgba8,

    /// <summary>R, G, B, X bytes; the fourth byte is ignored and the image has no alpha channel.</summary>
    Rgbx8,

    /// <summary>One luminance byte per pixel.</summary>
    Gray8,
}

/// <summary>
/// A fully decoded image: packed rows of R, G, B, A with straight alpha, 8 bits per
/// channel or, for <see cref="BitsPerChannel"/> 16, little-endian 16-bit channels.
/// Sources without alpha (and without a PNG tRNS chunk) carry an opaque alpha.
/// Grayscale sources are expanded to R = G = B.
/// </summary>
public sealed class HostDecodedImage(int width, int height, int bitsPerChannel, byte[] pixels)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public int BitsPerChannel { get; } = bitsPerChannel;

    public byte[] Pixels { get; } = pixels;

    public int BytesPerPixel => BitsPerChannel / 2;
}

/// <summary>
/// The host's PNG/JPEG codec: ImageIO on macOS. libScePngDec, libSceJpegDec, libScePngEnc
/// and libSceJpegEnc do their own argument validation and header parsing (the SDK's parse
/// calls only read the PNG IHDR / JPEG SOF, see <see cref="PngImageHeader"/> and
/// <see cref="JpegImageHeader"/>) and call this only for the actual pixel work.
/// </summary>
public interface IHostImageCodec
{
    /// <summary>
    /// Decodes a whole PNG or JPEG into a <see cref="HostDecodedImage"/> with
    /// <paramref name="bitsPerChannel"/> (8 or 16) bits per channel and no colour
    /// management: the samples are those stored in the file.
    /// </summary>
    bool TryDecode(
        ReadOnlySpan<byte> encoded,
        HostImageContainer container,
        int bitsPerChannel,
        [NotNullWhen(true)] out HostDecodedImage? image);

    /// <summary>
    /// Encodes packed <paramref name="pixels"/> into a PNG or JPEG file.
    /// <paramref name="jpegQuality"/> (1..100) is only used for JPEG.
    /// </summary>
    bool TryEncode(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        HostImagePixelLayout layout,
        HostImageContainer container,
        int jpegQuality,
        [NotNullWhen(true)] out byte[]? encoded);
}

/// <summary>
/// Registry for the host image codec the application composed in. The macOS platform
/// package registers its ImageIO codec at startup; with nothing registered the image
/// libraries fail their calls with the library's fatal error rather than pretend to decode.
/// </summary>
public static class HostImageCodec
{
    private static readonly object Gate = new();
    private static IHostImageCodec? _codec;
    private static string _name = "none";

    /// <summary>Installs the host codec. Called once from the composition root.</summary>
    public static void RegisterProvider(string name, IHostImageCodec codec)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(codec);
        lock (Gate)
        {
            _name = name;
            _codec = codec;
        }
    }

    public static IHostImageCodec? Current
    {
        get
        {
            lock (Gate)
            {
                return _codec;
            }
        }
    }

    public static string ProviderName
    {
        get
        {
            lock (Gate)
            {
                return _name;
            }
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _name = "none";
            _codec = null;
        }
    }
}
