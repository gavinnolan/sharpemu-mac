// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// Which guest textures of a v2 draw a host-decoded Bink frame replaces: the same
// observable choice the Vulkan presenter makes.
public sealed class MetalHostMovieSelectorTests
{
    private const ulong LumaA = 0x1000;
    private const ulong ChromaA = 0x2000;
    private const ulong LumaB = 0x3000;
    private const ulong ChromaB = 0x4000;

    [Fact]
    public void NothingIsReplacedWithoutADecodedFrame()
    {
        var selector = new MetalHostMovieSelector();
        Assert.Equal(MetalHostMovieSelector.Bindings.None, selector.Find([Luma(LumaA), Chroma(ChromaA)]));

        selector.Accept(false, [], 0, 0, false, 0, string.Empty);
        Assert.False(selector.HasFrame);
        Assert.Equal(MetalHostMovieSelector.Bindings.None, selector.Find([Luma(LumaA), Chroma(ChromaA)]));
    }

    [Fact]
    public void AFullSizeLumaPairsWithItsHalfSizeChroma()
    {
        var selector = WithFrame();
        var found = selector.Find([Other(0x9000), Chroma(ChromaA, dstSelect: 0x0D1), Luma(LumaA, dstSelect: 0x8)]);

        Assert.Equal(new MetalHostMovieSelector.Bindings(2, 1), found);
        Assert.Equal(0x8u, selector.LumaDstSelect);
        Assert.Equal(0x0D1u, selector.ChromaDstSelect);
    }

    [Fact]
    public void TheLargestLumaWins()
    {
        var selector = WithFrame(3840, 2160);
        var found = selector.Find(
        [
            Luma(LumaA), Chroma(ChromaA),
            Luma(LumaB, 3840, 2160), Chroma(ChromaB, 1920, 1080),
        ]);

        Assert.Equal(new MetalHostMovieSelector.Bindings(2, 3), found);
    }

    [Theory]
    // Another aspect than the decoded frame's.
    [InlineData(1920u, 1440u, 1u, 0u, false)]
    // Smaller than 1280x720.
    [InlineData(1024u, 576u, 1u, 0u, false)]
    // Not an 8-bit plane, or not unorm.
    [InlineData(1920u, 1080u, 4u, 0u, false)]
    [InlineData(1920u, 1080u, 1u, 1u, false)]
    // Storage is written, not sampled.
    [InlineData(1920u, 1080u, 1u, 0u, true)]
    public void ALumaOutsideTheRulesIsNotReplaced(uint width, uint height, uint format, uint numberType, bool storage)
    {
        var selector = WithFrame();
        var luma = new GuestDrawTexture(LumaA, width, height, format, numberType, [], false, storage);
        var chroma = Chroma(ChromaA, width / 2, height / 2);

        Assert.Equal(MetalHostMovieSelector.Bindings.None, selector.Find([luma, chroma]));
    }

    [Fact]
    public void AChromaOfTheWrongExtentIsNotPaired()
    {
        var selector = WithFrame();
        Assert.Equal(MetalHostMovieSelector.Bindings.None, selector.Find([Luma(LumaA), Chroma(ChromaA, 1920, 1080)]));
    }

    [Fact]
    public void TheRememberedPairIgnoresAnAuxiliaryOneByOneTextureAtTheSameAddress()
    {
        var selector = WithFrame();
        Assert.True(selector.Find([Luma(LumaA, 3840, 2160), Chroma(ChromaA, 1920, 1080)]).Found);

        var found = selector.Find(
        [
            Luma(LumaA, 3840, 2160),
            Chroma(ChromaA, 1920, 1080),
            Luma(LumaA, 1, 1),
            Luma(LumaA, 1, 1),
        ]);

        Assert.Equal(new MetalHostMovieSelector.Bindings(0, 1), found);
    }

    [Fact]
    public void AnAlternateSurfacePairIsDiscoveredInsteadOfSamplingTheStaleOne()
    {
        var selector = WithFrame();
        Assert.Equal(new MetalHostMovieSelector.Bindings(0, 1), selector.Find([Luma(LumaA), Chroma(ChromaA)]));

        // The decoder flipped to its other output pair.
        Assert.Equal(new MetalHostMovieSelector.Bindings(1, 0), selector.Find([Chroma(ChromaB), Luma(LumaB)]));
        Assert.Equal(new MetalHostMovieSelector.Bindings(0, 1), selector.Find([Luma(LumaA), Chroma(ChromaA)]));
    }

    [Fact]
    public void AFailedDecodeKeepsTheLastFrameAndAnUnadvancedOneDoesNotReplaceIt()
    {
        var selector = new MetalHostMovieSelector();
        var first = new byte[16];
        selector.Accept(true, first, 1920, 1080, true, 7, "intro.bk2");

        selector.Accept(false, [], 0, 0, false, 0, string.Empty);
        Assert.Same(first, selector.FramePixels);

        selector.Accept(true, new byte[16], 1920, 1080, false, 7, "intro.bk2");
        Assert.Same(first, selector.FramePixels);
        Assert.Equal(7, selector.FrameSerial);

        var second = new byte[16];
        selector.Accept(true, second, 1920, 1080, true, 8, "intro.bk2");
        Assert.Same(second, selector.FramePixels);
        Assert.Equal(8, selector.FrameSerial);
    }

    [Fact]
    public void ANewMovieForgetsTheRememberedPair()
    {
        var selector = WithFrame();
        Assert.True(selector.Find([Luma(LumaA), Chroma(ChromaA)]).Found);
        var epoch = selector.PathEpoch;

        selector.Accept(true, new byte[16], 1920, 1080, true, 9, "next.bk2");

        Assert.NotEqual(epoch, selector.PathEpoch);
        Assert.Equal(0u, selector.LumaDstSelect);
        Assert.Equal("next.bk2", selector.FramePath);
    }

    private static MetalHostMovieSelector WithFrame(uint width = 1920, uint height = 1080)
    {
        var selector = new MetalHostMovieSelector();
        selector.Accept(true, new byte[16], width, height, true, 1, "intro.bk2");
        return selector;
    }

    private static GuestDrawTexture Luma(ulong address, uint width = 1920, uint height = 1080, uint dstSelect = 0xFAC) =>
        new(address, width, height, Format: 1, NumberType: 0, [], IsFallback: false, IsStorage: false, DstSelect: dstSelect);

    private static GuestDrawTexture Chroma(ulong address, uint width = 960, uint height = 540, uint dstSelect = 0xFAC) =>
        new(address, width, height, Format: 3, NumberType: 0, [], IsFallback: false, IsStorage: false, DstSelect: dstSelect);

    private static GuestDrawTexture Other(ulong address) =>
        new(address, 256, 256, Format: 10, NumberType: 0, [], IsFallback: false, IsStorage: false);
}
