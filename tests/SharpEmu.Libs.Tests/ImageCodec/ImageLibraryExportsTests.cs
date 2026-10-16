// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.ImageCodec;
using Xunit;

namespace SharpEmu.Libs.Tests.ImageCodec;

/// <summary>
/// Argument validation, struct writes and handle lifetime of libScePngDec, libSceJpegDec,
/// libScePngEnc and libSceJpegEnc. None of these need a host codec, so they run everywhere.
/// </summary>
[Collection(ImageCodecStateCollection.Name)]
public sealed class ImageLibraryExportsTests : ImageCodecTestBase
{
    // ---- libScePngDec ----

    [Fact]
    public void PngDecQueryMemorySize_ValidatesTheCreateParam()
    {
        WriteCreateParam(0x0C, PngDecExports.AttributeNone, 1920);
        Assert.Equal(PngDecExports.WorkMemorySize, Call(PngDecExports.PngDecQueryMemorySize, ParamAddress));

        WriteCreateParam(0x0C, PngDecExports.AttributeBitDepth16, PngDecExports.MaxImageWidth);
        Assert.Equal(PngDecExports.WorkMemorySize, Call(PngDecExports.PngDecQueryMemorySize, ParamAddress));

        Assert.Equal(PngDecExports.ErrorInvalidParam, Call(PngDecExports.PngDecQueryMemorySize, 0));

        WriteCreateParam(0x10, 0, 1920);
        Assert.Equal(PngDecExports.ErrorInvalidSize, Call(PngDecExports.PngDecQueryMemorySize, ParamAddress));

        WriteCreateParam(0x0C, 2, 1920);
        Assert.Equal(PngDecExports.ErrorInvalidParam, Call(PngDecExports.PngDecQueryMemorySize, ParamAddress));

        WriteCreateParam(0x0C, 0, 0);
        Assert.Equal(PngDecExports.ErrorInvalidSize, Call(PngDecExports.PngDecQueryMemorySize, ParamAddress));

        WriteCreateParam(0x0C, 0, PngDecExports.MaxImageWidth + 1);
        Assert.Equal(PngDecExports.ErrorInvalidSize, Call(PngDecExports.PngDecQueryMemorySize, ParamAddress));
    }

    [Fact]
    public void PngDecCreate_PlacesAnAlignedHandleInTheWorkMemory_AndDeleteRetiresIt()
    {
        WriteCreateParam(0x0C, PngDecExports.AttributeBitDepth16, 640);
        var unaligned = WorkAddress + 3;
        var handle = Create(PngDecExports.PngDecCreate, unaligned, PngDecExports.WorkMemorySize);

        Assert.Equal(WorkAddress + 8, handle);
        Assert.Equal(handle, ReadU64(handle));
        Assert.Equal(PngDecExports.AttributeBitDepth16, ReadU32(handle + 0x08));
        Assert.Equal(640u, ReadU32(handle + 0x0C));

        Assert.Equal(PngDecExports.ErrorInvalidHandle, Call(PngDecExports.PngDecDelete, handle + 8));
        Assert.Equal(PngDecExports.ErrorInvalidHandle, Call(PngDecExports.PngDecDelete, handle + 1));
        Assert.Equal(0, Call(PngDecExports.PngDecDelete, handle));
        Assert.Equal(PngDecExports.ErrorInvalidHandle, Call(PngDecExports.PngDecDelete, handle));
        Assert.Equal(PngDecExports.ErrorInvalidHandle, Call(PngDecExports.PngDecDelete, 0));

        WritePngDecodeParam(FileAddress, ImageAddress, 64, 64, 0, 0xFF, 0);
        Assert.Equal(PngDecExports.ErrorInvalidHandle, Call(PngDecExports.PngDecDecode, handle, ParamAddress));
    }

    [Fact]
    public void PngDecCreate_RejectsMissingAddressesAndShortWorkMemory()
    {
        WriteCreateParam(0x0C, 0, 640);
        Assert.Equal(PngDecExports.ErrorInvalidAddr, Call(PngDecExports.PngDecCreate, ParamAddress, 0, 0x20, HandleAddress));
        Assert.Equal(PngDecExports.ErrorInvalidAddr, Call(PngDecExports.PngDecCreate, ParamAddress, WorkAddress, 0x20, 0));
        Assert.Equal(
            PngDecExports.ErrorInvalidWorkMemory,
            Call(PngDecExports.PngDecCreate, ParamAddress, WorkAddress, 0x1F, HandleAddress));
    }

    [Fact]
    public void PngDecParseHeader_ReportsIhdrAndTransparency()
    {
        var png = TestPng.Build(
            300, 200, 8, 2, [], interlaced: true, transparency: [0, 1, 0, 2, 0, 3], includeImageData: false);
        Write(FileAddress, png);
        WriteParseParam(FileAddress, (uint)png.Length);

        Assert.Equal(0, Call(PngDecExports.PngDecParseHeader, ParamAddress, InfoAddress));
        Assert.Equal(300u, ReadU32(InfoAddress));
        Assert.Equal(200u, ReadU32(InfoAddress + 0x04));
        Assert.Equal(PngDecExports.ColorSpaceRgb, ReadU16(InfoAddress + 0x08));
        Assert.Equal((ushort)8, ReadU16(InfoAddress + 0x0A));
        Assert.Equal(
            PngDecExports.ImageFlagAdam7Interlace | PngDecExports.ImageFlagTrnsChunkExist,
            ReadU32(InfoAddress + 0x0C));
    }

    [Theory]
    [InlineData(0, 8, PngDecExports.ColorSpaceGrayscale)]
    [InlineData(3, 4, PngDecExports.ColorSpaceClut)]
    [InlineData(4, 16, PngDecExports.ColorSpaceGrayscaleAlpha)]
    [InlineData(6, 16, PngDecExports.ColorSpaceRgba)]
    public void PngDecParseHeader_MapsColourTypes(byte colorType, byte depth, ushort colorSpace)
    {
        var png = TestPng.Build(4, 4, depth, colorType, [], includeImageData: false);
        Write(FileAddress, png);
        WriteParseParam(FileAddress, (uint)png.Length);

        Assert.Equal(0, Call(PngDecExports.PngDecParseHeader, ParamAddress, InfoAddress));
        Assert.Equal(colorSpace, ReadU16(InfoAddress + 0x08));
        Assert.Equal(depth, ReadU16(InfoAddress + 0x0A));
        Assert.Equal(0u, ReadU32(InfoAddress + 0x0C));
    }

    [Fact]
    public void PngDecParseHeader_ErrorPaths()
    {
        Assert.Equal(PngDecExports.ErrorInvalidParam, Call(PngDecExports.PngDecParseHeader, 0, InfoAddress));

        var png = TestPng.Build(4, 4, 8, 6, [], includeImageData: false);
        Write(FileAddress, png);
        WriteParseParam(FileAddress, (uint)png.Length);
        Assert.Equal(PngDecExports.ErrorInvalidAddr, Call(PngDecExports.PngDecParseHeader, ParamAddress, 0));

        WriteParseParam(0, (uint)png.Length);
        Assert.Equal(PngDecExports.ErrorInvalidAddr, Call(PngDecExports.PngDecParseHeader, ParamAddress, InfoAddress));

        WriteParseParam(FileAddress, 0);
        Assert.Equal(PngDecExports.ErrorInvalidSize, Call(PngDecExports.PngDecParseHeader, ParamAddress, InfoAddress));

        // A JPEG, a truncated PNG and a PNG with an invalid depth for its colour type.
        Write(FileAddress, TestJpeg.Header(4, 4, 0xC0, (1, 0x11)));
        WriteParseParam(FileAddress, 64);
        Assert.Equal(PngDecExports.ErrorInvalidData, Call(PngDecExports.PngDecParseHeader, ParamAddress, InfoAddress));

        Write(FileAddress, png);
        WriteParseParam(FileAddress, 20);
        Assert.Equal(PngDecExports.ErrorInvalidData, Call(PngDecExports.PngDecParseHeader, ParamAddress, InfoAddress));

        var badDepth = TestPng.Build(4, 4, 4, 2, [], includeImageData: false);
        Write(FileAddress, badDepth);
        WriteParseParam(FileAddress, (uint)badDepth.Length);
        Assert.Equal(PngDecExports.ErrorInvalidData, Call(PngDecExports.PngDecParseHeader, ParamAddress, InfoAddress));
    }

    [Fact]
    public void PngDecDecode_ValidatesBeforeDecoding_AndFailsWithoutAHostCodec()
    {
        WriteCreateParam(0x0C, 0, 640);
        var handle = Create(PngDecExports.PngDecCreate, WorkAddress, PngDecExports.WorkMemorySize);
        var png = TestPng.Build(8, 4, 8, 6, TestPng.Unfiltered(new byte[8 * 4 * 4], 32));
        Write(FileAddress, png);
        var size = (uint)png.Length;

        Assert.Equal(PngDecExports.ErrorInvalidParam, Call(PngDecExports.PngDecDecode, handle, 0));

        WritePngDecodeParam(FileAddress, 0, size, 128, 0, 0, 0);
        Assert.Equal(PngDecExports.ErrorInvalidAddr, Call(PngDecExports.PngDecDecode, handle, ParamAddress));

        WritePngDecodeParam(FileAddress, ImageAddress, size, 0, 0, 0, 0);
        Assert.Equal(PngDecExports.ErrorInvalidSize, Call(PngDecExports.PngDecDecode, handle, ParamAddress));

        WritePngDecodeParam(FileAddress, ImageAddress, size, 128, 2, 0, 0);
        Assert.Equal(PngDecExports.ErrorInvalidParam, Call(PngDecExports.PngDecDecode, handle, ParamAddress));

        // Pitch below a row, and an image buffer one byte short of 8x4 RGBA.
        WritePngDecodeParam(FileAddress, ImageAddress, size, 4096, 0, 0, 31);
        Assert.Equal(PngDecExports.ErrorInvalidParam, Call(PngDecExports.PngDecDecode, handle, ParamAddress));

        WritePngDecodeParam(FileAddress, ImageAddress, size, 127, 0, 0, 0);
        Assert.Equal(PngDecExports.ErrorInvalidSize, Call(PngDecExports.PngDecDecode, handle, ParamAddress));

        WritePngDecodeParam(FileAddress, ImageAddress, size, 64 * 3 + 32 - 1, 0, 0, 64);
        Assert.Equal(PngDecExports.ErrorInvalidSize, Call(PngDecExports.PngDecDecode, handle, ParamAddress));

        Write(FileAddress, [1, 2, 3, 4]);
        WritePngDecodeParam(FileAddress, ImageAddress, 4, 128, 0, 0, 0);
        Assert.Equal(PngDecExports.ErrorInvalidData, Call(PngDecExports.PngDecDecode, handle, ParamAddress));

        Write(FileAddress, png);
        WritePngDecodeParam(FileAddress, ImageAddress, size, 128, 0, 0, 0);
        Assert.Equal(PngDecExports.ErrorFatal, Call(PngDecExports.PngDecDecode, handle, ParamAddress, InfoAddress));
    }

    // ---- libSceJpegDec ----

    [Fact]
    public void JpegDecQueryMemorySize_ValidatesTheCreateParam()
    {
        WriteCreateParam(0x0C, JpegDecExports.AttributeCscOrdinary, 1920);
        Assert.Equal(JpegDecExports.WorkMemorySize, Call(JpegDecExports.JpegDecQueryMemorySize, ParamAddress));

        WriteCreateParam(0x0C, JpegDecExports.AttributeCscFull, 1920);
        Assert.Equal(JpegDecExports.WorkMemorySize, Call(JpegDecExports.JpegDecQueryMemorySize, ParamAddress));

        Assert.Equal(JpegDecExports.ErrorInvalidParam, Call(JpegDecExports.JpegDecQueryMemorySize, 0));

        WriteCreateParam(0x08, JpegDecExports.AttributeCscOrdinary, 1920);
        Assert.Equal(JpegDecExports.ErrorInvalidSize, Call(JpegDecExports.JpegDecQueryMemorySize, ParamAddress));

        WriteCreateParam(0x0C, 0, 1920);
        Assert.Equal(JpegDecExports.ErrorInvalidParam, Call(JpegDecExports.JpegDecQueryMemorySize, ParamAddress));
    }

    [Fact]
    public void JpegDecCreateAndDelete_ManageTheHandle()
    {
        WriteCreateParam(0x0C, JpegDecExports.AttributeCscOrdinary, 1920);
        Assert.Equal(
            JpegDecExports.ErrorInvalidWorkMemory,
            Call(JpegDecExports.JpegDecCreate, ParamAddress, WorkAddress, 0x10, HandleAddress));
        Assert.Equal(
            JpegDecExports.ErrorInvalidAddr,
            Call(JpegDecExports.JpegDecCreate, ParamAddress, WorkAddress, 0x20, 0));

        var handle = Create(JpegDecExports.JpegDecCreate, WorkAddress + 1, JpegDecExports.WorkMemorySize);
        Assert.Equal(WorkAddress + 8, handle);
        Assert.Equal(JpegDecExports.AttributeCscOrdinary, ReadU32(handle + 0x08));

        Assert.Equal(0, Call(JpegDecExports.JpegDecDelete, handle));
        Assert.Equal(JpegDecExports.ErrorInvalidHandle, Call(JpegDecExports.JpegDecDelete, handle));
    }

    [Fact]
    public void JpegDecParseHeader_ReportsTheFrameHeader()
    {
        var jpeg = TestJpeg.Header(1001, 333, 0xC2, (1, 0x22), (2, 0x11), (3, 0x11));
        Write(FileAddress, jpeg);
        WriteParseParam(FileAddress, (uint)jpeg.Length, downScale: 4);

        Assert.Equal(0, Call(JpegDecExports.JpegDecParseHeader, ParamAddress, InfoAddress));
        Assert.Equal(1001u, ReadU32(InfoAddress));
        Assert.Equal(333u, ReadU32(InfoAddress + 0x04));
        Assert.Equal(JpegDecExports.ColorSpaceRgb, ReadU16(InfoAddress + 0x08));
        Assert.Equal((ushort)3, ReadU16(InfoAddress + 0x0A));
        Assert.Equal(new byte[] { 1, 2, 3, 0 }, Read(InfoAddress + 0x0C, 4));
        Assert.Equal(new byte[] { 0x22, 0x11, 0x11, 0 }, Read(InfoAddress + 0x10, 4));
        Assert.Equal(0u, ReadU32(InfoAddress + 0x14));
        Assert.Equal(JpegDecExports.AttributeCscOrdinary, ReadU32(InfoAddress + 0x18));
        Assert.Equal(251u, ReadU32(InfoAddress + 0x1C));
        Assert.Equal(84u, ReadU32(InfoAddress + 0x20));

        var gray = TestJpeg.Header(16, 8, 0xC0, (1, 0x11));
        Write(FileAddress, gray);
        WriteParseParam(FileAddress, (uint)gray.Length);
        Assert.Equal(0, Call(JpegDecExports.JpegDecParseHeader, ParamAddress, InfoAddress));
        Assert.Equal(JpegDecExports.ColorSpaceGrayscale, ReadU16(InfoAddress + 0x08));
        Assert.Equal((ushort)1, ReadU16(InfoAddress + 0x0A));
        Assert.Equal(16u, ReadU32(InfoAddress + 0x1C));
        Assert.Equal(8u, ReadU32(InfoAddress + 0x20));
    }

    [Fact]
    public void JpegDecParseHeader_ErrorPaths()
    {
        Assert.Equal(JpegDecExports.ErrorInvalidParam, Call(JpegDecExports.JpegDecParseHeader, 0, InfoAddress));

        var jpeg = TestJpeg.Header(16, 16, 0xC0, (1, 0x11), (2, 0x11), (3, 0x11));
        Write(FileAddress, jpeg);
        WriteParseParam(FileAddress, (uint)jpeg.Length);
        Assert.Equal(JpegDecExports.ErrorInvalidAddr, Call(JpegDecExports.JpegDecParseHeader, ParamAddress, 0));

        WriteParseParam(FileAddress, 0);
        Assert.Equal(JpegDecExports.ErrorInvalidSize, Call(JpegDecExports.JpegDecParseHeader, ParamAddress, InfoAddress));

        WriteParseParam(FileAddress, (uint)jpeg.Length, downScale: 3);
        Assert.Equal(JpegDecExports.ErrorInvalidParam, Call(JpegDecExports.JpegDecParseHeader, ParamAddress, InfoAddress));

        WriteParseParam(FileAddress, (uint)jpeg.Length, decodeMode: 1);
        Assert.Equal(JpegDecExports.ErrorInvalidParam, Call(JpegDecExports.JpegDecParseHeader, ParamAddress, InfoAddress));

        var png = TestPng.Build(4, 4, 8, 6, [], includeImageData: false);
        Write(FileAddress, png);
        WriteParseParam(FileAddress, (uint)png.Length);
        Assert.Equal(JpegDecExports.ErrorInvalidData, Call(JpegDecExports.JpegDecParseHeader, ParamAddress, InfoAddress));
    }

    [Fact]
    public void JpegDecDecode_ValidatesBeforeDecoding_AndFailsWithoutAHostCodec()
    {
        WriteCreateParam(0x0C, JpegDecExports.AttributeCscOrdinary, 1920);
        var handle = Create(JpegDecExports.JpegDecCreate, WorkAddress, JpegDecExports.WorkMemorySize);
        var jpeg = TestJpeg.Header(16, 16, 0xC0, (1, 0x11), (2, 0x11), (3, 0x11));
        Write(FileAddress, jpeg);
        var size = (uint)jpeg.Length;

        Assert.Equal(JpegDecExports.ErrorInvalidHandle, Call(JpegDecExports.JpegDecDecode, handle + 8, ParamAddress));
        Assert.Equal(JpegDecExports.ErrorInvalidParam, Call(JpegDecExports.JpegDecDecode, handle, 0));

        WriteJpegDecodeParam(0, ImageAddress, size, 1024, 0, 0xFF, 0);
        Assert.Equal(JpegDecExports.ErrorInvalidAddr, Call(JpegDecExports.JpegDecDecode, handle, ParamAddress));

        WriteJpegDecodeParam(FileAddress, ImageAddress, 0, 1024, 0, 0xFF, 0);
        Assert.Equal(JpegDecExports.ErrorInvalidSize, Call(JpegDecExports.JpegDecDecode, handle, ParamAddress));

        WriteJpegDecodeParam(FileAddress, ImageAddress, size, 1024, 5, 0xFF, 0);
        Assert.Equal(JpegDecExports.ErrorInvalidParam, Call(JpegDecExports.JpegDecDecode, handle, ParamAddress));

        WriteJpegDecodeParam(FileAddress, ImageAddress, size, 1024, 0, 0xFF, 0, downScale: 0);
        Assert.Equal(JpegDecExports.ErrorInvalidParam, Call(JpegDecExports.JpegDecDecode, handle, ParamAddress));

        WriteJpegDecodeParam(FileAddress, ImageAddress, size, 16 * 16 * 4 - 1, 0, 0xFF, 0);
        Assert.Equal(JpegDecExports.ErrorInvalidSize, Call(JpegDecExports.JpegDecDecode, handle, ParamAddress));

        // Down scale 2 halves the buffer the decode needs.
        WriteJpegDecodeParam(FileAddress, ImageAddress, size, 8 * 8 * 4, 0, 0xFF, 0, downScale: 2);
        Assert.Equal(JpegDecExports.ErrorFatal, Call(JpegDecExports.JpegDecDecode, handle, ParamAddress));

        WriteJpegDecodeParam(FileAddress, ImageAddress, size, 16 * 16 * 4, 0, 0xFF, 63);
        Assert.Equal(JpegDecExports.ErrorInvalidParam, Call(JpegDecExports.JpegDecDecode, handle, ParamAddress));

        WriteJpegDecodeParam(FileAddress, ImageAddress, size, 16 * 16 * 4, 0, 0xFF, 0);
        Assert.Equal(JpegDecExports.ErrorFatal, Call(JpegDecExports.JpegDecDecode, handle, ParamAddress));
    }

    // ---- libScePngEnc ----

    [Fact]
    public void PngEncQueryMemorySize_ValidatesTheCreateParam()
    {
        WriteCreateParam(0x10, 0, 1920, 5);
        Assert.Equal(PngEncExports.WorkMemorySize, Call(PngEncExports.PngEncQueryMemorySize, ParamAddress));

        Assert.Equal(PngEncExports.ErrorInvalidAddr, Call(PngEncExports.PngEncQueryMemorySize, 0));

        WriteCreateParam(0x0C, 0, 1920, 0);
        Assert.Equal(PngEncExports.ErrorInvalidParam, Call(PngEncExports.PngEncQueryMemorySize, ParamAddress));

        WriteCreateParam(0x10, 1, 1920, 0);
        Assert.Equal(PngEncExports.ErrorInvalidParam, Call(PngEncExports.PngEncQueryMemorySize, ParamAddress));

        WriteCreateParam(0x10, 0, 1920, 6);
        Assert.Equal(PngEncExports.ErrorInvalidParam, Call(PngEncExports.PngEncQueryMemorySize, ParamAddress));

        WriteCreateParam(0x10, 0, 0, 0);
        Assert.Equal(PngEncExports.ErrorInvalidSize, Call(PngEncExports.PngEncQueryMemorySize, ParamAddress));

        WriteCreateParam(0x10, 0, PngEncExports.MaxImageWidth + 1, 0);
        Assert.Equal(PngEncExports.ErrorInvalidSize, Call(PngEncExports.PngEncQueryMemorySize, ParamAddress));
    }

    [Fact]
    public void PngEncCreateAndDelete_ManageTheHandle()
    {
        WriteCreateParam(0x10, 0, 64, 0);
        Assert.Equal(
            PngEncExports.ErrorInvalidSize,
            Call(PngEncExports.PngEncCreate, ParamAddress, WorkAddress, 0x0F, HandleAddress));
        Assert.Equal(
            PngEncExports.ErrorInvalidAddr,
            Call(PngEncExports.PngEncCreate, ParamAddress, 0, 0x10, HandleAddress));

        var handle = Create(PngEncExports.PngEncCreate, WorkAddress, PngEncExports.WorkMemorySize);
        Assert.Equal(WorkAddress, handle);
        Assert.Equal(64u, ReadU32(handle + 0x08));

        Assert.Equal(0, Call(PngEncExports.PngEncDelete, handle));
        Assert.Equal(PngEncExports.ErrorInvalidHandle, Call(PngEncExports.PngEncDelete, handle));
        Assert.Equal(PngEncExports.ErrorInvalidHandle, Call(PngEncExports.PngEncEncode, handle, ParamAddress));
    }

    [Fact]
    public void PngEncEncode_ValidatesBeforeEncoding_AndFailsWithoutAHostCodec()
    {
        WriteCreateParam(0x10, 0, 64, 0);
        var handle = Create(PngEncExports.PngEncCreate, WorkAddress, PngEncExports.WorkMemorySize);

        Assert.Equal(PngEncExports.ErrorInvalidParam, Call(PngEncExports.PngEncEncode, handle, 0));

        WritePngEncodeParam(ImageAddress, 0, 1024, 1024, 8, 8, 32, 0, PngEncExports.ColorSpaceRgba);
        Assert.Equal(PngEncExports.ErrorInvalidAddr, Call(PngEncExports.PngEncEncode, handle, ParamAddress));

        WritePngEncodeParam(ImageAddress, FileAddress, 1024, 1024, 8, 8, 32, 2, PngEncExports.ColorSpaceRgba);
        Assert.Equal(PngEncExports.ErrorInvalidParam, Call(PngEncExports.PngEncEncode, handle, ParamAddress));

        WritePngEncodeParam(ImageAddress, FileAddress, 1024, 1024, 8, 8, 32, 0, 2);
        Assert.Equal(PngEncExports.ErrorInvalidParam, Call(PngEncExports.PngEncEncode, handle, ParamAddress));

        WritePngEncodeParam(ImageAddress, FileAddress, 1024, 1024, 8, 8, 32, 0, PngEncExports.ColorSpaceRgba, bitDepth: 16);
        Assert.Equal(PngEncExports.ErrorInvalidParam, Call(PngEncExports.PngEncEncode, handle, ParamAddress));

        WritePngEncodeParam(ImageAddress, FileAddress, 1024, 1024, 8, 8, 32, 0, PngEncExports.ColorSpaceRgba, filter: 0x10);
        Assert.Equal(PngEncExports.ErrorInvalidParam, Call(PngEncExports.PngEncEncode, handle, ParamAddress));

        WritePngEncodeParam(ImageAddress, FileAddress, 1024, 1024, 8, 8, 32, 0, PngEncExports.ColorSpaceRgba, compression: 10);
        Assert.Equal(PngEncExports.ErrorInvalidParam, Call(PngEncExports.PngEncEncode, handle, ParamAddress));

        // Wider than the encoder's maxImageWidth, pitch below a row, and an image buffer too small.
        WritePngEncodeParam(ImageAddress, FileAddress, 4096, 1024, 65, 1, 260, 0, PngEncExports.ColorSpaceRgba);
        Assert.Equal(PngEncExports.ErrorInvalidSize, Call(PngEncExports.PngEncEncode, handle, ParamAddress));

        WritePngEncodeParam(ImageAddress, FileAddress, 1024, 1024, 8, 8, 31, 0, PngEncExports.ColorSpaceRgba);
        Assert.Equal(PngEncExports.ErrorInvalidSize, Call(PngEncExports.PngEncEncode, handle, ParamAddress));

        WritePngEncodeParam(ImageAddress, FileAddress, 255, 1024, 8, 8, 32, 0, PngEncExports.ColorSpaceRgba);
        Assert.Equal(PngEncExports.ErrorInvalidSize, Call(PngEncExports.PngEncEncode, handle, ParamAddress));

        WritePngEncodeParam(ImageAddress, FileAddress, 256, 1024, 8, 8, 32, 0, PngEncExports.ColorSpaceRgba);
        Assert.Equal(PngEncExports.ErrorFatal, Call(PngEncExports.PngEncEncode, handle, ParamAddress));
    }

    // ---- libSceJpegEnc ----

    [Fact]
    public void JpegEncQueryMemorySize_ValidatesTheCreateParam()
    {
        WriteCreateParam(0x08, 0);
        Assert.Equal(JpegEncExports.WorkMemorySize, Call(JpegEncExports.JpegEncQueryMemorySize, ParamAddress));

        Assert.Equal(JpegEncExports.ErrorInvalidAddr, Call(JpegEncExports.JpegEncQueryMemorySize, 0));

        WriteCreateParam(0x0C, 0);
        Assert.Equal(JpegEncExports.ErrorInvalidSize, Call(JpegEncExports.JpegEncQueryMemorySize, ParamAddress));

        WriteCreateParam(0x08, 1);
        Assert.Equal(JpegEncExports.ErrorInvalidParam, Call(JpegEncExports.JpegEncQueryMemorySize, ParamAddress));
    }

    [Fact]
    public void JpegEncCreateAndDelete_ManageA32ByteAlignedHandle()
    {
        WriteCreateParam(0x08, 0);
        Assert.Equal(
            JpegEncExports.ErrorInvalidSize,
            Call(JpegEncExports.JpegEncCreate, ParamAddress, WorkAddress, 0x7FF, HandleAddress));
        Assert.Equal(
            JpegEncExports.ErrorInvalidAddr,
            Call(JpegEncExports.JpegEncCreate, ParamAddress, WorkAddress, 0x800, 0));

        var handle = Create(JpegEncExports.JpegEncCreate, WorkAddress + 4, JpegEncExports.WorkMemorySize);
        Assert.Equal(WorkAddress + 0x20, handle);

        Assert.Equal(JpegEncExports.ErrorInvalidHandle, Call(JpegEncExports.JpegEncDelete, handle + 8));
        Assert.Equal(0, Call(JpegEncExports.JpegEncDelete, handle));
        Assert.Equal(JpegEncExports.ErrorInvalidHandle, Call(JpegEncExports.JpegEncDelete, handle));
    }

    [Fact]
    public void JpegEncEncode_ValidatesBeforeEncoding_AndFailsWithoutAHostCodec()
    {
        WriteCreateParam(0x08, 0);
        var handle = Create(JpegEncExports.JpegEncCreate, WorkAddress, JpegEncExports.WorkMemorySize);
        const ushort Ycc = JpegEncExports.ColorSpaceYcc;
        const byte S420 = JpegEncExports.SamplingType420;

        Assert.Equal(JpegEncExports.ErrorInvalidAddr, Call(JpegEncExports.JpegEncEncode, handle, 0));

        // An RGBA source must be 4-byte aligned; a Y8 source need not be.
        WriteJpegEncodeParam(ImageAddress + 1, FileAddress, 1024, 1024, 8, 8, 32, 0, 0, Ycc, S420, 0);
        Assert.Equal(JpegEncExports.ErrorInvalidAddr, Call(JpegEncExports.JpegEncEncode, handle, ParamAddress));

        WriteJpegEncodeParam(ImageAddress, FileAddress, 1024, 0, 8, 8, 32, 0, 0, Ycc, S420, 0);
        Assert.Equal(JpegEncExports.ErrorInvalidSize, Call(JpegEncExports.JpegEncEncode, handle, ParamAddress));

        WriteJpegEncodeParam(ImageAddress, FileAddress, 1024, 1024, 0, 8, 32, 0, 0, Ycc, S420, 0);
        Assert.Equal(JpegEncExports.ErrorInvalidParam, Call(JpegEncExports.JpegEncEncode, handle, ParamAddress));

        WriteJpegEncodeParam(ImageAddress, FileAddress, 1024, 1024, 8, 8, 30, 0, 0, Ycc, S420, 0);
        Assert.Equal(JpegEncExports.ErrorInvalidParam, Call(JpegEncExports.JpegEncEncode, handle, ParamAddress));

        WriteJpegEncodeParam(ImageAddress, FileAddress, 255, 1024, 8, 8, 32, 0, 0, Ycc, S420, 0);
        Assert.Equal(JpegEncExports.ErrorInvalidParam, Call(JpegEncExports.JpegEncEncode, handle, ParamAddress));

        WriteJpegEncodeParam(ImageAddress, FileAddress, 1024, 1024, 8, 8, 32, 0, 2, Ycc, S420, 0);
        Assert.Equal(JpegEncExports.ErrorInvalidParam, Call(JpegEncExports.JpegEncEncode, handle, ParamAddress));

        // RGBA needs YCC with subsampled chroma; Y8 needs grayscale at full sampling.
        WriteJpegEncodeParam(ImageAddress, FileAddress, 1024, 1024, 8, 8, 32, 0, 0, Ycc, JpegEncExports.SamplingTypeFull, 0);
        Assert.Equal(JpegEncExports.ErrorInvalidParam, Call(JpegEncExports.JpegEncEncode, handle, ParamAddress));

        WriteJpegEncodeParam(ImageAddress, FileAddress, 1024, 1024, 8, 8, 32, 0, 0, JpegEncExports.ColorSpaceGrayscale, S420, 0);
        Assert.Equal(JpegEncExports.ErrorInvalidParam, Call(JpegEncExports.JpegEncEncode, handle, ParamAddress));

        WriteJpegEncodeParam(ImageAddress, FileAddress, 1024, 1024, 8, 8, 8, JpegEncExports.PixelFormatY8, 0, Ycc, JpegEncExports.SamplingTypeFull, 0);
        Assert.Equal(JpegEncExports.ErrorInvalidParam, Call(JpegEncExports.JpegEncEncode, handle, ParamAddress));

        WriteJpegEncodeParam(ImageAddress, FileAddress, 1024, 1024, 8, 8, 32, 0, 0, Ycc, S420, 0, restart: -2);
        Assert.Equal(JpegEncExports.ErrorInvalidParam, Call(JpegEncExports.JpegEncEncode, handle, ParamAddress));

        WriteJpegEncodeParam(ImageAddress, FileAddress, 1024, 1024, 8, 8, 32, 7, 0, Ycc, S420, 0);
        Assert.Equal(JpegEncExports.ErrorInvalidParam, Call(JpegEncExports.JpegEncEncode, handle, ParamAddress));

        WriteJpegEncodeParam(ImageAddress + 1, FileAddress, 64, 1024, 8, 8, 8, JpegEncExports.PixelFormatY8, 0, JpegEncExports.ColorSpaceGrayscale, JpegEncExports.SamplingTypeFull, 0);
        Assert.Equal(JpegEncExports.ErrorFatal, Call(JpegEncExports.JpegEncEncode, handle, ParamAddress));

        WriteJpegEncodeParam(ImageAddress, FileAddress, 1024, 1024, 8, 8, 32, 0, 0, Ycc, S420, 0);
        Assert.Equal(JpegEncExports.ErrorFatal, Call(JpegEncExports.JpegEncEncode, handle, ParamAddress));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(255, 1)]
    [InlineData(128, 51)]
    public void JpegEncQuality_MapsCompressionRatio(byte ratio, int quality)
    {
        Assert.Equal(quality, JpegEncExports.Quality(ratio));
    }

    [Fact]
    public void JpegDecDownScale_AveragesBlocksAndKeepsPartialEdges()
    {
        // 3x1 RGBA: the 2x down scale averages the first two pixels and keeps the third.
        byte[] pixels = [0, 0, 0, 255, 100, 50, 10, 255, 7, 8, 9, 255];
        var scaled = JpegDecExports.DownScale(pixels, 3, 1, 2);
        Assert.Equal(new byte[] { 50, 25, 5, 255, 7, 8, 9, 255 }, scaled);
    }
}
