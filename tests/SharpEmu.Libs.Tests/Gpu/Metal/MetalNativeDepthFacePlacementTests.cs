// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

public sealed partial class MetalRenderHostTests
{
    private static DepthTargetWords NativeFaceWords(uint face) => new(
        (uint)GuestDepthFormat.Z16 | (24u << 4), 0, face | (face << 13),
        255 | (255u << 16), true, 0, 1, 2 | 4 | (7u << 4),
        MemoryBase, MemoryBase, 0, 0, 0);
    private DepthTargetResolution ResolveNativeFace(uint face) =>
        ImageRequestBuilders.DepthTarget(NativeFaceWords(face), ((IRenderHost)_host).FormatSupport)!.Value;

    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(2u)] [InlineData(3u)] [InlineData(4u)] [InlineData(5u)]
    public void NativeDepthFaceOriginalNormalizedSelectedLayerAddressesAreDistinct(uint face)
    {
        var words = NativeFaceWords(face);
        var original = ResolveNativeFace(face);
        Assert.Equal(256u, original.Request.Description.Pitch);
        Assert.Equal(0x20000UL * (face + 1UL), original.DepthSize);
        Assert.Equal(face, original.Request.View.BaseLayer);
        Assert.Equal(1u, original.Request.View.LayerCount);
        Assert.Equal(MemoryBase + face * 0x20000UL, MetalDepthFacePlacement.SelectedAddress(original, words));
        Assert.Equal(MemoryBase, original.Request.Description.Data.Address); // original descriptor is unchanged
    }

    [Theory]
    [InlineData("span")] [InlineData("pitch")] [InlineData("layout")]
    [InlineData("view")] [InlineData("format")] [InlineData("mips")] [InlineData("extent")]
    public void NativeDepthFaceRejectsForgedNormalizedPlacementBeforeAnyProviderOrNativeCall(string kind)
    {
        var original = ResolveNativeFace(3);
        var request = original.Request;
        switch (kind)
        {
            case "span": original = original with { DepthSize = 0x20000 }; break;
            case "pitch": request.Description.Pitch = 128; break;
            case "layout": request.Description.MipLayout = default; break;
            case "view": request.View = request.View with { BaseLayer = 0 }; break;
            case "format": request.Description.PixelFormat = GpuFormat.D32Sfloat; break;
            case "mips": request.Description.Resources = new(2, 4); break;
            case "extent": request.Description.Extent = new(128, 256, 1); break;
        }
        original = original with { Request = request };
        Assert.Throws<NotSupportedException>(() => MetalDepthFacePlacement.SelectedAddress(original, NativeFaceWords(3)));
    }

    [Fact]
    public void NativeDepthFaceDefaultOffPreservesLegacyRawBaseAndNeverProbesMemory()
    {
        using var flag = new NativeDepthFaceFlag(false);
        var words = NativeFaceWords(3);
        var target = new GuestDepthTarget(MemoryBase, MemoryBase, 256, 256,
            (uint)GuestDepthFormat.Z16, 24, 0.25f, false, Registers: words);
        var memory = new ColorPhysicalNeverReadMemory();
        Assert.Same(target, MetalDepthFacePlacement.Project(memory, ResolveNativeFace(3), words, target));
        Assert.Equal(0, memory.Reads); Assert.False(MetalDepthFacePlacement.TryGet(target, out _));
    }

    [Fact]
    public void NativeDepthFaceMissingOriginalProviderRefusesBeforeCpuShadowReadOrAdmission()
    {
        using var flag = new NativeDepthFaceFlag(true);
        var words = NativeFaceWords(3);
        var target = new GuestDepthTarget(MemoryBase, MemoryBase, 256, 256,
            (uint)GuestDepthFormat.Z16, 24, 0.25f, false, Registers: words);
        var memory = new ColorPhysicalNeverReadMemory();
        Assert.Throws<NotSupportedException>(() => MetalDepthFacePlacement.Project(memory, ResolveNativeFace(3), words, target));
        Assert.Equal(0, memory.Reads); Assert.False(MetalDepthFacePlacement.TryGet(target, out _));
        Assert.Empty(_backend.Draws);
    }

    private sealed class NativeDepthFaceFlag : IDisposable
    {
        private readonly bool _original = MetalDepthFacePlacement.Enabled;
        public NativeDepthFaceFlag(bool enabled) => MetalDepthFacePlacement.Enabled = enabled;
        public void Dispose() => MetalDepthFacePlacement.Enabled = _original;
    }
}
