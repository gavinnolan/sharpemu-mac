// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Theory]
    [InlineData(3u, 2u, 64u)]
    [InlineData(64u, 2u, 128u)]
    [InlineData(512u, 128u, 576u)]
    public void ProductionSampledLinearNativeUploadPreservesEveryPixelAfterGuestSeedPoison(uint width, uint height, uint pitch)
    {
        // Disabled-device source cases must be excluded from genuine managed counts.
        if (!RequireDevice()) return;
        const ulong address = 0x71F5_A000_00;
        var seed = new byte[checked((int)(pitch * height * 4))];
        Array.Fill(seed, (byte)0xE9); // Distinct row padding, never part of the logical pixel oracle.
        var expected = new byte[checked((int)(width * height * 4))];
        for (uint y = 0; y < height; y++)
            for (uint x = 0; x < width; x++)
            {
                var logical = checked((int)((y * width + x) * 4));
                expected[logical] = (byte)((x * 17 + y * 29 + 11) % 251);
                expected[logical + 1] = (byte)((x * 7 + y * 13 + 37) % 253);
                expected[logical + 2] = (byte)((x * 3 + y * 5 + 71) % 247);
                expected[logical + 3] = (byte)(128 + (x * 11 + y * 19) % 127);
                expected.AsSpan(logical, 4).CopyTo(seed.AsSpan(checked((int)((y * pitch + x) * 4)), 4));
            }
        var descriptor = new AgcExports.TextureDescriptor(address, width, height, 10, 0, 0, 9, 0, 0, pitch, 0xFAC);
        var binding = new AgcExports.TranslatedImageBinding(descriptor, false, 0, [],
            Shape: new ShaderImageShape(false, false, false, false, TextureNumericClass.Float), Access: GuestImageAccess.Read);
        var memory = new LinearReadbackMemory(address, seed);
        var snapshots = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalTextureSnapshots");
        var backend = typeof(GuestGpu).GetField("_instance", Methods)!;
        var previous = backend.GetValue(null);
        var backendType = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalGuestGpuBackend");
        backend.SetValue(null, RuntimeHelpers.GetUninitializedObject(backendType));
        nint texture = 0;
        try
        {
            var arguments = new object?[] { new CpuContext(memory, Generation.Gen5), new[] { binding }, 0 };
            var snapshot = Assert.Single((IReadOnlyList<GuestDrawTexture>)snapshots.GetMethod("CreateTextureSnapshots", Methods)!.Invoke(null, arguments)!);
            Assert.Equal(0, arguments[2]); Assert.False(snapshot.IsFallback); Assert.False(snapshot.IsStorage);
            Assert.Equal(width, snapshot.Width); Assert.Equal(height, snapshot.Height); Assert.Equal(pitch, snapshot.Pitch);
            Assert.Equal((ulong)seed.Length, snapshot.SourceByteCount); Assert.NotSame(seed, snapshot.RgbaPixels);
            Assert.Null(snapshot.TiledSource); Assert.Null(snapshot.Detile); Assert.Null(snapshot.MipUploads);
            Assert.Equal(1, memory.Reads); Assert.Equal(seed.Length, memory.ReadBytes);
            Array.Fill(seed, (byte)0xDB); // All original logical bytes AND padding now differ.
            memory.RejectFurtherReads = true; // Materialization cannot recover by rereading the guest address.
            texture = Create(snapshot); // Actual production CreateDrawTexture; helper requires owned, nonnil native handle.
            Assert.Equal(70UL, ReadInteger(texture, Selector("pixelFormat"))); // exact RGBA8Unorm
            Assert.Equal(2UL, ReadInteger(texture, Selector("textureType")));
            Assert.Equal((ulong)width, ReadInteger(texture, Selector("width")));
            Assert.Equal((ulong)height, ReadInteger(texture, Selector("height")));
            Assert.Equal(1UL, ReadInteger(texture, Selector("mipmapLevelCount")));
            var actual = ReadNativeRows(texture, width, height, 4);
            Assert.Equal(expected, actual); // Every logical pixel/channel, excluding row padding.
            Assert.Equal(1, memory.Reads);
            output.WriteLine($"Actual production sampled-linear snapshot -> CreateDrawTexture RGBA8Unorm native70: {width}x{height} paddedpitch={pitch}, all{actual.Length} logical bytes matched after full guest seed poison; exactly one{memory.ReadBytes}-byte guest read and no late read.");
        }
        finally
        {
            try { if (texture != 0) SendVoid(texture, Selector("release")); }
            finally
            {
                try { GuestImageWriteTracker.Untrack(address); }
                finally { backend.SetValue(null, previous); }
            }
        }
    }

    private sealed class LinearReadbackMemory(ulong address, byte[] bytes) : ICpuMemory
    {
        public int Reads, ReadBytes;
        public bool RejectFurtherReads;
        public bool TryRead(ulong target, Span<byte> destination)
        {
            if (RejectFurtherReads) throw new InvalidOperationException("Native linear materialization attempted a late guest-memory read.");
            Reads++; ReadBytes = destination.Length;
            if (target < address || target - address > (ulong)bytes.Length ||
                (ulong)destination.Length > (ulong)bytes.Length - (target - address)) return false;
            bytes.AsSpan(checked((int)(target - address)), destination.Length).CopyTo(destination);
            return true;
        }
        public bool TryWrite(ulong target, ReadOnlySpan<byte> source) => false;
    }
}
