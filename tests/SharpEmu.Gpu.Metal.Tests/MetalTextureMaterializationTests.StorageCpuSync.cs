// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Tests.Fixtures;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [TypedCpuSyncNativeTheory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void TypedCpuSyncNativeFreshTexturePreservesOriginalAndAcceptsCompleteZeroSeed(int kind)
    {
        const ulong address = 0x71F5_4000_00;
        uint[] raw = kind switch
        {
            0 => [(uint)(address >> 8), 0xC0500000, 0x021BC3BF, 0xD1800204, 0, 0x00700000, 0, 0],
            1 => [(uint)(address >> 8), 0xC3800000, 0x00FFC0FF, 0xD1B00F2E, 0, 0x00700000, 0, 0],
            2 => [(uint)(address >> 8), 0x01400000, 0, 0xD1B00204, 0, 0x00700000, 0, 0],
            _ => [(uint)(address >> 8), 0x04700000, 0, 0xD1B00FAC, 0, 0x00700000, 0, 0],
        };
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE"));
        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH"));
        Assert.Equal(RuntimeInformation.FrameworkDescription, Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_FRAMEWORK"));
        Assert.True(RequireDevice());
        Assert.True(AgcBackendServices.TryDecodeTextureDescriptor(raw, out var descriptor));
        var physical = kind == 0 ? R8FourKOracle.PhysicalSeed() : kind == 1 ? Bgra1024Oracle.PhysicalSeed() :
            Enumerable.Repeat((byte)0xD3, 65536).ToArray();
        if (kind == 3)
            for (var channel = 0; channel < 4; channel++)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(physical.AsSpan(channel * 2),
                    BitConverter.HalfToUInt16Bits((Half)(channel + 0.25f)));
        var expected = kind == 0 ? R8FourKOracle.LinearSeed() : kind == 1 ? Bgra1024Oracle.LinearSeed() :
            physical.AsSpan(0, kind == 2 ? 4 : 8).ToArray();
        var numeric = kind is 0 or 2 ? TextureNumericClass.Uint : TextureNumericClass.Float;
        var binding = new AgcExports.TranslatedImageBinding(descriptor, true, 0, [], IsArrayed: true, ResourceDescriptor: raw,
            Shape: new ShaderImageShape(false, true, true, false, numeric), Access: GuestImageAccess.Write);
        var backendField = typeof(GuestGpu).GetField("_instance", Methods)!;
        var previousBackend = backendField.GetValue(null);
        backendField.SetValue(null, RuntimeHelpers.GetUninitializedObject(Presenter.Assembly.GetTypes().Single(static t => t.Name == "MetalGuestGpuBackend")));
        nint original = 0, variedReplacement = 0, zeroReplacement = 0;
        Exception? primary = null;
        try
        {
            var snapshots = Presenter.Assembly.GetTypes().Single(static t => t.Name == "MetalTextureSnapshots");
            var args = new object?[] { new CpuContext(new SnapshotMemory(address, physical), Generation.Gen5), new[] { binding }, 0 };
            var snapshot = Assert.Single((IReadOnlyList<GuestDrawTexture>)snapshots.GetMethod("CreateTextureSnapshots", Methods)!.Invoke(null, args)!);
            Assert.Equal(expected, snapshot.RgbaPixels);
            var image = Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [Device.Value, snapshot])!;
            original = (nint)image.GetType().GetField("Texture")!.GetValue(image)!;
            Assert.NotNull(image.GetType().GetField("StorageCpuLayout")!.GetValue(image));
            var format = Presenter.GetMethod("StorageTextureFormat", Methods)!.Invoke(null, [snapshot])!;
            var varied = new byte[expected.Length];
            if (kind == 3)
                for (var channel = 0; channel < 4; channel++)
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(varied.AsSpan(channel * 2),
                        BitConverter.HalfToUInt16Bits((Half)(channel + 2.75f)));
            else
                for (var i = 0; i < varied.Length; i++) varied[i] = (byte)((i * 113 + 31) & 255);
            Assert.False(expected.AsSpan().SequenceEqual(varied));
            variedReplacement = (nint)Presenter.GetMethod("CreateTypedStorageCpuTexture", Methods)!.Invoke(null,
                [Device.Value, snapshot with { RgbaPixels = varied }, format])!;
            VerifyNativeShape(variedReplacement);
            var bpp = (uint)(expected.Length / checked((int)(descriptor.Width * descriptor.Height)));
            Assert.Equal(varied, ReadNativeRows(variedReplacement, descriptor.Width, descriptor.Height, bpp));
            Assert.Equal(expected, ReadNativeRows(original, descriptor.Width, descriptor.Height, bpp));
            var zero = snapshot with { RgbaPixels = new byte[expected.Length] };
            zeroReplacement = (nint)Presenter.GetMethod("CreateTypedStorageCpuTexture", Methods)!.Invoke(null, [Device.Value, zero, format])!;
            VerifyNativeShape(zeroReplacement);
            Assert.NotEqual(variedReplacement, zeroReplacement);
            Assert.Equal(expected, ReadNativeRows(original, descriptor.Width, descriptor.Height, bpp));
            Assert.Equal(varied, ReadNativeRows(variedReplacement, descriptor.Width, descriptor.Height, bpp));
            Assert.Equal(new byte[expected.Length], ReadNativeRows(zeroReplacement, descriptor.Width, descriptor.Height, bpp));
            output.WriteLine($"typed-cpu-sync native architecture={RuntimeInformation.ProcessArchitecture} framework={RuntimeInformation.FrameworkDescription} kind={kind} full_bytes={expected.Length}; distinct fresh type3 varied and zero uploads matched every byte; original and varied native pixels unchanged after zero creation. This qualifies allocation/upload only, not mapping/content authority or full dirty synchronization.");

            void VerifyNativeShape(nint texture)
            {
                Assert.NotEqual((nint)0, texture); Assert.NotEqual(original, texture);
                Assert.Equal(3UL, ReadInteger(texture, Selector("textureType")));
                Assert.Equal(1UL, ReadInteger(texture, Selector("depth")));
                Assert.Equal(1UL, ReadInteger(texture, Selector("arrayLength")));
                Assert.Equal(ReadInteger(original, Selector("pixelFormat")), ReadInteger(texture, Selector("pixelFormat")));
                Assert.Equal(3UL, ReadInteger(texture, Selector("usage")));
            }
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            Exception? cleanup = null;
            // Consume each known retain before release; an uncertain release is never retried.
            var releaseZero = zeroReplacement; zeroReplacement = 0;
            if (releaseZero != 0) Attempt(() => SendVoid(releaseZero, Selector("release")));
            var releaseVaried = variedReplacement; variedReplacement = 0;
            if (releaseVaried != 0) Attempt(() => SendVoid(releaseVaried, Selector("release")));
            var releaseOriginal = original; original = 0;
            if (releaseOriginal != 0) Attempt(() => ReleaseStorageVolume(address, releaseOriginal));
            Attempt(() => GuestImageWriteTracker.Untrack(address));
            Attempt(() => backendField.SetValue(null, previousBackend));
            if (cleanup != null)
            {
                if (primary != null)
                {
                    try { primary.Data["TypedCpuSyncFixtureCleanupFailure"] = cleanup; }
                    catch { /* Diagnostic attachment must not replace the primary failure. */ }
                }
                else System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanup).Throw();
            }

            void Attempt(Action action)
            {
                try { action(); }
                catch (Exception error)
                {
                    if (cleanup == null) cleanup = error;
                    else
                    {
                        try { cleanup.Data[$"SecondaryCleanupFailure{cleanup.Data.Count}"] = error; }
                        catch { /* Continue attempting the remaining independently owned cleanup. */ }
                    }
                }
            }
        }
    }
}

internal sealed class TypedCpuSyncNativeTheoryAttribute : TheoryAttribute
{
    public TypedCpuSyncNativeTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
            Skip = "Typed CPU native materialization requires a separately granted exclusive Metal device worker.";
    }
}
