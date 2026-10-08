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
    [InlineData(5u, 0u, 0u, true)]
    [InlineData(5u, 1u, 0u, false)]
    [InlineData(5u, 0u, 1u, false)]
    [InlineData(5u, 0u, uint.MaxValue, false)]
    [InlineData(60u, 0u, 0u, true)]
    [InlineData(60u, 1u, 0u, false)]
    [InlineData(60u, 0u, 1u, false)]
    [InlineData(60u, 0u, uint.MaxValue, false)]
    public void SmallUintArrayNativeOwnedSeedTypedStoreLoadAndSliceBounds(uint unified, uint x, uint layer, bool writes)
    {
        var single = unified == 5;
        var address = single ? 0x71F5_6000_00UL : 0x71F5_7000_00UL;
        const TextureNumericClass numeric = TextureNumericClass.Uint;
        const string mslType = "uint";
        var nativeFormat = single ? 13u : 73u;
        var bytes = single ? 1u : 4u;
        var dstSelect = single ? 0x204u : 0xFACu;
        var tile = single ? 24u : 27u;
        byte[] seed = single ? [0xA7] : [0x17, 0x53, 0x95, 0xE3];
        byte[] stored = single ? [0xD9] : [0xCD, 0x2B, 0x91, 0x07];
        uint[] seedChannels = single ? [0xA7, 0, 0, 1] : [0x17, 0x53, 0x95, 0xE3];
        uint[] storedChannels = single ? [0xD9, 0, 0, 1] : [0xCD, 0x2B, 0x91, 0x07];
        uint[] storeValues = [0xCD, 0x2B, 0x91, 0x07];
        if (single) storeValues[0] = 0xD9;
        uint[] raw = [(uint)(address >> 8), (unified << 20), 0, (13u << 28) | (tile << 20) | dstSelect, 0, 0x00700000, 0, 0];
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        var source = Enumerable.Repeat((byte)0xD3, 65536).ToArray();
        seed.CopyTo(source, 0); var original = source.ToArray();
        var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [], IsArrayed: true, ResourceDescriptor: raw,
            Shape: new ShaderImageShape(false, true, true, false, numeric), Access: GuestImageAccess.Write);
        Assert.True(Gen5ImageFixtures.TryCompile("ImageStore", 13, out var store, out var error,
            unifiedFormat: unified, dstSelect: dstSelect, addressValues: [x, 0, layer], storeValues: storeValues), error);
        Assert.Contains($"texture2d_array<{mslType}", store!.Source); Assert.Contains("get_array_size()", store.Source);
        Assert.True(Gen5ImageFixtures.TryCompile("ImageLoad", 13, out var load, out error,
            unifiedFormat: unified, dstSelect: dstSelect, addressValues: [0, 0, 0], storeResults: true), error);
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
            Assert.Equal(original.AsSpan(0, (int)bytes).ToArray(), snapshot.RgbaPixels);
            Assert.Equal(65536UL, snapshot.SourceByteCount); Assert.Equal(raw, snapshot.Descriptor);
            source[0] ^= 0xFF; // Upload must consume detached original integer bytes.
            var image = Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [Device.Value, snapshot])!;
            texture = (nint)image.GetType().GetField("Texture")!.GetValue(image)!;
            Assert.Equal(3UL, ReadInteger(texture, Selector("textureType")));
            Assert.Equal(1UL, ReadInteger(texture, Selector("arrayLength")));
            Assert.Equal((ulong)nativeFormat, ReadInteger(texture, Selector("pixelFormat"))); // exact typed format, never a converted numeric class
            Assert.Equal(3UL, ReadInteger(texture, Selector("usage")));
            Assert.Equal(seed, ReadNativeRows(texture, 1, 1, bytes));
            Assert.True(DeviceShader.TryCompileLibrary(store.Source, out storeLibrary, out error), error);
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(storeLibrary, store.EntryPoint, new byte[16],
                new byte[PushData.ByteSize], store.ArgumentLayout!, 1, out _, out error, texture), error);
            var expected = writes ? stored : seed;
            Assert.Equal(expected, ReadNativeRows(texture, 1, 1, bytes));
            // FAC preserves all four independent bytes; X001 supplies Uint0/0/1.
            // This explicit sampled view is test-only, not the production sampler resolver.
            view = NewSwizzledVolumeView(texture, Selector("newTextureViewWithPixelFormat:textureType:levels:slices:swizzle:"),
                nativeFormat, 3, new NativeRange { Location = 0, Length = 1 }, new NativeRange { Location = 0, Length = 1 },
                single ? new NativeSwizzle { Red = 2, Green = 0, Blue = 0, Alpha = 1 }
                    : new NativeSwizzle { Red = 2, Green = 3, Blue = 4, Alpha = 5 });
            Assert.NotEqual((nint)0, view);
            Assert.True(DeviceShader.TryCompileLibrary(load.Source, out loadLibrary, out error), error);
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(loadLibrary, load.EntryPoint, new byte[16],
                new byte[PushData.ByteSize], load.ArgumentLayout!, 1, out var result, out error, view), error);
            var channels = writes ? storedChannels : seedChannels;
            for (var c = 0; c < 4; c++) Assert.Equal(channels[c], BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(c * 4)));
            Assert.Equal(original.AsSpan((int)bytes).ToArray(), source.AsSpan((int)bytes).ToArray()); // physical padding/neighbor sentinels
            output.WriteLine($"Actual {(single ? "R8Uint" : "RGBA8Uint")} mode{tile}/PERF7 dst={dstSelect:X} native2DArray layer1; {mslType} store({x},0,slice={layer}) writes={writes} physical={Convert.ToHexString(expected)}, all4 exact integer load channels and{65536 - bytes} guest neighbor bytes matched.");
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
