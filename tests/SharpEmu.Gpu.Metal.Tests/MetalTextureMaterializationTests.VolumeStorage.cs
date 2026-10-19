// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
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
    [InlineData(1u)]
    [InlineData(3u)]
    public void ProductionUintVolumeSeedAndTranslatedStorePreserveEveryOtherSlice(uint depth)
    {
        const uint width = 3, height = 2, pitch = 5;
        var seed = new byte[pitch * height * depth * 4];
        Array.Fill(seed, (byte)0xDD); // padding must never become a texel
        var expected = new byte[width * height * depth * 4];
        for (uint z = 0; z < depth; z++)
        for (uint y = 0; y < height; y++)
        for (uint x = 0; x < width; x++)
        for (uint c = 0; c < 4; c++)
        {
            var value = (byte)(17 + z * 53 + y * 19 + x * 7 + c);
            seed[((z * height + y) * pitch + x) * 4 + c] = value;
            expected[((z * height + y) * width + x) * 4 + c] = value;
        }
        var address = 0x71F3_3000_00UL + depth * 0x10000;
        var guest = Guest(10, 1, depth, false, height, seed, extra: new()
        {
            ["Address"] = address, ["Width"] = width, ["Pitch"] = pitch,
            ["Format"] = 10u, ["NumberType"] = 4u, ["IsStorage"] = true,
            ["Access"] = GuestImageAccess.Read | GuestImageAccess.Write, ["Descriptor"] = VolumeResourceWords(address, width, height, depth, 0),
            ["Shape"] = new ShaderImageShape(true, false, true, false, TextureNumericClass.Uint),
        });
        var selectedZ = depth - 1;
        uint[] coordinate = [1, 1, selectedZ];
        uint[] valueToStore = [211, 29, 87, 143];
        Assert.True(Gen5ImageFixtures.TryCompile("ImageStore", 10, out var store, out var error,
            unifiedFormat: 60, addressValues: coordinate, storeValues: valueToStore), error);
        Assert.Contains("texture3d<uint", store!.Source);
        Assert.True(Gen5ImageFixtures.TryCompile("ImageLoad", 10, out var load, out error,
            unifiedFormat: 60, addressValues: coordinate, storeResults: true), error);
        if (!RequireDevice()) return;
        var image = Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [Device.Value, guest])!;
        var texture = (nint)image.GetType().GetField("Texture")!.GetValue(image)!;
        nint library = 0, loadLibrary = 0;
        try
        {
            Assert.Equal(7UL, ReadInteger(texture, Selector("textureType")));
            Assert.Equal(depth, ReadInteger(texture, Selector("depth")));
            Assert.Equal(73UL, ReadInteger(texture, Selector("pixelFormat")));
            Assert.Equal(3UL, ReadInteger(texture, Selector("usage"))); // no render target usage
            Assert.Equal(expected, ReadNativeVolume(texture, width, height, depth));
            Assert.True(DeviceShader.TryCompileLibrary(store.Source, out library, out error), error);
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(library, store.EntryPoint, new byte[16],
                new byte[PushData.ByteSize], store.ArgumentLayout!, 1, out _, out error, texture), error);
            for (uint c = 0; c < 4; c++)
                expected[((selectedZ * height + 1) * width + 1) * 4 + c] = (byte)valueToStore[c];
            Assert.Equal(expected, ReadNativeVolume(texture, width, height, depth));
            Assert.True(DeviceShader.TryCompileLibrary(load!.Source, out loadLibrary, out error), error);
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(loadLibrary, load.EntryPoint, new byte[16],
                new byte[PushData.ByteSize], load.ArgumentLayout!, 1, out var result, out error, texture), error);
            for (var c = 0; c < 4; c++)
                Assert.Equal(valueToStore[c], System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(c * 4)));
            output.WriteLine($"Actual production RGBA8Uint texture3d depth={depth}, padded pitch={pitch}: every seed slice and guest-translated selected store/load matched sentinel oracle.");
        }
        finally
        {
            if (loadLibrary != 0) SendVoid(loadLibrary, Selector("release"));
            if (library != 0) SendVoid(library, Selector("release"));
            ReleaseStorageVolume(address, texture);
        }
    }

    [Fact]
    public void ProductionPoppySingletonMode27SnapshotRetainsOriginTexelAndNativeVolume()
    {
        const ulong address = 0x71F3_4000_00;
        var source = new byte[65536];
        Array.Fill(source, (byte)0xE7);
        byte[] expected = [11, 47, 103, 199];
        expected.CopyTo(source, 0); // independently qualified origin at base0/XOR0
        var backendField = typeof(GuestGpu).GetField("_instance", Methods)!;
        var previous = backendField.GetValue(null);
        var backendType = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalGuestGpuBackend");
        backendField.SetValue(null, RuntimeHelpers.GetUninitializedObject(backendType));
        nint texture = 0;
        try
        {
            var descriptor = new AgcExports.TextureDescriptor(address, 1, 1, 10, 4, 27, 10, 0, 0, 1, 0xFAC, Depth: 1, HasExtendedDescriptor: true);
            var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [], ResourceDescriptor: VolumeResourceWords(address, 1, 1, 1, 27),
                Shape: new ShaderImageShape(true, false, true, false, TextureNumericClass.Uint), Access: GuestImageAccess.Write);
            var snapshots = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalTextureSnapshots");
            var args = new object?[] { new CpuContext(new SnapshotMemory(address, source), Generation.Gen5), new[] { binding }, 0 };
            var snapshot = Assert.Single((IReadOnlyList<GuestDrawTexture>)snapshots.GetMethod("CreateTextureSnapshots", Methods)!.Invoke(null, args)!);
            Assert.Equal(expected, snapshot.RgbaPixels);
            if (!RequireDevice()) return;
            var image = Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [Device.Value, snapshot])!;
            texture = (nint)image.GetType().GetField("Texture")!.GetValue(image)!;
            Assert.Equal(7UL, ReadInteger(texture, Selector("textureType")));
            Assert.Equal(1UL, ReadInteger(texture, Selector("depth")));
            Assert.Equal(expected, ReadNativeVolume(texture, 1, 1, 1));
            output.WriteLine("Actual Poppy-shaped mode27 singleton -> native RGBA8Uint texture3d depth1; origin texel preserved against nonzero tile padding.");
        }
        finally
        {
            if (texture != 0) ReleaseStorageVolume(address, texture);
            GuestImageWriteTracker.Untrack(address);
            backendField.SetValue(null, previous);
        }
    }

    private static uint[] VolumeResourceWords(ulong address, uint width, uint height, uint depth, uint tile) =>
        [(uint)(address >> 8), (uint)(address >> 40) | (60u << 20) | (((width - 1) & 3) << 30),
         ((width - 1) >> 2) | ((height - 1) << 14), (10u << 28) | (tile << 20) | 0xFAC, depth - 1, 0, 0, 0];

    private static void ReleaseStorageVolume(ulong address, nint texture)
    {
        ((IDictionary)Presenter.GetField("_guestImages", Methods)!.GetValue(null)!).Remove(address);
        ((IDictionary)Presenter.GetField("_guestImageExtents", Methods)!.GetValue(null)!).Remove(address);
        SendVoid(texture, Selector("release"));
    }

    private static unsafe byte[] ReadNativeVolume(nint texture, uint width, uint height, uint depth, uint bytesPerPixel = 4)
    {
        var result = new byte[width * height * depth * bytesPerPixel];
        fixed (byte* bytes = result)
            GetBytes(texture, Selector("getBytes:bytesPerRow:bytesPerImage:fromRegion:mipmapLevel:slice:"),
                (nint)bytes, width * bytesPerPixel, width * height * bytesPerPixel,
                new Region { Width = width, Height = height, Depth = depth }, 0, 0);
        return result;
    }
}
