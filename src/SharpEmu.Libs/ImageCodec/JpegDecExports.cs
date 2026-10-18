// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.ImageCodec;

/// <summary>
/// libSceJpegDec. The frame header is parsed in managed code (that is all the SDK's parse
/// call reads); the pixel decode goes to the registered <see cref="IHostImageCodec"/>.
/// </summary>
/// <remarks>
/// SDK structures (little endian):
/// <code>
/// SceJpegDecCreateParam (0x0C)  0x00 u32 thisSize, 0x04 u32 attribute, 0x08 u32 maxImageWidth
/// SceJpegDecParseParam  (0x10)  0x00 ptr jpegMemAddr, 0x08 u32 jpegMemSize, 0x0C u16 decodeMode,
///                               0x0E u16 downScale
/// SceJpegDecDecodeParam (0x30)  0x00 ptr jpegMemAddr, 0x08 ptr imageMemAddr, 0x10 ptr coefficientMemAddr,
///                               0x18 u32 jpegMemSize, 0x1C u32 imageMemSize, 0x20 u32 coefficientMemSize,
///                               0x24 u16 decodeMode, 0x26 u16 downScale, 0x28 u16 pixelFormat,
///                               0x2A u16 alphaValue, 0x2C u32 imagePitch
/// SceJpegDecImageInfo   (0x24)  0x00 u32 imageWidth, 0x04 u32 imageHeight, 0x08 u16 colorSpace,
///                               0x0A u16 componentNumber, 0x0C u8[4] componentId,
///                               0x10 u8[4] samplingFactor, 0x14 u32 coefficientMemSize,
///                               0x18 u32 suitableCscAttribute, 0x1C u32 outputImageWidth,
///                               0x20 u32 outputImageHeight
/// </code>
/// The decoder context occupies the work memory: 0x00 self pointer, 0x08 u32 attribute,
/// at the work address aligned up to 8.
///
/// Error codes: libSceJpegEnc's are 0x806501xx, mirroring libScePngEnc's 0x806901xx, and
/// SharpProspero's JpegDec path raises 0x80650020 where its PngDec path raises libScePngDec's
/// 0x80690020 (FATAL); the decoder codes below are libScePngDec's numbering in the 0x8065 facility.
/// </remarks>
public static class JpegDecExports
{
    private const string Library = "libSceJpegDec";

    public const int ErrorInvalidAddr = unchecked((int)0x80650001);
    public const int ErrorInvalidSize = unchecked((int)0x80650002);
    public const int ErrorInvalidParam = unchecked((int)0x80650003);
    public const int ErrorInvalidHandle = unchecked((int)0x80650004);
    public const int ErrorInvalidWorkMemory = unchecked((int)0x80650005);
    public const int ErrorInvalidData = unchecked((int)0x80650010);
    public const int ErrorUnsupportData = unchecked((int)0x80650011);
    public const int ErrorDecodeError = unchecked((int)0x80650012);
    public const int ErrorFatal = unchecked((int)0x80650020);

    public const uint AttributeCscOrdinary = 1;
    public const uint AttributeCscFull = 2;
    public const int WorkMemorySize = 0x20;
    private const ulong HandleAlignment = 8;

    public const ushort DecodeModeNormal = 0;

    public const ushort PixelFormatR8G8B8A8 = 0;
    public const ushort PixelFormatB8G8R8A8 = 1;

    // Colour space reported in the image info. The console's enumeration for JPEG sources is
    // undocumented; grayscale and colour use libScePngDec's / libSceJpegEnc's numbering.
    public const ushort ColorSpaceGrayscale = 2;
    public const ushort ColorSpaceRgb = 3;

    private const uint CreateParamSize = 0x0C;
    private const int BytesPerPixel = 4;

    [SysAbiExport(
        Nid = "uNAUmANZMEw",
        ExportName = "sceJpegDecQueryMemorySize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int JpegDecQueryMemorySize(CpuContext ctx)
    {
        var result = ValidateCreateParam(ctx, ctx[CpuRegister.Rdi], out _);
        return ctx.SetReturn(result != 0 ? result : WorkMemorySize);
    }

    [SysAbiExport(
        Nid = "JPh3Zgg0Zwc",
        ExportName = "sceJpegDecCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int JpegDecCreate(CpuContext ctx)
    {
        var memoryAddress = ctx[CpuRegister.Rsi];
        var memorySize = (uint)ctx[CpuRegister.Rdx];
        var handleAddress = ctx[CpuRegister.Rcx];

        var result = ValidateCreateParam(ctx, ctx[CpuRegister.Rdi], out var attribute);
        if (result != 0)
        {
            return ctx.SetReturn(result);
        }

        if (memoryAddress == 0 || handleAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (memorySize < WorkMemorySize)
        {
            return ctx.SetReturn(ErrorInvalidWorkMemory);
        }

        var handle = ImageLibraryGuest.AlignUp(memoryAddress, HandleAlignment);
        if (!ImageLibraryGuest.TryMarkLive(ctx, handle) ||
            !ctx.TryWriteUInt32(handle + 0x08, attribute) ||
            !ctx.TryWriteUInt64(handleAddress, handle))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "Hwh11+m5KoI",
        ExportName = "sceJpegDecDelete",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int JpegDecDelete(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (!ImageLibraryGuest.IsLive(ctx, handle, HandleAlignment))
        {
            return ctx.SetReturn(ErrorInvalidHandle);
        }

        ctx.TryWriteUInt64(handle, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "LSinoSQH790",
        ExportName = "sceJpegDecParseHeader",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int JpegDecParseHeader(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var infoAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidParam);
        }

        if (!ctx.TryReadUInt64(paramAddress, out var jpegAddress) ||
            !ctx.TryReadUInt32(paramAddress + 0x08, out var jpegSize) ||
            !ctx.TryReadUInt16(paramAddress + 0x0C, out var decodeMode) ||
            !ctx.TryReadUInt16(paramAddress + 0x0E, out var downScale) ||
            jpegAddress == 0 ||
            infoAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (jpegSize == 0)
        {
            return ctx.SetReturn(ErrorInvalidSize);
        }

        if (!IsSupportedMode(decodeMode, downScale))
        {
            return ctx.SetReturn(ErrorInvalidParam);
        }

        if (!ImageLibraryGuest.TryReadBytes(ctx, jpegAddress, jpegSize, out var jpeg))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (!JpegImageHeader.TryParse(jpeg, out var header))
        {
            return ctx.SetReturn(ErrorInvalidData);
        }

        return ctx.SetReturn(WriteImageInfo(ctx, infoAddress, header, downScale) ? 0 : ErrorInvalidAddr);
    }

    [SysAbiExport(
        Nid = "1kzQRoWEgSA",
        ExportName = "sceJpegDecDecode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int JpegDecDecode(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        var infoAddress = ctx[CpuRegister.Rdx];

        if (!ImageLibraryGuest.IsLive(ctx, handle, HandleAlignment))
        {
            return ctx.SetReturn(ErrorInvalidHandle);
        }

        if (paramAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidParam);
        }

        // coefficientMemAddr/Size (0x10/0x20) are not read: the host decoder needs no
        // coefficient buffer, which is also why the image info reports a size of 0.
        if (!ctx.TryReadUInt64(paramAddress, out var jpegAddress) ||
            !ctx.TryReadUInt64(paramAddress + 0x08, out var imageAddress) ||
            !ctx.TryReadUInt32(paramAddress + 0x18, out var jpegSize) ||
            !ctx.TryReadUInt32(paramAddress + 0x1C, out var imageSize) ||
            !ctx.TryReadUInt16(paramAddress + 0x24, out var decodeMode) ||
            !ctx.TryReadUInt16(paramAddress + 0x26, out var downScale) ||
            !ctx.TryReadUInt16(paramAddress + 0x28, out var pixelFormat) ||
            !ctx.TryReadUInt16(paramAddress + 0x2A, out var alphaValue) ||
            !ctx.TryReadUInt32(paramAddress + 0x2C, out var imagePitch))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (jpegAddress == 0 || imageAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (jpegSize == 0 || imageSize == 0)
        {
            return ctx.SetReturn(ErrorInvalidSize);
        }

        if (pixelFormat is not (PixelFormatR8G8B8A8 or PixelFormatB8G8R8A8) ||
            !IsSupportedMode(decodeMode, downScale))
        {
            return ctx.SetReturn(ErrorInvalidParam);
        }

        if (!ImageLibraryGuest.TryReadBytes(ctx, jpegAddress, jpegSize, out var jpeg))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (!JpegImageHeader.TryParse(jpeg, out var header))
        {
            return ctx.SetReturn(ErrorInvalidData);
        }

        var outputWidth = ScaledDimension(header.Width, downScale);
        var outputHeight = ScaledDimension(header.Height, downScale);
        var rowBytes = (ulong)outputWidth * BytesPerPixel;
        var pitch = imagePitch == 0 ? rowBytes : imagePitch;
        if (pitch < rowBytes)
        {
            return ctx.SetReturn(ErrorInvalidParam);
        }

        if ((outputHeight - 1) * pitch + rowBytes > imageSize)
        {
            return ctx.SetReturn(ErrorInvalidSize);
        }

        // No host codec composed in: the decode genuinely cannot run here.
        if (HostImageCodec.Current is not { } codec)
        {
            return ctx.SetReturn(ErrorFatal);
        }

        if (!codec.TryDecode(jpeg, HostImageContainer.Jpeg, 8, out var image) ||
            image.Width != header.Width ||
            image.Height != header.Height ||
            image.BitsPerChannel != 8 ||
            image.Pixels.LongLength != (long)header.Width * header.Height * BytesPerPixel)
        {
            return ctx.SetReturn(ErrorDecodeError);
        }

        var pixels = downScale == 1
            ? image.Pixels
            : DownScale(image.Pixels, (int)header.Width, (int)header.Height, downScale);
        PngDecExports.ConvertPixels(pixels, 8, pixelFormat == PixelFormatB8G8R8A8, alphaValue);
        if (!ImageLibraryGuest.TryWriteRows(ctx, imageAddress, pitch, pixels, (int)rowBytes, (int)outputHeight))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (infoAddress != 0 && !WriteImageInfo(ctx, infoAddress, header, downScale))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        // Packed like libScePngDec's result, from the dimensions actually written.
        return ctx.SetReturn(ImageLibraryGuest.PackDimensions(outputWidth, outputHeight));
    }

    /// <summary>
    /// Down scales 1, 2, 4 and 8 are the documented values. Only the normal decode mode is
    /// accepted: the other modes' values and meanings are undocumented, so they are refused
    /// rather than decoded as if normal.
    /// </summary>
    private static bool IsSupportedMode(ushort decodeMode, ushort downScale) =>
        decodeMode == DecodeModeNormal && downScale is 1 or 2 or 4 or 8;

    private static uint ScaledDimension(uint dimension, ushort downScale) =>
        (dimension + downScale - 1u) / downScale;

    /// <summary>Box-filters 8-bit RGBA by <paramref name="scale"/>, rounding partial edge blocks in.</summary>
    internal static byte[] DownScale(byte[] pixels, int width, int height, int scale)
    {
        var outWidth = (width + scale - 1) / scale;
        var outHeight = (height + scale - 1) / scale;
        var output = new byte[outWidth * outHeight * BytesPerPixel];
        for (var oy = 0; oy < outHeight; oy++)
        {
            var y0 = oy * scale;
            var y1 = Math.Min(y0 + scale, height);
            for (var ox = 0; ox < outWidth; ox++)
            {
                var x0 = ox * scale;
                var x1 = Math.Min(x0 + scale, width);
                int r = 0, g = 0, b = 0, a = 0;
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var source = (y * width + x) * BytesPerPixel;
                        r += pixels[source];
                        g += pixels[source + 1];
                        b += pixels[source + 2];
                        a += pixels[source + 3];
                    }
                }

                var count = (y1 - y0) * (x1 - x0);
                var target = (oy * outWidth + ox) * BytesPerPixel;
                output[target] = (byte)((r + count / 2) / count);
                output[target + 1] = (byte)((g + count / 2) / count);
                output[target + 2] = (byte)((b + count / 2) / count);
                output[target + 3] = (byte)((a + count / 2) / count);
            }
        }

        return output;
    }

    private static int ValidateCreateParam(CpuContext ctx, ulong paramAddress, out uint attribute)
    {
        attribute = 0;
        if (paramAddress == 0)
        {
            return ErrorInvalidParam;
        }

        if (!ctx.TryReadUInt32(paramAddress, out var thisSize) ||
            !ctx.TryReadUInt32(paramAddress + 0x04, out attribute))
        {
            return ErrorInvalidAddr;
        }

        if (thisSize != CreateParamSize)
        {
            return ErrorInvalidSize;
        }

        // maxImageWidth is not range-checked: no reference documents its limit for JPEG.
        return attribute is AttributeCscOrdinary or AttributeCscFull ? 0 : ErrorInvalidParam;
    }

    private static bool WriteImageInfo(CpuContext ctx, ulong address, in JpegImageHeader header, ushort downScale)
    {
        var info = new byte[0x24];
        var span = info.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, header.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x04..], header.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(
            span[0x08..],
            header.ComponentCount == 1 ? ColorSpaceGrayscale : ColorSpaceRgb);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x0A..], (ushort)header.ComponentCount);
        for (var index = 0; index < Math.Min(header.ComponentCount, 4); index++)
        {
            span[0x0C + index] = header.ComponentId(index);
            span[0x10 + index] = header.SamplingFactor(index);
        }

        // 0x14 coefficientMemSize stays 0: the host decoder needs no coefficient buffer.
        // Which images the console reports the full-range CSC attribute (2) for is
        // undocumented; every image reports the ordinary attribute (1).
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x18..], AttributeCscOrdinary);
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x1C..], ScaledDimension(header.Width, downScale));
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x20..], ScaledDimension(header.Height, downScale));
        return ctx.Memory.TryWrite(address, info);
    }
}
