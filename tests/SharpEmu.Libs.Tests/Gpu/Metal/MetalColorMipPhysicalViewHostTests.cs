// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

public sealed partial class MetalRenderHostTests
{
    private static ColorTargetWords PhysicalViewColor(ulong address) => new(address, 0, 0x40730, 0,
        0x907FC1FF, 0x4DC6C000, 72, 0, 0, 0, 0);

    [Fact]
    public void ColorPhysicalHostClearPreservesResolvedMipZeroAndOriginalRegisters()
    {
        using var enabled = new ColorPhysicalFlagScope();
        var words = PhysicalViewColor(ColorBase);
        _banks.Context.ColorTargets[0] = words;
        var resolution = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
        var state = new ColorTargetState(resolution, 0, new ResourceSlotIdentifier(1, 1));
        ((IRenderHost)_host).AcquireColorAttachment(state);
        ((IRenderHost)_host).ClearColorTargets([state], new SolidColorClear(1, 0, 0, 1));
        var target = Assert.Single(Assert.Single(_backend.Draws).Targets);
        Assert.Equal(ColorBase + 0xB0000, target.Address);
        Assert.Equal(words, target.Registers);
        Assert.Equal(1u, target.MipLevels);
        Assert.True(MetalColorMipPlacement.TryGet(target, out var observation));
        Assert.Same(_memory, observation.Memory);
        Assert.Equal(0x200000UL, observation.ByteCount);
        Assert.Equal(0x2B0000UL, observation.BackingByteCount);
        Assert.Equal(ColorBase, observation.Registers.BaseAddress);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ColorPhysicalHostRejectsUnqualifiedChainBeforeClearAdmission(int neighbor)
    {
        using var enabled = new ColorPhysicalFlagScope();
        var words = PhysicalViewColor(ColorBase);
        words = neighbor switch
        {
            1 => words with { Attrib2 = words.Attrib2 ^ (1u << 28) },
            2 => words with { View = 1u << 26 },
            3 => words with { Info = words.Info | (1u << 28), DccAddress = ColorBase + 0x40000 },
            _ => words with { DccControl = words.DccControl ^ 1u },
        };
        _banks.Context.ColorTargets[0] = words;
        var resolution = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
        var state = new ColorTargetState(resolution, 0, new ResourceSlotIdentifier(1, 1));
        Assert.Throws<NotSupportedException>(() => ((IRenderHost)_host).ClearColorTargets([state], default));
        Assert.Empty(_backend.Draws);
    }

    [Fact]
    public void ColorPhysicalProjectionRejectsForgedNormalizedPlacement()
    {
        using var enabled = new ColorPhysicalFlagScope();
        var words = PhysicalViewColor(ColorBase);
        var resolution = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
        var forged = resolution with { BackingSize = 0x200000 };
        var target = new SharpEmu.Libs.Gpu.GuestRenderTarget(ColorBase, 512, 512,
            (uint)words.Layout, (uint)words.NumberType, Registers: words, TileMode: 27);
        Assert.Throws<NotSupportedException>(() => MetalColorMipPlacement.Project(_memory, forged, words, target));
    }

    [Fact]
    public void ColorPhysicalProjectionRejectsDefaultSelectedLayout()
    {
        using var enabled = new ColorPhysicalFlagScope();
        var words = PhysicalViewColor(ColorBase);
        var resolution = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
        var request = resolution.Request;
        request.Description.MipLayout = default; // Fixed sixteen-slot capacity, but no valid selected placement.
        var forged = resolution with { Request = request };
        var target = new SharpEmu.Libs.Gpu.GuestRenderTarget(ColorBase, 512, 512,
            (uint)words.Layout, (uint)words.NumberType, Registers: words, TileMode: 27);
        Assert.Throws<NotSupportedException>(() => MetalColorMipPlacement.Project(_memory, forged, words, target));
        Assert.False(MetalColorMipPlacement.TryGet(target, out _));
    }
    [Fact]
    public void ColorPhysicalDefaultOffLeavesLegacySingleTargetUnchanged()
    {
        using var disabled = new ColorPhysicalFlagScope(false);
        var words = PhysicalViewColor(ColorBase);
        _banks.Context.ColorTargets[0] = words;
        var resolution = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
        var state = new ColorTargetState(resolution, 0, new ResourceSlotIdentifier(1, 1));
        ((IRenderHost)_host).ClearColorTargets([state], default);
        var target = Assert.Single(Assert.Single(_backend.Draws).Targets);
        Assert.Equal(ColorBase, target.Address);
        Assert.False(MetalColorMipPlacement.TryGet(target, out _));
    }

    [Fact]
    public void ColorPhysicalExactStandaloneTailDoesNotBorrowTheLargerNativeColorImage()
    {
        using var enabled = new ColorPhysicalFlagScope();
        using var inventory = new ColorPhysicalInventoryScope(_memory);
        var binding = ColorPhysicalTailBinding();
        MetalVideoPresenter.ValidateColorPhysicalSnapshotBinding(_memory, binding);
        var texture = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(_memory, Generation.Gen5), [binding], out var fallback));
        Assert.Equal(0, fallback);
        Assert.Equal(ColorBase, texture.Address);
        Assert.Equal(128u, texture.Width); Assert.Equal(64u, texture.Height);
        Assert.Equal(0x10000, texture.RgbaPixels.Length);
        Assert.Empty(texture.TiledSource ?? []);
        Assert.NotEqual(ColorBase + 0xB0000, texture.Address);
    }

    [Theory]
    [InlineData("raw-flags")] [InlineData("incomplete")] [InlineData("dynamic")]
    [InlineData("overlap")] [InlineData("domain")] [InlineData("dimension")]
    public void ColorPhysicalUnsupportedViewRejectsBeforeAnyCpuRead(string kind)
    {
        using var enabled = new ColorPhysicalFlagScope();
        var memory = new ColorPhysicalNeverReadMemory();
        using var inventory = new ColorPhysicalInventoryScope(memory);
        var binding = ColorPhysicalTailBinding();
        var raw = binding.ResourceDescriptor!.ToArray();
        if (kind == "raw-flags") raw[7] = 1;
        if (kind == "incomplete") raw = raw[..7];
        if (kind == "overlap") raw = [(uint)(ColorBase >> 8), 0xC4700000, 0x007FC07F, 0x91B00FAC, 511, 0x00700090, 0, 0];
        if (raw.Length == 8 && kind != "raw-flags")
        {
            Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var decoded));
            binding = binding with { Descriptor = decoded };
        }
        binding = binding with { ResourceDescriptor = raw, DynamicMip = kind == "dynamic", Dimension = kind == "dimension" ? 2u : 1u };
        var actualMemory = kind == "domain" ? new ColorPhysicalNeverReadMemory() : memory;
        Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(actualMemory, Generation.Gen5), [binding], out _));
        Assert.Equal(0, memory.Reads); Assert.Equal(0, actualMemory.Reads);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ColorPhysicalSelectedStandaloneDescriptorReportsOriginalPhysicalOverlapBeforeRead(bool storage)
    {
        using var enabled = new ColorPhysicalFlagScope();
        var memory = new ColorPhysicalNeverReadMemory();
        using var inventory = new ColorPhysicalInventoryScope(memory);
        var selected = ColorBase + 0xB0000;
        uint[] raw = [(uint)(selected >> 8), 0xC4700000, 0x007FC07F, 0x91B00FAC, 0, 0x00700000, 0, 0];
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        var binding = ColorPhysicalTailBinding() with
        {
            Descriptor = descriptor, ResourceDescriptor = raw, IsStorage = storage,
            Shape = ColorPhysicalTailBinding().Shape with { Storage = storage },
            Access = storage ? GuestImageAccess.Write : GuestImageAccess.Read,
        };
        var failure = Assert.Throws<NotSupportedException>(() =>
            MetalVideoPresenter.ValidateColorPhysicalSnapshotBinding(memory, binding));
        Assert.Contains("request_addr=0x1000B0000 request_bytes=2097152", failure.Message);
        Assert.Contains("request_extent=512x512x1", failure.Message);
        Assert.Contains("original_base=0x100000000 original_backing_bytes=2818048", failure.Message);
        Assert.Contains("original_selected=0x1000B0000 original_selected_bytes=2097152", failure.Message);
        Assert.Contains("selected_overlap=True", failure.Message);
        Assert.Equal(0, memory.Reads); Assert.Empty(_backend.Draws);
    }

    [Fact]
    public void ColorPhysicalNullResourceRemainsAReadFreeFallback()
    {
        using var enabled = new ColorPhysicalFlagScope();
        var memory = new ColorPhysicalNeverReadMemory();
        using var inventory = new ColorPhysicalInventoryScope(memory);
        var binding = ColorPhysicalTailBinding() with { Descriptor = default, ResourceDescriptor = null };
        var texture = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(memory, Generation.Gen5), [binding], out var fallback));
        Assert.Equal(1, fallback); Assert.True(texture.IsFallback); Assert.Equal(0, memory.Reads);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ColorPhysicalCrossingStartDisplaySeedRejectsBeforePendingDataOrRead(bool paddedRows)
    {
        using var enabled = new ColorPhysicalFlagScope();
        var memory = new ColorPhysicalNeverReadMemory();
        using var inventory = new ColorPhysicalInventoryScope(memory);
        const ulong start = ColorBase - 0x10000;
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var available = (IDictionary)typeof(MetalVideoPresenter).GetField("_availableGuestImages", flags)!.GetValue(null)!;
        var pending = (IDictionary)typeof(MetalVideoPresenter).GetField("_pendingGuestImageInitialData", flags)!.GetValue(null)!;
        var images = (IDictionary)typeof(MetalVideoPresenter).GetField("_guestImages", flags)!.GetValue(null)!;
        var hadAvailable = available.Contains(start); var oldAvailable = available[start];
        var hadPending = pending.Contains(start); var oldPending = pending[start];
        var hadImage = images.Contains(start); var oldImage = images[start];
        var originalPending = new byte[8];
        try
        {
            available[start] = 0x80000C07u; // Actual render namespace RGBA16Float.
            pending[start] = originalPending; images.Remove(start);
            var width = paddedRows ? 128u : 512u;
            var height = paddedRows ? 64u : 512u;
            var pitch = paddedRows ? 4096u : 0u;
            var failure = Assert.Throws<TargetInvocationException>(() =>
                typeof(MetalVideoPresenter).GetMethod("EnsureGuestImage", flags)!.Invoke(null,
                    [nint.Zero, start, width, height, pitch]));
            Assert.IsType<NotSupportedException>(failure.InnerException);
            Assert.Same(originalPending, pending[start]); // No accepted seed consumption.
            Assert.False(images.Contains(start)); // No native allocation/publication.
            Assert.Equal(0, memory.Reads);
        }
        finally
        {
            if (hadAvailable) available[start] = oldAvailable; else available.Remove(start);
            if (hadPending) pending[start] = oldPending; else pending.Remove(start);
            if (hadImage) images[start] = oldImage; else images.Remove(start);
        }
    }

    private static AgcExports.TranslatedImageBinding ColorPhysicalTailBinding()
    {
        uint[] raw = [(uint)(ColorBase >> 8), 0xC4700000, 0x000FC01F, 0x91B00FAC, 0, 0x00700000, 0, 0];
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        return new(descriptor, true, 0, [], ResourceDescriptor: raw,
            Shape: new ShaderImageShape(false, false, true, false, TextureNumericClass.Float), Access: GuestImageAccess.Write);
    }

    private sealed class ColorPhysicalNeverReadMemory : ICpuMemory
    {
        internal int Reads;
        public bool TryRead(ulong address, Span<byte> bytes) { Reads++; throw new InvalidOperationException("No CPU read after rejected placement."); }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new NotSupportedException();
    }

    private sealed class ColorPhysicalInventoryScope : IDisposable
    {
        private static readonly FieldInfo Field = typeof(MetalVideoPresenter).GetField("_colorPhysicalAllocations", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly IDictionary values = (IDictionary)Field.GetValue(null)!;
        private readonly DictionaryEntry[] previous;
        internal ColorPhysicalInventoryScope(ICpuMemory memory)
        {
            previous = values.Keys.Cast<ulong>().Select(key => new DictionaryEntry(key, values[key])).ToArray(); values.Clear();
            var words = PhysicalViewColor(ColorBase);
            var resolution = ImageRequestBuilders.ColorTarget(words, 0xF, 0, false)!.Value;
            var original = new GuestRenderTarget(ColorBase, 512, 512, (uint)words.Layout,
                (uint)words.NumberType, Registers: words, TileMode: 27);
            var target = MetalColorMipPlacement.Project(memory, resolution, words, original);
            Assert.True(MetalColorMipPlacement.TryGet(target, out var view));
            values.Add(ColorBase, view);
        }
        public void Dispose() { values.Clear(); foreach (var entry in previous) values.Add(entry.Key, entry.Value); }
    }

    private sealed class ColorPhysicalFlagScope : IDisposable
    {
        private readonly bool previous = MetalColorMipPlacement.Enabled;
        internal ColorPhysicalFlagScope(bool enabled = true) => MetalColorMipPlacement.Enabled = enabled;
        public void Dispose() => MetalColorMipPlacement.Enabled = previous;
    }

}
