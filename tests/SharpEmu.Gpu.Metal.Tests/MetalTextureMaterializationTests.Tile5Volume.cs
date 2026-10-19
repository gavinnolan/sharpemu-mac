// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Metal.Tests;
using SharpEmu.Tests.Fixtures;
using Xunit;
using DeviceShader = SharpEmu.ShaderCompiler.Metal.Tests.MetalNative;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Fact]
    public void ExactMode5R16VolumeUploadsEveryOracleTexelAndTranslatedWritePreservesAllOthers()
    {
        var (source, expected) = Tile5VolumeOracle.Seed();
        var raw = Tile5VolumeOracle.Raw;
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [], ResourceDescriptor: raw,
            Shape: new ShaderImageShape(true, false, true, false, TextureNumericClass.Uint), Access: GuestImageAccess.Write);
        // Exercise Z15 and the second physical4KiB X block. R16 stores only X;
        // nonzero ignored components detect accidental RGBA upload/reinterpretation.
        uint[] coordinate = [13, 7, 15];
        uint[] write = [54321, 211, 987, 319];
        Assert.True(Gen5ImageFixtures.TryCompile("ImageStore", 10, out var store, out var error,
            unifiedFormat: 11, dstSelect: 4, addressValues: coordinate, storeValues: write), error);
        Assert.True(Gen5ImageFixtures.TryCompile("ImageLoad", 10, out var load, out error,
            unifiedFormat: 11, dstSelect: 4, addressValues: coordinate, storeResults: true), error);
        // ImageLoad-only is a sampled binding; Materializer retains identity
        // shader swizzle because the native sampled view performs T# DST_SEL.
        Assert.Contains("sampled_uint_3d", load!.Source);
        Assert.Contains("texture3d<uint", store!.Source);
        if (!RequireDevice()) return;
        var backendField = typeof(GuestGpu).GetField("_instance", Methods)!;
        var previous = backendField.GetValue(null);
        var backendType = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalGuestGpuBackend");
        backendField.SetValue(null, RuntimeHelpers.GetUninitializedObject(backendType));
        nint texture = 0, sampledView = 0, storeLibrary = 0, loadLibrary = 0;
        try
        {
            var snapshots = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalTextureSnapshots");
            var args = new object?[] { new CpuContext(new SnapshotMemory(descriptor.Address, source), Generation.Gen5), new[] { binding }, 0 };
            var snapshot = Assert.Single((IReadOnlyList<GuestDrawTexture>)snapshots.GetMethod("CreateTextureSnapshots", Methods)!.Invoke(null, args)!);
            Assert.Equal(expected, snapshot.RgbaPixels);
            var image = Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [Device.Value, snapshot])!;
            texture = (nint)image.GetType().GetField("Texture")!.GetValue(image)!;
            Assert.Equal(7UL, ReadInteger(texture, Selector("textureType")));
            Assert.Equal(16UL, ReadInteger(texture, Selector("depth")));
            Assert.Equal(23UL, ReadInteger(texture, Selector("pixelFormat"))); // R16Uint
            Assert.Equal(3UL, ReadInteger(texture, Selector("usage")));
            Assert.Equal(expected, ReadNativeVolume(texture, 16, 16, 16, bytesPerPixel: 2));
            Assert.True(DeviceShader.TryCompileLibrary(store.Source, out storeLibrary, out error), error);
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(storeLibrary, store.EntryPoint, new byte[16],
                new byte[PushData.ByteSize], store.ArgumentLayout!, 1, out _, out error, texture), error);
            BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(((15 * 16 + 7) * 16 + 13) * 2), (ushort)write[0]);
            Assert.Equal(expected, ReadNativeVolume(texture, 16, 16, 16, bytesPerPixel: 2));
            sampledView = NewSwizzledVolumeView(texture,
                Selector("newTextureViewWithPixelFormat:textureType:levels:slices:swizzle:"),
                23, 7, new NativeRange { Location = 0, Length = 1 }, new NativeRange { Location = 0, Length = 1 },
                new NativeSwizzle { Red = 2, Green = 0, Blue = 0, Alpha = 0 });
            Assert.NotEqual((nint)0, sampledView);
            Assert.Equal(7UL, ReadInteger(sampledView, Selector("textureType")));
            Assert.Equal(16UL, ReadInteger(sampledView, Selector("depth")));
            Assert.True(DeviceShader.TryCompileLibrary(load!.Source, out loadLibrary, out error), error);
            Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(loadLibrary, load.EntryPoint, new byte[16],
                new byte[PushData.ByteSize], load.ArgumentLayout!, 1, out var result, out error, sampledView), error);
            uint[] expectedLoad = [write[0], 0, 0, 0]; // actual T# DstSelect4/X000
            for (var c = 0; c < 4; c++)
                Assert.Equal(expectedLoad[c], BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(c * 4)));
            output.WriteLine("Exact GFX10 mode 5 R16Uint 16³: all 4096 primary AddrLib seeded texels matched actual native texture3d readback; translated store/load at(13,7,15) matched X000 through an explicit sampled view, with every other texel unchanged.");
        }
        finally
        {
            if (sampledView != 0) SendVoid(sampledView, Selector("release"));
            if (loadLibrary != 0) SendVoid(loadLibrary, Selector("release"));
            if (storeLibrary != 0) SendVoid(storeLibrary, Selector("release"));
            if (texture != 0) ReleaseStorageVolume(descriptor.Address, texture);
            GuestImageWriteTracker.Untrack(descriptor.Address);
            backendField.SetValue(null, previous);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRange { public nuint Location, Length; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSwizzle { public byte Red, Green, Blue, Alpha; }
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nint NewSwizzledVolumeView(nint texture, nint selector,
        nuint pixelFormat, nuint type, NativeRange levels, NativeRange slices, NativeSwizzle swizzle);
}
