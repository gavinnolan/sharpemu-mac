// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;

namespace SharpEmu.Gpu.Metal.V2;

// The shader prewarm progress bar: while the prewarm holds the guest and no guest
// frame has been presented, a small CPU-drawn panel (bar, caption, hint) is shown
// over the splash. It is drawn with CoreGraphics into a BGRA texture sized to the
// drawable, redrawn only when the count changes, and it costs one volatile read per
// present once the prewarm is over. SHARPEMU_PREWARM_PROGRESS=0 turns it off.
internal static partial class MetalV2Presenter
{
    private static readonly bool PrewarmProgressEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_PREWARM_PROGRESS"), "0", StringComparison.Ordinal);

    private static nint _prewarmTexture;
    private static uint _prewarmWidth;
    private static uint _prewarmHeight;
    private static int _prewarmDrawnProgress = -1;
    private static int _prewarmDrawnTotal = -1;
    private static byte[]? _prewarmPixels;

    // Runs on the window thread, inside the present encoder, after the splash is drawn.
    private static void EncodePrewarmProgress(nint encoder)
    {
        if (!PrewarmProgressEnabled || _splashHidden)
        {
            return;
        }

        var gate = MetalShaderPrewarm.Gate;
        var total = gate.Total;
        if (total <= 0 || !gate.IsRunning)
        {
            ReleasePrewarmTexture();
            return;
        }

        // Laid out against a 16:9 area, so an ultrawide or portrait window keeps the
        // panel's proportions instead of stretching it.
        var referenceWidth = Math.Min(_drawableWidth, _drawableHeight * 16.0 / 9.0);
        var width = (uint)Math.Max(240, Math.Round(referenceWidth * 0.46));
        var height = (uint)Math.Round(width * 0.22);
        if (width > _drawableWidth || height > _drawableHeight)
        {
            return;
        }

        var progress = Math.Min(gate.Progress, total);
        if (_prewarmTexture == 0 || width != _prewarmWidth || height != _prewarmHeight)
        {
            ReleasePrewarmTexture();
            var descriptor = MetalNative.SendTextureDescriptor(
                MetalNative.Class("MTLTextureDescriptor"),
                MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
                (nuint)PixelFormatBgra8Unorm,
                width,
                height,
                mipmapped: false);
            _prewarmTexture = MetalNative.Send(
                _device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
            if (_prewarmTexture == 0)
            {
                return;
            }

            _prewarmWidth = width;
            _prewarmHeight = height;
        }

        if (progress != _prewarmDrawnProgress || total != _prewarmDrawnTotal)
        {
            if (!DrawPrewarmPanel(width, height, progress, total))
            {
                return;
            }

            _prewarmDrawnProgress = progress;
            _prewarmDrawnTotal = total;
        }

        if (_layerHdrActive)
        {
            EncodeHdrPresentState(encoder, _hdrPresentPipeline, false, _edrCurrentHeadroom);
        }
        else
        {
            MetalNative.SendVoid(encoder, MetalNative.Selector("setRenderPipelineState:"), _presentPipeline);
        }

        MetalNative.SendVoidViewport(
            encoder,
            MetalNative.Selector("setViewport:"),
            new MtlViewport
            {
                OriginX = Math.Floor((_drawableWidth - width) * 0.5),
                OriginY = Math.Floor(_drawableHeight * 0.68 - height * 0.5),
                Width = width,
                Height = height,
                ZNear = 0,
                ZFar = 1,
            });
        MetalNative.SendSetAtIndex(
            encoder, MetalNative.Selector("setFragmentTexture:atIndex:"), _prewarmTexture, 0);
        MetalNative.SendSetAtIndex(
            encoder, MetalNative.Selector("setFragmentSamplerState:atIndex:"), _presentSampler, 0);
        MetalNative.SendDrawPrimitives(
            encoder, MetalNative.Selector("drawPrimitives:vertexStart:vertexCount:"), Mtl.PrimitiveTriangle, 0, 3);
    }

    private static void ReleasePrewarmTexture()
    {
        if (_prewarmTexture != 0)
        {
            MetalNative.SendVoid(_prewarmTexture, MetalNative.Selector("release"));
            _prewarmTexture = 0;
            _prewarmPixels = null;
            _prewarmDrawnProgress = -1;
            _prewarmDrawnTotal = -1;
        }
    }

    private static unsafe bool DrawPrewarmPanel(uint width, uint height, int progress, int total)
    {
        var pixels = _prewarmPixels;
        if (pixels is null || pixels.Length != checked((int)(width * height * 4)))
        {
            pixels = _prewarmPixels = new byte[checked((int)(width * height * 4))];
        }

        var space = CgNative.CGColorSpaceCreateDeviceRGB();
        if (space == 0)
        {
            return false;
        }

        try
        {
            fixed (byte* data = pixels)
            {
                // BGRA, premultiplied first, little-endian: the layout of the texture.
                var context = CgNative.CGBitmapContextCreate(
                    (nint)data, width, height, 8, width * 4, space, 8194);
                if (context == 0)
                {
                    return false;
                }

                try
                {
                    var unit = width / 800.0;
                    CgNative.CGContextSetRGBFillColor(context, 0.05, 0.05, 0.06, 1);
                    CgNative.CGContextFillRect(context, new CgRect(0, 0, width, height));

                    var margin = 40 * unit;
                    var barHeight = Math.Max(4, 14 * unit);
                    var barWidth = width - 2 * margin;
                    var barY = height - 34 * unit - barHeight;
                    CgNative.CGContextSetRGBFillColor(context, 0.18, 0.18, 0.20, 1);
                    CgNative.CGContextFillRect(context, new CgRect(margin, barY, barWidth, barHeight));
                    CgNative.CGContextSetRGBFillColor(context, 0.30, 0.62, 1.0, 1);
                    CgNative.CGContextFillRect(
                        context, new CgRect(margin, barY, barWidth * progress / total, barHeight));

                    var caption = string.Create(
                        CultureInfo.InvariantCulture, $"Preparing shaders… {progress} / {total}");
                    DrawCenteredText(context, caption, 26 * unit, width, barY - 12 * unit - 26 * unit * 0.75, 0.93);
                    DrawCenteredText(
                        context,
                        "Depending on your hardware this may take a few minutes",
                        17 * unit,
                        width,
                        18 * unit,
                        0.55);
                }
                finally
                {
                    CgNative.CGContextRelease(context);
                }

                MetalNative.SendReplaceRegion(
                    _prewarmTexture,
                    MetalNative.Selector("replaceRegion:mipmapLevel:withBytes:bytesPerRow:"),
                    new MtlRegion { Width = width, Height = height, Depth = 1 },
                    0,
                    (nint)data,
                    width * 4);
            }
        }
        finally
        {
            CgNative.CGColorSpaceRelease(space);
        }

        return true;
    }

    private static unsafe void DrawCenteredText(
        nint context, string text, double size, uint width, double baseline, double gray)
    {
        // MacRoman encodes the ellipsis as 0xC9.
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            bytes[i] = text[i] == '…' ? (byte)0xC9 : (byte)text[i];
        }

        fixed (byte* name = "Helvetica\0"u8)
        fixed (byte* chars = bytes)
        {
            CgNative.CGContextSelectFont(context, name, size, 1);
            CgNative.CGContextSetTextDrawingMode(context, 3); // invisible: measure only
            CgNative.CGContextSetTextPosition(context, 0, 0);
            CgNative.CGContextShowText(context, chars, (nuint)bytes.Length);
            var advance = CgNative.CGContextGetTextPosition(context).X;
            CgNative.CGContextSetTextDrawingMode(context, 0);
            CgNative.CGContextSetRGBFillColor(context, gray, gray, gray, 1);
            CgNative.CGContextShowTextAtPoint(
                context, (width - advance) * 0.5, baseline, chars, (nuint)bytes.Length);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CgRect(double X, double Y, double Width, double Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct CgPoint
    {
        public double X;
        public double Y;
    }

    private static unsafe class CgNative
    {
        private const string Lib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        [DllImport(Lib)] public static extern nint CGColorSpaceCreateDeviceRGB();
        [DllImport(Lib)] public static extern void CGColorSpaceRelease(nint space);
        [DllImport(Lib)] public static extern nint CGBitmapContextCreate(
            nint data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint space, uint bitmapInfo);
        [DllImport(Lib)] public static extern void CGContextRelease(nint context);
        [DllImport(Lib)] public static extern void CGContextSetRGBFillColor(nint context, double r, double g, double b, double a);
        [DllImport(Lib)] public static extern void CGContextFillRect(nint context, CgRect rect);
        [DllImport(Lib)] public static extern void CGContextSelectFont(nint context, byte* name, double size, int encoding);
        [DllImport(Lib)] public static extern void CGContextSetTextDrawingMode(nint context, int mode);
        [DllImport(Lib)] public static extern void CGContextSetTextPosition(nint context, double x, double y);
        [DllImport(Lib)] public static extern CgPoint CGContextGetTextPosition(nint context);
        [DllImport(Lib)] public static extern void CGContextShowText(nint context, byte* text, nuint length);
        [DllImport(Lib)] public static extern void CGContextShowTextAtPoint(nint context, double x, double y, byte* text, nuint length);
    }
}
