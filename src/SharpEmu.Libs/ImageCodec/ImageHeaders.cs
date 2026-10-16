// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.ImageCodec;

/// <summary>PNG IHDR colour types (PNG spec 11.2.2).</summary>
public enum PngColorType : byte
{
    Grayscale = 0,
    Rgb = 2,
    Palette = 3,
    GrayscaleAlpha = 4,
    Rgba = 6,
}

/// <summary>
/// What scePngDecParseHeader reports: the IHDR fields plus whether a tRNS chunk precedes
/// the image data. Parsing the header is the SDK contract of the parse call, so it is done
/// here rather than by the host codec.
/// </summary>
public readonly record struct PngImageHeader(
    uint Width,
    uint Height,
    byte BitDepth,
    PngColorType ColorType,
    bool Interlaced,
    bool HasTransparencyChunk)
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private const uint IhdrType = 0x49484452; // "IHDR"
    private const uint TrnsType = 0x74524E53; // "tRNS"
    private const uint IdatType = 0x49444154; // "IDAT"
    private const uint IendType = 0x49454E44; // "IEND"

    /// <summary>The image carries alpha samples (an alpha channel or a tRNS chunk).</summary>
    public bool HasAlpha =>
        HasTransparencyChunk || ColorType is PngColorType.GrayscaleAlpha or PngColorType.Rgba;

    public static bool TryParse(ReadOnlySpan<byte> data, out PngImageHeader header)
    {
        header = default;

        // Signature (8) + IHDR length/type (8) + IHDR body (13) + CRC (4).
        if (data.Length < 33 || !data[..8].SequenceEqual(Signature))
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(data[8..]) != 13 ||
            BinaryPrimitives.ReadUInt32BigEndian(data[12..]) != IhdrType)
        {
            return false;
        }

        var ihdr = data.Slice(16, 13);
        var width = BinaryPrimitives.ReadUInt32BigEndian(ihdr);
        var height = BinaryPrimitives.ReadUInt32BigEndian(ihdr[4..]);
        var bitDepth = ihdr[8];
        var colorType = ihdr[9];
        var compression = ihdr[10];
        var filter = ihdr[11];
        var interlace = ihdr[12];

        // The spec caps both dimensions at 2^31 - 1.
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue ||
            compression != 0 || filter != 0 || interlace > 1 ||
            !IsValidDepth((PngColorType)colorType, bitDepth))
        {
            return false;
        }

        var hasTransparency = false;
        var offset = 33;
        while (offset + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            var type = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
            if (type is IdatType or IendType)
            {
                break;
            }

            if (type == TrnsType)
            {
                hasTransparency = true;
                break;
            }

            var next = (long)offset + 12 + length;
            if (next > data.Length)
            {
                break;
            }

            offset = (int)next;
        }

        header = new PngImageHeader(
            width,
            height,
            bitDepth,
            (PngColorType)colorType,
            interlace == 1,
            hasTransparency);
        return true;
    }

    private static bool IsValidDepth(PngColorType colorType, byte depth) => colorType switch
    {
        PngColorType.Grayscale => depth is 1 or 2 or 4 or 8 or 16,
        PngColorType.Palette => depth is 1 or 2 or 4 or 8,
        PngColorType.Rgb or PngColorType.GrayscaleAlpha or PngColorType.Rgba => depth is 8 or 16,
        _ => false,
    };
}

/// <summary>
/// The frame header (SOFn) of a JPEG stream: what sceJpegDecParseHeader reports.
/// </summary>
public readonly record struct JpegImageHeader(
    uint Width,
    uint Height,
    byte Precision,
    int ComponentCount,
    uint ComponentIds,
    uint SamplingFactors,
    bool Progressive)
{
    /// <summary>Component id <paramref name="index"/> (0..3) as stored in the SOF segment.</summary>
    public byte ComponentId(int index) => (byte)(ComponentIds >> (index * 8));

    /// <summary>Sampling factor byte (H &lt;&lt; 4 | V) of component <paramref name="index"/> (0..3).</summary>
    public byte SamplingFactor(int index) => (byte)(SamplingFactors >> (index * 8));

    public static bool TryParse(ReadOnlySpan<byte> data, out JpegImageHeader header)
    {
        header = default;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return false;
        }

        var offset = 2;
        while (offset + 4 <= data.Length)
        {
            if (data[offset] != 0xFF)
            {
                return false;
            }

            var marker = data[offset + 1];
            if (marker == 0xFF)
            {
                // Fill byte before a marker.
                offset++;
                continue;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                // Stand-alone markers: no length field.
                offset += 2;
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                // EOI or start of scan before any frame header.
                return false;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            if (length < 2 || offset + 2 + length > data.Length)
            {
                return false;
            }

            var segment = data.Slice(offset + 4, length - 2);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return TryParseFrame(marker, segment, out header);
            }

            offset += 2 + length;
        }

        return false;
    }

    private static bool TryParseFrame(byte marker, ReadOnlySpan<byte> segment, out JpegImageHeader header)
    {
        header = default;
        if (segment.Length < 6)
        {
            return false;
        }

        var precision = segment[0];
        var height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..]);
        var width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]);
        var components = segment[5];

        // A zero height defers to a DNL marker, which the image libraries do not take.
        if (width == 0 || height == 0 || components == 0 || segment.Length < 6 + components * 3)
        {
            return false;
        }

        uint ids = 0;
        uint factors = 0;
        for (var index = 0; index < Math.Min((int)components, 4); index++)
        {
            ids |= (uint)segment[6 + index * 3] << (index * 8);
            factors |= (uint)segment[7 + index * 3] << (index * 8);
        }

        header = new JpegImageHeader(
            width,
            height,
            precision,
            components,
            ids,
            factors,
            Progressive: marker is 0xC2 or 0xC6 or 0xCA or 0xCE);
        return true;
    }
}
