// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using SharpEmu.Fonts.Abstractions;

namespace SharpEmu.Fonts.CoreTextMac;

/// <summary>Guest font rendering using only CoreText, CoreGraphics and CoreFoundation.</summary>
public sealed class CoreTextFontFactory : IFontFactory
{
    public string BackendName => "coretext";
    private static void RequireMac()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("CoreText fonts require macOS.");
    }
    public IFontFace CreateSystemFace(FontStyle style)
    {
        RequireMac();
        if ((style & ~(FontStyle.Bold | FontStyle.Italic)) != 0) throw new ArgumentOutOfRangeException(nameof(style));
        var font = Native.CTFontCreateUIFontForLanguage(2, 16, 0); // kCTFontUIFontSystem
        if (font == 0) throw new InvalidOperationException("CoreText could not create a system face.");
        var traits = ((style & FontStyle.Bold) != 0 ? 2u : 0) | ((style & FontStyle.Italic) != 0 ? 1u : 0);
        if (traits != 0)
        {
            var styled = Native.CTFontCreateCopyWithSymbolicTraits(font, 16, 0, traits, 3);
            Native.CFRelease(font);
            if (styled == 0) throw new NotSupportedException("The system font cannot satisfy the requested bold/italic traits.");
            font = styled;
        }
        try { return new Face(font, 0); }
        catch { Native.CFRelease(font); throw; }
    }
    public unsafe IFontFace? CreateMemoryFace(ReadOnlySpan<byte> bytes, uint collectionIndex)
    {
        RequireMac();
        if (bytes.IsEmpty || bytes.Length > 64 * 1024 * 1024) return null;
        nint data;
        fixed (byte* pointer = bytes) data = Native.CFDataCreate(0, pointer, bytes.Length);
        if (data == 0) throw new OutOfMemoryException("CoreFoundation could not own the font data.");
        nint descriptors = 0;
        try
        {
            descriptors = Native.CTFontManagerCreateFontDescriptorsFromData(data);
            if (descriptors == 0 || collectionIndex >= (ulong)Native.CFArrayGetCount(descriptors)) return null;
            var descriptor = Native.CFArrayGetValueAtIndex(descriptors, (nint)collectionIndex);
            var font = Native.CTFontCreateWithFontDescriptor(descriptor, 16, 0);
            if (font == 0) return null;
            Face result;
            try { result = new Face(font, data); }
            catch { Native.CFRelease(font); throw; }
            data = 0;
            return result;
        }
        finally
        {
            if (descriptors != 0) Native.CFRelease(descriptors);
            if (data != 0) Native.CFRelease(data);
        }
    }

    private sealed class Face(nint font, nint ownedData) : IFontFace
    {
        private readonly object gate = new();
        private nint face = font;
        private nint data = ownedData;
        private readonly Dictionary<(FontCharacter, float, float), FontGlyphBitmap> cache = [];
        private long cachedBytes;
        private nint Sized(float width, float height)
        {
            FontScale.Validate(width, height);
            ObjectDisposedException.ThrowIf(face == 0, this);
            var sized = Native.CTFontCreateCopyWithAttributes(face, height, 0, 0);
            if (sized == 0) throw new InvalidOperationException("CoreText could not size the selected face.");
            return sized;
        }
        public FontMetrics GetMetrics(float pixelWidth, float pixelHeight)
        {
            lock (gate)
            {
                var sized = Sized(pixelWidth, pixelHeight);
                try { return new(-(float)Native.CTFontGetAscent(sized), (float)Native.CTFontGetDescent(sized), (float)Native.CTFontGetLeading(sized)); }
                finally { Native.CFRelease(sized); }
            }
        }
        public FontGlyphBitmap GetGlyph(FontCharacter character, float pixelWidth, float pixelHeight)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(face == 0, this);
                FontScale.Validate(pixelWidth, pixelHeight);
                var key = (character, pixelWidth, pixelHeight);
                if (cache.TryGetValue(key, out var existing)) return existing;
                var glyph = Rasterize(character, pixelWidth, pixelHeight);
                if (cache.Count >= 1024 || cachedBytes + glyph.Coverage.Length > 16 * 1024 * 1024)
                { cache.Clear(); cachedBytes = 0; }
                cache.Add(key, glyph); cachedBytes += glyph.Coverage.Length;
                return glyph;
            }
        }
        private unsafe FontGlyphBitmap Rasterize(FontCharacter character, float width, float height)
        {
            var sized = Sized(width, height);
            nint text = 0;
            nint fallback = 0;
            try
            {
                ushort glyph;
                bool usedFallback = false;
                if (character.IsGlyphId)
                    glyph = character.Value < (ulong)Native.CTFontGetGlyphCount(sized) ? (ushort)character.Value : (ushort)0;
                else
                {
                    Span<char> chars = stackalloc char[2];
                    var count = new System.Text.Rune((int)character.Value).EncodeToUtf16(chars);
                    glyph = MapGlyph(sized, chars[..count]);
                    if (glyph == 0)
                    {
                        fixed (char* charsPointer = chars) text = Native.CFStringCreateWithCharacters(0, charsPointer, count);
                        if (text == 0) throw new OutOfMemoryException("CoreFoundation could not own the character string.");
                        fallback = Native.CTFontCreateForString(sized, text, new Native.Range(0, count));
                        if (fallback != 0)
                        {
                            var candidate = MapGlyph(fallback, chars[..count]);
                            if (candidate != 0) { glyph = candidate; usedFallback = true; }
                        }
                    }
                }
                var selected = usedFallback ? fallback : sized;
                var bounds = Native.CTFontGetBoundingRectsForGlyphs(selected, 0, &glyph, null, 1);
                var advance = Native.CTFontGetAdvancesForGlyphs(selected, 0, &glyph, null, 1) * width / height;
                var xScale = width / height;
                if (!double.IsFinite(advance) || !double.IsFinite(bounds.Origin.X) ||
                    !double.IsFinite(bounds.Origin.Y) || !double.IsFinite(bounds.Size.Width) ||
                    !double.IsFinite(bounds.Size.Height) || bounds.Size.Width < 0 || bounds.Size.Height < 0 ||
                    Math.Abs(bounds.Origin.X * xScale) > int.MaxValue / 4 ||
                    Math.Abs((bounds.Origin.X + bounds.Size.Width) * xScale) > int.MaxValue / 4 ||
                    Math.Abs(bounds.Origin.Y) > int.MaxValue / 4 ||
                    Math.Abs(bounds.Origin.Y + bounds.Size.Height) > int.MaxValue / 4)
                    throw new NotSupportedException("CoreText glyph geometry is invalid or exceeds integer bitmap coordinates.");
                var left = (int)Math.Floor(bounds.Origin.X * xScale);
                var right = (int)Math.Ceiling((bounds.Origin.X + bounds.Size.Width) * xScale);
                var bottom = (int)Math.Floor(bounds.Origin.Y);
                var top = (int)Math.Ceiling(bounds.Origin.Y + bounds.Size.Height);
                var bitmapWidth = Math.Max(0, right - left);
                var bitmapHeight = Math.Max(0, top - bottom);
                if (bitmapWidth == 0 || bitmapHeight == 0)
                    return new(0, 0, left, top, (float)advance, [], glyph, glyph != 0, usedFallback);
                if ((long)bitmapWidth * bitmapHeight > 16 * 1024 * 1024) throw new NotSupportedException("CoreText glyph bitmap exceeds the coverage budget.");
                var coverage = new byte[checked(bitmapWidth * bitmapHeight)];
                fixed (byte* pixels = coverage)
                {
                    var context = Native.CGBitmapContextCreate(pixels, (nuint)bitmapWidth, (nuint)bitmapHeight, 8, (nuint)bitmapWidth, 0, 7); // kCGImageAlphaOnly
                    if (context == 0) throw new InvalidOperationException("CoreGraphics could not create an Alpha8 glyph context.");
                    try
                    {
                        Native.CGContextSetGrayFillColor(context, 1, 1);
                        Native.CGContextSetShouldAntialias(context, true);
                        Native.CGContextSetShouldSmoothFonts(context, false);
                        Native.CGContextTranslateCTM(context, -left, -bottom);
                        Native.CGContextScaleCTM(context, xScale, 1);
                        var position = new Native.Point(0, 0);
                        Native.CTFontDrawGlyphs(selected, &glyph, &position, 1, context);
                    }
                    finally { Native.CGContextRelease(context); }
                }
                // CoreGraphics exposes these Alpha8 scanlines top-to-bottom.
                // The asymmetric Arial F native regression checks this contract
                // independently of the glyph-ID/Unicode equality fixture.
                return new(bitmapWidth, bitmapHeight, left, top, (float)advance, coverage, glyph, glyph != 0, usedFallback);
            }
            finally
            {
                if (fallback != 0) Native.CFRelease(fallback);
                if (text != 0) Native.CFRelease(text);
                Native.CFRelease(sized);
            }
        }
        private static unsafe ushort MapGlyph(nint font, ReadOnlySpan<char> chars)
        {
            Span<ushort> glyphs = stackalloc ushort[2];
            glyphs.Clear();
            fixed (char* characters = chars)
            fixed (ushort* mapped = glyphs)
                Native.CTFontGetGlyphsForCharacters(font, characters, mapped, chars.Length);
            return glyphs[0] != 0 ? glyphs[0] : glyphs[1];
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (face != 0) Native.CFRelease(face);
                if (data != 0) Native.CFRelease(data);
                face = data = 0; cache.Clear(); cachedBytes = 0;
            }
        }
    }
}
