// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
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
    [InlineData(0u, 0u, 0u, true)]
    [InlineData(0u, 0u, 1u, false)] // singleton array still has an explicit layer bound
    [InlineData(0u, 0u, uint.MaxValue, false)]
    [InlineData(1u, 0u, 0u, false)]
    public void ProductionSingletonArraySeedAndTranslatedStoreLoadKeepNativeSliceSemantics(uint x, uint y, uint layer, bool writes)
    {
        const ulong address = 0x71F5_2000_00;
        uint[] raw = [(uint)(address >> 8), (uint)(address >> 40) | (56u << 20), 0,
            (13u << 28) | (27u << 20) | 0xF2E, 0, 0x00700000, 0, 0];
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        var source = Enumerable.Repeat((byte)0xD3, 65536).ToArray();
        byte[] seed = [17, 83, 149, 227]; seed.CopyTo(source, 0); var original = source.ToArray();
        var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [], IsArrayed: true, ResourceDescriptor: raw,
            Shape: new ShaderImageShape(false, true, true, false, TextureNumericClass.Float), Access: GuestImageAccess.Write);
        // Logical BGRA-view values R=1,G=0.5,B=0,A=0.25; R!=B.
        // The physical oracle below is explicit, independent of translator helpers.
        uint[] bits = [0x3F800000, 0x3F000000, 0, 0x3E800000];
        Assert.True(Gen5ImageFixtures.TryCompile("ImageStore", 13, out var store, out var error,
            unifiedFormat: 56, dstSelect: 0xF2E, addressValues: [x, y, layer], storeValues: bits), error);
        Assert.Contains("texture2d_array<float", store!.Source); Assert.Contains("get_array_size()", store.Source);
        Assert.True(Gen5ImageFixtures.TryCompile("ImageLoad", 13, out var load, out error,
            unifiedFormat: 56, dstSelect: 0xF2E, addressValues: [0, 0, 0], storeResults: true), error);
        if (!RequireDevice()) return; // explicitly gated native proof, never counted as device coverage while disabled
        var backendField = typeof(GuestGpu).GetField("_instance", Methods)!; var previous = backendField.GetValue(null);
        backendField.SetValue(null, RuntimeHelpers.GetUninitializedObject(Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalGuestGpuBackend")));
        nint texture = 0, sampledView = 0, storeLibrary = 0, loadLibrary = 0;
        try
        {
            var args = new object?[] { new CpuContext(new SnapshotMemory(address, source), Generation.Gen5), new[] { binding }, 0 };
            var snapshots = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalTextureSnapshots");
            var snapshot = Assert.Single((IReadOnlyList<GuestDrawTexture>)snapshots.GetMethod("CreateTextureSnapshots", Methods)!.Invoke(null, args)!);
            Assert.Equal(seed, snapshot.RgbaPixels); Assert.Equal(65536UL, snapshot.SourceByteCount); Assert.Equal(raw, snapshot.Descriptor);
            source[0] ^= 0xFF; // native upload must use the original owned copy
            var image = Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [Device.Value, snapshot])!;
            texture = (nint)image.GetType().GetField("Texture")!.GetValue(image)!;
            Assert.Equal(3UL, ReadInteger(texture, Selector("textureType")));
            Assert.Equal(1UL, ReadInteger(texture, Selector("arrayLength")));
            Assert.Equal(1UL, ReadInteger(texture, Selector("depth")));
            Assert.Equal(70UL, ReadInteger(texture, Selector("pixelFormat")));
            Assert.Equal(3UL, ReadInteger(texture, Selector("usage")));
            Assert.Equal(seed, ReadNativeRows(texture, 1, 1, 4));
            Assert.True(DeviceShader.TryCompileLibrary(store.Source, out storeLibrary, out error), error);
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(storeLibrary, store.EntryPoint, new byte[16],
                new byte[PushData.ByteSize], store.ArgumentLayout!, 1, out _, out error, texture), error);
            // F2E means B,G,R,A. Inverse view store places logical B in
            // physical R and logical R in physical B; normalized bytes are
            // independently specified (not GetImageStoreSourceIndex output).
            var expected = writes ? new byte[] { 0, 128, 255, 64 } : seed;
            Assert.Equal(expected, ReadNativeRows(texture, 1, 1, 4));
            // ImageLoad-only normalizes to a sampled class; its read swizzle is
            // supplied by the native view, as the production sampled binding
            // contract requires. Keep the writable underlying array identity.
            sampledView = NewSwizzledVolumeView(texture,
                Selector("newTextureViewWithPixelFormat:textureType:levels:slices:swizzle:"),
                70, 3, new NativeRange { Location = 0, Length = 1 }, new NativeRange { Location = 0, Length = 1 },
                new NativeSwizzle { Red = 4, Green = 3, Blue = 2, Alpha = 5 });
            Assert.NotEqual((nint)0, sampledView);
            Assert.True(DeviceShader.TryCompileLibrary(load!.Source, out loadLibrary, out error), error);
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(loadLibrary, load.EntryPoint, new byte[16],
                new byte[PushData.ByteSize], load.ArgumentLayout!, 1, out var result, out error, sampledView), error);
            byte[] expectedLogical = writes ? [255, 128, 0, 64] : [149, 83, 17, 227];
            for (var component = 0; component < 4; component++)
            {
                var actual = BitConverter.UInt32BitsToSingle(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(component * 4)));
                Assert.InRange(Math.Abs(actual - expectedLogical[component] / 255.0f), 0, 1e-6f);
            }
            Assert.Equal(original.AsSpan(4).ToArray(), source.AsSpan(4).ToArray()); // untouched guest physical padding/neighbors
            output.WriteLine($"Actual raw mode27 PerfMod7/F2E singleton RGBA8Unorm -> native texture2d_array layer1; translated MIMG store ({x},{y},slice={layer}) writes={writes}, all4 physical RGBA bytes and distinct logical BGRA-view float load matched; guest padding unchanged.");
        }
        finally
        {
            if (sampledView != 0) SendVoid(sampledView, Selector("release"));
            if (loadLibrary != 0) SendVoid(loadLibrary, Selector("release"));
            if (storeLibrary != 0) SendVoid(storeLibrary, Selector("release"));
            if (texture != 0) ReleaseStorageVolume(address, texture);
            GuestImageWriteTracker.Untrack(address); backendField.SetValue(null, previous);
        }
    }
}
