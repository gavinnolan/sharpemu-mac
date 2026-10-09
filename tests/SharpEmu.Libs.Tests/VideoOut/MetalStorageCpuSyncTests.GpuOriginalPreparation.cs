// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.Core.Cpu;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed partial class MetalStorageCpuSyncTests
{
    private static GuestRenderTarget ProjectGpuViewColor(ICpuMemory memory)
    {
        var words = BorrowColorWords;
        var resolution = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
        return MetalColorMipPlacement.Project(memory, resolution, words,
            new GuestRenderTarget(Address, 512, 512, (uint)words.Layout, (uint)words.NumberType, Registers: words, TileMode: 27));
    }

    [Fact]
    public void TrackedColorPreparationKeepsActualOriginalProviderThroughQueueCancellation()
    {
        ActualArchitecture(); using var state = new BorrowColorState(); var memory = new BorrowColorMemory();
        var thread = new TrackedCpuMemory(memory); var nested = new TrackedCpuMemory(new TrackedCpuMemory(memory));
        MetalVideoPresenter.AttachGuestMemory(thread);
        var target = ProjectGpuViewColor(nested);
        try
        {
            Assert.True(MetalColorMipPlacement.TryGet(target, out var placement)); Assert.Same(memory, placement.Memory);
            var original = ((IEnumerable)Field("_nativeColorOriginals").GetValue(null)!).Cast<object>().Single();
            Assert.Same(memory, original.GetType().GetField("Source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original));
            SubmitBorrowColor(target);
            Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.PlainReads);
            Assert.True((long)Field("_enqueuedGuestWorkSequence").GetValue(null)! > 0);
            MetalVideoPresenter.ReleasePreparedNativeColorTarget(target); Assert.Equal(0, memory.Releases);
            InvokeVolume("CompletePresenterShutdown"); Assert.Equal(1, memory.Releases);
            Assert.Equal(0L, Field("_completedGuestWorkSequence").GetValue(null));
            Assert.Null(thread.LastFailure); Assert.Null(nested.LastFailure);
        }
        finally { MetalVideoPresenter.ReleasePreparedNativeColorTarget(target); }
        Assert.Equal(1, memory.Releases);
    }

    [Fact]
    public void DifferentResolvedColorRootCannotAdmitOriginalAndReturnsItsOnlyWatch()
    {
        ActualArchitecture(); using var state = new BorrowColorState();
        var attached = new BorrowColorMemory(); var foreign = new BorrowColorMemory();
        MetalVideoPresenter.AttachGuestMemory(new TrackedCpuMemory(attached));
        var target = ProjectGpuViewColor(new TrackedCpuMemory(foreign));
        try
        {
            Assert.Throws<NotSupportedException>(() => SubmitBorrowColor(target));
            Assert.Equal(0, attached.Copies); Assert.Equal(1, foreign.Copies);
            Assert.Equal(0L, Field("_enqueuedGuestWorkSequence").GetValue(null));
            Assert.Equal(0, foreign.PlainReads);
        }
        finally { MetalVideoPresenter.ReleasePreparedNativeColorTarget(target); }
        Assert.Equal(1, foreign.Releases);
    }

    [Fact]
    public void OpaqueColorDecoratorNeverBorrowsItsInnerProvider()
    {
        ActualArchitecture(); using var state = new BorrowColorState(); var memory = new BorrowColorMemory();
        var opaque = new OpaqueGpuViewDecorator(memory);
        Assert.Throws<NotSupportedException>(() => ProjectGpuViewColor(new TrackedCpuMemory(opaque)));
        Assert.Equal(0, opaque.InnerQueries); Assert.Equal(0, opaque.Reads);
        Assert.Equal(0, memory.Copies); Assert.Equal(0, memory.Releases); Assert.Equal(0, memory.PlainReads);
        Assert.Empty((IEnumerable)Field("_nativeColorOriginals").GetValue(null)!);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SnapshotPreparationReadsSelectedRootAndPreservesCallerCpuState(bool nested)
    {
        ActualArchitecture(); using var state = new State(); var memory = new Memory(Physical(0));
        var thread = new TrackedCpuMemory(memory);
        ICpuMemory input = nested ? new TrackedCpuMemory(thread) : thread;
        var context = new CpuContext(input, Generation.Gen5) { Rip = 12345, ActiveImportIndex = 17, Mxcsr = 0x1FC0 };
        var texture = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(context, [Binding(0)], out var fallback));
        Assert.Equal(Linear(0), texture.RgbaPixels); Assert.Equal(0, fallback); Assert.Equal(1, memory.Reads);
        Assert.Same(input, context.Memory); Assert.Equal(12345UL, context.Rip);
        Assert.Equal(17, context.ActiveImportIndex); Assert.Equal(0x1FC0u, context.Mxcsr); Assert.Null(thread.LastFailure);
    }

    [Fact]
    public void SnapshotPreparationKeepsOpaqueDecoratorReadBehavior()
    {
        ActualArchitecture(); using var state = new State(); var memory = new Memory(Physical(0));
        var opaque = new OpaqueGpuViewDecorator(memory);
        var texture = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(new TrackedCpuMemory(opaque), Generation.Gen5), [Binding(0)], out _));
        Assert.All(texture.RgbaPixels, b => Assert.Equal((byte)0xD3, b));
        Assert.Equal(1, opaque.Reads); Assert.Equal(0, opaque.InnerQueries); Assert.Equal(0, memory.Reads);
    }

    [Theory]
    [InlineData("null")] [InlineData("self")] [InlineData("alternating")]
    public void MalformedColorAndSnapshotViewsFailBeforeAnyOriginalOrBytes(string variant)
    {
        ActualArchitecture(); using var state = new BorrowColorState(); var first = new BorrowColorMemory(); var second = new BorrowColorMemory();
        var link = InvalidPreparationLink(variant, first, second);
        Assert.Throws<InvalidOperationException>(() => ProjectGpuViewColor(link)); link.Queries = 0;
        Assert.Throws<InvalidOperationException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(link, Generation.Gen5), [Binding(0)], out _));
        Assert.Equal(0, first.Copies); Assert.Equal(0, second.Copies);
        Assert.Equal(0, first.PlainReads); Assert.Equal(0, second.PlainReads);
        Assert.Empty((IEnumerable)Field("_nativeColorOriginals").GetValue(null)!);
    }

    private static GpuViewLinkControl InvalidPreparationLink(string variant, ICpuMemory first, ICpuMemory second)
    {
        var link = new GpuViewLinkControl(() => first);
        link.Link = variant switch
        {
            "null" => () => null, "self" => () => link,
            "alternating" => () => (link.Queries & 1) == 1 ? first : second,
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        return link;
    }

    private static (GuestDrawTexture Texture, AgcExports.TranslatedImageBinding Binding) GpuViewStorage()
    {
        var raw = RegisterWords.Texture(Address, GuestPixelFormat.Bits16_16_16_16Float, 64, 64, tile: GuestTileMode.RenderTarget);
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var d));
        var shape = new ShaderImageShape(false, false, true, false, TextureNumericClass.Float);
        var binding = new AgcExports.TranslatedImageBinding(d, true, 0, [], ResourceDescriptor: raw, Shape: shape, Access: GuestImageAccess.Write);
        return (new GuestDrawTexture(d.Address, d.Width, d.Height, d.Format, d.NumberType, [], false, true,
            Pitch: d.Pitch, Depth: d.Depth, Type: d.Type, TileMode: d.TileMode,
            Descriptor: raw, Shape: shape, Access: GuestImageAccess.Write), binding);
    }

    [Fact]
    public void StoragePlacementSelectsExactTrackedProviderBeforeItsOnlyDiscardedProbe()
    {
        ActualArchitecture(); var memory = new PreparationMappingMemory(); var other = new PreparationMappingMemory();
        var texture = GpuViewStorage().Texture;
        var placement = MetalStoragePhysicalPlacement.Capture(new TrackedCpuMemory(new TrackedCpuMemory(memory)), texture, 7);
        Assert.Same(memory, placement.Memory); Assert.Same(memory, placement.Source); Assert.Same(memory.Mapping, placement.Mapping);
        Assert.Equal(1, memory.Copies); Assert.Equal(0, memory.Reads); Assert.Equal(0, other.Copies);
        var opaque = new OpaqueGpuViewDecorator(other);
        Assert.Throws<NotSupportedException>(() => MetalStoragePhysicalPlacement.Capture(new TrackedCpuMemory(opaque), texture, 7));
        Assert.Equal(0, opaque.InnerQueries); Assert.Equal(0, other.Copies);
        var invalid = InvalidPreparationLink("alternating", memory, other);
        Assert.Throws<InvalidOperationException>(() => MetalStoragePhysicalPlacement.Capture(invalid, texture, 7));
        Assert.Same(memory.Mapping, placement.Mapping); Assert.Equal(1, memory.Copies); Assert.Equal(0, other.Copies);
    }

    // Fresh worker only: no readonly static flag is reflected or changed.
    [GpuOriginalPreparationTheory]
    [InlineData("same-root")] [InlineData("different-root")]
    [InlineData("opaque")] [InlineData("malformed")]
    public void OrdinaryStoragePreparationResolvesBeforeExistingOriginalCheckOrCapture(string variant)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_METAL_R32_NATIVE_ORIGINAL"));
        ActualArchitecture(); using var state = new State();
        var memory = new PreparationMappingMemory(); var other = new PreparationMappingMemory();
        var registry = (IDictionary)Field("_ordinaryStorageOriginalByAddress").GetValue(null)!; Assert.Empty(registry);
        MetalVideoPresenter.AttachGuestMemory(new TrackedCpuMemory(memory)); var binding = GpuViewStorage().Binding;
        try
        {
            var original = MetalVideoPresenter.PrepareOrdinaryStorageOriginal(new TrackedCpuMemory(memory), binding);
            Assert.NotNull(original); Assert.Equal(1, memory.Copies);
            if (variant == "same-root")
                Assert.Same(original, MetalVideoPresenter.PrepareOrdinaryStorageOriginal(new TrackedCpuMemory(new TrackedCpuMemory(memory)), binding));
            else if (variant == "different-root")
                Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.PrepareOrdinaryStorageOriginal(new TrackedCpuMemory(other), binding));
            else if (variant == "opaque")
            {
                var opaque = new OpaqueGpuViewDecorator(memory);
                Assert.Throws<NotSupportedException>(() => MetalVideoPresenter.PrepareOrdinaryStorageOriginal(new TrackedCpuMemory(opaque), binding));
                Assert.Equal(0, opaque.InnerQueries); Assert.Equal(0, opaque.Reads);
            }
            else
                Assert.Throws<InvalidOperationException>(() => MetalVideoPresenter.PrepareOrdinaryStorageOriginal(
                    InvalidPreparationLink("alternating", memory, other), binding));
            Assert.Same(original, registry[Address]); Assert.Equal(1, memory.Copies); Assert.Equal(0, other.Copies);
            Assert.Equal(0, memory.Reads); Assert.Equal(0, other.Reads);
        }
        finally { registry.Remove(Address); } // mapping-only synthetic owner; no watch/pin/native receipt exists
    }

    private sealed class PreparationMappingMemory : ICpuMemory, ICpuMappedCopySource
    {
        internal int Copies, Reads;
        internal CpuMappedCopyObservation? Mapping;
        public bool TryRead(ulong address, Span<byte> bytes) { Reads++; throw new InvalidOperationException("No plain snapshot fallback."); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new NotSupportedException();
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprint, ulong count, ulong source,
            Span<byte> destination, out CpuMappedCopyObservation? mapping)
        {
            Copies++; Assert.Equal(8, destination.Length); destination.Fill(0xA5);
            mapping = Mapping = new(this, footprint, count, source, (ulong)destination.Length,
                [new(footprint, count, footprint, count, 1, 1, count, 0, 16384, GuestPageProtection.Read | GuestPageProtection.Write)]);
            return CpuMappedCopyResult.Copied;
        }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation mapping) => ReferenceEquals(mapping, Mapping);
    }
}

internal sealed class GpuOriginalPreparationTheoryAttribute : TheoryAttribute
{
    public GpuOriginalPreparationTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_METAL_R32_NATIVE_ORIGINAL") != "1")
            Skip = "Requires a fresh worker with SHARPEMU_METAL_R32_NATIVE_ORIGINAL=1 before Metal type initialization";
    }
}
