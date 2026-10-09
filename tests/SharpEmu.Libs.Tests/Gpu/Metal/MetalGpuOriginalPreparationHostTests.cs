// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Core.Cpu;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

public sealed partial class MetalRenderHostTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DirectHostConstructionSelectsOneTrackedRootForInterpreterAndPreparation(bool nested)
    {
        ICpuMemory input = new TrackedCpuMemory(_memory);
        if (nested) input = new TrackedCpuMemory(input);
        var host = new MetalCommandStreamHost(input, _backend, _backend);
        Assert.Same(_memory, host.Memory);
        var context = (CpuContext)typeof(MetalCommandStreamHost).GetField("_context", Members)!.GetValue(host)!;
        Assert.Same(host.Memory, context.Memory); Assert.Equal(Generation.Gen5, context.TargetGeneration);
        Assert.Empty(_backend.Records);
    }

    [Fact]
    public void DirectHostConstructionPreservesOpaqueDecoratorAsBothGpuReaders()
    {
        var opaque = new PreparationHostOpaque(_memory);
        var host = new MetalCommandStreamHost(new TrackedCpuMemory(opaque), _backend, _backend);
        Assert.Same(opaque, host.Memory);
        var context = (CpuContext)typeof(MetalCommandStreamHost).GetField("_context", Members)!.GetValue(host)!;
        Assert.Same(opaque, context.Memory); Assert.Equal(0, opaque.InnerQueries); Assert.Equal(0, opaque.Reads);
        Assert.Empty(_backend.Records);
    }

    [Theory]
    [InlineData("null")] [InlineData("self")] [InlineData("alternating")]
    public void InvalidDirectHostViewRefusesBeforeAnyInterpreterOrPreparationEffect(string variant)
    {
        var link = BadPreparationHostLink(variant);
        Assert.Throws<InvalidOperationException>(() => new MetalCommandStreamHost(link, _backend, _backend));
        Assert.Equal(0, link.Reads); Assert.Empty(_backend.Records);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DepthSelectedFactoryQueriesActualTrackedRootAndKeepsFirstRefusal(bool nested)
    {
        using var flag = new NativeDepthFaceFlag(true);
        var enabled = typeof(GuestImageWriteTracker).GetField("_enabled", BindingFlags.NonPublic | BindingFlags.Static)!;
        var before = enabled.GetValue(null); enabled.SetValue(null, false);
        try
        {
            // Negative metadata/provider control, no native tracking or lease.
            var memory = new DepthFaceRefusedMappingMemory(CpuMappedCopyResult.Rejected);
            ICpuMemory input = new TrackedCpuMemory(memory); if (nested) input = new TrackedCpuMemory(input);
            var words = NativeFaceWords(3);
            var target = new GuestDepthTarget(MemoryBase, MemoryBase, 1024, 1024,
                (uint)GuestDepthFormat.Z16, 24, 0.25f, false, Registers: words);
            var failure = Assert.Throws<NotSupportedException>(() =>
                MetalDepthFacePlacement.Project(input, ResolveNativeFace(3), words, target));
            Assert.Contains("reason=mapped-copy-result", failure.Message);
            Assert.Equal((MemoryBase, 8388608UL, MemoryBase + 6291456UL, 8), memory.Probe);
            Assert.Equal(1, memory.Probes); Assert.Equal(0, memory.CurrentQueries);
            Assert.False(MetalDepthFacePlacement.TryGet(target, out _)); Assert.Empty(_backend.Records);
        }
        finally { enabled.SetValue(null, before); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DepthSiblingAndColorSpanFactoriesKeepSameResolvedRootWithoutCopy(bool nested)
    {
        using var flag = new NativeDepthFaceFlag(true);
        ICpuMemory input = new TrackedCpuMemory(_memory); if (nested) input = new TrackedCpuMemory(input);
        var words = NativeFaceWords(0) with { DepthSizeXy = 511 | (511u << 16) };
        var resolution = ImageRequestBuilders.DepthTarget(words, ((IRenderHost)_host).FormatSupport)!.Value;
        var target = new GuestDepthTarget(MemoryBase, MemoryBase, 512, 512,
            (uint)GuestDepthFormat.Z16, 24, 0.25f, false, Registers: words);
        Assert.Same(target, MetalDepthFacePlacement.Project(input, resolution, words, target));
        Assert.True(MetalDepthFacePlacement.TryGetDepthSpan(target, out var depth));
        Assert.Same(_memory, depth.Memory); Assert.Equal(resolution.DepthAddress, depth.Address);
        Assert.Equal(resolution.DepthSize, depth.Bytes);
        var colorWords = PhysicalViewColor(ColorBase);
        var colorResolution = ImageRequestBuilders.ColorTarget(colorWords, 0xF, 0, false)!.Value;
        var colorTarget = new GuestRenderTarget(ColorBase, 512, 512, (uint)colorWords.Layout,
            (uint)colorWords.NumberType, Registers: colorWords, TileMode: 27);
        Assert.Same(colorTarget, MetalDepthFacePlacement.RecordColorSpan(input, colorResolution, colorTarget));
        Assert.True(MetalDepthFacePlacement.TryGetColorSpan(colorTarget, out var color));
        Assert.Same(_memory, color.Memory); Assert.Equal(colorResolution.Request.Description.Data.Address, color.Address);
        Assert.Equal(colorResolution.Request.Description.Data.Size, color.Bytes); Assert.Empty(_backend.Records);
    }

    [Fact]
    public void DepthSiblingSpanFactoryKeepsOpaqueDecoratorIdentity()
    {
        using var flag = new NativeDepthFaceFlag(true); var opaque = new PreparationHostOpaque(_memory);
        var words = NativeFaceWords(0) with { DepthSizeXy = 511 | (511u << 16) };
        var resolution = ImageRequestBuilders.DepthTarget(words, ((IRenderHost)_host).FormatSupport)!.Value;
        var target = new GuestDepthTarget(MemoryBase, MemoryBase, 512, 512,
            (uint)GuestDepthFormat.Z16, 24, 0.25f, false, Registers: words);
        MetalDepthFacePlacement.Project(new TrackedCpuMemory(opaque), resolution, words, target);
        Assert.True(MetalDepthFacePlacement.TryGetDepthSpan(target, out var span)); Assert.Same(opaque, span.Memory);
        Assert.Equal(0, opaque.InnerQueries); Assert.Equal(0, opaque.Reads); Assert.Empty(_backend.Records);
    }

    [Theory]
    [InlineData("null")] [InlineData("self")] [InlineData("alternating")]
    public void InvalidDepthAndColorSpanViewRefusesBeforeInstallingAnyOriginal(string variant)
    {
        using var flag = new NativeDepthFaceFlag(true); var link = BadPreparationHostLink(variant);
        var words = NativeFaceWords(3);
        var target = new GuestDepthTarget(MemoryBase, MemoryBase, 1024, 1024,
            (uint)GuestDepthFormat.Z16, 24, 0.25f, false, Registers: words);
        Assert.Throws<InvalidOperationException>(() => MetalDepthFacePlacement.Project(link, ResolveNativeFace(3), words, target));
        Assert.False(MetalDepthFacePlacement.TryGet(target, out _)); Assert.False(MetalDepthFacePlacement.TryGetDepthSpan(target, out _));
        var colorWords = PhysicalViewColor(ColorBase);
        var colorResolution = ImageRequestBuilders.ColorTarget(colorWords, 0xF, 0, false)!.Value;
        var color = new GuestRenderTarget(ColorBase, 512, 512, (uint)colorWords.Layout,
            (uint)colorWords.NumberType, Registers: colorWords, TileMode: 27);
        link.Queries = 0;
        Assert.Throws<InvalidOperationException>(() => MetalDepthFacePlacement.RecordColorSpan(link, colorResolution, color));
        Assert.False(MetalDepthFacePlacement.TryGetColorSpan(color, out _)); Assert.Equal(0, link.Reads); Assert.Empty(_backend.Records);
    }

    private PreparationHostLink BadPreparationHostLink(string variant)
    {
        var link = new PreparationHostLink(() => _memory);
        var other = new PreparationHostOpaque(_memory);
        link.Link = variant switch
        {
            "null" => () => null, "self" => () => link,
            "alternating" => () => (link.Queries & 1) == 1 ? _memory : other,
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        return link;
    }
    private sealed class PreparationHostLink(Func<ICpuMemory?> link) : ICpuMemory, ICpuGpuMemoryView
    {
        internal Func<ICpuMemory?> Link = link; internal int Queries, Reads;
        public ICpuMemory GpuMemoryView { get { Queries++; return Link()!; } }
        public bool TryRead(ulong address, Span<byte> bytes) { Reads++; throw new InvalidOperationException("Malformed view cannot read bytes."); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new NotSupportedException();
    }
    private sealed class PreparationHostOpaque(ICpuMemory memory) : ICpuMemory, ICpuMemoryWrapper
    {
        internal int InnerQueries, Reads;
        public ICpuMemory Inner { get { InnerQueries++; return memory; } }
        public bool TryRead(ulong address, Span<byte> bytes) { Reads++; throw new InvalidOperationException("Metadata control must not read bytes."); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new NotSupportedException();
    }
}
