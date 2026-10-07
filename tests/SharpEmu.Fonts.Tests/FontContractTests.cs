// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Fonts.Abstractions;
using Xunit;

namespace SharpEmu.Fonts.Tests;

public sealed class FontContractTests
{
    [Fact]
    public void CoverageOwnsItsBytesAndRejectsMismatchedGeometry()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var glyph = new FontGlyphBitmap(2, 2, -1, 3, 4, bytes, 1, true);
        Array.Fill(bytes, (byte)99);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, glyph.Coverage.ToArray());
        Assert.Throws<ArgumentException>(() => new FontGlyphBitmap(2, 3, 0, 0, 1, bytes, 1, true));
    }
    [Theory]
    [InlineData(0xD800u)] [InlineData(0x110000u)] [InlineData(uint.MaxValue)]
    public void InvalidUnicodeCannotEnterFaceContract(uint scalar) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FontCharacter.FromUnicode(scalar));
    [Fact]
    public void GlyphIdsRemainDistinctFromUnicode()
    {
        Assert.NotEqual(FontCharacter.FromGlyphId(65), FontCharacter.FromUnicode(65));
        Assert.True(FontCharacter.FromGlyphId(ushort.MaxValue).IsGlyphId);
        Assert.Equal(0x10480u, FontCharacter.FromUnicode(0x10480).Value);
    }
}
