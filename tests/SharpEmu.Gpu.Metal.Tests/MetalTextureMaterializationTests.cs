// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>Calls production texture creation/upload, then reads actual Metal
/// texture bytes. Reflection keeps the test independent of platform namespace
/// extraction and does not widen the shared guest-contract surface.</summary>
public sealed partial class MetalTextureMaterializationTests(ITestOutputHelper output)
{
    private static readonly Type Presenter = Assembly.Load("SharpEmu.Gpu.Metal").GetTypes()
        .Single(static type => type.Name == "MetalVideoPresenter");
    private static readonly Assembly Contracts = Assembly.Load("SharpEmu.Libs");
    private static readonly Type TextureType = Contracts.GetTypes().Single(static type => type.Name == "GuestDrawTexture");
    private static readonly Type MipType = Contracts.GetTypes().Single(static type => type.Name == "GuestTextureMipUpload");
    private const BindingFlags Methods = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    [Theory]
    [InlineData(10u, 0u, 70u)]
    [InlineData(14u, 7u, 125u)]
    [InlineData(14u, 4u, 123u)]
    [InlineData(169u, 0u, 130u)]
    [InlineData(175u, 1u, 141u)]
    public void KnownFormatsPreserveTheirNativePixelInterpretation(uint format, uint numberType, uint expected)
    {
        var formats = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalGuestFormats");
        var value = formats.GetMethod("DecodeTextureFormat", Methods)!.Invoke(null, [format, numberType])!;
        Assert.Equal(expected, Convert.ToUInt32(value.GetType().GetProperty("Format")!.GetValue(value)));
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(999u, 0u)]
    [InlineData(10u, 2u)]
    [InlineData(13u, 7u)]
    public void UnsupportedRealFormatsFailRatherThanReinterpretingBytes(uint format, uint numberType)
    {
        var formats = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalGuestFormats");
        var exception = Assert.Throws<TargetInvocationException>(() =>
            formats.GetMethod("DecodeTextureFormat", Methods)!.Invoke(null, [format, numberType]));
        Assert.IsType<NotSupportedException>(exception.InnerException);
        Assert.Contains($"{format}/{numberType}", exception.InnerException!.Message);
    }

    [Theory]
    [InlineData(8u, 1u, 1u, false, 2u)]
    [InlineData(12u, 1u, 1u, true, 3u)]
    [InlineData(13u, 1u, 1u, true, 3u)]
    [InlineData(13u, 3u, 1u, true, 3u)]
    [InlineData(11u, 6u, 1u, true, 3u)]
    [InlineData(10u, 1u, 1u, false, 7u)]
    [InlineData(10u, 1u, 3u, false, 7u)]
    public void ProductionUploadPreservesEveryLayerAndDepthSlice(uint type, uint layers,
        uint depth, bool arrayed, uint expectedMetalType)
    {
        var height = type is 8 or 12 ? 1u : 4u;
        var data = DistinctBytes(4, height, layers, depth, 0);
        var guest = Guest(type, layers, depth, arrayed, height, data);
        var shape = Presenter.GetMethod("GuestTextureShape", Methods)!.Invoke(null, [guest]);
        Assert.Equal(expectedMetalType, (uint)shape!.GetType().GetField("Item1")!.GetValue(shape)!);
        if (!RequireDevice()) return;
        var handle = Create(guest);
        try
        {
            Assert.Equal((ulong)expectedMetalType, ReadInteger(handle, Selector("textureType")));
            Assert.Equal(data, ReadTexture(handle, 4, height, layers, depth, 0));
        }
        finally { SendVoid(handle, Selector("release")); }
    }

    [Theory]
    [InlineData(8u, 1u, false)]
    [InlineData(12u, 3u, true)]
    [InlineData(9u, 1u, false)]
    [InlineData(13u, 3u, true)]
    public void ProductionMipUploadUsesEachRangeAndItsLayerStride(uint type, uint layers, bool arrayed)
    {
        var height = type is 8 or 12 ? 1u : 4u;
        var baseData = DistinctBytes(4, height, layers, 1, 0);
        var mipData = DistinctBytes(2, Math.Max(height / 2, 1), layers, 1, 1);
        var data = baseData.Concat(mipData).ToArray();
        var uploads = Array.CreateInstance(MipType, 2);
        uploads.SetValue(Activator.CreateInstance(MipType, [0UL, 0u, 4u, height, 4u]), 0);
        uploads.SetValue(Activator.CreateInstance(MipType, [(ulong)baseData.Length, 1u, 2u, Math.Max(height / 2, 1), 2u]), 1);
        var guest = Guest(type, layers, 1, arrayed, height, data, uploads);
        if (!RequireDevice()) return;
        var handle = Create(guest);
        try
        {
            Assert.Equal(2UL, ReadInteger(handle, Selector("mipmapLevelCount")));
            Assert.Equal(baseData, ReadTexture(handle, 4, height, layers, 1, 0));
            Assert.Equal(mipData, ReadTexture(handle, 2, Math.Max(height / 2, 1), layers, 1, 1));
        }
        finally { SendVoid(handle, Selector("release")); }
    }

    [Theory]
    [InlineData(14u)]
    [InlineData(15u)]
    public void UnsupportedMsaaSnapshotThrowsBeforeAnyNativeTextureIsCreated(uint type)
    {
        var guest = Guest(type, 1, 1, type == 15, 4, new byte[256]);
        var exception = Assert.Throws<TargetInvocationException>(() => Create(guest, device: 0));
        Assert.IsType<NotSupportedException>(exception.InnerException);
        Assert.Contains("per-sample", exception.InnerException!.Message);
    }

    [Fact]
    public void UndersizedRequiredTextureFailsBeforeNativeAllocation()
    {
        var guest = Guest(9, 1, 1, false, 4, [1, 2, 3]);
        var exception = Assert.Throws<TargetInvocationException>(() => Create(guest, device: 0));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("source_bytes=3 required_bytes=256", exception.InnerException!.Message);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(3u)]
    public void DeferredTiledSourceIsDetiledBeforeAnyGuestMemoryRecovery(uint layers)
    {
        var expected = DistinctBytes(4, 4, layers, 1, 0);
        var source = new byte[expected.Length];
        // A deliberately permuted block fixture makes raw upload and detile
        // observably different without using the production detiler as oracle.
        var table = Enumerable.Range(0, 16).Select(static pixel => 15 - pixel).ToArray();
        for (var layer = 0; layer < layers; layer++)
            for (var pixel = 0; pixel < 16; pixel++)
                Array.Copy(expected, layer * 256 + pixel * 16, source, layer * 256 + table[pixel] * 16, 16);
        var parametersType = Contracts.GetTypes().Single(static type => type.Name == "DetileParams");
        var equationType = Contracts.GetTypes().Single(static type => type.Name == "DetileEquation");
        var parameters = Activator.CreateInstance(parametersType,
            [Enum.ToObject(equationType, 2), 4, 4, 16, 4, 4, 16, 256, 1, Array.Empty<int>(), 0,
                Array.Empty<int>(), 0, table]);
        var guest = Guest(layers == 1 ? 9u : 13u, layers, 1, layers > 1, 4, [],
            extra: new() { ["Address"] = 0x1000UL, ["TileMode"] = 5u,
                ["TiledSource"] = source, ["Detile"] = parameters });
        if (!RequireDevice()) return;
        var handle = Create(guest);
        try { Assert.Equal(expected, ReadTexture(handle, 4, 4, layers, 1, 0)); }
        finally { SendVoid(handle, Selector("release")); }
    }

    [Fact]
    public void IncompleteLayerDataReportsActualDescriptorRatherThanInventingTexels()
    {
        if (!RequireDevice()) return;
        var guest = Guest(13, 3, 1, true, 4, DistinctBytes(4, 4, 1, 1, 0));
        var exception = Assert.Throws<TargetInvocationException>(() => Create(guest));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("layers=3", exception.InnerException!.Message);
        Assert.Contains("source_bytes=256", exception.InnerException.Message);
    }

    [Theory]
    [InlineData(9u, 1u, true, 16)]
    [InlineData(10u, 3u, false, 48)]
    [InlineData(11u, 1u, true, 96)]
    public void NullResourceAllocatesZeroBytesForItsActualFormatAndShape(uint type,
        uint depth, bool arrayed, int expectedBytes)
    {
        var guest = NullGuest(type, depth, arrayed, 14, 7);
        var data = (byte[])TextureType.GetProperty("RgbaPixels")!.GetValue(guest)!;
        Assert.Equal(expectedBytes, data.Length);
        Assert.All(data, static value => Assert.Equal(0, value));
        if (!RequireDevice()) return;
        var handle = Create(guest);
        try
        {
            var read = ReadTexture(handle, 1, 1, type == 11 ? 6u : 1u, type == 10 ? depth : 1u, 0);
            Assert.Equal(data, read);
        }
        finally { SendVoid(handle, Selector("release")); }
    }

    [Fact]
    public void AllZeroResourceUsesTheExplicitRgba8NullContract()
    {
        var guest = NullGuest(0, 1, false, 0, 0);
        Assert.Equal(10u, TextureType.GetProperty("Format")!.GetValue(guest));
        Assert.Equal(0u, TextureType.GetProperty("NumberType")!.GetValue(guest));
        Assert.Equal(9u, TextureType.GetProperty("Type")!.GetValue(guest));
        Assert.Equal(new byte[4], TextureType.GetProperty("RgbaPixels")!.GetValue(guest));
    }

    [Fact]
    public void RealInvalidResourceCannotBeTurnedIntoAFabricatedNullImage()
    {
        var exception = Assert.Throws<TargetInvocationException>(() => NullGuest(9, 1, false, 0, 0, 0x1000));
        Assert.IsType<NotSupportedException>(exception.InnerException);
        Assert.Contains("addr=0x1000", exception.InnerException!.Message);
    }

    private static object NullGuest(uint type, uint depth, bool arrayed, uint format,
        uint numberType, ulong address = 0)
    {
        var snapshots = Presenter.Assembly.GetTypes().Single(static candidate => candidate.Name == "MetalTextureSnapshots");
        var descriptorType = Contracts.GetTypes().Single(static candidate => candidate.Name == "TextureDescriptor");
        var constructor = descriptorType.GetConstructors().Single(static candidate => candidate.GetParameters().Length > 10);
        var values = new Dictionary<string, object?> { ["Address"] = address, ["Width"] = 1u,
            ["Height"] = 1u, ["Format"] = format, ["NumberType"] = numberType, ["Type"] = type, ["Depth"] = depth };
        var arguments = constructor.GetParameters().Select(parameter => values.TryGetValue(parameter.Name!, out var value)
            ? value : parameter.HasDefaultValue ? parameter.DefaultValue
                : Activator.CreateInstance(parameter.ParameterType)).ToArray();
        var descriptor = constructor.Invoke(arguments);
        return snapshots.GetMethod("CreateNullGuestDrawTexture", Methods)!.Invoke(null,
            [false, descriptor, arrayed, depth])!;
    }

    [Theory]
    [InlineData(2u, 7u, 2u)]
    [InlineData(11u, 4u, 8u)]
    public void ProductionStorageUploadPreservesPaddedNativeRowsAndFinalRow(uint format, uint number, uint bytesPerTexel)
    {
        if (!RequireDevice()) return;
        const uint width = 3, height = 2, pitch = 5;
        var bytes = new byte[8 * bytesPerTexel];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(17 + index * 7);
        var expected = bytes.AsSpan(0, checked((int)(width * bytesPerTexel))).ToArray()
            .Concat(bytes.AsSpan(checked((int)(pitch * bytesPerTexel)), checked((int)(width * bytesPerTexel))).ToArray()).ToArray();
        var address = 0x71F3_0000_00UL + format * 0x1000UL;
        var guest = Guest(9, 1, 1, false, height, bytes, extra: new()
        {
            ["Address"] = address, ["Width"] = width, ["Pitch"] = pitch,
            ["Format"] = format, ["NumberType"] = number, ["IsStorage"] = true,
        });
        var image = Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [Device.Value, guest])!;
        var texture = (nint)image.GetType().GetField("Texture")!.GetValue(image)!;
        try
        {
            Assert.True((bool)image.GetType().GetField("Initialized")!.GetValue(image)!);
            Assert.False((bool)image.GetType().GetField("GpuWritten")!.GetValue(image)!);
            var formats = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalGuestFormats");
            var native = formats.GetMethod("DecodeTextureFormat", Methods)!.Invoke(null, [format, number])!;
            var expectedFormat = Convert.ToUInt64(native.GetType().GetProperty("Format")!.GetValue(native));
            Assert.Equal(expectedFormat, ReadInteger(texture, Selector("pixelFormat")));
            Assert.Equal(expected, ReadNativeRows(texture, width, height, bytesPerTexel));
            output.WriteLine($"Actual production storage readback {format}/{number}: {width}x{height}, pitch={pitch}, bpp={bytesPerTexel}; every native row matched.");
        }
        finally
        {
            ((IDictionary)Presenter.GetField("_guestImages", Methods)!.GetValue(null)!).Remove(address);
            ((IDictionary)Presenter.GetField("_guestImageExtents", Methods)!.GetValue(null)!).Remove(address);
            SendVoid(texture, Selector("release"));
        }
    }

    private static unsafe byte[] ReadNativeRows(nint texture, uint width, uint height, uint bytesPerTexel)
    {
        var bytes = new byte[width * height * bytesPerTexel];
        fixed (byte* destination = bytes)
            GetBytes(texture, Selector("getBytes:bytesPerRow:bytesPerImage:fromRegion:mipmapLevel:slice:"),
                (nint)destination, width * bytesPerTexel, 0,
                new Region { Width = width, Height = height, Depth = 1 }, 0, 0);
        return bytes;
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Production upload execution requires the exclusive device gate.");
            return false;
        }
        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        return true;
    }

    private static object Guest(uint type, uint layers, uint depth, bool arrayed, uint height,
        byte[] data, Array? uploads = null, Dictionary<string, object?>? extra = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["Address"] = 0UL, ["Width"] = 4u, ["Height"] = height,
            ["Format"] = 14u, ["NumberType"] = 7u, ["RgbaPixels"] = data,
            ["IsFallback"] = false, ["IsStorage"] = false,
            ["Type"] = type, ["ArrayedView"] = arrayed, ["ArrayLayers"] = layers,
            ["Depth"] = depth, ["Pitch"] = 4u, ["MipUploads"] = uploads,
        };
        if (extra is not null)
            foreach (var (name, value) in extra) values[name] = value;
        var ctor = TextureType.GetConstructors().Single(static constructor => constructor.GetParameters().Length > 20);
        var arguments = ctor.GetParameters().Select(parameter => values.TryGetValue(parameter.Name!, out var value)
            ? value : parameter.HasDefaultValue && parameter.DefaultValue is not null
                ? parameter.DefaultValue : parameter.ParameterType.IsValueType
                    ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray();
        return ctor.Invoke(arguments);
    }

    private static nint Create(object guest, nint? device = null)
    {
        var arguments = new object?[] { device ?? Device.Value, (nint)0, guest, false };
        var handle = (nint)Presenter.GetMethod("CreateDrawTexture", Methods)!.Invoke(null, arguments)!;
        Assert.True((bool)arguments[3]!);
        Assert.NotEqual(0, handle);
        return handle;
    }

    private static byte[] DistinctBytes(uint width, uint height, uint layers, uint depth, uint mip)
    {
        var bytes = new byte[width * height * layers * depth * 16];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = (byte)((index * 31 + mip * 83 + 17) % 251);
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Region { public nuint X, Y, Z, Width, Height, Depth; }
    private static readonly Lazy<nint> Device = new(MTLCreateSystemDefaultDevice);
    [LibraryImport("/System/Library/Frameworks/Metal.framework/Metal")]
    private static partial nint MTLCreateSystemDefaultDevice();
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial ulong ReadInteger(nint receiver, nint selector);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial void SendVoid(nint receiver, nint selector);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial void GetBytes(nint receiver, nint selector, nint bytes,
        nuint rowBytes, nuint imageBytes, Region region, nuint level, nuint slice);

    private static unsafe byte[] ReadTexture(nint texture, uint width, uint height,
        uint layers, uint depth, uint level)
    {
        var bytes = new byte[width * height * layers * depth * 16];
        fixed (byte* destination = bytes)
        {
            for (uint layer = 0; layer < layers; layer++)
                GetBytes(texture, Selector("getBytes:bytesPerRow:bytesPerImage:fromRegion:mipmapLevel:slice:"),
                    (nint)(destination + layer * width * height * 16), width * 16,
                    ReadInteger(texture, Selector("textureType")) == 7 ? width * height * 16 : 0,
                    new Region { Width = width, Height = height, Depth = depth }, level, layer);
        }
        return bytes;
    }
}
