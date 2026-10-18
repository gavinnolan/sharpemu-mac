// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.ImageCodec;

/// <summary>
/// libScePngEnc. Arguments are validated here; the PNG itself is produced by the registered
/// <see cref="IHostImageCodec"/>.
/// </summary>
/// <remarks>
/// SDK structures (little endian):
/// <code>
/// ScePngEncCreateParam (0x10)  0x00 u32 thisSize, 0x04 u32 attribute, 0x08 u32 maxImageWidth,
///                              0x0C u32 maxFilterNumber
/// ScePngEncEncodeParam (0x30)  0x00 ptr imageMemAddr, 0x08 ptr pngMemAddr, 0x10 u32 imageMemSize,
///                              0x14 u32 pngMemSize, 0x18 u32 imageWidth, 0x1C u32 imageHeight,
///                              0x20 u32 imagePitch, 0x24 u16 pixelFormat, 0x26 u16 colorSpace,
///                              0x28 u16 bitDepth, 0x2A u16 clutNumber, 0x2C u16 filterType,
///                              0x2E u16 compressionLevel
/// ScePngEncOutputInfo  (0x08)  0x00 u32 dataSize, 0x04 u32 processedHeight
/// </code>
/// The encoder context is the work memory itself (0x10 bytes): 0x00 self pointer,
/// 0x08 u32 maxImageWidth.
///
/// The host encoder chooses its own row filters and deflate level, so filterType and
/// compressionLevel are validated but only influence the size of the (always valid) PNG.
/// </remarks>
public static class PngEncExports
{
    private const string Library = "libScePngEnc";

    public const int ErrorInvalidAddr = unchecked((int)0x80690101);
    public const int ErrorInvalidSize = unchecked((int)0x80690102);
    public const int ErrorInvalidParam = unchecked((int)0x80690103);
    public const int ErrorInvalidHandle = unchecked((int)0x80690104);
    public const int ErrorDataOverflow = unchecked((int)0x80690110);
    public const int ErrorFatal = unchecked((int)0x80690120);

    public const uint MaxImageWidth = 1_000_000;
    public const uint MaxImageHeight = 1_000_000;
    public const uint MaxFilterNumber = 5;
    public const int WorkMemorySize = 0x10;
    private const ulong HandleAlignment = 1;

    public const ushort ColorSpaceRgb = 3;
    public const ushort ColorSpaceRgba = 19;
    public const ushort PixelFormatR8G8B8A8 = 0;
    public const ushort PixelFormatB8G8R8A8 = 1;

    public const ushort FilterNone = 0;
    public const ushort FilterSub = 1;
    public const ushort FilterUp = 2;
    public const ushort FilterAverage = 4;
    public const ushort FilterPaeth = 8;
    public const ushort FilterAll = FilterSub | FilterUp | FilterAverage | FilterPaeth;

    private const uint CreateParamSize = 0x10;

    [SysAbiExport(
        Nid = "9030RnBDoh4",
        ExportName = "scePngEncQueryMemorySize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int PngEncQueryMemorySize(CpuContext ctx)
    {
        var result = ValidateCreateParam(ctx, ctx[CpuRegister.Rdi], out _);
        return ctx.SetReturn(result != 0 ? result : WorkMemorySize);
    }

    [SysAbiExport(
        Nid = "7aGTPfrqT9s",
        ExportName = "scePngEncCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int PngEncCreate(CpuContext ctx)
    {
        var memoryAddress = ctx[CpuRegister.Rsi];
        var memorySize = (uint)ctx[CpuRegister.Rdx];
        var handleAddress = ctx[CpuRegister.Rcx];

        var result = ValidateCreateParam(ctx, ctx[CpuRegister.Rdi], out var maxWidth);
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
            return ctx.SetReturn(ErrorInvalidSize);
        }

        if (!ImageLibraryGuest.TryMarkLive(ctx, memoryAddress) ||
            !ctx.TryWriteUInt32(memoryAddress + 0x08, maxWidth) ||
            !ctx.TryWriteUInt32(memoryAddress + 0x0C, 0) ||
            !ctx.TryWriteUInt64(handleAddress, memoryAddress))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "RUrWdwTWZy8",
        ExportName = "scePngEncDelete",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int PngEncDelete(CpuContext ctx)
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
        Nid = "xgDjJKpcyHo",
        ExportName = "scePngEncEncode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int PngEncEncode(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        var outputInfoAddress = ctx[CpuRegister.Rdx];

        if (!ImageLibraryGuest.IsLive(ctx, handle, HandleAlignment) ||
            !ctx.TryReadUInt32(handle + 0x08, out var maxWidth))
        {
            return ctx.SetReturn(ErrorInvalidHandle);
        }

        if (paramAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidParam);
        }

        if (!ctx.TryReadUInt64(paramAddress, out var imageAddress) ||
            !ctx.TryReadUInt64(paramAddress + 0x08, out var pngAddress) ||
            !ctx.TryReadUInt32(paramAddress + 0x10, out var imageSize) ||
            !ctx.TryReadUInt32(paramAddress + 0x14, out var pngSize) ||
            !ctx.TryReadUInt32(paramAddress + 0x18, out var width) ||
            !ctx.TryReadUInt32(paramAddress + 0x1C, out var height) ||
            !ctx.TryReadUInt32(paramAddress + 0x20, out var pitch) ||
            !ctx.TryReadUInt16(paramAddress + 0x24, out var pixelFormat) ||
            !ctx.TryReadUInt16(paramAddress + 0x26, out var colorSpace) ||
            !ctx.TryReadUInt16(paramAddress + 0x28, out var bitDepth) ||
            !ctx.TryReadUInt16(paramAddress + 0x2A, out var clutNumber) ||
            !ctx.TryReadUInt16(paramAddress + 0x2C, out var filterType) ||
            !ctx.TryReadUInt16(paramAddress + 0x2E, out var compressionLevel))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (imageAddress == 0 || pngAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (pixelFormat is not (PixelFormatR8G8B8A8 or PixelFormatB8G8R8A8) ||
            colorSpace is not (ColorSpaceRgb or ColorSpaceRgba) ||
            bitDepth != 8 ||
            clutNumber != 0 ||
            (filterType & ~FilterAll) != 0 ||
            compressionLevel > 9)
        {
            return ctx.SetReturn(ErrorInvalidParam);
        }

        var rowBytes = (ulong)width * 4;
        if (width == 0 || height == 0 || width > maxWidth || height > MaxImageHeight ||
            pngSize == 0 || pitch < rowBytes ||
            (ulong)pitch * (height - 1) + rowBytes > imageSize)
        {
            return ctx.SetReturn(ErrorInvalidSize);
        }

        if (!ImageLibraryGuest.TryReadRows(ctx, imageAddress, pitch, (int)rowBytes, (int)height, out var pixels))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (pixelFormat == PixelFormatB8G8R8A8)
        {
            PngDecExports.ConvertPixels(pixels, 8, swapRedBlue: true, fillAlpha: null);
        }

        // No host codec composed in, or the host encoder failed: nothing was produced.
        if (HostImageCodec.Current is not { } codec ||
            !codec.TryEncode(
                pixels,
                (int)width,
                (int)height,
                colorSpace == ColorSpaceRgba ? HostImagePixelLayout.Rgba8 : HostImagePixelLayout.Rgbx8,
                HostImageContainer.Png,
                jpegQuality: 0,
                out var png))
        {
            return ctx.SetReturn(ErrorFatal);
        }

        var overflow = (ulong)png.LongLength > pngSize;
        if (outputInfoAddress != 0 &&
            (!ctx.TryWriteUInt32(outputInfoAddress, overflow ? 0 : (uint)png.Length) ||
             !ctx.TryWriteUInt32(outputInfoAddress + 0x04, overflow ? 0 : height)))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (overflow)
        {
            return ctx.SetReturn(ErrorDataOverflow);
        }

        if (!ctx.Memory.TryWrite(pngAddress, png))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        return ctx.SetReturn(png.Length);
    }

    private static int ValidateCreateParam(CpuContext ctx, ulong paramAddress, out uint maxWidth)
    {
        maxWidth = 0;
        if (paramAddress == 0)
        {
            return ErrorInvalidAddr;
        }

        if (!ctx.TryReadUInt32(paramAddress, out var thisSize) ||
            !ctx.TryReadUInt32(paramAddress + 0x04, out var attribute) ||
            !ctx.TryReadUInt32(paramAddress + 0x08, out maxWidth) ||
            !ctx.TryReadUInt32(paramAddress + 0x0C, out var maxFilterNumber))
        {
            return ErrorInvalidAddr;
        }

        if (thisSize != CreateParamSize || attribute != 0 || maxFilterNumber > MaxFilterNumber)
        {
            return ErrorInvalidParam;
        }

        return maxWidth == 0 || maxWidth > MaxImageWidth ? ErrorInvalidSize : 0;
    }
}
