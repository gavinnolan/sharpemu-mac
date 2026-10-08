// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Metal.Tests;
using Xunit;
using DeviceShader = SharpEmu.ShaderCompiler.Metal.Tests.MetalNative;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Theory]
    [InlineData(22u, 0u, 0u, true)]
    [InlineData(22u, 1u, 0u, false)]
    [InlineData(22u, 0u, 1u, false)]
    [InlineData(22u, 0u, uint.MaxValue, false)]
    public void R32FloatArrayNativeOwnedSeedTypedStoreLoadAndSliceBounds(uint unified, uint x, uint layer, bool writes)
    {
        const ulong address = 0x71F5_4000_00;
        const TextureNumericClass numeric = TextureNumericClass.Float;
        const string mslType = "float";
        const uint nativeFormat = 55;
        const uint seed = 0xC19A0000; // finite -19.25f
        const uint stored = 0x40490000; // finite 3.140625f
        uint[] raw = [(uint)(address >> 8), (unified << 20), 0, 0xD1B00204, 0, 0x00700000, 0, 0];
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        var source = Enumerable.Repeat((byte)0xD3, 65536).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(source, seed); var original = source.ToArray();
        var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [], IsArrayed: true, ResourceDescriptor: raw,
            Shape: new ShaderImageShape(false, true, true, false, numeric), Access: GuestImageAccess.Write);
        Assert.True(Gen5ImageFixtures.TryCompile("ImageStore", 13, out var store, out var error,
            unifiedFormat: unified, dstSelect: 0x204, addressValues: [x, 0, layer], storeValues: [stored, 0x13579BDF, 0x2468ACE0, 0x12345678]), error);
        Assert.Contains($"texture2d_array<{mslType}", store!.Source); Assert.Contains("get_array_size()", store.Source);
        Assert.True(Gen5ImageFixtures.TryCompile("ImageLoad", 13, out var load, out error,
            unifiedFormat: unified, dstSelect: 0x204, addressValues: [0, 0, 0], storeResults: true), error);
        Assert.Contains($"texture2d_array<{mslType}", load!.Source);
        if (!RequireDevice()) return; // Compile-only when disabled, never counted as native execution.
        var backend = typeof(GuestGpu).GetField("_instance", Methods)!; var previous = backend.GetValue(null);
        backend.SetValue(null, RuntimeHelpers.GetUninitializedObject(Presenter.Assembly.GetTypes().Single(static t => t.Name == "MetalGuestGpuBackend")));
        nint texture = 0, view = 0, storeLibrary = 0, loadLibrary = 0;
        try
        {
            var snapshots = Presenter.Assembly.GetTypes().Single(static t => t.Name == "MetalTextureSnapshots");
            var snapshot = Assert.Single((IReadOnlyList<GuestDrawTexture>)snapshots.GetMethod("CreateTextureSnapshots", Methods)!.Invoke(null,
                [new CpuContext(new SnapshotMemory(address, source), Generation.Gen5), new[] { binding }, 0])!);
            Assert.Equal(original.AsSpan(0, 4).ToArray(), snapshot.RgbaPixels);
            Assert.Equal(65536UL, snapshot.SourceByteCount); Assert.Equal(raw, snapshot.Descriptor);
            source[0] ^= 0xFF; // Upload must consume detached original float bits.
            var image = Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [Device.Value, snapshot])!;
            texture = (nint)image.GetType().GetField("Texture")!.GetValue(image)!;
            Assert.Equal(3UL, ReadInteger(texture, Selector("textureType")));
            Assert.Equal(1UL, ReadInteger(texture, Selector("arrayLength")));
            Assert.Equal((ulong)nativeFormat, ReadInteger(texture, Selector("pixelFormat"))); // exact typed format, never a converted numeric class
            Assert.Equal(3UL, ReadInteger(texture, Selector("usage")));
            Assert.Equal(seed, BinaryPrimitives.ReadUInt32LittleEndian(ReadNativeRows(texture, 1, 1, 4)));
            Assert.True(DeviceShader.TryCompileLibrary(store.Source, out storeLibrary, out error), error);
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(storeLibrary, store.EntryPoint, new byte[16],
                new byte[PushData.ByteSize], store.ArgumentLayout!, 1, out _, out error, texture), error);
            var expected = writes ? stored : seed;
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(ReadNativeRows(texture, 1, 1, 4)));
            // DstSelect204 is X001; explicit sampled view supplies component
            // selection to the load-only class. Float constant ONE has the bits of1.0f.
            view = NewSwizzledVolumeView(texture, Selector("newTextureViewWithPixelFormat:textureType:levels:slices:swizzle:"),
                nativeFormat, 3, new NativeRange { Location = 0, Length = 1 }, new NativeRange { Location = 0, Length = 1 },
                new NativeSwizzle { Red = 2, Green = 0, Blue = 0, Alpha = 1 });
            Assert.NotEqual((nint)0, view);
            Assert.True(DeviceShader.TryCompileLibrary(load.Source, out loadLibrary, out error), error);
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(loadLibrary, load.EntryPoint, new byte[16],
                new byte[PushData.ByteSize], load.ArgumentLayout!, 1, out var result, out error, view), error);
            uint[] channels = [expected, 0, 0, 0x3F800000u];
            for (var c = 0; c < 4; c++) Assert.Equal(channels[c], BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(c * 4)));
            Assert.Equal(original.AsSpan(4).ToArray(), source.AsSpan(4).ToArray()); // physical padding/neighbor sentinels
            output.WriteLine($"Actual R32Float mode27/PERF7 X001 native2DArray layer1; {mslType} store({x},0,slice={layer}) writes={writes} physical={expected:X8}, all4 exact typed load channels and65532 guest neighbor bytes matched.");
        }
        finally
        {
            if (view != 0) SendVoid(view, Selector("release"));
            if (loadLibrary != 0) SendVoid(loadLibrary, Selector("release"));
            if (storeLibrary != 0) SendVoid(storeLibrary, Selector("release"));
            if (texture != 0) ReleaseStorageVolume(address, texture);
            GuestImageWriteTracker.Untrack(address); backend.SetValue(null, previous);
        }
    }
}
