// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler.Resources;
using System.Runtime.CompilerServices;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Theory]
    [InlineData(3u, 2u)]
    [InlineData(64u, 2u)]
    [InlineData(512u, 128u)]
    public void LinearSampledSnapshotKeepsOwnedPaddedRowsAfterGuestMutationWithOnePayloadAllocation(uint width, uint height)
    {
        const ulong address = 0x71F5900000;
        var pitch = ((width * 4 + 255) / 256) * 64;
        var seed = new byte[checked((int)(pitch * height * 4))];
        for (var i = 0; i < seed.Length; i++) seed[i] = (byte)(i * 13 + (i >> 8) * 7);
        var expected = seed.ToArray();
        var descriptor = new AgcExports.TextureDescriptor(address, width, height, 10, 0, 0, 9, 0, 0, width, 0xFAC);
        var binding = new AgcExports.TranslatedImageBinding(descriptor, false, 0, [],
            Shape: new ShaderImageShape(false, false, false, false, TextureNumericClass.Float), Access: GuestImageAccess.Read);
        var snapshots = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalTextureSnapshots");
        var create = snapshots.GetMethod("CreateTextureSnapshots", Methods)!;
        var context = new CpuContext(new SnapshotMemory(address, seed), Generation.Gen5);
        GuestDrawTexture Snapshot() => Assert.Single((IReadOnlyList<GuestDrawTexture>)create.Invoke(null, [context, new[] { binding }, 0])!);
        var backend = typeof(GuestGpu).GetField("_instance", Methods)!;
        var previous = backend.GetValue(null);
        backend.SetValue(null, RuntimeHelpers.GetUninitializedObject(Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalGuestGpuBackend")));
        try
        {
            _ = Snapshot(); // Warm reflection/runtime initialization outside measured call.
            var before = GC.GetAllocatedBytesForCurrentThread();
            var texture = Snapshot();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.False(texture.IsFallback); Assert.Equal(width, texture.Width); Assert.Equal(height, texture.Height);
            Assert.Equal(pitch, texture.Pitch); Assert.Equal((ulong)seed.Length, texture.SourceByteCount);
            Assert.Equal(expected, texture.RgbaPixels); Assert.NotSame(seed, texture.RgbaPixels);
            Assert.Null(texture.TiledSource); Assert.Null(texture.MipUploads);
            Array.Fill(seed, (byte)0xE9);
            Assert.Equal(expected, texture.RgbaPixels); // Later guest changes cannot alter queued bytes, including padding.
            output.WriteLine($"Actual sampled linear snapshot {width}x{height}, padded pitch={pitch}, owned payload={seed.Length} bytes, current-thread allocation={allocated} bytes; guest mutation did not change queued bytes.");
            if (seed.Length >= 262144)
                Assert.InRange(allocated, seed.Length, seed.Length + 32768L); // Includes DTO/provenance overhead, excludes a second full payload.
        }
        finally { GuestImageWriteTracker.Untrack(address); backend.SetValue(null, previous); }
    }
}
