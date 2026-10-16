// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.ImageCodec;
using Xunit;

namespace SharpEmu.Libs.Tests.ImageCodec;

/// <summary>
/// The image libraries end to end through the macOS ImageIO codec: encode through
/// scePngEnc/sceJpegEnc, decode back through scePngDec/sceJpegDec. Each test is a no-op on
/// hosts other than macOS, where no codec is composed in.
/// </summary>
[Collection(ImageCodecStateCollection.Name)]
public sealed class MacImageIOCodecTests : ImageCodecTestBase
{
    private const int Width = 13;
    private const int Height = 7;
    private const ulong EncodedAddress = MemoryBase + 0x4_0000;
    private const ulong DecodedAddress = MemoryBase + 0xC_0000;

    [Fact]
    public void PngRoundTrip_IsExact_IncludingTranslucentAlpha()
    {
        if (!UseMacCodec())
        {
            return;
        }

        var source = Pattern(Width, Height, translucent: true);
        var pngSize = EncodePng(source, PngEncExports.PixelFormatR8G8B8A8, PngEncExports.ColorSpaceRgba);

        // Parse reports the encoded file's header.
        WriteParseParam(EncodedAddress, (uint)pngSize);
        Assert.Equal(0, Call(PngDecExports.PngDecParseHeader, ParamAddress, InfoAddress));
        Assert.Equal((uint)Width, ReadU32(InfoAddress));
        Assert.Equal((uint)Height, ReadU32(InfoAddress + 0x04));
        Assert.Equal(PngDecExports.ColorSpaceRgba, ReadU16(InfoAddress + 0x08));
        Assert.Equal((ushort)8, ReadU16(InfoAddress + 0x0A));

        var decoded = DecodePng((uint)pngSize, PngDecExports.PixelFormatR8G8B8A8, alpha: 0x12, pitch: 0);
        Assert.Equal(source, decoded);
    }

    [Fact]
    public void PngDecode_WritesBgraIntoAPitchedBuffer_AndFillsAlphaForOpaqueSources()
    {
        if (!UseMacCodec())
        {
            return;
        }

        var source = Pattern(Width, Height, translucent: false);
        var pngSize = EncodePng(source, PngEncExports.PixelFormatB8G8R8A8, PngEncExports.ColorSpaceRgb);

        // The BGRA input was swapped to RGB on the way in, so decoding as BGRA gives it back
        // with the caller's alpha (the file has no alpha channel).
        const uint Pitch = Width * 4 + 12;
        Write(DecodedAddress, Enumerable.Repeat((byte)0xEE, (int)Pitch * Height).ToArray());
        WriteCreateParam(0x0C, 0, 1920);
        var handle = Create(PngDecExports.PngDecCreate, SecondWorkAddress, PngDecExports.WorkMemorySize);
        WritePngDecodeParam(EncodedAddress, DecodedAddress, (uint)pngSize, Pitch * Height, PngDecExports.PixelFormatB8G8R8A8, 0x80, Pitch);
        Assert.Equal((Width << 16) | Height, Call(PngDecExports.PngDecDecode, handle, ParamAddress, InfoAddress));
        Assert.Equal(PngDecExports.ColorSpaceRgb, ReadU16(InfoAddress + 0x08));

        for (var y = 0; y < Height; y++)
        {
            var row = Read(DecodedAddress + (ulong)(y * Pitch), (int)Pitch);
            for (var x = 0; x < Width; x++)
            {
                var expected = source.AsSpan((y * Width + x) * 4, 3).ToArray();
                Assert.Equal(expected, row.AsSpan(x * 4, 3).ToArray());
                Assert.Equal(0x80, row[x * 4 + 3]);
            }

            // The padding between rows is untouched.
            Assert.All(row[(Width * 4)..], value => Assert.Equal(0xEE, value));
        }
    }

    [Fact]
    public void PngDecode_HandlesGrayPaletteAndSixteenBitSources()
    {
        if (!UseMacCodec())
        {
            return;
        }

        // 8-bit grayscale: R = G = B = gray, alpha from the caller.
        byte[] gray = [0, 64, 128, 255, 1, 2];
        var grayPng = TestPng.Build(3, 2, 8, 0, TestPng.Unfiltered(gray, 3));
        Write(FileAddress, grayPng);
        var decoded = DecodePng((uint)grayPng.Length, PngDecExports.PixelFormatR8G8B8A8, alpha: 0x55, pitch: 0, file: FileAddress, width: 3, height: 2);
        for (var pixel = 0; pixel < gray.Length; pixel++)
        {
            Assert.Equal(new byte[] { gray[pixel], gray[pixel], gray[pixel], 0x55 }, decoded.AsSpan(pixel * 4, 4).ToArray());
        }

        // Palette with tRNS: alpha comes from the file, not the caller.
        byte[] palette = [255, 0, 0, 0, 255, 0, 0, 0, 255];
        byte[] indices = [0, 1, 2, 1];
        var palettePng = TestPng.Build(2, 2, 8, 3, TestPng.Unfiltered(indices, 2), palette: palette, transparency: [0x40, 0xFF, 0x00]);
        Write(FileAddress, palettePng);
        decoded = DecodePng((uint)palettePng.Length, PngDecExports.PixelFormatR8G8B8A8, alpha: 0x99, pitch: 0, file: FileAddress, width: 2, height: 2);
        Assert.Equal(new byte[] { 255, 0, 0, 0x40, 0, 255, 0, 0xFF, 0, 0, 255, 0x00, 0, 255, 0, 0xFF }, decoded);

        // 16-bit RGBA with the 16-bit attribute: little-endian 16-bit channels, R/B swapped for BGRA.
        ushort[] samples = [0x1234, 0xABCD, 0x0001, 0xFFFF, 0x8000, 0x7FFF, 0x00FF, 0x4321];
        var raw = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(index * 2), samples[index]);
        }

        var deepPng = TestPng.Build(2, 1, 16, 6, TestPng.Unfiltered(raw, 16));
        Write(FileAddress, deepPng);
        WriteCreateParam(0x0C, PngDecExports.AttributeBitDepth16, 1920);
        var handle = Create(PngDecExports.PngDecCreate, SecondWorkAddress, PngDecExports.WorkMemorySize);
        WritePngDecodeParam(FileAddress, DecodedAddress, (uint)deepPng.Length, 16, PngDecExports.PixelFormatB8G8R8A8, 0, 0);
        Assert.Equal((2 << 16) | 1, Call(PngDecExports.PngDecDecode, handle, ParamAddress));
        var deep = Read(DecodedAddress, 16);
        ushort[] expected = [0x0001, 0xABCD, 0x1234, 0xFFFF, 0x00FF, 0x7FFF, 0x8000, 0x4321];
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], BinaryPrimitives.ReadUInt16LittleEndian(deep.AsSpan(index * 2)));
        }
    }

    [Fact]
    public void JpegRoundTrip_StaysWithinTolerance()
    {
        if (!UseMacCodec())
        {
            return;
        }

        const int W = 32;
        const int H = 16;
        var source = Gradient(W, H);
        Write(ImageAddress, source);
        WriteCreateParam(0x08, 0);
        var encoder = Create(JpegEncExports.JpegEncCreate, WorkAddress, JpegEncExports.WorkMemorySize);
        WriteJpegEncodeParam(
            ImageAddress, EncodedAddress, (uint)source.Length, 0x8000, W, H, W * 4,
            JpegEncExports.PixelFormatR8G8B8A8, JpegEncExports.EncodeModeNormal, JpegEncExports.ColorSpaceYcc,
            JpegEncExports.SamplingType420, ratio: 0);
        const ulong OutputInfo = MemoryBase + 0x400;
        Assert.Equal(0, Call(JpegEncExports.JpegEncEncode, encoder, ParamAddress, OutputInfo));
        var jpegSize = ReadU32(OutputInfo);
        Assert.InRange(jpegSize, 100u, 0x8000u);
        Assert.Equal((uint)H, ReadU32(OutputInfo + 0x04));
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, Read(EncodedAddress, 2));

        WriteParseParam(EncodedAddress, jpegSize);
        Assert.Equal(0, Call(JpegDecExports.JpegDecParseHeader, ParamAddress, InfoAddress));
        Assert.Equal((uint)W, ReadU32(InfoAddress));
        Assert.Equal((uint)H, ReadU32(InfoAddress + 0x04));
        Assert.Equal((ushort)3, ReadU16(InfoAddress + 0x0A));

        WriteCreateParam(0x0C, JpegDecExports.AttributeCscOrdinary, 1920);
        var decoder = Create(JpegDecExports.JpegDecCreate, SecondWorkAddress, JpegDecExports.WorkMemorySize);
        WriteJpegDecodeParam(EncodedAddress, DecodedAddress, jpegSize, W * H * 4, JpegDecExports.PixelFormatR8G8B8A8, 0x7F, 0);
        Assert.Equal((W << 16) | H, Call(JpegDecExports.JpegDecDecode, decoder, ParamAddress, InfoAddress));

        var decoded = Read(DecodedAddress, W * H * 4);
        for (var pixel = 0; pixel < W * H; pixel++)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                Assert.InRange(Math.Abs(decoded[pixel * 4 + channel] - source[pixel * 4 + channel]), 0, 12);
            }

            Assert.Equal(0x7F, decoded[pixel * 4 + 3]);
        }

        // Down scale 2 writes a 16x8 image.
        WriteJpegDecodeParam(EncodedAddress, DecodedAddress, jpegSize, 16 * 8 * 4, JpegDecExports.PixelFormatB8G8R8A8, 0xFF, 0, downScale: 2);
        Assert.Equal((16 << 16) | 8, Call(JpegDecExports.JpegDecDecode, decoder, ParamAddress, InfoAddress));
        Assert.Equal(16u, ReadU32(InfoAddress + 0x1C));
        Assert.Equal(8u, ReadU32(InfoAddress + 0x20));
    }

    [Fact]
    public void JpegEncode_GrayscaleAndOverflow()
    {
        if (!UseMacCodec())
        {
            return;
        }

        const int W = 16;
        const int H = 16;
        var gray = new byte[W * H];
        for (var index = 0; index < gray.Length; index++)
        {
            gray[index] = (byte)(index % W * 16);
        }

        Write(ImageAddress, gray);
        WriteCreateParam(0x08, 0);
        var encoder = Create(JpegEncExports.JpegEncCreate, WorkAddress, JpegEncExports.WorkMemorySize);
        WriteJpegEncodeParam(
            ImageAddress, EncodedAddress, W * H, 0x8000, W, H, W, JpegEncExports.PixelFormatY8,
            JpegEncExports.EncodeModeNormal, JpegEncExports.ColorSpaceGrayscale, JpegEncExports.SamplingTypeFull, ratio: 10);
        const ulong OutputInfo = MemoryBase + 0x400;
        Assert.Equal(0, Call(JpegEncExports.JpegEncEncode, encoder, ParamAddress, OutputInfo));
        var jpegSize = ReadU32(OutputInfo);

        WriteParseParam(EncodedAddress, jpegSize);
        Assert.Equal(0, Call(JpegDecExports.JpegDecParseHeader, ParamAddress, InfoAddress));
        Assert.Equal((ushort)1, ReadU16(InfoAddress + 0x0A));
        Assert.Equal(JpegDecExports.ColorSpaceGrayscale, ReadU16(InfoAddress + 0x08));

        // An output buffer one byte too small.
        WriteJpegEncodeParam(
            ImageAddress, EncodedAddress, W * H, jpegSize - 1, W, H, W, JpegEncExports.PixelFormatY8,
            JpegEncExports.EncodeModeNormal, JpegEncExports.ColorSpaceGrayscale, JpegEncExports.SamplingTypeFull, ratio: 10);
        Assert.Equal(JpegEncExports.ErrorInvalidSize, Call(JpegEncExports.JpegEncEncode, encoder, ParamAddress, OutputInfo));
    }

    [Fact]
    public void PngEncode_OverflowZeroesTheOutputInfo()
    {
        if (!UseMacCodec())
        {
            return;
        }

        var source = Pattern(Width, Height, translucent: true);
        Write(ImageAddress, source);
        WriteCreateParam(0x10, 0, 1920, 0);
        var encoder = Create(PngEncExports.PngEncCreate, WorkAddress, PngEncExports.WorkMemorySize);
        const ulong OutputInfo = MemoryBase + 0x400;
        Write(OutputInfo, [1, 1, 1, 1, 1, 1, 1, 1]);
        WritePngEncodeParam(ImageAddress, EncodedAddress, (uint)source.Length, 16, Width, Height, Width * 4, 0, PngEncExports.ColorSpaceRgba);
        Assert.Equal(PngEncExports.ErrorDataOverflow, Call(PngEncExports.PngEncEncode, encoder, ParamAddress, OutputInfo));
        Assert.Equal(0ul, ReadU64(OutputInfo));
    }

    private int EncodePng(byte[] source, ushort pixelFormat, ushort colorSpace)
    {
        Write(ImageAddress, source);
        WriteCreateParam(0x10, 0, 1920, 5);
        var encoder = Create(PngEncExports.PngEncCreate, WorkAddress, PngEncExports.WorkMemorySize);
        WritePngEncodeParam(
            ImageAddress, EncodedAddress, (uint)source.Length, 0x8000, Width, Height, Width * 4,
            pixelFormat, colorSpace, filter: PngEncExports.FilterAll);
        const ulong OutputInfo = MemoryBase + 0x400;
        var size = Call(PngEncExports.PngEncEncode, encoder, ParamAddress, OutputInfo);
        Assert.True(size > 0, $"scePngEncEncode returned 0x{size:X8}");
        Assert.Equal((uint)size, ReadU32(OutputInfo));
        Assert.Equal((uint)Height, ReadU32(OutputInfo + 0x04));
        Assert.Equal(0, Call(PngEncExports.PngEncDelete, encoder));
        return size;
    }

    private byte[] DecodePng(
        uint pngSize, ushort pixelFormat, ushort alpha, uint pitch,
        ulong file = EncodedAddress, int width = Width, int height = Height)
    {
        WriteCreateParam(0x0C, 0, 1920);
        var handle = Create(PngDecExports.PngDecCreate, SecondWorkAddress, PngDecExports.WorkMemorySize);
        var size = (uint)(width * height * 4);
        WritePngDecodeParam(file, DecodedAddress, pngSize, size, pixelFormat, alpha, pitch);
        Assert.Equal((width << 16) | height, Call(PngDecExports.PngDecDecode, handle, ParamAddress, InfoAddress));
        Assert.Equal((uint)width, ReadU32(InfoAddress));
        Assert.Equal(0, Call(PngDecExports.PngDecDelete, handle));
        return Read(DecodedAddress, (int)size);
    }

    private static byte[] Pattern(int width, int height, bool translucent)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = (byte)(x * 19 + y * 3);
                pixels[offset + 1] = (byte)(y * 37 + x);
                pixels[offset + 2] = (byte)((x ^ y) * 23);
                pixels[offset + 3] = translucent ? (byte)(x * 20 + y * 5 + 1) : (byte)0xFF;
            }
        }

        return pixels;
    }

    private static byte[] Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = (byte)(x * 255 / (width - 1));
                pixels[offset + 1] = (byte)(y * 255 / (height - 1));
                pixels[offset + 2] = 96;
                pixels[offset + 3] = 0xFF;
            }
        }

        return pixels;
    }
}
