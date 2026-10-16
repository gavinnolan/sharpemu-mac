// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.ImageCodec;

/// <summary>
/// libScePngDec. Header parsing is managed (it is what the SDK's parse call does); the pixel
/// decode goes to the registered <see cref="IHostImageCodec"/>.
/// </summary>
/// <remarks>
/// SDK structures (little endian):
/// <code>
/// ScePngDecCreateParam (0x0C)  0x00 u32 thisSize, 0x04 u32 attribute, 0x08 u32 maxImageWidth
/// ScePngDecParseParam  (0x10)  0x00 ptr pngMemAddr, 0x08 u32 pngMemSize, 0x0C u32 reserved
/// ScePngDecDecodeParam (0x20)  0x00 ptr pngMemAddr, 0x08 ptr imageMemAddr, 0x10 u32 pngMemSize,
///                              0x14 u32 imageMemSize, 0x18 u16 pixelFormat, 0x1A u16 alphaValue,
///                              0x1C u32 imagePitch
/// ScePngDecImageInfo   (0x10)  0x00 u32 imageWidth, 0x04 u32 imageHeight, 0x08 u16 colorSpace,
///                              0x0A u16 bitDepth, 0x0C u32 imageFlag
/// </code>
/// The decoder context occupies the work memory: 0x00 self pointer, 0x08 u32 attribute,
/// 0x0C u32 maxImageWidth, at the work address aligned up to 8.
/// </remarks>
public static class PngDecExports
{
    private const string Library = "libScePngDec";

    public const int ErrorInvalidAddr = unchecked((int)0x80690001);
    public const int ErrorInvalidSize = unchecked((int)0x80690002);
    public const int ErrorInvalidParam = unchecked((int)0x80690003);
    public const int ErrorInvalidHandle = unchecked((int)0x80690004);
    public const int ErrorInvalidWorkMemory = unchecked((int)0x80690005);
    public const int ErrorInvalidData = unchecked((int)0x80690010);
    public const int ErrorUnsupportData = unchecked((int)0x80690011);
    public const int ErrorDecodeError = unchecked((int)0x80690012);
    public const int ErrorFatal = unchecked((int)0x80690020);

    public const uint AttributeNone = 0;
    public const uint AttributeBitDepth16 = 1;
    public const uint MaxImageWidth = 1_000_000;
    public const int WorkMemorySize = 0x20;
    private const ulong HandleAlignment = 8;

    public const ushort PixelFormatR8G8B8A8 = 0;
    public const ushort PixelFormatB8G8R8A8 = 1;

    public const ushort ColorSpaceGrayscale = 2;
    public const ushort ColorSpaceRgb = 3;
    public const ushort ColorSpaceClut = 4;
    public const ushort ColorSpaceGrayscaleAlpha = 18;
    public const ushort ColorSpaceRgba = 19;

    public const uint ImageFlagAdam7Interlace = 1;
    public const uint ImageFlagTrnsChunkExist = 2;

    private const uint CreateParamSize = 0x0C;

    [SysAbiExport(
        Nid = "-6srIGbLTIU",
        ExportName = "scePngDecQueryMemorySize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int PngDecQueryMemorySize(CpuContext ctx)
    {
        var result = ValidateCreateParam(ctx, ctx[CpuRegister.Rdi], out _, out _);
        return ctx.SetReturn(result != 0 ? result : WorkMemorySize);
    }

    [SysAbiExport(
        Nid = "m0uW+8pFyaw",
        ExportName = "scePngDecCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int PngDecCreate(CpuContext ctx)
    {
        var memoryAddress = ctx[CpuRegister.Rsi];
        var memorySize = (uint)ctx[CpuRegister.Rdx];
        var handleAddress = ctx[CpuRegister.Rcx];

        var result = ValidateCreateParam(ctx, ctx[CpuRegister.Rdi], out var attribute, out var maxWidth);
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
            !ctx.TryWriteUInt32(handle + 0x0C, maxWidth) ||
            !ctx.TryWriteUInt64(handleAddress, handle))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "QbD+eENEwo8",
        ExportName = "scePngDecDelete",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int PngDecDelete(CpuContext ctx)
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
        Nid = "U6h4e5JRPaQ",
        ExportName = "scePngDecParseHeader",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int PngDecParseHeader(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        var infoAddress = ctx[CpuRegister.Rsi];
        if (paramAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidParam);
        }

        if (!ctx.TryReadUInt64(paramAddress, out var pngAddress) ||
            !ctx.TryReadUInt32(paramAddress + 0x08, out var pngSize) ||
            pngAddress == 0 ||
            infoAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (pngSize == 0)
        {
            return ctx.SetReturn(ErrorInvalidSize);
        }

        if (!ImageLibraryGuest.TryReadBytes(ctx, pngAddress, pngSize, out var png))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (!PngImageHeader.TryParse(png, out var header))
        {
            return ctx.SetReturn(ErrorInvalidData);
        }

        return ctx.SetReturn(WriteImageInfo(ctx, infoAddress, header) ? 0 : ErrorInvalidAddr);
    }

    [SysAbiExport(
        Nid = "WC216DD3El4",
        ExportName = "scePngDecDecode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int PngDecDecode(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        var infoAddress = ctx[CpuRegister.Rdx];

        if (!ImageLibraryGuest.IsLive(ctx, handle, HandleAlignment) ||
            !ctx.TryReadUInt32(handle + 0x08, out var attribute))
        {
            return ctx.SetReturn(ErrorInvalidHandle);
        }

        if (paramAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidParam);
        }

        if (!ctx.TryReadUInt64(paramAddress, out var pngAddress) ||
            !ctx.TryReadUInt64(paramAddress + 0x08, out var imageAddress) ||
            !ctx.TryReadUInt32(paramAddress + 0x10, out var pngSize) ||
            !ctx.TryReadUInt32(paramAddress + 0x14, out var imageSize) ||
            !ctx.TryReadUInt16(paramAddress + 0x18, out var pixelFormat) ||
            !ctx.TryReadUInt16(paramAddress + 0x1A, out var alphaValue) ||
            !ctx.TryReadUInt32(paramAddress + 0x1C, out var imagePitch))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (pngAddress == 0 || imageAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (pngSize == 0 || imageSize == 0)
        {
            return ctx.SetReturn(ErrorInvalidSize);
        }

        if (pixelFormat is not (PixelFormatR8G8B8A8 or PixelFormatB8G8R8A8))
        {
            return ctx.SetReturn(ErrorInvalidParam);
        }

        if (!ImageLibraryGuest.TryReadBytes(ctx, pngAddress, pngSize, out var png))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (!PngImageHeader.TryParse(png, out var header))
        {
            return ctx.SetReturn(ErrorInvalidData);
        }

        // ATTRIBUTE_BIT_DEPTH_16 keeps 16-bit sources at 16 bits per channel (8 bytes per
        // pixel); everything else is reduced or expanded to 8 bits per channel.
        var bitsPerChannel = attribute == AttributeBitDepth16 && header.BitDepth == 16 ? 16 : 8;
        var bytesPerPixel = (ulong)bitsPerChannel / 2;
        var rowBytes = header.Width * bytesPerPixel;
        var pitch = imagePitch == 0 ? rowBytes : imagePitch;
        if (pitch < rowBytes)
        {
            return ctx.SetReturn(ErrorInvalidParam);
        }

        if ((header.Height - 1) * pitch + rowBytes > imageSize)
        {
            return ctx.SetReturn(ErrorInvalidSize);
        }

        // No host codec composed in: the decode genuinely cannot run here.
        if (HostImageCodec.Current is not { } codec)
        {
            return ctx.SetReturn(ErrorFatal);
        }

        if (!codec.TryDecode(png, HostImageContainer.Png, bitsPerChannel, out var image) ||
            image.Width != header.Width ||
            image.Height != header.Height ||
            image.BitsPerChannel != bitsPerChannel ||
            (ulong)image.Pixels.LongLength != rowBytes * header.Height)
        {
            return ctx.SetReturn(ErrorDecodeError);
        }

        var pixels = image.Pixels;
        ConvertPixels(pixels, bitsPerChannel, pixelFormat == PixelFormatB8G8R8A8, header.HasAlpha ? null : alphaValue);
        if (!ImageLibraryGuest.TryWriteRows(ctx, imageAddress, pitch, pixels, (int)rowBytes, (int)header.Height))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (infoAddress != 0 && !WriteImageInfo(ctx, infoAddress, header))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        return ctx.SetReturn(ImageLibraryGuest.PackDimensions(header.Width, header.Height));
    }

    /// <summary>
    /// In-place R/B swap for BGRA output and, for images without alpha samples, the caller's
    /// alpha value (its low byte at 8 bits per channel, all 16 bits at 16).
    /// </summary>
    internal static void ConvertPixels(Span<byte> pixels, int bitsPerChannel, bool swapRedBlue, ushort? fillAlpha)
    {
        if (bitsPerChannel == 16)
        {
            for (var offset = 0; offset + 8 <= pixels.Length; offset += 8)
            {
                if (swapRedBlue)
                {
                    (pixels[offset], pixels[offset + 4]) = (pixels[offset + 4], pixels[offset]);
                    (pixels[offset + 1], pixels[offset + 5]) = (pixels[offset + 5], pixels[offset + 1]);
                }

                if (fillAlpha is { } alpha16)
                {
                    pixels[offset + 6] = (byte)alpha16;
                    pixels[offset + 7] = (byte)(alpha16 >> 8);
                }
            }

            return;
        }

        for (var offset = 0; offset + 4 <= pixels.Length; offset += 4)
        {
            if (swapRedBlue)
            {
                (pixels[offset], pixels[offset + 2]) = (pixels[offset + 2], pixels[offset]);
            }

            if (fillAlpha is { } alpha)
            {
                pixels[offset + 3] = (byte)alpha;
            }
        }
    }

    private static int ValidateCreateParam(CpuContext ctx, ulong paramAddress, out uint attribute, out uint maxWidth)
    {
        attribute = 0;
        maxWidth = 0;
        if (paramAddress == 0)
        {
            return ErrorInvalidParam;
        }

        if (!ctx.TryReadUInt32(paramAddress, out var thisSize) ||
            !ctx.TryReadUInt32(paramAddress + 0x04, out attribute) ||
            !ctx.TryReadUInt32(paramAddress + 0x08, out maxWidth))
        {
            return ErrorInvalidAddr;
        }

        if (thisSize != CreateParamSize)
        {
            return ErrorInvalidSize;
        }

        if (attribute is not (AttributeNone or AttributeBitDepth16))
        {
            return ErrorInvalidParam;
        }

        if (maxWidth == 0 || maxWidth > MaxImageWidth)
        {
            return ErrorInvalidSize;
        }

        return 0;
    }

    private static bool WriteImageInfo(CpuContext ctx, ulong address, in PngImageHeader header)
    {
        var colorSpace = header.ColorType switch
        {
            PngColorType.Grayscale => ColorSpaceGrayscale,
            PngColorType.Rgb => ColorSpaceRgb,
            PngColorType.Palette => ColorSpaceClut,
            PngColorType.GrayscaleAlpha => ColorSpaceGrayscaleAlpha,
            _ => ColorSpaceRgba,
        };
        var flags = (header.Interlaced ? ImageFlagAdam7Interlace : 0) |
                    (header.HasTransparencyChunk ? ImageFlagTrnsChunkExist : 0);
        return ctx.TryWriteUInt32(address, header.Width) &&
               ctx.TryWriteUInt32(address + 0x04, header.Height) &&
               ctx.TryWriteUInt16(address + 0x08, colorSpace) &&
               ctx.TryWriteUInt16(address + 0x0A, header.BitDepth) &&
               ctx.TryWriteUInt32(address + 0x0C, flags);
    }
}
