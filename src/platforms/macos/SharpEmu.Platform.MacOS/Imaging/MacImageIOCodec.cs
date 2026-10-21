// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpEmu.Libs.ImageCodec;

namespace SharpEmu.Platform.MacOS.Imaging;

/// <summary>
/// PNG/JPEG through ImageIO: <c>CGImageSource</c> decodes, <c>CGImageDestination</c> encodes,
/// and vImage (Accelerate) unpacks whatever CGImage layout ImageIO produced into straight-alpha
/// RGBA. vImage is used rather than drawing into a CGBitmapContext because bitmap contexts
/// only take premultiplied alpha, which would not round-trip translucent PNG pixels.
/// </summary>
/// <remarks>
/// No colour management is applied: the destination format of the unpack uses the image's
/// own colour space (its base space for indexed images), so samples come out as stored in
/// the file, which is what the console's decoders return. Only colour spaces that are neither
/// RGB nor gray (CMYK JPEGs) are converted, to device RGB.
/// Every CoreFoundation/CoreGraphics object created here is released before returning.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed partial class MacImageIOCodec : IHostImageCodec
{
    public static readonly MacImageIOCodec Instance = new();

    private const string CoreFoundationFramework =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ImageIOFramework = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    private const string AccelerateFramework = "/System/Library/Frameworks/Accelerate.framework/Accelerate";
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";

    private const uint CFStringEncodingUtf8 = 0x0800_0100;
    private const nint CFNumberDoubleType = 13;

    // CGColorSpaceModel
    private const int ColorSpaceModelMonochrome = 0;
    private const int ColorSpaceModelRgb = 1;
    private const int ColorSpaceModelIndexed = 5;

    // CGImageAlphaInfo / CGBitmapInfo
    private const uint AlphaNone = 0;
    private const uint AlphaLast = 3;
    private const uint AlphaNoneSkipLast = 5;
    private const uint ByteOrder16Little = 1 << 12;

    private static readonly Lazy<Symbols> SymbolTable = new(Symbols.Resolve);

    private MacImageIOCodec()
    {
    }

    public bool TryDecode(
        ReadOnlySpan<byte> encoded,
        HostImageContainer container,
        int bitsPerChannel,
        [NotNullWhen(true)] out HostDecodedImage? image)
    {
        image = null;
        if (encoded.IsEmpty || bitsPerChannel is not (8 or 16))
        {
            return false;
        }

        nint data = 0, source = 0, cgImage = 0, ownedColorSpace = 0;
        try
        {
            data = CreateData(encoded);
            if (data == 0)
            {
                return false;
            }

            source = CGImageSourceCreateWithData(data, 0);
            if (source == 0 || CGImageSourceGetCount(source) == 0)
            {
                return false;
            }

            cgImage = CGImageSourceCreateImageAtIndex(source, 0, 0);
            if (cgImage == 0)
            {
                return false;
            }

            var width = (int)CGImageGetWidth(cgImage);
            var height = (int)CGImageGetHeight(cgImage);
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            var colorSpace = CGImageGetColorSpace(cgImage);
            var model = colorSpace == 0 ? -1 : CGColorSpaceGetModel(colorSpace);
            if (model == ColorSpaceModelIndexed)
            {
                colorSpace = CGColorSpaceGetBaseColorSpace(colorSpace);
                model = colorSpace == 0 ? -1 : CGColorSpaceGetModel(colorSpace);
            }

            if (model is not (ColorSpaceModelRgb or ColorSpaceModelMonochrome))
            {
                ownedColorSpace = CGColorSpaceCreateDeviceRGB();
                colorSpace = ownedColorSpace;
                model = ColorSpaceModelRgb;
            }

            var gray = model == ColorSpaceModelMonochrome;
            var channels = gray ? 2 : 4;
            var bytesPerChannel = bitsPerChannel / 8;
            var format = new VImageCGImageFormat
            {
                BitsPerComponent = (uint)bitsPerChannel,
                BitsPerPixel = (uint)(bitsPerChannel * channels),
                ColorSpace = colorSpace,
                BitmapInfo = AlphaLast | (bitsPerChannel == 16 ? ByteOrder16Little : 0),
            };

            var pixels = Unpack(cgImage, ref format, width, height, channels * bytesPerChannel);
            if (pixels is null)
            {
                return false;
            }

            if (gray)
            {
                pixels = ExpandGrayAlpha(pixels, width * height, bytesPerChannel);
            }

            image = new HostDecodedImage(width, height, bitsPerChannel, pixels);
            return true;
        }
        finally
        {
            Release(cgImage);
            Release(source);
            Release(data);
            Release(ownedColorSpace);
        }
    }

    public bool TryEncode(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        HostImagePixelLayout layout,
        HostImageContainer container,
        int jpegQuality,
        [NotNullWhen(true)] out byte[]? encoded)
    {
        encoded = null;
        var bytesPerPixel = layout == HostImagePixelLayout.Gray8 ? 1 : 4;
        if (width <= 0 || height <= 0 || pixels.Length < (long)width * height * bytesPerPixel)
        {
            return false;
        }

        var symbols = SymbolTable.Value;
        nint pixelData = 0, provider = 0, colorSpace = 0, cgImage = 0;
        nint output = 0, type = 0, destination = 0, quality = 0, properties = 0;
        try
        {
            pixelData = CreateData(pixels[..(width * height * bytesPerPixel)]);
            provider = pixelData == 0 ? 0 : CGDataProviderCreateWithCFData(pixelData);
            colorSpace = layout == HostImagePixelLayout.Gray8
                ? CGColorSpaceCreateDeviceGray()
                : CGColorSpaceCreateDeviceRGB();
            if (provider == 0 || colorSpace == 0)
            {
                return false;
            }

            var bitmapInfo = layout switch
            {
                HostImagePixelLayout.Rgba8 => AlphaLast,
                HostImagePixelLayout.Rgbx8 => AlphaNoneSkipLast,
                _ => AlphaNone,
            };
            cgImage = CGImageCreate(
                (nuint)width,
                (nuint)height,
                8,
                (nuint)(bytesPerPixel * 8),
                (nuint)(width * bytesPerPixel),
                colorSpace,
                bitmapInfo,
                provider,
                0,
                false,
                0);
            if (cgImage == 0)
            {
                return false;
            }

            output = CFDataCreateMutable(0, 0);
            type = CFStringCreateWithCString(
                0,
                container == HostImageContainer.Png ? "public.png" : "public.jpeg",
                CFStringEncodingUtf8);
            if (output == 0 || type == 0)
            {
                return false;
            }

            destination = CGImageDestinationCreateWithData(output, type, 1, 0);
            if (destination == 0)
            {
                return false;
            }

            if (container == HostImageContainer.Jpeg)
            {
                var value = Math.Clamp(jpegQuality, 1, 100) / 100.0;
                unsafe
                {
                    quality = CFNumberCreate(0, CFNumberDoubleType, &value);
                    if (quality == 0)
                    {
                        return false;
                    }

                    var key = symbols.LossyCompressionQuality;
                    var number = quality;
                    properties = CFDictionaryCreate(
                        0,
                        &key,
                        &number,
                        1,
                        symbols.DictionaryKeyCallBacks,
                        symbols.DictionaryValueCallBacks);
                }

                if (properties == 0)
                {
                    return false;
                }
            }

            CGImageDestinationAddImage(destination, cgImage, properties);
            if (!CGImageDestinationFinalize(destination))
            {
                return false;
            }

            var length = (int)CFDataGetLength(output);
            var bytes = CFDataGetBytePtr(output);
            if (length <= 0 || bytes == 0)
            {
                return false;
            }

            encoded = new byte[length];
            Marshal.Copy(bytes, encoded, 0, length);
            return true;
        }
        finally
        {
            Release(properties);
            Release(quality);
            Release(destination);
            Release(type);
            Release(output);
            Release(cgImage);
            Release(colorSpace);
            Release(provider);
            Release(pixelData);
        }
    }

    /// <summary>Converts the CGImage to <paramref name="format"/> and returns packed rows.</summary>
    private static unsafe byte[]? Unpack(nint cgImage, ref VImageCGImageFormat format, int width, int height, int bytesPerPixel)
    {
        VImageBuffer buffer = default;
        nint error;
        fixed (VImageCGImageFormat* formatPointer = &format)
        {
            error = vImageBuffer_InitWithCGImage(&buffer, formatPointer, null, cgImage, 0);
        }

        if (error != 0 || buffer.Data == 0)
        {
            return null;
        }

        try
        {
            if ((long)buffer.Width != width || (long)buffer.Height != height)
            {
                return null;
            }

            var rowBytes = width * bytesPerPixel;
            var pixels = new byte[rowBytes * height];
            for (var y = 0; y < height; y++)
            {
                new ReadOnlySpan<byte>((byte*)buffer.Data + (nint)y * (nint)buffer.RowBytes, rowBytes)
                    .CopyTo(pixels.AsSpan(y * rowBytes, rowBytes));
            }

            return pixels;
        }
        finally
        {
            free(buffer.Data);
        }
    }

    /// <summary>Gray + alpha samples to R = G = B = gray, A = alpha.</summary>
    private static byte[] ExpandGrayAlpha(byte[] grayAlpha, int pixelCount, int bytesPerChannel)
    {
        var output = new byte[pixelCount * 4 * bytesPerChannel];
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var source = grayAlpha.AsSpan(pixel * 2 * bytesPerChannel, 2 * bytesPerChannel);
            var target = output.AsSpan(pixel * 4 * bytesPerChannel, 4 * bytesPerChannel);
            var gray = source[..bytesPerChannel];
            gray.CopyTo(target);
            gray.CopyTo(target[bytesPerChannel..]);
            gray.CopyTo(target[(2 * bytesPerChannel)..]);
            source[bytesPerChannel..].CopyTo(target[(3 * bytesPerChannel)..]);
        }

        return output;
    }

    private static unsafe nint CreateData(ReadOnlySpan<byte> bytes)
    {
        fixed (byte* pointer = bytes)
        {
            return CFDataCreate(0, pointer, bytes.Length);
        }
    }

    private static void Release(nint value)
    {
        if (value != 0)
        {
            CFRelease(value);
        }
    }

    /// <summary>The CF data symbols the encode needs, resolved once from the frameworks.</summary>
    private sealed class Symbols
    {
        public nint LossyCompressionQuality { get; private init; }

        public nint DictionaryKeyCallBacks { get; private init; }

        public nint DictionaryValueCallBacks { get; private init; }

        public static Symbols Resolve()
        {
            var coreFoundation = NativeLibrary.Load(CoreFoundationFramework);
            var imageIO = NativeLibrary.Load(ImageIOFramework);
            return new Symbols
            {
                // A CFStringRef variable: read the pointer it holds.
                LossyCompressionQuality = Marshal.ReadIntPtr(
                    NativeLibrary.GetExport(imageIO, "kCGImageDestinationLossyCompressionQuality")),

                // Callback structs: CFDictionaryCreate takes their addresses.
                DictionaryKeyCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks"),
                DictionaryValueCallBacks = NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks"),
            };
        }
    }

    /// <summary>vImage_Buffer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VImageBuffer
    {
        public nint Data;
        public nuint Height;
        public nuint Width;
        public nuint RowBytes;
    }

    /// <summary>vImage_CGImageFormat (40 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VImageCGImageFormat
    {
        public uint BitsPerComponent;
        public uint BitsPerPixel;
        public nint ColorSpace;
        public uint BitmapInfo;
        public uint Version;
        public nint Decode;
        public int RenderingIntent;
    }

    // ---- CoreFoundation ----

    [LibraryImport(CoreFoundationFramework)]
    private static unsafe partial nint CFDataCreate(nint allocator, byte* bytes, nint length);

    [LibraryImport(CoreFoundationFramework)]
    private static partial nint CFDataCreateMutable(nint allocator, nint capacity);

    [LibraryImport(CoreFoundationFramework)]
    private static partial nint CFDataGetLength(nint data);

    [LibraryImport(CoreFoundationFramework)]
    private static partial nint CFDataGetBytePtr(nint data);

    [LibraryImport(CoreFoundationFramework, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string text, uint encoding);

    [LibraryImport(CoreFoundationFramework)]
    private static unsafe partial nint CFNumberCreate(nint allocator, nint type, void* value);

    [LibraryImport(CoreFoundationFramework)]
    private static unsafe partial nint CFDictionaryCreate(
        nint allocator,
        nint* keys,
        nint* values,
        nint count,
        nint keyCallBacks,
        nint valueCallBacks);

    [LibraryImport(CoreFoundationFramework)]
    private static partial void CFRelease(nint value);

    // ---- CoreGraphics ----

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static partial nuint CGImageGetWidth(nint image);

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static partial nuint CGImageGetHeight(nint image);

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static partial nint CGImageGetColorSpace(nint image);

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static partial int CGColorSpaceGetModel(nint colorSpace);

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static partial nint CGColorSpaceGetBaseColorSpace(nint colorSpace);

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static partial nint CGColorSpaceCreateDeviceRGB();

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static partial nint CGColorSpaceCreateDeviceGray();

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static partial nint CGDataProviderCreateWithCFData(nint data);

    [LibraryImport(MacObjC.CoreGraphicsFramework)]
    private static partial nint CGImageCreate(
        nuint width,
        nuint height,
        nuint bitsPerComponent,
        nuint bitsPerPixel,
        nuint bytesPerRow,
        nint colorSpace,
        uint bitmapInfo,
        nint provider,
        nint decode,
        [MarshalAs(UnmanagedType.I1)] bool shouldInterpolate,
        int intent);

    // ---- ImageIO ----

    [LibraryImport(ImageIOFramework)]
    private static partial nint CGImageSourceCreateWithData(nint data, nint options);

    [LibraryImport(ImageIOFramework)]
    private static partial nuint CGImageSourceGetCount(nint source);

    [LibraryImport(ImageIOFramework)]
    private static partial nint CGImageSourceCreateImageAtIndex(nint source, nuint index, nint options);

    [LibraryImport(ImageIOFramework)]
    private static partial nint CGImageDestinationCreateWithData(nint data, nint type, nuint count, nint options);

    [LibraryImport(ImageIOFramework)]
    private static partial void CGImageDestinationAddImage(nint destination, nint image, nint properties);

    [LibraryImport(ImageIOFramework)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool CGImageDestinationFinalize(nint destination);

    // ---- Accelerate / libSystem ----

    [LibraryImport(AccelerateFramework)]
    private static unsafe partial nint vImageBuffer_InitWithCGImage(
        VImageBuffer* buffer,
        VImageCGImageFormat* format,
        double* backgroundColor,
        nint image,
        uint flags);

    [LibraryImport(SystemLibrary)]
    private static partial void free(nint pointer);
}
