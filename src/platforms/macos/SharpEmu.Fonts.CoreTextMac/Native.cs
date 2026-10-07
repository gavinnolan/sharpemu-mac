// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;

namespace SharpEmu.Fonts.CoreTextMac;

// SDK declarations use CGFloat/CFIndex on the supported 64-bit hosts. No
// Objective-C message dispatch or backend-native type is exposed publicly.
internal static unsafe class Native
{
    private const string CT = "/System/Library/Frameworks/CoreText.framework/CoreText";
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Range(nint Location, nint Length);
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Point(double X, double Y);
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Size(double Width, double Height);
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct Rect(Point Origin, Size Size);
    [DllImport(CF)] internal static extern void CFRelease(nint value);
    [DllImport(CF)] internal static extern nint CFDataCreate(nint allocator, byte* bytes, nint length);
    [DllImport(CF)] internal static extern nint CFStringCreateWithCharacters(nint allocator, char* chars, nint length);
    [DllImport(CF)] internal static extern nint CFArrayGetCount(nint array);
    [DllImport(CF)] internal static extern nint CFArrayGetValueAtIndex(nint array, nint index);
    [DllImport(CT)] internal static extern nint CTFontCreateUIFontForLanguage(uint type, double size, nint language);
    [DllImport(CT)] internal static extern nint CTFontCreateCopyWithSymbolicTraits(nint font, double size, nint matrix, uint desired, uint mask);
    [DllImport(CT)] internal static extern nint CTFontCreateCopyWithAttributes(nint font, double size, nint matrix, nint attributes);
    [DllImport(CT)] internal static extern nint CTFontManagerCreateFontDescriptorsFromData(nint data);
    [DllImport(CT)] internal static extern nint CTFontCreateWithFontDescriptor(nint descriptor, double size, nint matrix);
    [DllImport(CT)] internal static extern nint CTFontCreateForString(nint font, nint text, Range range);
    [DllImport(CT)] internal static extern double CTFontGetAscent(nint font);
    [DllImport(CT)] internal static extern double CTFontGetDescent(nint font);
    [DllImport(CT)] internal static extern double CTFontGetLeading(nint font);
    [DllImport(CT)] internal static extern nint CTFontGetGlyphCount(nint font);
    [DllImport(CT)] [return: MarshalAs(UnmanagedType.I1)] internal static extern bool CTFontGetGlyphsForCharacters(nint font, char* characters, ushort* glyphs, nint count);
    [DllImport(CT)] internal static extern Rect CTFontGetBoundingRectsForGlyphs(nint font, uint orientation, ushort* glyphs, Rect* bounds, nint count);
    [DllImport(CT)] internal static extern double CTFontGetAdvancesForGlyphs(nint font, uint orientation, ushort* glyphs, Size* advances, nint count);
    [DllImport(CT)] internal static extern void CTFontDrawGlyphs(nint font, ushort* glyphs, Point* positions, nint count, nint context);
    [DllImport(CG)] internal static extern nint CGBitmapContextCreate(byte* data, nuint width, nuint height, nuint bits, nuint bytesPerRow, nint colorSpace, uint bitmapInfo);
    [DllImport(CG)] internal static extern void CGContextRelease(nint context);
    [DllImport(CG)] internal static extern void CGContextSetGrayFillColor(nint context, double gray, double alpha);
    [DllImport(CG)] internal static extern void CGContextTranslateCTM(nint context, double x, double y);
    [DllImport(CG)] internal static extern void CGContextScaleCTM(nint context, double x, double y);
    [DllImport(CG)] internal static extern void CGContextSetShouldAntialias(nint context, [MarshalAs(UnmanagedType.I1)] bool enabled);
    [DllImport(CG)] internal static extern void CGContextSetShouldSmoothFonts(nint context, [MarshalAs(UnmanagedType.I1)] bool enabled);
}
