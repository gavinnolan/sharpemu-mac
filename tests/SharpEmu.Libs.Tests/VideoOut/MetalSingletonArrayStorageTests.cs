// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalSingletonArrayStorageTests
{
    private const ulong Address = 0x71F5_0000_00;
    private const BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;
    internal static uint[] Raw(ulong address = Address) =>
        [(uint)(address >> 8), (uint)(address >> 40) | (56u << 20), 0, (13u << 28) | (27u << 20) | 0xFAC, 0, 0, 0, 0];
    private static AgcExports.TranslatedImageBinding Binding(uint[]? raw = null)
    {
        raw ??= Raw();
        Assert.True(AgcExports.TryDecodeTextureDescriptor(raw, out var descriptor));
        return new(descriptor, true, 0, [], IsArrayed: true, ResourceDescriptor: raw,
            Shape: new ShaderImageShape(false, true, true, false, TextureNumericClass.Float), Access: GuestImageAccess.Write);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualSnapshotOwnsOriginTexelAndPhysicalSliceWithoutFlattening(bool zero)
    {
        using var state = new State();
        var source = Enumerable.Repeat((byte)0xD3, 65536).ToArray();
        byte[] expected = zero ? [0, 0, 0, 0] : [17, 83, 149, 227]; expected.CopyTo(source, 0);
        var original = source.ToArray(); var memory = new Memory(source); var raw = Raw(); var binding = Binding(raw);
        var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out var fallbacks));
        Assert.Equal(10u, binding.Descriptor.Format); Assert.Equal(0u, binding.Descriptor.NumberType);
        Assert.Equal(0, fallbacks); Assert.Equal(1, memory.Reads); Assert.Equal(65536, memory.LastReadBytes);
        Assert.Equal(expected, snapshot.RgbaPixels); Assert.Equal(original, source);
        Assert.True(snapshot.ArrayedView); Assert.Equal(1u, snapshot.ArrayLayers);
        Assert.Equal((3u, 1u, 1u), MetalVideoPresenter.GuestTextureShape(snapshot));
        Assert.Equal(65536UL, snapshot.SourceByteCount); Assert.Equal(Raw(), snapshot.Descriptor);
        Assert.NotSame(binding.ResourceDescriptor, snapshot.Descriptor);
        var format = MetalVideoPresenter.StorageTextureFormat(snapshot);
        Assert.Equal(MtlPixelFormat.Rgba8Unorm, format.Format);
        Assert.Equal((1u, 4UL), MetalVideoPresenter.StorageTextureSeedLayout(snapshot, format));
        source[0] ^= 0xFF; raw[0] ^= 1;
        Assert.Equal(expected, snapshot.RgbaPixels); Assert.Equal(Raw(), snapshot.Descriptor);
        foreach (var invalid in new[] { snapshot with { RgbaPixels = new byte[3] }, snapshot with { CpuSnapshotStable = false } })
            Assert.Throws<InvalidOperationException>(() => Ensure(invalid));
    }

    [Theory]
    [InlineData(0, 1u)] // unaligned address / embedded address bits
    [InlineData(1, 1u << 8)] // min LOD
    [InlineData(2, 1u)] // width !=1
    [InlineData(3, 1u << 12)] // base mip
    [InlineData(3, 1u << 16)] // last mip
    [InlineData(4, 1u)] // second layer
    [InlineData(4, 1u << 16)] // base array
    [InlineData(5, 1u)] // array pitch
    [InlineData(5, 1u << 4)] // resource mip chain
    [InlineData(6, 1u << 21)] // compression flag
    [InlineData(7, 1u)] // metadata
    public void UnsupportedRawViewRejectsBeforeReadingOrQueueMutation(int word, uint bit)
    {
        using var state = new State(); var raw = Raw(); raw[word] ^= bit;
        var memory = new Memory(new byte[65536]); var binding = Binding(raw);
        Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [binding], out _));
        Assert.Equal(0, memory.Reads);
    }

    [Fact]
    public void ContradictoryCompiledShapeAndCopiedRawMetadataRejectBeforeRead()
    {
        using var state = new State(); var binding = Binding(); var memory = new Memory(new byte[65536]);
        foreach (var invalid in new[] { binding with { IsArrayed = false }, binding with { MipLevel = 1 },
            binding with { ResourceDescriptor = null }, binding with { ResourceDescriptor = Raw()[..4] },
            binding with { Shape = binding.Shape with { Arrayed = false } },
            binding with { Shape = binding.Shape with { NumericClass = TextureNumericClass.Uint } },
            binding with { Shape = binding.Shape with { Atomic = true } },
            binding with { Access = GuestImageAccess.Unknown }, binding with { Access = GuestImageAccess.Atomic },
            binding with { Descriptor = binding.Descriptor with { DstSelect = 4 } } })
            Assert.Throws<NotSupportedException>(() => MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [invalid], out _));
        Assert.Equal(0, memory.Reads);
    }

    [Fact]
    public void LiveTwoDimensionalOrWrongLengthImageCannotSatisfyAnArrayView()
    {
        using var state = new State(); var memory = new Memory(new byte[65536]);
        var snapshot = Assert.Single(MetalTextureSnapshots.CreateTextureSnapshots(new CpuContext(memory, Generation.Gen5), [Binding()], out _));
        var images = (IDictionary)typeof(MetalVideoPresenter).GetField("_guestImages", Private)!.GetValue(null)!;
        var type = typeof(MetalVideoPresenter).GetNestedType("GuestImage", BindingFlags.NonPublic)!;
        var image = Activator.CreateInstance(type, nonPublic: true)!;
        foreach (var (field, value) in new (string, object)[] { ("Width", 1u), ("Height", 1u), ("Format", MtlPixelFormat.Rgba8Unorm), ("Initialized", true) }) type.GetField(field)!.SetValue(image, value);
        images.Add(Address, image);
        try
        {
            Assert.Throws<NotSupportedException>(() => Ensure(snapshot));
            type.GetField("TextureType")!.SetValue(image, 3u);
            type.GetField("ArrayLength")!.SetValue(image, 2u);
            Assert.Throws<NotSupportedException>(() => Ensure(snapshot));
            type.GetField("ArrayLength")!.SetValue(image, 1u);
            Assert.Same(image, Ensure(snapshot));
        }
        finally { images.Remove(Address); }
    }

    private static object? Ensure(GuestDrawTexture descriptor)
    {
        try { return typeof(MetalVideoPresenter).GetMethod("EnsureStorageImage", Private)!.Invoke(null, [(nint)0, descriptor]); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); throw; }
    }
    private sealed class Memory(byte[] bytes) : ICpuMemory
    {
        public int Reads, LastReadBytes;
        public bool TryRead(ulong address, Span<byte> destination)
        {
            Reads++; LastReadBytes = destination.Length;
            if (address != Address || destination.Length != bytes.Length) return false;
            bytes.CopyTo(destination); return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => throw new InvalidOperationException("Snapshot may not modify guest neighbors.");
    }
    private sealed class State : IDisposable
    {
        private readonly MetalGuestFailureTests.PresenterState state = new();
        private readonly FieldInfo field = typeof(GuestGpu).GetField("_instance", Private)!;
        private readonly object? prior;
        public State() { prior = field.GetValue(null); field.SetValue(null, RuntimeHelpers.GetUninitializedObject(typeof(MetalGuestGpuBackend))); }
        public void Dispose() { GuestImageWriteTracker.Untrack(Address); field.SetValue(null, prior); state.Dispose(); }
    }
}
