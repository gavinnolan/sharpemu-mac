// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

// Shape/immutability controls only. These exercise no provider, native mapping
// or currentness implementation; they do not qualify retention or content.
public sealed class CpuMappedCopyObservationTests
{
    private const ulong Base = 0x7092_C80000;
    private const ulong Page = 4096;
    private const ulong Prefix = 262144;
    private const ulong SelectedOffset = 131072;

    [Fact]
    public void SelectedSourceAndCompletePrefixRemainDistinctAcrossExactSegments()
    {
        var segments = Segments(2, SelectedOffset);
        var observation = Create(new object(), segments);
        Assert.Equal(Base, observation.FootprintAddress);
        Assert.Equal(Prefix, observation.FootprintByteCount);
        Assert.Equal(Base + SelectedOffset, observation.SourceAddress);
        Assert.Equal(SelectedOffset, observation.SourceByteCount);
        Assert.Equal(segments, observation.Segments);
        Assert.Equal(SelectedOffset, observation.Segments[1].MappingBackingOffset);
    }

    [Fact]
    public void SegmentStorageIsDetachedAndRejectsMutationThroughCollectionCast()
    {
        var segments = Segments(2, SelectedOffset);
        var original = segments[0];
        var observation = Create(new object(), segments);
        segments[0] = segments[0] with { MappingIncarnation = 99, MappingBackingOffset = Page };
        Assert.Equal(original, observation.Segments[0]);
        var exposed = Assert.IsAssignableFrom<IList<CpuMappingFootprintSegment>>(observation.Segments);
        Assert.Throws<NotSupportedException>(() => exposed[0] = segments[0]);
    }

    [Fact]
    public void IdenticalPublicValuesCannotSubstituteDifferentOwnerIdentity()
    {
        var owner = new object();
        var segments = Segments(2, SelectedOffset);
        var first = Create(owner, segments);
        var reconstructed = Create(new object(), segments);
        Assert.Equal(first.Segments, reconstructed.Segments);
        Assert.True(BelongsTo(first, owner));
        Assert.False(BelongsTo(reconstructed, owner));
        Assert.False(BelongsTo(first, new object()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void SegmentLimitRejectsEmptyOrOversizedFootprintBeforeConstruction(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(new object(), Segments(count, Page),
            checked((ulong)count * Page), Base, Page));
    }

    [Fact]
    public void SixtyFourSegmentsAreSupportedAndFinalPositiveIncarnationIsMetadataOnly()
    {
        var segments = Segments(CpuMappedCopyObservation.MaximumSegments, Page);
        segments[^1] = segments[^1] with { MappingIncarnation = long.MaxValue };
        var result = Create(new object(), segments);
        Assert.Equal(64, result.Segments.Count);
        Assert.Equal(long.MaxValue, result.Segments[^1].MappingIncarnation);
        // No allocator runs here: no wrap/recycle permission follows this shape.
    }

    [Theory]
    [InlineData("hole")]
    [InlineData("overlap")]
    [InlineData("incomplete")]
    [InlineData("coverage-overrun")]
    [InlineData("mapping-overflow")]
    [InlineData("backing-overrun")]
    [InlineData("backing-contradiction")]
    [InlineData("incarnation-zero")]
    [InlineData("incarnation-negative")]
    [InlineData("backing-zero")]
    [InlineData("page-zero")]
    [InlineData("page-non-power-two")]
    [InlineData("mapping-misaligned")]
    [InlineData("offset-misaligned")]
    [InlineData("read-revoked")]
    [InlineData("unknown-access")]
    public void MalformedMappingFootprintsDecline(string failure)
    {
        var values = Segments(2, SelectedOffset);
        switch (failure)
        {
            case "hole": values[1] = values[1] with { CoveredAddress = values[1].CoveredAddress + 1 }; break;
            case "overlap": values[1] = values[1] with { CoveredAddress = values[1].CoveredAddress - 1 }; break;
            case "incomplete": values[1] = values[1] with { CoveredByteCount = SelectedOffset - 1 }; break;
            case "coverage-overrun": values[1] = values[1] with { CoveredByteCount = SelectedOffset + 1 }; break;
            case "mapping-overflow": values[0] = values[0] with { MappingByteCount = ulong.MaxValue }; break;
            case "backing-overrun": values[1] = values[1] with { MappingBackingOffset = Prefix }; break;
            case "backing-contradiction": values[1] = values[1] with { BackingLogicalByteCount = Prefix + Page }; break;
            case "incarnation-zero": values[1] = values[1] with { MappingIncarnation = 0 }; break;
            case "incarnation-negative": values[1] = values[1] with { MappingIncarnation = -1 }; break;
            case "backing-zero": values[1] = values[1] with { BackingIdentity = 0 }; break;
            case "page-zero": values[1] = values[1] with { ProviderPageSize = 0 }; break;
            case "page-non-power-two": values[1] = values[1] with { ProviderPageSize = 3 }; break;
            case "mapping-misaligned": values[0] = values[0] with { MappingAddress = Base - 1, MappingByteCount = SelectedOffset + 1 }; break;
            case "offset-misaligned": values[1] = values[1] with { MappingBackingOffset = Page + 1 }; break;
            case "read-revoked": values[1] = values[1] with { GuestAccess = GuestPageProtection.Write }; break;
            case "unknown-access": values[1] = values[1] with { GuestAccess = (GuestPageProtection)9 }; break;
        }
        Assert.Throws<ArgumentException>(() => Create(new object(), values));
    }

    [Theory]
    [InlineData("before-footprint")]
    [InlineData("after-footprint")]
    [InlineData("empty")]
    [InlineData("overflow")]
    [InlineData("larger-than-span")]
    public void InvalidSourceCannotBecomeCompletedCopyMetadata(string failure)
    {
        var address = Base + SelectedOffset;
        var size = SelectedOffset;
        var footprint = Prefix;
        var segments = Segments(2, SelectedOffset);
        switch (failure)
        {
            case "before-footprint": address = Base - 1; break;
            case "after-footprint": address = Base + Prefix; break;
            case "empty": size = 0; break;
            case "overflow": address = ulong.MaxValue - size + 1; break;
            case "larger-than-span":
                size = (ulong)int.MaxValue + 1; address = Base; footprint = size;
                segments = [new(Base, size, Base, size, 1, 7, size, 0, Page, GuestPageProtection.Read)];
                break;
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(new object(), segments, footprint, address, size));
    }

    private static CpuMappingFootprintSegment[] Segments(int count, ulong size) =>
        Enumerable.Range(0, count).Select(index => new CpuMappingFootprintSegment(
            Base + (ulong)index * size, size, Base + (ulong)index * size, size,
            index + 1, 7, checked((ulong)count * size), (ulong)index * size,
            Page, GuestPageProtection.Read | GuestPageProtection.Write)).ToArray();

    private static CpuMappedCopyObservation Create(object owner, CpuMappingFootprintSegment[] segments,
        ulong prefix = Prefix, ulong sourceAddress = Base + SelectedOffset, ulong sourceByteCount = SelectedOffset)
    {
        var constructor = typeof(CpuMappedCopyObservation).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        try { return (CpuMappedCopyObservation)constructor.Invoke([owner, Base, prefix, sourceAddress, sourceByteCount, segments]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static bool BelongsTo(CpuMappedCopyObservation observation, object owner) =>
        (bool)typeof(CpuMappedCopyObservation).GetMethod("BelongsTo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(observation, [owner])!;
}
