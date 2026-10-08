// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Fact]
    public void ActualNativeDepthCubeProductionSnapshotPreservesAllSixR16FacesAfterGuestPoison()
    {
        if (!RequireDevice()) return;
        const ulong address = 0x71F6_A000_00;
        uint[] raw = [(uint)(address >> 8), 0xC0700000, 0x0000C000, 0xB0000204, 5, 0x00700000, 0, 0];
        var agc = typeof(AgcExports);
        var decode = agc.GetMethod("TryDecodeTextureDescriptor", Methods)!;
        var decodeArgs = new object?[] { raw, null };
        Assert.True((bool)decode.Invoke(null, decodeArgs)!);
        var descriptor = (AgcExports.TextureDescriptor)decodeArgs[1]!;
        var seed = new byte[6 * 4 * 256];
        var expected = new byte[6 * 4 * 4 * 2];
        Array.Fill(seed, (byte)0xED);
        for (var face = 0; face < 6; face++)
            for (var y = 0; y < 4; y++)
                for (var x = 0; x < 4; x++)
                {
                    var value = checked((ushort)(face * 9000 + y * 100 + x * 3 + 17));
                    BinaryPrimitives.WriteUInt16LittleEndian(seed.AsSpan(face * 1024 + y * 256 + x * 2), value);
                    BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(face * 32 + (y * 4 + x) * 2), value);
                }
        var binding = new AgcExports.TranslatedImageBinding(descriptor, false, 0, [], true,
            ResourceDescriptor: raw,
            Shape: new ShaderImageShape(false, true, true, false, false, TextureNumericClass.Float, DepthCompare: true),
            Access: GuestImageAccess.Read);
        var memory = new LinearReadbackMemory(address, seed);
        var backend = typeof(GuestGpu).GetField("_instance", Methods)!;
        var previous = backend.GetValue(null);
        nint color = 0;
        Exception? failure = null;
        try
        {
            var backendType = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalGuestGpuBackend");
            backend.SetValue(null, RuntimeHelpers.GetUninitializedObject(backendType));
            var snapshots = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalTextureSnapshots");
            var arguments = new object?[] { new CpuContext(memory, Generation.Gen5), new[] { binding }, 0 };
            var snapshot = Assert.Single((IReadOnlyList<GuestDrawTexture>)snapshots.GetMethod("CreateTextureSnapshots", Methods)!.Invoke(null, arguments)!);
            Assert.Equal(0, arguments[2]); Assert.Equal(6u, snapshot.ArrayLayers); Assert.True(snapshot.Shape.Cube);
            Assert.Equal(128u, snapshot.Pitch); Assert.Equal((ulong)seed.Length, snapshot.SourceByteCount);
            Assert.Equal(6, memory.Reads); Assert.NotSame(seed, snapshot.RgbaPixels);
            Array.Fill(seed, (byte)0xA1); memory.RejectFurtherReads = true;
            var createArgs = new object?[] { Device.Value, nint.Zero, snapshot, false };
            color = (nint)Presenter.GetMethod("CreateDrawColorTexture", Methods)!.Invoke(null, createArgs)!;
            Assert.True((bool)createArgs[3]!); Assert.NotEqual(0, color);
            Assert.Equal(20UL, ReadInteger(color, Selector("pixelFormat"))); // exact R16Unorm
            Assert.Equal(3UL, ReadInteger(color, Selector("textureType")));
            Assert.Equal(6UL, ReadInteger(color, Selector("arrayLength")));
            Assert.Equal(expected, ReadDepthCubeR16Faces(color)); // ALL96 logical texels, separate face offsets.
            for (var face = 0; face < 6; face++)
            {
                var comparison = face >= 4 ? 1f : 0f;
                RunDepthComparison(snapshot, 3, "ImageSampleCLz", [0.5f, 0.625f, 0.375f, face],
                    [comparison, comparison, comparison, 1f], unifiedFormat: 7);
            }
            Assert.Equal(6, memory.Reads);
            output.WriteLine("Production raw cube snapshot -> native R16Unorm type3 layers6: every192 logical bytes survived guest poison; all six translated comparison faces passed after real conversion/consumer completion. Linear test layout only, not actual title tile/producer proof.");
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception ex)
                {
                    if (failure is null) failure = ex;
                    else { try { failure.Data["Depth cube fixture cleanup " + Guid.NewGuid()] = ex; } catch (Exception) { } }
                }
            }
            if (color != 0) { var owned = color; color = 0; Cleanup(() => SendVoid(owned, Selector("release"))); }
            Cleanup(() => GuestImageWriteTracker.Untrack(address));
            Cleanup(() => backend.SetValue(null, previous));
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static unsafe byte[] ReadDepthCubeR16Faces(nint texture)
    {
        var actual = new byte[6 * 4 * 4 * 2];
        fixed (byte* pointer = actual)
            for (uint face = 0; face < 6; face++)
                GetBytes(texture, Selector("getBytes:bytesPerRow:bytesPerImage:fromRegion:mipmapLevel:slice:"),
                    (nint)(pointer + face * 32), 8, 32,
                    new Region { Width = 4, Height = 4, Depth = 1 }, 0, face);
        return actual;
    }
}
