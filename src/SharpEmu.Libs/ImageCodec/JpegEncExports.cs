// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.ImageCodec;

/// <summary>
/// libSceJpegEnc. Arguments are validated here; the JPEG itself is produced by the
/// registered <see cref="IHostImageCodec"/>.
/// </summary>
/// <remarks>
/// SDK structures (little endian):
/// <code>
/// SceJpegEncCreateParam (0x08)  0x00 u32 size, 0x04 u32 attr
/// SceJpegEncEncodeParam (0x30)  0x00 ptr image, 0x08 ptr jpeg, 0x10 u32 imageSize, 0x14 u32 jpegSize,
///                               0x18 u32 imageWidth, 0x1C u32 imageHeight, 0x20 u32 imagePitch,
///                               0x24 u16 pixelFormat, 0x26 u16 encodeMode, 0x28 u16 colorSpace,
///                               0x2A u8 samplingType, 0x2B u8 compressionRatio, 0x2C s32 restartInterval
/// SceJpegEncOutputInfo  (0x08)  0x00 u32 size, 0x04 u32 height
/// </code>
/// The encoder context lives in the work memory (0x800 bytes) at the work address aligned
/// up to 0x20; its first quadword is the self pointer.
///
/// Host encoder limits (the output is always a valid baseline JPEG of the image):
/// ImageIO picks the chroma subsampling itself and writes no DRI restart markers, so
/// samplingType and restartInterval are validated but do not shape the stream; and MJPEG
/// mode keeps the DHT segments a strict MJPEG frame would omit (decoders accept both).
/// </remarks>
public static class JpegEncExports
{
    private const string Library = "libSceJpegEnc";

    public const int ErrorInvalidAddr = unchecked((int)0x80650101);
    public const int ErrorInvalidSize = unchecked((int)0x80650102);
    public const int ErrorInvalidParam = unchecked((int)0x80650103);
    public const int ErrorInvalidHandle = unchecked((int)0x80650104);

    // Not in any reference: libScePngEnc's FATAL (0x80690120) in libSceJpegEnc's facility,
    // whose other codes mirror libScePngEnc's one for one. Returned when no host encoder is
    // composed in or the host encoder fails.
    public const int ErrorFatal = unchecked((int)0x80650120);

    public const uint AttributeNone = 0;
    public const int WorkMemorySize = 0x800;
    private const ulong HandleAlignment = 0x20;

    public const ushort PixelFormatR8G8B8A8 = 0;
    public const ushort PixelFormatB8G8R8A8 = 1;
    public const ushort PixelFormatY8U8Y8V8 = 10;
    public const ushort PixelFormatY8 = 11;

    public const ushort EncodeModeNormal = 0;
    public const ushort EncodeModeMjpeg = 1;

    public const ushort ColorSpaceYcc = 1;
    public const ushort ColorSpaceGrayscale = 2;

    public const byte SamplingTypeFull = 0;
    public const byte SamplingType422 = 1;
    public const byte SamplingType420 = 2;

    private const uint CreateParamSize = 0x08;
    private const uint MaxImageDimension = 0xFFFF;
    private const uint MaxImagePitch = 0x0FFF_FFFF;
    private const ulong MaxImageSize = 0x7FFF_FFFF;

    [SysAbiExport(
        Nid = "o6ZgXfFdWXQ",
        ExportName = "sceJpegEncQueryMemorySize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int JpegEncQueryMemorySize(CpuContext ctx)
    {
        var result = ValidateCreateParam(ctx, ctx[CpuRegister.Rdi]);
        return ctx.SetReturn(result != 0 ? result : WorkMemorySize);
    }

    [SysAbiExport(
        Nid = "K+rocojkr-I",
        ExportName = "sceJpegEncCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int JpegEncCreate(CpuContext ctx)
    {
        var memoryAddress = ctx[CpuRegister.Rsi];
        var memorySize = (uint)ctx[CpuRegister.Rdx];
        var handleAddress = ctx[CpuRegister.Rcx];

        var result = ValidateCreateParam(ctx, ctx[CpuRegister.Rdi]);
        if (result != 0)
        {
            return ctx.SetReturn(result);
        }

        if (memoryAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (memorySize < WorkMemorySize)
        {
            return ctx.SetReturn(ErrorInvalidSize);
        }

        if (handleAddress == 0)
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        var handle = ImageLibraryGuest.AlignUp(memoryAddress, HandleAlignment);
        if (!ImageLibraryGuest.TryMarkLive(ctx, handle) || !ctx.TryWriteUInt64(handleAddress, handle))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "j1LyMdaM+C0",
        ExportName = "sceJpegEncDelete",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int JpegEncDelete(CpuContext ctx)
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
        Nid = "QbrU0cUghEM",
        ExportName = "sceJpegEncEncode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = Library)]
    public static int JpegEncEncode(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var paramAddress = ctx[CpuRegister.Rsi];
        var outputInfoAddress = ctx[CpuRegister.Rdx];

        if (!ImageLibraryGuest.IsLive(ctx, handle, HandleAlignment))
        {
            return ctx.SetReturn(ErrorInvalidHandle);
        }

        if (!TryReadEncodeParam(ctx, paramAddress, out var param))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        var result = ValidateEncodeParam(param);
        if (result != 0)
        {
            return ctx.SetReturn(result);
        }

        var grayscale = param.PixelFormat == PixelFormatY8;
        var sourceRowBytes = param.PixelFormat switch
        {
            PixelFormatY8 => (int)param.Width,
            PixelFormatY8U8Y8V8 => (int)((param.Width + 1) & ~1u) * 2,
            _ => (int)param.Width * 4,
        };
        if (!ImageLibraryGuest.TryReadRows(
                ctx, param.Image, param.Pitch, sourceRowBytes, (int)param.Height, out var source))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        var pixels = param.PixelFormat switch
        {
            PixelFormatY8 => source,
            PixelFormatY8U8Y8V8 => YuyvToRgbx(source, (int)param.Width, (int)param.Height, sourceRowBytes),
            _ => source,
        };
        if (param.PixelFormat == PixelFormatB8G8R8A8)
        {
            PngDecExports.ConvertPixels(pixels, 8, swapRedBlue: true, fillAlpha: null);
        }

        if (HostImageCodec.Current is not { } codec ||
            !codec.TryEncode(
                pixels,
                (int)param.Width,
                (int)param.Height,
                grayscale ? HostImagePixelLayout.Gray8 : HostImagePixelLayout.Rgbx8,
                HostImageContainer.Jpeg,
                Quality(param.CompressionRatio),
                out var jpeg))
        {
            return ctx.SetReturn(ErrorFatal);
        }

        if ((ulong)jpeg.LongLength > param.JpegSize)
        {
            return ctx.SetReturn(ErrorInvalidSize);
        }

        if (!ctx.Memory.TryWrite(param.Jpeg, jpeg))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        if (outputInfoAddress != 0 &&
            (!ctx.TryWriteUInt32(outputInfoAddress, (uint)jpeg.Length) ||
             !ctx.TryWriteUInt32(outputInfoAddress + 0x04, param.Height)))
        {
            return ctx.SetReturn(ErrorInvalidAddr);
        }

        return ctx.SetReturn(0);
    }

    /// <summary>compressionRatio 0 is the best quality (100), 255 the most compressed (1).</summary>
    internal static int Quality(byte compressionRatio) => 100 - compressionRatio * 99 / 255;

    private readonly record struct EncodeParam(
        ulong Image,
        ulong Jpeg,
        uint ImageSize,
        uint JpegSize,
        uint Width,
        uint Height,
        uint Pitch,
        ushort PixelFormat,
        ushort EncodeMode,
        ushort ColorSpace,
        byte SamplingType,
        byte CompressionRatio,
        int RestartInterval);

    private static bool TryReadEncodeParam(CpuContext ctx, ulong address, out EncodeParam param)
    {
        param = default;
        Span<byte> raw = stackalloc byte[0x30];
        if (address == 0 || !ctx.Memory.TryRead(address, raw))
        {
            return false;
        }

        param = new EncodeParam(
            BitConverter.ToUInt64(raw),
            BitConverter.ToUInt64(raw[0x08..]),
            BitConverter.ToUInt32(raw[0x10..]),
            BitConverter.ToUInt32(raw[0x14..]),
            BitConverter.ToUInt32(raw[0x18..]),
            BitConverter.ToUInt32(raw[0x1C..]),
            BitConverter.ToUInt32(raw[0x20..]),
            BitConverter.ToUInt16(raw[0x24..]),
            BitConverter.ToUInt16(raw[0x26..]),
            BitConverter.ToUInt16(raw[0x28..]),
            raw[0x2A],
            raw[0x2B],
            BitConverter.ToInt32(raw[0x2C..]));
        return true;
    }

    private static int ValidateEncodeParam(in EncodeParam param)
    {
        var grayscaleInput = param.PixelFormat == PixelFormatY8;
        if (param.Image == 0 || (!grayscaleInput && param.Image % 4 != 0) || param.Jpeg == 0)
        {
            return ErrorInvalidAddr;
        }

        if (param.ImageSize == 0 || param.JpegSize == 0)
        {
            return ErrorInvalidSize;
        }

        if (param.Width == 0 || param.Height == 0 ||
            param.Width > MaxImageDimension || param.Height > MaxImageDimension ||
            param.Pitch == 0 || param.Pitch > MaxImagePitch ||
            (!grayscaleInput && param.Pitch % 4 != 0))
        {
            return ErrorInvalidParam;
        }

        // restartInterval: 0 none, -1 one per MCU row, positive an MCU count. No reference
        // gives other negative values a meaning, so they are refused.
        var requiredSize = (ulong)param.Height * param.Pitch;
        if (requiredSize > MaxImageSize || requiredSize > param.ImageSize ||
            param.EncodeMode is not (EncodeModeNormal or EncodeModeMjpeg) ||
            param.ColorSpace is not (ColorSpaceYcc or ColorSpaceGrayscale) ||
            param.SamplingType is not (SamplingTypeFull or SamplingType422 or SamplingType420) ||
            param.RestartInterval > (int)MaxImageDimension ||
            param.RestartInterval < -1)
        {
            return ErrorInvalidParam;
        }

        return param.PixelFormat switch
        {
            PixelFormatR8G8B8A8 or PixelFormatB8G8R8A8 =>
                param.Pitch / 4 >= param.Width &&
                param.ColorSpace == ColorSpaceYcc &&
                param.SamplingType != SamplingTypeFull ? 0 : ErrorInvalidParam,
            PixelFormatY8U8Y8V8 =>
                param.Pitch / 2 >= ((param.Width + 1) & ~1u) &&
                param.ColorSpace == ColorSpaceYcc &&
                param.SamplingType != SamplingTypeFull ? 0 : ErrorInvalidParam,
            PixelFormatY8 =>
                param.Pitch >= param.Width &&
                param.ColorSpace == ColorSpaceGrayscale &&
                param.SamplingType == SamplingTypeFull ? 0 : ErrorInvalidParam,
            _ => ErrorInvalidParam,
        };
    }

    /// <summary>Packed Y0 U Y1 V (full-range BT.601) to R, G, B, X.</summary>
    internal static byte[] YuyvToRgbx(byte[] source, int width, int height, int sourceRowBytes)
    {
        var output = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var row = y * sourceRowBytes;
            for (var x = 0; x < width; x++)
            {
                var pair = row + (x / 2) * 4;
                var luma = source[pair + (x % 2) * 2];
                var cb = source[pair + 1] - 128.0f;
                var cr = source[pair + 3] - 128.0f;
                var target = (y * width + x) * 4;
                output[target] = ClampToByte(luma + 1.402f * cr);
                output[target + 1] = ClampToByte(luma - 0.344136f * cb - 0.714136f * cr);
                output[target + 2] = ClampToByte(luma + 1.772f * cb);
                output[target + 3] = 0xFF;
            }
        }

        return output;
    }

    private static byte ClampToByte(float value) => (byte)Math.Clamp(value + 0.5f, 0.0f, 255.0f);

    private static int ValidateCreateParam(CpuContext ctx, ulong paramAddress)
    {
        if (paramAddress == 0 ||
            !ctx.TryReadUInt32(paramAddress, out var size) ||
            !ctx.TryReadUInt32(paramAddress + 0x04, out var attribute))
        {
            return ErrorInvalidAddr;
        }

        if (size != CreateParamSize)
        {
            return ErrorInvalidSize;
        }

        return attribute == AttributeNone ? 0 : ErrorInvalidParam;
    }
}
