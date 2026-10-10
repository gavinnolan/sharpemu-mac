// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    // Synthetic raw register controls normalized by the actual shared builder.
    // They are not captured Superliminal CB words or native stage authority.
    private static ColorTargetWords RegularColorWords(ulong address) =>
        new(address, 0, 0x40730, 0, 31u | (31u << 14), (1u << 24) | (27u << 14), 72, 0, 0, 0, 0);
    private static GuestRenderTarget RegularColorTarget(ColorTargetWords words, ColorTargetResolution normalized) =>
        new(words.BaseAddress, normalized.Extent.Width, normalized.Extent.Height,
            (uint)words.Layout, (uint)words.NumberType, ComponentSwap: (uint)words.Order,
            Registers: words, TileMode: (uint)words.TileMode);

    [Fact]
    public void RegularColorMappingOwnsActualNormalizedLayoutAndSoleOriginalProbe()
    {
        ActualArchitecture(); using var memory = new DisplayLedgerMemory();
        var words = RegularColorWords(Address);
        var normalized = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
        var target = RegularColorTarget(words, normalized);
        var layout = MetalRegularColorPhysicalPlacement.Layout.Describe(normalized, words, target);
        var before = memory.Expected;
        var original = MetalRegularColorPhysicalPlacement.Observation.Capture(memory, layout, 17, () => true);
        Assert.Same(memory, original.Memory); Assert.Same(memory, original.Source); Assert.Same(layout, original.Placement);
        Assert.Equal(17L, original.Session); Assert.Equal(words, layout.Registers);
        Assert.Equal(normalized.Request.Description.Data.Address, layout.Address);
        Assert.Equal(normalized.Request.Description.Data.Size, layout.ByteCount);
        Assert.Equal(layout.Address, original.Mapping.FootprintAddress);
        Assert.Equal(layout.ByteCount, original.Mapping.FootprintByteCount);
        Assert.Equal(layout.Address, original.Mapping.SourceAddress); Assert.Equal(8UL, original.Mapping.SourceByteCount);
        Assert.NotEmpty(original.Spans.ToArray());
        Assert.Equal(layout.ByteCount, original.Spans.ToArray().Aggregate(0UL, (sum, span) => sum + span.ByteCount));
        Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads); Assert.Equal(before, memory.Expected);
        // Mutating the caller's value copy cannot alter owned normalized facts.
        var mutated = normalized.Request;
        mutated.Description.Data = new(Address + 64, 8);
        mutated.Description.MipLayout[0].Size = 8;
        normalized = normalized with { Request = mutated };
        Assert.Equal(original.Mapping.FootprintByteCount, layout.ByteCount);
        Assert.NotEqual(8UL, layout.MipBytes); Assert.True(layout.MatchesTarget(target));
        Assert.True(memory.IsMappedCopyObservationCurrent(original.Mapping));
        Assert.Equal(0, memory.Backing.ActiveAccesses);
    }

    [Theory]
    [InlineData("mips")] [InlineData("layers")] [InlineData("samples")]
    [InlineData("volume")] [InlineData("metadata")] [InlineData("unknown-tile")]
    [InlineData("wrong-span")] [InlineData("wrong-pitch")] [InlineData("extra-mip")]
    [InlineData("wrong-view")] [InlineData("wrong-target")]
    public void RegularColorMalformedOrUnsupportedNormalizationRefusesBeforeAnyProbe(string variant)
    {
        ActualArchitecture(); using var memory = new DisplayLedgerMemory();
        var words = RegularColorWords(Address);
        var normalized = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
        var target = RegularColorTarget(words, normalized);
        var request = normalized.Request;
        switch (variant)
        {
            case "mips": words = words with { Attrib2 = words.Attrib2 | (1u << 28) }; break;
            case "layers": words = words with { View = 1u << 13 }; break;
            case "samples": words = words with { Attrib = (1u << 12) | (1u << 15) }; break;
            case "volume": words = words with { Attrib3 = (words.Attrib3 & ~(3u << 24)) | (2u << 24) }; break;
            case "metadata": words = words with { Info = words.Info | (1u << 28), DccAddress = Address + 65536 }; break;
            case "unknown-tile": words = words with { Attrib3 = (words.Attrib3 & ~(31u << 14)) | (31u << 14) }; break;
            case "wrong-span": request.Description.Data = new(Address, 8); normalized = normalized with { BackingSize = 8 }; break;
            case "wrong-pitch": request.Description.Pitch++; break;
            case "extra-mip": request.Description.MipLayout[1].Size = 8; break;
            case "wrong-view": request.View = request.View with { BaseLevel = 1 }; break;
            case "wrong-target": target = target with { Address = Address + 64 }; break;
        }
        normalized = normalized with { Request = request };
        Assert.Throws<NotSupportedException>(() => MetalRegularColorPhysicalPlacement.Layout.Describe(normalized, words, target));
        Assert.Equal(0, memory.Copies); Assert.Equal(0, memory.Reads); Assert.Equal(0, memory.Backing.ActiveAccesses);
    }

    [Theory]
    [InlineData("mapping-replaced")] [InlineData("owner-changed")] [InlineData("owner-missing-before")]
    public void RegularColorCaptureNeverRecapturesChangedFirstMappingOrOwner(string variant)
    {
        ActualArchitecture(); using var memory = new DisplayLedgerMemory();
        var words = RegularColorWords(Address);
        var normalized = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
        var layout = MetalRegularColorPhysicalPlacement.Layout.Describe(normalized, words, RegularColorTarget(words, normalized));
        var current = variant != "owner-missing-before";
        if (variant == "mapping-replaced") memory.AfterCopy = memory.Map;
        if (variant == "owner-changed") memory.AfterCopy = () => current = false;
        Assert.Throws<NotSupportedException>(() => MetalRegularColorPhysicalPlacement.Observation.Capture(memory, layout, 17, () => current));
        Assert.Equal(variant == "owner-missing-before" ? 0 : 1, memory.Copies);
        Assert.Equal(0, memory.Reads); Assert.Equal(0, memory.Backing.ActiveAccesses);
        if (variant == "mapping-replaced") Assert.False(memory.IsMappedCopyObservationCurrent(memory.First!));
    }

    [Fact]
    public void RegularColorVirtualDisjointViewsRetainActualPhysicalAlias()
    {
        ActualArchitecture(); using var memory = new DisplayLedgerMemory();
        var aliasAddress = Address + DisplayLedgerMemory.ByteCount;
        lock (memory.Gate)
        {
            var alias = memory.Ledger.PrepareView(memory.Backing, aliasAddress, DisplayLedgerMemory.ByteCount, 0, HostPageProtection.ReadWrite);
            Assert.NotNull(alias); Assert.True(memory.Ledger.BeforeNativeEffect(aliasAddress, DisplayLedgerMemory.ByteCount));
            memory.Ledger.PublishView(alias!);
        }
        MetalRegularColorPhysicalPlacement.Observation Capture(ulong address)
        {
            var words = RegularColorWords(address);
            var normalized = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
            var layout = MetalRegularColorPhysicalPlacement.Layout.Describe(normalized, words, RegularColorTarget(words, normalized));
            return MetalRegularColorPhysicalPlacement.Observation.Capture(memory, layout, 17, () => true);
        }
        var first = Capture(Address); var aliasOriginal = Capture(aliasAddress);
        Assert.True(first.Placement.Address + first.Placement.ByteCount <= aliasOriginal.Placement.Address);
        Assert.True(memory.IsMappedCopyObservationCurrent(first.Mapping)); Assert.True(memory.IsMappedCopyObservationCurrent(aliasOriginal.Mapping));
        Assert.True((bool)Presenter.GetMethod("PhysicalMappingOverlaps", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [first.Mapping, aliasOriginal.Mapping])!);
        var firstSpans = first.Spans.ToArray(); var aliasSpans = aliasOriginal.Spans.ToArray();
        Assert.True(firstSpans.Any(a => aliasSpans.Any(b => MetalRegularColorPhysicalPlacement.Overlap(a, b))));
        Assert.Equal(2, memory.Copies); Assert.Equal(0, memory.Reads); Assert.Equal(0, memory.Backing.ActiveAccesses);
    }

    [Theory]
    [InlineData("empty")] [InlineData("gap")] [InlineData("short")]
    [InlineData("outside-mapping")] [InlineData("outside-backing")] [InlineData("read-only")]
    [InlineData("mapping-wrap")] [InlineData("identity-zero")] [InlineData("incarnation-zero")]
    [InlineData("page-zero")] [InlineData("internal-physical-alias")]
    public void RegularColorPhysicalSpanValidationRefusesMalformedMetadataWithoutReceiptAuthority(string variant)
    {
        ActualArchitecture(); using var memory = new DisplayLedgerMemory();
        var words = RegularColorWords(Address);
        var normalized = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
        var layout = MetalRegularColorPhysicalPlacement.Layout.Describe(normalized, words, RegularColorTarget(words, normalized));
        var original = MetalRegularColorPhysicalPlacement.Observation.Capture(memory, layout, 17, () => true);
        // Refusal-only scalar seam. HLE's real observation constructor also
        // rejects malformed coverage; do not forge a new provider receipt or
        // substitute a fake currentness result to reach the lower validator.
        var segments = original.Mapping.Segments.ToArray(); var first = segments[0];
        var walkBytes = layout.ByteCount;
        switch (variant)
        {
            case "empty": segments = []; break;
            case "gap": segments[0] = first with { CoveredAddress = first.CoveredAddress + 1 }; break;
            case "short": segments[^1] = segments[^1] with { CoveredByteCount = segments[^1].CoveredByteCount - 1 }; break;
            case "outside-mapping": segments[0] = first with { CoveredByteCount = first.MappingByteCount + 1 }; break;
            case "outside-backing": segments[0] = first with { BackingLogicalByteCount = first.MappingByteCount - 1 }; break;
            case "read-only": segments[0] = first with { GuestAccess = GuestPageProtection.Read }; break;
            case "mapping-wrap": segments[0] = first with { MappingAddress = ulong.MaxValue - 3, MappingByteCount = 8 }; break;
            case "identity-zero": segments[0] = first with { BackingIdentity = 0 }; break;
            case "incarnation-zero": segments[0] = first with { MappingIncarnation = 0 }; break;
            case "page-zero": segments[0] = first with { ProviderPageSize = 0 }; break;
            case "internal-physical-alias":
                // A pure two-page geometry control, independent of the synthetic
                // texture's normalized size. First prove all earlier checks can
                // pass, then change ONLY the second physical offset to alias.
                var page = first.ProviderPageSize;
                Assert.True(page >= 8); Assert.Equal(0UL, Address % page);
                Assert.True(page <= MetalRegularColorPhysicalPlacement.MaximumBytes / 2);
                walkBytes = checked(page * 2);
                Assert.True(walkBytes <= first.BackingLogicalByteCount);
                segments = [first with { MappingAddress = Address, MappingByteCount = page, CoveredAddress = Address,
                    CoveredByteCount = page, MappingBackingOffset = 0 },
                    first with { MappingAddress = Address + page, MappingByteCount = page, CoveredAddress = Address + page,
                    CoveredByteCount = page, MappingBackingOffset = page }];
                Assert.Equal(2, MetalRegularColorPhysicalPlacement.DescribeSpans(Address, walkBytes, segments).Length);
                segments[1] = segments[1] with { MappingBackingOffset = 0 };
                break;
        }
        var refusal = Assert.Throws<NotSupportedException>(() => MetalRegularColorPhysicalPlacement.DescribeSpans(layout.Address, walkBytes, segments));
        if (variant == "internal-physical-alias") Assert.Contains("internally alias", refusal.Message);
        Assert.True(memory.IsMappedCopyObservationCurrent(original.Mapping));
        Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads); Assert.Equal(0, memory.Backing.ActiveAccesses);
    }
}
