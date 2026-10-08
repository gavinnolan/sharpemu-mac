// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
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

    [Fact]
    public void SampledSingletonVolumeGpuAdvertisementRejectsBeforeReadAndBeforeNativeUpload()
    {
        var binding = SampledVolumeBinding(); var memory = new OriginVolumeMemory([128, 64, 32, 192]);
        var texture = Assert.Single(SampledVolumeSnapshots(memory, binding));
        var advertised = (IDictionary)Presenter.GetField("_availableGuestImages", Methods)!.GetValue(null)!;
        var gate = Presenter.GetField("_gate", Methods)!.GetValue(null)!;
        lock (gate) { Assert.False(advertised.Contains(SampledVolumeAddress)); advertised.Add(SampledVolumeAddress + 16, 999u); }
        try
        {
            var reads = memory.Reads.Count;
            Assert.Throws<NotSupportedException>(() => SampledVolumeSnapshots(memory, binding)); Assert.Equal(reads, memory.Reads.Count);
            var failure = Assert.Throws<TargetInvocationException>(() => Presenter.GetMethod("CreateDrawTexture", Methods)!
                .Invoke(null, [nint.Zero, nint.Zero, texture, true]));
            Assert.IsType<NotSupportedException>(failure.InnerException); Assert.Contains("GPU alias", failure.InnerException!.Message);
        }
        finally { lock (gate) advertised.Remove(SampledVolumeAddress + 16); GuestImageWriteTracker.Untrack(SampledVolumeAddress); }
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
}
