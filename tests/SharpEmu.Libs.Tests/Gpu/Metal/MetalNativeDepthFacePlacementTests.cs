// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using System.Reflection;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

public sealed partial class MetalRenderHostTests
{
    [Fact]
    public void ActualSuperliminalDepthCubeRawDecodes1024SquareSixFaceContract()
    {
        uint[] raw = [0x707B7D00, 0xC0700000, 0x00FFC0FF, 0xB1800924, 5, 0x00700000, 0, 0];
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        Assert.Equal(0x707B7D0000UL, descriptor.Address);
        Assert.Equal(1024u, descriptor.Width); Assert.Equal(1024u, descriptor.Height);
        Assert.Equal(1024u, descriptor.Pitch); Assert.Equal(6u, descriptor.Depth);
        Assert.Equal(2u, descriptor.Format); Assert.Equal(0u, descriptor.NumberType);
        Assert.Equal(11u, descriptor.Type); Assert.Equal(24u, descriptor.TileMode);
        Assert.Equal(1u, descriptor.ResourceMipLevels);
        Assert.Equal(0x200000UL, MetalDepthFacePlacement.FaceBytes);
    }

    private static DepthTargetWords NativeFaceWords(uint face) => new(
        (uint)GuestDepthFormat.Z16 | (24u << 4), 0, face | (face << 13),
        1023 | (1023u << 16), true, 0, 1, 2 | 4 | (7u << 4),
        MemoryBase, MemoryBase, 0, 0, 0);
    private DepthTargetResolution ResolveNativeFace(uint face) =>
        ImageRequestBuilders.DepthTarget(NativeFaceWords(face), ((IRenderHost)_host).FormatSupport)!.Value;

    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(2u)] [InlineData(3u)] [InlineData(4u)] [InlineData(5u)]
    public void NativeDepthFaceOriginalNormalizedSelectedLayerAddressesAreDistinct(uint face)
    {
        var words = NativeFaceWords(face);
        var original = ResolveNativeFace(face);
        Assert.Equal(1024u, original.Request.Description.Pitch);
        Assert.Equal(0x200000UL * (face + 1UL), original.DepthSize);
        Assert.Equal(face, original.Request.View.BaseLayer);
        Assert.Equal(1u, original.Request.View.LayerCount);
        Assert.Equal(MemoryBase + face * 0x200000UL, MetalDepthFacePlacement.SelectedAddress(original, words));
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
            case "span": original = original with { DepthSize = 0x200000 }; break;
            case "pitch": request.Description.Pitch = 128; break;
            case "layout": request.Description.MipLayout = default; break;
            case "view": request.View = request.View with { BaseLayer = 0 }; break;
            case "format": request.Description.PixelFormat = GpuFormat.D32Sfloat; break;
            case "mips": request.Description.Resources = new(2, 4); break;
            case "extent": request.Description.Extent = new(128, 1024, 1); break;
        }
        original = original with { Request = request };
        Assert.Throws<NotSupportedException>(() => MetalDepthFacePlacement.SelectedAddress(original, NativeFaceWords(3)));
    }

    [Fact]
    public void NativeDepthFaceDefaultOffPreservesLegacyRawBaseAndNeverProbesMemory()
    {
        using var flag = new NativeDepthFaceFlag(false);
        var words = NativeFaceWords(3);
        var target = new GuestDepthTarget(MemoryBase, MemoryBase, 1024, 1024,
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
        var target = new GuestDepthTarget(MemoryBase, MemoryBase, 1024, 1024,
            (uint)GuestDepthFormat.Z16, 24, 0.25f, false, Registers: words);
        var memory = new ColorPhysicalNeverReadMemory();
        Assert.Throws<NotSupportedException>(() => MetalDepthFacePlacement.Project(memory, ResolveNativeFace(3), words, target));
        Assert.Equal(0, memory.Reads); Assert.False(MetalDepthFacePlacement.TryGet(target, out _));
        Assert.Empty(_backend.Draws);
    }

    [Theory]
    [InlineData(CpuMappedCopyResult.Rejected, "mapped-copy-result")]
    [InlineData(CpuMappedCopyResult.Unavailable, "mapped-copy-result")]
    [InlineData(CpuMappedCopyResult.Copied, "missing-mapping")]
    public void NativeDepthFaceAdmissionDiagnosticPreservesFirstRejectedMappingPredicate(
        CpuMappedCopyResult result, string reason)
    {
        using var flag = new NativeDepthFaceFlag(true);
        // Managed negative only: disable native page tracking. No synthetic
        // mapping/content proof can reach a positive admission in these rows.
        var enabled = typeof(GuestImageWriteTracker).GetField("_enabled", BindingFlags.NonPublic | BindingFlags.Static)!;
        var before = enabled.GetValue(null);
        enabled.SetValue(null, false);
        try
        {
            var words = NativeFaceWords(3);
            var target = new GuestDepthTarget(MemoryBase, MemoryBase, 1024, 1024,
                (uint)GuestDepthFormat.Z16, 24, 0.25f, false, Registers: words);
            var memory = new DepthFaceRefusedMappingMemory(result);
            var error = Assert.Throws<NotSupportedException>(() =>
                MetalDepthFacePlacement.Project(memory, ResolveNativeFace(3), words, target));
            Assert.Contains("reason=" + reason, error.Message);
            Assert.Contains("selected_addr=0x100600000", error.Message);
            Assert.Contains("backing_bytes=8388608", error.Message);
            Assert.Contains("mapped_result=" + result, error.Message);
            Assert.Equal(1, memory.Probes); Assert.Equal(0, memory.CurrentQueries);
            Assert.Equal((MemoryBase, 0x800000UL, MemoryBase + 0x600000UL, 8), memory.Probe);
            Assert.False(MetalDepthFacePlacement.TryGet(target, out _)); Assert.Empty(_backend.Draws);
        }
        finally { enabled.SetValue(null, before); }
    }

    private sealed class DepthFaceRefusedMappingMemory(CpuMappedCopyResult result) : ICpuMemory, ICpuMappedCopySource
    {
        internal int Probes, CurrentQueries;
        internal (ulong, ulong, ulong, int) Probe;
        public bool TryRead(ulong address, Span<byte> bytes) => throw new InvalidOperationException("No CPU shadow fallback.");
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new NotSupportedException();
        public CpuMappedCopyResult TryReadMappedCopy(ulong footprintAddress, ulong footprintBytes,
            ulong sourceAddress, Span<byte> bytes, out CpuMappedCopyObservation? observation)
        {
            Probes++; Probe = (footprintAddress, footprintBytes, sourceAddress, bytes.Length);
            observation = null; return result;
        }
        public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation)
        { CurrentQueries++; throw new InvalidOperationException("Later predicate must remain short-circuited."); }
    }

    private sealed class NativeDepthFaceFlag : IDisposable
    {
        private readonly bool _original = MetalDepthFacePlacement.Enabled;
        public NativeDepthFaceFlag(bool enabled) => MetalDepthFacePlacement.Enabled = enabled;
        public void Dispose() => MetalDepthFacePlacement.Enabled = _original;
    }
}
