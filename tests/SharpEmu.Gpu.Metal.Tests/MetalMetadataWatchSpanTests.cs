// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;
using Span = SharpEmu.Gpu.Metal.V2.MetalImageCache.MetadataWatchSpan;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// The CPU-write fast path asks whether a store meets watched DCC metadata without the
/// cache lock, by a binary search of the published ranges: sorted, disjoint, and possibly
/// touching end to begin.
/// </summary>
public sealed class MetalMetadataWatchSpanTests
{
    private static readonly Span[] Spans =
    [
        new(0x1000, 0x2000),
        new(0x2000, 0x3000),
        new(0x8000, 0xA000),
    ];

    [Fact]
    public void NoSpans_OverlapNothing()
    {
        Assert.False(MetalImageCache.AnySpanOverlaps([], 0, ulong.MaxValue));
        Assert.False(MetalImageCache.AnySpanOverlaps([], 0x1000, 8));
    }

    [Fact]
    public void EmptyRange_OverlapsNothing() =>
        Assert.False(MetalImageCache.AnySpanOverlaps(Spans, 0x1800, 0));

    [Theory]
    [InlineData(0x0000UL, 0x1000UL, false)] // ends where the first span begins
    [InlineData(0x0000UL, 0x1001UL, true)]
    [InlineData(0x0FF8UL, 0x0010UL, true)] // straddles the first begin
    [InlineData(0x1000UL, 0x0001UL, true)] // first byte
    [InlineData(0x1FFFUL, 0x0001UL, true)] // last byte of the first span
    [InlineData(0x1FFCUL, 0x0008UL, true)] // across two adjacent spans
    [InlineData(0x2000UL, 0x0001UL, true)] // first byte of the adjacent span
    [InlineData(0x2FFFUL, 0x0001UL, true)]
    [InlineData(0x3000UL, 0x0008UL, false)] // just past the adjacent pair
    [InlineData(0x3000UL, 0x5000UL, false)] // the whole gap
    [InlineData(0x3000UL, 0x5001UL, true)]
    [InlineData(0x4000UL, 0x8000UL, true)] // covers the last span
    [InlineData(0x9FFFUL, 0x0001UL, true)]
    [InlineData(0xA000UL, 0x0008UL, false)] // past the last span
    [InlineData(0x0000UL, 0xF000UL, true)] // covers all of them
    public void Range_OverlapsExactlyTheSpansItMeets(ulong address, ulong size, bool overlaps) =>
        Assert.Equal(overlaps, MetalImageCache.AnySpanOverlaps(Spans, address, size));

    [Fact]
    public void SingleSpan_EdgesAreExclusiveAtTheEnd()
    {
        Span[] one = [new(0x4000, 0x5000)];
        Assert.False(MetalImageCache.AnySpanOverlaps(one, 0x3FF8, 8));
        Assert.True(MetalImageCache.AnySpanOverlaps(one, 0x3FF8, 9));
        Assert.True(MetalImageCache.AnySpanOverlaps(one, 0x4FFF, 1));
        Assert.False(MetalImageCache.AnySpanOverlaps(one, 0x5000, 1));
    }

    // A metadata watch is added only for pages one published span does not already hold whole;
    // a range over two spans that merely touch is not taken as covered, which only costs the
    // full scan the watch would have made anyway.
    [Theory]
    [InlineData(0x1000UL, 0x2000UL, true)] // a whole span
    [InlineData(0x1400UL, 0x1800UL, true)] // inside one
    [InlineData(0x8000UL, 0xA000UL, true)]
    [InlineData(0x1800UL, 0x2800UL, false)] // across two touching spans
    [InlineData(0x0800UL, 0x1800UL, false)] // starts before the first
    [InlineData(0x2800UL, 0x3800UL, false)] // runs past the adjacent pair
    [InlineData(0x4000UL, 0x5000UL, false)] // in the gap
    [InlineData(0x9000UL, 0xB000UL, false)] // runs past the last
    public void Range_IsCoveredOnlyWhenOneSpanHoldsIt(ulong begin, ulong end, bool covered) =>
        Assert.Equal(covered, MetalImageCache.SpanCovers(Spans, begin, end));

    [Fact]
    public void NoSpans_CoverNothing() =>
        Assert.False(MetalImageCache.SpanCovers([], 0x1000, 0x2000));
}
