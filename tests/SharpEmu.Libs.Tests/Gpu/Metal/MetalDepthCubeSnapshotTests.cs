// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Reflection;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.HLE;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

public sealed partial class MetalRenderHostTests
{
    private static AgcExports.TranslatedImageBinding DepthCubeLinearBinding(uint faces = 6)
    {
        // Four logical texels per row, real production linear pitch128 u16.
        uint[] raw = [(uint)(VertexBase >> 8), 0xC0700000, 0x0000C000,
            0xB0000204, faces - 1, 0x00700000, 0, 0];
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        return new AgcExports.TranslatedImageBinding(descriptor, false, 0, [], true,
            ResourceDescriptor: raw,
            Shape: new ShaderImageShape(false, true, true, false, false, TextureNumericClass.Float, DepthCompare: true),
            Access: GuestImageAccess.Read);
    }

    [Fact]
    public void DepthCubeProductionSnapshotOwnsEveryDeclaredFaceAndPaddedRow()
    {
        var binding = DepthCubeLinearBinding();
        var physical = new byte[6 * 4 * 256];
        for (var face = 0; face < 6; face++)
            for (var y = 0; y < 4; y++)
                for (var x = 0; x < 128; x++)
                    BinaryPrimitives.WriteUInt16LittleEndian(physical.AsSpan(face * 1024 + y * 256 + x * 2),
                        checked((ushort)(face * 7000 + y * 128 + x + 1)));
        Assert.True(_memory.TryWrite(VertexBase, physical));
        var texture = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(_memory, Generation.Gen5), [binding], out var fallback));
        Assert.Equal(0, fallback);
        Assert.Equal(11u, texture.Type); Assert.Equal(6u, texture.ArrayLayers);
        Assert.True(texture.ArrayedView); Assert.True(texture.Shape.Cube); Assert.True(texture.Shape.DepthCompare);
        Assert.Equal(128u, texture.Pitch); Assert.Equal((ulong)physical.Length, texture.SourceByteCount);
        Assert.Equal(physical, texture.RgbaPixels);
        Assert.True(_memory.TryWrite(VertexBase, new byte[physical.Length]));
        Assert.Equal(physical, texture.RgbaPixels); // Detached original bytes survive guest poison.
    }

    [Theory]
    [InlineData("faces")] [InlineData("shape")] [InlineData("raw")]
    [InlineData("base-layer")] [InlineData("partial-mip")]
    public void DepthCubeProductionRejectsIncompleteOrAmbiguousViewsBeforeCpuRead(string kind)
    {
        var binding = DepthCubeLinearBinding();
        if (kind == "faces") binding = DepthCubeLinearBinding(5);
        if (kind == "shape") binding = binding with { Shape = binding.Shape with { Arrayed = false } };
        if (kind == "raw") binding = binding with { ResourceDescriptor = binding.ResourceDescriptor!.Take(7).ToArray() };
        if (kind == "base-layer") binding = binding with { Descriptor = binding.Descriptor with { BaseArray = 1 } };
        if (kind == "partial-mip") binding = binding with { Descriptor = binding.Descriptor with { MaxMip = 1 } };
        var memory = new ColorPhysicalNeverReadMemory();
        Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(memory, Generation.Gen5), [binding], out _));
        Assert.Equal(0, memory.Reads);
    }

    [Fact]
    public void DepthCubeKnownNativeFaceRejectsFullCubeReadBeforeGuestMemoryAccess()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var registry = (System.Collections.IDictionary)typeof(MetalVideoPresenter).GetField("_guestDepthImages", flags)!.GetValue(null)!;
        var imageType = typeof(MetalVideoPresenter).GetNestedType("GuestImage", System.Reflection.BindingFlags.NonPublic)!;
        var image = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(imageType);
        imageType.GetField("Width")!.SetValue(image, 4u); imageType.GetField("Height")!.SetValue(image, 4u);
        var nativeFaceAddress = VertexBase + 3 * 1024;
        var had = registry.Contains(nativeFaceAddress); var previous = registry[nativeFaceAddress];
        var memory = new ColorPhysicalNeverReadMemory();
        try
        {
            registry[nativeFaceAddress] = image; // A known native face strictly inside the full cube range.
            Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
                new CpuContext(memory, Generation.Gen5), [DepthCubeLinearBinding()], out _));
            Assert.Equal(0, memory.Reads);
        }
        finally { if (had) registry[nativeFaceAddress] = previous; else registry.Remove(nativeFaceAddress); }
    }

    [Fact]
    public void DepthCubeIncompletePhysicalReadCannotPublishOnlyBaseFace()
    {
        // The original generic path would read only face0 and publish layers1.
        var memory = new FakeCpuMemory(VertexBase, 1024);
        Assert.Throws<InvalidOperationException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(memory, Generation.Gen5), [DepthCubeLinearBinding()], out _));
    }
    private static void SubmitDepthCubePrecedingDraw(GuestDepthTarget? depth, GuestRenderTarget? color = null) =>
        MetalVideoPresenter.SubmitOffscreenTranslatedDraw(Shader("cube queued writer", Gen5MslStage.Pixel), [], [], 0,
            [color ?? new GuestRenderTarget(IndexBase, 4, 4, 10, 0)], null, 3, 1, 4, null, null, null, depth, 0);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DepthCubePublicPendingOrTakenDepthProducerRejectsBeforeCpuRead(bool taken)
    {
        using var state = CompletionState();
        SubmitDepthCubePrecedingDraw(new(VertexBase + 3 * 1024, VertexBase + 3 * 1024, 4, 4, 3, 0, 0, false));
        Assert.Equal(1L, ReadCompletionState("_enqueuedGuestWorkSequence"));
        if (taken)
        {
            object?[] arguments = [null];
            Assert.True((bool)typeof(MetalVideoPresenter).GetMethod("TryTakeGuestWork", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments)!);
            Assert.NotNull(ReadCompletionState("_takenDepthCubeProducerWork"));
        }
        var memory = new ColorPhysicalNeverReadMemory();
        Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(memory, Generation.Gen5), [DepthCubeLinearBinding()], out _));
        Assert.Equal(0, memory.Reads);
        Assert.Equal(0L, ReadCompletionState("_completedGuestWorkSequence")); // no GPU completion fabricated
    }

    [Fact]
    public void DepthCubePublicUnknownTiledColorProducerRefusesBeforeCpuRead()
    {
        using var state = CompletionState();
        SubmitDepthCubePrecedingDraw(null, new GuestRenderTarget(IndexBase, 4, 4, 10, 0, TileMode: 27));
        var memory = new ColorPhysicalNeverReadMemory();
        Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(memory, Generation.Gen5), [DepthCubeLinearBinding()], out _));
        Assert.Equal(0, memory.Reads); // logical disjointness cannot certify unknown tiled extent
    }

    [Fact]
    public void DepthCubePublicDisjointCompleteLinearProducerDoesNotRejectCpuFaces()
    {
        using var state = CompletionState();
        SubmitDepthCubePrecedingDraw(null);
        var physical = new byte[6144];
        Assert.True(_memory.TryWrite(VertexBase, physical));
        var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(
            new CpuContext(_memory, Generation.Gen5), [DepthCubeLinearBinding()], out _));
        Assert.Equal(6u, snapshot.ArrayLayers); Assert.Equal(physical, snapshot.RgbaPixels);
        Assert.Equal(0L, ReadCompletionState("_completedGuestWorkSequence"));
    }

    [Fact]
    public void DepthCubeNativeOwnerAppearingDuringCopyRefusesBeforeSnapshotPublication()
    {
        using var state = CompletionState();
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var registry = (System.Collections.IDictionary)typeof(MetalVideoPresenter).GetField("_guestDepthImages", flags)!.GetValue(null)!;
        var imageType = typeof(MetalVideoPresenter).GetNestedType("GuestImage", BindingFlags.NonPublic)!;
        var image = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(imageType);
        imageType.GetField("Width")!.SetValue(image, 4u); imageType.GetField("Height")!.SetValue(image, 4u);
        var address = VertexBase + 3 * 1024;
        var had = registry.Contains(address); var previous = registry[address];
        var memory = new DepthCubeOwnerDuringReadMemory(() => registry[address] = image);
        try
        {
            Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(
                new CpuContext(memory, Generation.Gen5), [DepthCubeLinearBinding()], out _));
            Assert.Equal(6, memory.Reads); // initial veto passed; final publication veto caught actual registry change
        }
        finally { if (had) registry[address] = previous; else registry.Remove(address); }
    }

    private sealed class DepthCubeOwnerDuringReadMemory(Action afterCompleteCopy) : ICpuMemory
    {
        internal int Reads;
        public bool TryRead(ulong address, Span<byte> bytes)
        {
            Reads++; bytes.Fill(0x71);
            if (Reads == 6) afterCompleteCopy();
            return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new NotSupportedException();
    }

}
