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
using SharpEmu.Tests.Fixtures;
using DeviceShader = SharpEmu.ShaderCompiler.Metal.Tests.MetalNative;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Theory]
    [InlineData(257u, 511u, 0u, true)]
    [InlineData(3839u, 2159u, 0u, true)]
    [InlineData(257u, 511u, 1u, false)]
    [InlineData(3840u, 0u, 0u, false)]
    public void R8FourKArrayNativeFullSeedTypedStoreLoadAndBounds(uint x, uint y, uint layer, bool writes)
    {
        const ulong address = 0x71F5_2000_00;
        uint[] raw = [(uint)(address >> 8), (uint)(address >> 40) | 0xC0500000u, 0x021BC3BF,
            (13u << 28) | (24u << 20) | 0x204, 0, 0x00700000, 0, 0];
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        var source = R8FourKOracle.PhysicalSeed();
        var seed = R8FourKOracle.LinearSeed(); var original = source.ToArray();
        var loadX = writes ? x : 257u; var loadY = writes ? y : 511u;
        var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [], IsArrayed: true, ResourceDescriptor: raw,
            Shape: new ShaderImageShape(false, true, true, false, TextureNumericClass.Uint), Access: GuestImageAccess.Write);
        // Logical Uint X001 store: physical R=173; the other view lanes are constants.
        // Independent full-byte oracle does not use translator swizzle helpers.
        uint[] bits = [0xAD, 0, 0, 1];
        Assert.True(Gen5ImageFixtures.TryCompile("ImageStore", 13, out var store, out var error,
            unifiedFormat: 5, dstSelect: 0x204, addressValues: [x, y, layer], storeValues: bits), error);
        Assert.Contains("texture2d_array<uint", store!.Source); Assert.Contains("get_array_size()", store.Source);
        Assert.True(Gen5ImageFixtures.TryCompile("ImageLoad", 13, out var load, out error,
            unifiedFormat: 5, dstSelect: 0x204, addressValues: [loadX, loadY, 0], storeResults: true), error);
        if (!RequireDevice()) return; // explicitly gated native proof, never counted as device coverage while disabled
        var backendField = typeof(GuestGpu).GetField("_instance", Methods)!; var previous = backendField.GetValue(null);
        backendField.SetValue(null, RuntimeHelpers.GetUninitializedObject(Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalGuestGpuBackend")));
        nint texture = 0, sampledView = 0, storeLibrary = 0, loadLibrary = 0;
        try
        {
            var args = new object?[] { new CpuContext(new SnapshotMemory(address, source), Generation.Gen5), new[] { binding }, 0 };
            var snapshots = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalTextureSnapshots");
            var snapshot = Assert.Single((IReadOnlyList<GuestDrawTexture>)snapshots.GetMethod("CreateTextureSnapshots", Methods)!.Invoke(null, args)!);
            Assert.Equal(seed, snapshot.RgbaPixels); Assert.Equal(8847360UL, snapshot.SourceByteCount); Assert.Equal(raw, snapshot.Descriptor);
            source[0] ^= 0xFF; // native upload must use the original owned copy
            var image = Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [Device.Value, snapshot])!;
            texture = (nint)image.GetType().GetField("Texture")!.GetValue(image)!;
            Assert.Equal(3UL, ReadInteger(texture, Selector("textureType")));
            Assert.Equal(1UL, ReadInteger(texture, Selector("arrayLength")));
            Assert.Equal(1UL, ReadInteger(texture, Selector("depth")));
            Assert.Equal(13UL, ReadInteger(texture, Selector("pixelFormat")));
            Assert.Equal(3UL, ReadInteger(texture, Selector("usage")));
            Assert.Equal(3840UL, ReadInteger(texture, Selector("width"))); Assert.Equal(2160UL, ReadInteger(texture, Selector("height")));
            Assert.Equal(seed, ReadNativeRows(texture, 3840, 2160, 1));
            Assert.True(DeviceShader.TryCompileLibrary(store.Source, out storeLibrary, out error), error);
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(storeLibrary, store.EntryPoint, new byte[16],
                new byte[PushData.ByteSize], store.ArgumentLayout!, 1, out _, out error, texture), error);
            // Only the physical R byte may change for this exact typed view.
            var expected = seed.ToArray();
            if (writes) expected[checked((int)(y * 3840 + x))] = 0xAD;
            Assert.Equal(expected, ReadNativeRows(texture, 3840, 2160, 1));
            // ImageLoad-only normalizes to a sampled class; its read swizzle is
            // supplied by the native view, as the production sampled binding
            // contract requires. Keep the writable underlying array identity.
            sampledView = NewSwizzledVolumeView(texture,
                Selector("newTextureViewWithPixelFormat:textureType:levels:slices:swizzle:"),
                13, 3, new NativeRange { Location = 0, Length = 1 }, new NativeRange { Location = 0, Length = 1 },
                new NativeSwizzle { Red = 2, Green = 0, Blue = 0, Alpha = 1 });
            Assert.NotEqual((nint)0, sampledView);
            Assert.True(DeviceShader.TryCompileLibrary(load!.Source, out loadLibrary, out error), error);
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(loadLibrary, load.EntryPoint, new byte[16],
                new byte[PushData.ByteSize], load.ArgumentLayout!, 1, out var result, out error, sampledView), error);
            var pixel = checked((int)(loadY * 3840 + loadX));
            uint[] expectedLogical = [expected[pixel], 0, 0, 1];
            for (var component = 0; component < 4; component++)
                Assert.Equal(expectedLogical[component], System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(component * 4)));
            original[0] ^= 0xFF;
            Assert.Equal(original, source); // only the explicit post-capture mutation; GPU does not publish tiled pixels into guest RAM
            output.WriteLine($"Actual mode24 R8Uint 3840x2160 array: full8,294,400-byte native seed/post-store readback + translated typed X001 output match at({x},{y},layer={layer}) writes={writes}; all other pixels/guest padding unchanged.");
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
