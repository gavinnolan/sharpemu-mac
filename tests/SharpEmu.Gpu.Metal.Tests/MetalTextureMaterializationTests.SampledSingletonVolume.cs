// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Metal.Tests;
using DeviceShader = SharpEmu.ShaderCompiler.Metal.Tests.MetalNative;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    private const ulong SampledVolumeAddress = 0x7015288000;
    private static uint[] SampledVolumeWords() => [0x70152880, 0x08200000, 0, 0xA0500FAC, 0, 0x00700000, 0, 0];
    private static ShaderImageShape SampledVolumeShape => new(true, false, false, false, false, TextureNumericClass.Float);
    private static AgcExports.TranslatedImageBinding SampledVolumeBinding(uint[]? raw = null)
    {
        raw ??= SampledVolumeWords();
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        return new(descriptor, false, 0, [], ResourceDescriptor: raw, Shape: SampledVolumeShape, Access: GuestImageAccess.Read);
    }

    private static IReadOnlyList<GuestDrawTexture> SampledVolumeSnapshots(OriginVolumeMemory memory,
        params AgcExports.TranslatedImageBinding[] bindings)
    {
        var type = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalTextureSnapshots");
        try
        {
            return (IReadOnlyList<GuestDrawTexture>)type.GetMethod("CreateTextureSnapshots", Methods)!.Invoke(null,
                [new CpuContext(memory, Generation.Gen5), bindings, 0])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw;
        }
    }

    [Fact]
    public void SampledSingletonVolumeSnapshotOwnsOnlyFourOriginBytesAndPreservesTypedMetadata()
    {
        var bytes = Enumerable.Repeat((byte)0xCD, 4096).ToArray();
        byte[] origin = [128, 64, 32, 192]; origin.CopyTo(bytes, 0);
        var memory = new OriginVolumeMemory(bytes);
        var binding = SampledVolumeBinding();
        var texture = Assert.Single(SampledVolumeSnapshots(memory, binding));
        Assert.Equal(origin, texture.RgbaPixels); Assert.Equal(new[] { (SampledVolumeAddress, 4) }, memory.Reads);
        Assert.Equal(10u, texture.Type); Assert.Equal(1u, texture.Depth); Assert.Equal(9u, texture.NumberType);
        Assert.Equal(4UL, texture.SourceByteCount); Assert.Equal(binding.Shape, texture.Shape); Assert.Equal(GuestImageAccess.Read, texture.Access);
        Assert.Equal(binding.ResourceDescriptor, texture.Descriptor); Assert.NotSame(binding.ResourceDescriptor, texture.Descriptor);
        Assert.Null(texture.TiledSource); Assert.Null(texture.Detile); Assert.Null(texture.MipUploads);
        bytes[0] = 0; ((uint[])binding.ResourceDescriptor!)[0] = 0; // Caller mutation cannot alter the owned snapshot.
        Assert.Equal(origin, texture.RgbaPixels); Assert.Equal(SampledVolumeWords(), texture.Descriptor);
        var shape = Presenter.GetMethod("GuestTextureShape", Methods)!.Invoke(null, [texture])!;
        Assert.Equal(7u, (uint)shape.GetType().GetField("Item1")!.GetValue(shape)!);
        var upload = Presenter.GetMethod("PrepareDrawTextureUpload", Methods)!.Invoke(null, [texture]);
        Assert.Same(texture, upload);
        GuestImageWriteTracker.Untrack(SampledVolumeAddress);
    }

    [Theory]
    [InlineData(0)] // compact raw source cannot certify typed origin
    [InlineData(1)] // raw metadata
    [InlineData(2)] // actual width2
    [InlineData(3)] // unknown numeric type
    [InlineData(4)] // compiled non-volume
    [InlineData(5)] // dynamic mip
    [InlineData(6)] // write intent
    [InlineData(7)] // descriptor/raw mismatch
    [InlineData(8)] // depth2
    [InlineData(9)] // PERF_MOD must match observed7, not broaden descriptor policy
    [InlineData(10)] // neighboring word5 BIG_PAGE bit cannot be silently ignored
    public void SampledSingletonVolumeUnsupportedViewsFailBeforeGuestRead(int kind)
    {
        var binding = SampledVolumeBinding(); var raw = SampledVolumeWords();
        binding = kind switch
        {
            0 => binding with { ResourceDescriptor = raw[..4] },
            1 => binding with { ResourceDescriptor = raw.Select((v, i) => i == 6 ? 1u : v).ToArray() },
            2 => SampledVolumeBinding(raw.Select((v, i) => i == 1 ? v | 0x40000000 : v).ToArray()),
            3 => binding with { Shape = binding.Shape with { NumericClass = TextureNumericClass.Unsupported } },
            4 => binding with { Shape = binding.Shape with { Volume = false } },
            5 => binding with { DynamicMip = true },
            6 => binding with { Access = GuestImageAccess.Write },
            7 => binding with { Descriptor = binding.Descriptor with { Pitch = 2 } },
            8 => SampledVolumeBinding(raw.Select((v, i) => i == 4 ? 1u : v).ToArray()),
            9 => SampledVolumeBinding(raw.Select((v, i) => i == 5 ? 0x00600000u : v).ToArray()),
            10 => SampledVolumeBinding(raw.Select((v, i) => i == 5 ? v | 0x80000000u : v).ToArray()),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var memory = new OriginVolumeMemory([128, 64, 32, 192]);
        Assert.Throws<NotSupportedException>(() => SampledVolumeSnapshots(memory, binding));
        Assert.Empty(memory.Reads);
    }

    [Fact]
    public void SampledSingletonVolumeShortSourceDoesNotFillMissingChannelsOrReadPadding()
    {
        var memory = new OriginVolumeMemory([128, 64, 32]);
        var failure = Assert.Throws<InvalidOperationException>(() => SampledVolumeSnapshots(memory, SampledVolumeBinding()));
        Assert.Contains("required=4", failure.Message); Assert.Single(memory.Reads);
        GuestImageWriteTracker.Untrack(SampledVolumeAddress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SampledSingletonVolumeGpuAdvertisementRejectsBeforeReadAndBeforeNativeUpload(bool earlierExtent)
    {
        var binding = SampledVolumeBinding(); var memory = new OriginVolumeMemory([128, 64, 32, 192]);
        var texture = Assert.Single(SampledVolumeSnapshots(memory, binding));
        var advertised = (IDictionary)Presenter.GetField("_availableGuestImages", Methods)!.GetValue(null)!;
        var extents = (IDictionary)Presenter.GetField("_guestImageExtents", Methods)!.GetValue(null)!;
        var gate = Presenter.GetField("_gate", Methods)!.GetValue(null)!;
        var key = earlierExtent ? SampledVolumeAddress - 4096 : SampledVolumeAddress + 16;
        lock (gate)
        {
            Assert.False(advertised.Contains(key)); Assert.False(extents.Contains(key));
            if (earlierExtent) extents.Add(key, (64u, 32u, 8192UL)); else advertised.Add(key, 999u);
        }
        try
        {
            var reads = memory.Reads.Count;
            Assert.Throws<NotSupportedException>(() => SampledVolumeSnapshots(memory, binding)); Assert.Equal(reads, memory.Reads.Count);
            var failure = Assert.Throws<TargetInvocationException>(() => Presenter.GetMethod("CreateDrawTexture", Methods)!
                .Invoke(null, [nint.Zero, nint.Zero, texture, true]));
            Assert.IsType<NotSupportedException>(failure.InnerException); Assert.Contains("GPU alias", failure.InnerException!.Message);
        }
        finally
        {
            lock (gate) { advertised.Remove(key); extents.Remove(key); }
            GuestImageWriteTracker.Untrack(SampledVolumeAddress);
        }
    }

    [Fact]
    public void SampledSingletonVolumeEmptyOrDeferredSourceCannotUseLateAddressRecovery()
    {
        var texture = Assert.Single(SampledVolumeSnapshots(new OriginVolumeMemory([128, 64, 32, 192]), SampledVolumeBinding()));
        foreach (var rejected in new[] { texture with { RgbaPixels = [] }, texture with { TiledSource = new byte[4096] } })
        {
            var failure = Assert.Throws<TargetInvocationException>(() => Presenter.GetMethod("CreateDrawTexture", Methods)!
                .Invoke(null, [nint.Zero, nint.Zero, rejected, true]));
            Assert.IsType<NotSupportedException>(failure.InnerException); Assert.Contains("four owned origin bytes", failure.InnerException!.Message);
        }
        GuestImageWriteTracker.Untrack(SampledVolumeAddress);
    }

    private sealed class OriginVolumeMemory(byte[] bytes) : ICpuMemory
    {
        internal readonly List<(ulong Address, int Bytes)> Reads = [];
        public bool TryRead(ulong address, Span<byte> destination)
        {
            Reads.Add((address, destination.Length));
            if (address != SampledVolumeAddress || destination.Length > bytes.Length) return false;
            bytes.AsSpan(0, destination.Length).CopyTo(destination); return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }

    [Theory]
    [InlineData(0)] // actual sampler: RGB decodes sRGB, alpha remains linear
    [InlineData(1)] // exact integer origin load
    [InlineData(2)] // signed negative x is outside the native1x1x1 view
    [InlineData(3)] // z=1 is outside a native3D depth1 view
    public void SampledSingletonVolumeNativeTypedSampleLoadAndSignedBoundsMatchIndependentOracle(int kind)
    {
        byte[] seed = [128, 64, 32, 192];
        var source = Enumerable.Repeat((byte)0xCD, 4096).ToArray(); seed.CopyTo(source, 0);
        var original = source.ToArray(); var memory = new OriginVolumeMemory(source);
        var snapshot = Assert.Single(SampledVolumeSnapshots(memory, SampledVolumeBinding()));
        var sampling = kind == 0;
        uint[] coordinate = kind switch
        {
            0 => [BitConverter.SingleToUInt32Bits(0.5f), BitConverter.SingleToUInt32Bits(0.5f), BitConverter.SingleToUInt32Bits(0.5f)],
            1 => [0, 0, 0], 2 => [uint.MaxValue, 0, 0], 3 => [0, 0, 1],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Assert.True(Gen5ImageFixtures.TryCompile(sampling ? "ImageSampleLz" : "ImageLoad", 10,
            out var shader, out var error, unifiedFormat: 130, addressValues: coordinate, storeResults: true), error);
        Assert.Contains("texture3d<float", shader!.Source);
        if (!RequireDevice()) return; // Required-device runs must fail if the native gate cannot execute.
        nint texture = 0, library = 0, sampler = 0;
        try
        {
            var args = new object?[] { Device.Value, nint.Zero, snapshot, false };
            texture = (nint)Presenter.GetMethod("CreateDrawTexture", Methods)!.Invoke(null, args)!;
            Assert.True((bool)args[3]!); Assert.NotEqual(nint.Zero, texture);
            Assert.Equal(7UL, ReadInteger(texture, Selector("textureType")));
            Assert.Equal(1UL, ReadInteger(texture, Selector("width"))); Assert.Equal(1UL, ReadInteger(texture, Selector("height")));
            Assert.Equal(1UL, ReadInteger(texture, Selector("depth"))); Assert.Equal(1UL, ReadInteger(texture, Selector("mipmapLevelCount")));
            Assert.Equal(71UL, ReadInteger(texture, Selector("pixelFormat")));
            Assert.Equal(seed, ReadNativeRows(texture, 1, 1, 4));
            Assert.True(DeviceShader.TryCompileLibrary(shader.Source, out library, out error), error);
            if (sampling) { sampler = DeviceShader.CreatePointSampler(); Assert.NotEqual(nint.Zero, sampler); }
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(library, shader.EntryPoint,
                Enumerable.Repeat((byte)0xA7, 16).ToArray(), new byte[PushData.ByteSize], shader.ArgumentLayout!, 1,
                out var result, out error, texture, sampler), error);
            // Independent IEC sRGB constants for RGB128/64/32; alpha192/255.
            // In particular alpha is NOT decoded through the RGB transfer curve.
            float[] expected = kind < 2 ? [0.2158605001f, 0.0512694584f, 0.0144438436f, 0.7529411765f] : [0, 0, 0, 0];
            for (var component = 0; component < 4; component++)
            {
                var actual = BitConverter.UInt32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(component * 4)));
                Assert.InRange(Math.Abs(actual - expected[component]), 0, component == 3 ? 1e-6f : 1e-4f);
            }
            Assert.Equal(original, source); Assert.Equal(new[] { (SampledVolumeAddress, 4) }, memory.Reads);
            output.WriteLine($"Actual production owned-origin upload native texture3D RGBA8sRGB depth1, translated {(sampling ? "sample" : "load")} case{kind}; all four output components matched independent sRGB/linear-alpha or signed-bound zero oracle; guest4096bytes unchanged, no late guest read.");
        }
        finally
        {
            if (sampler != 0) SendVoid(sampler, Selector("release"));
            if (library != 0) SendVoid(library, Selector("release"));
            if (texture != 0) SendVoid(texture, Selector("release"));
            GuestImageWriteTracker.Untrack(SampledVolumeAddress);
        }
    }
}
