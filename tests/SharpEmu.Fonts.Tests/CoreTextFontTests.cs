// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Fonts.Abstractions;
using SharpEmu.Fonts.CoreTextMac;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Fonts.Tests;

public sealed class CoreTextFontTests(ITestOutputHelper output)
{
    private static bool NativeRequested()
    {
        var requested = Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_NATIVE_FONTS") == "1";
        if (requested) Assert.True(OperatingSystem.IsMacOS(), "Native font qualification requires macOS; no fallback adapter.");
        return requested;
    }
    private static byte[] Arial() => File.ReadAllBytes("/System/Library/Fonts/Supplemental/Arial.ttf");
    [Theory]
    [InlineData(FontStyle.Normal)] [InlineData(FontStyle.Bold)] [InlineData(FontStyle.Italic)] [InlineData(FontStyle.Bold | FontStyle.Italic)]
    public void SystemFaceRendersOwnedAlphaCoverageAndMetrics(FontStyle style)
    {
        if (!NativeRequested()) return;
        using var face = new CoreTextFontFactory().CreateSystemFace(style);
        var metrics = face.GetMetrics(32, 32);
        Assert.True(metrics.Ascent < 0 && metrics.Descent >= 0 && metrics.Leading >= 0);
        var glyph = face.GetGlyph(FontCharacter.FromUnicode('A'), 32, 32);
        Assert.True(glyph.HasGlyph && glyph.Width > 0 && glyph.Height > 0 && glyph.Advance > 0);
        Assert.Contains(glyph.Coverage.ToArray(), value => value > 0);
        Assert.Contains(glyph.Coverage.ToArray(), value => value == 0);
        var owned = glyph.Coverage.ToArray();
        face.Dispose(); face.Dispose();
        Assert.Equal(owned, glyph.Coverage.ToArray());
        Assert.Throws<ObjectDisposedException>(() => face.GetGlyph(FontCharacter.FromUnicode('A'), 32, 32));
        output.WriteLine($"CoreText actual system style={style}: glyph={glyph.GlyphId}; {glyph.Width}x{glyph.Height}; advance={glyph.Advance}; ascent={metrics.Ascent}; owned coverage survives disposal.");
    }
    [Fact]
    public void OwnedMemoryFaceGlyphIdAndAnisotropicScalePreserveSelectedFace()
    {
        if (!NativeRequested()) return;
        var bytes = Arial();
        using var face = new CoreTextFontFactory().CreateMemoryFace(bytes, 0);
        Assert.NotNull(face);
        Array.Clear(bytes);
        var scalar = face.GetGlyph(FontCharacter.FromUnicode('F'), 48, 48);
        var byId = face.GetGlyph(FontCharacter.FromGlyphId(scalar.GlyphId), 48, 48);
        Assert.False(byId.UsedFallback);
        Assert.Equal(scalar.Coverage.ToArray(), byId.Coverage.ToArray());
        var wide = face.GetGlyph(FontCharacter.FromUnicode('F'), 96, 48);
        Assert.Equal(scalar.Advance * 2, wide.Advance, 3);
        Assert.Equal(scalar.Height, wide.Height);
        Assert.InRange(wide.Width, scalar.Width * 2 - 2, scalar.Width * 2 + 2);
        var normalMetrics = face.GetMetrics(48, 48);
        Assert.Equal(normalMetrics, face.GetMetrics(96, 48));
        // Arial F has its widest bar above its shorter middle bar and stem.
        // This checks the guest top-down scanline contract, not just nonzero bytes.
        var pixels = scalar.Coverage.ToArray();
        var top = Enumerable.Range(0, scalar.Height / 4).Sum(y => pixels.Skip(y * scalar.Width).Take(scalar.Width).Sum(b => (int)b));
        var bottom = Enumerable.Range(scalar.Height * 3 / 4, scalar.Height - scalar.Height * 3 / 4).Sum(y => pixels.Skip(y * scalar.Width).Take(scalar.Width).Sum(b => (int)b));
        Assert.True(top > bottom * 1.3, $"Expected top bar coverage > bottom stem: top={top}, bottom={bottom}.");
        output.WriteLine($"CoreText actual owned Arial bytes overwritten after creation; glyph-ID coverage exact; anisotropic advance={scalar.Advance}->{wide.Advance}; top={top}>bottom={bottom}.");
    }
    [Fact]
    public void SupplementaryUnicodeAndFallbackAreCoverageBasedWithoutGlyphIdFallback()
    {
        if (!NativeRequested()) return;
        var factory = new CoreTextFontFactory();
        using var osmanya = factory.CreateMemoryFace(File.ReadAllBytes("/System/Library/Fonts/Supplemental/NotoSansOsmanya-Regular.ttf"), 0);
        Assert.NotNull(osmanya);
        var supplementary = osmanya.GetGlyph(FontCharacter.FromUnicode(0x10480), 32, 32);
        Assert.True(supplementary.HasGlyph);
        Assert.False(supplementary.UsedFallback);
        Assert.Contains(supplementary.Coverage.ToArray(), value => value > 0);
        var fallback = osmanya.GetGlyph(FontCharacter.FromUnicode('A'), 32, 32);
        Assert.True(fallback.HasGlyph && fallback.UsedFallback);
        var missing = osmanya.GetGlyph(FontCharacter.FromGlyphId(ushort.MaxValue), 32, 32);
        Assert.False(missing.HasGlyph || missing.UsedFallback);
        output.WriteLine($"CoreText actual supplementary U+10480 glyph={supplementary.GlyphId}; coverage fallback Latin glyph={fallback.GlyphId}; out-of-range glyph-ID did not fallback.");
    }
    [Fact]
    public void InvalidMemoryDataAndCollectionIndexNeverSubstituteSystemFont()
    {
        if (!NativeRequested()) return;
        var factory = new CoreTextFontFactory();
        Assert.Null(factory.CreateMemoryFace(new byte[32], 0));
        Assert.Null(factory.CreateMemoryFace(Arial(), 1));
        Assert.Null(factory.CreateMemoryFace(Arial(), uint.MaxValue));
        using var collection = factory.CreateMemoryFace(File.ReadAllBytes("/System/Library/Fonts/Helvetica.ttc"), 0);
        // A present valid collection is required evidence rather than an ignored missing file.
        Assert.NotNull(collection);
        Assert.True(collection.GetGlyph(FontCharacter.FromUnicode('A'), 32, 32).HasGlyph);
        output.WriteLine("CoreText actual invalid font/index rejection and TTC face0 rendering completed.");
    }
}
