// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Metal.Tests;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using DeviceShader = SharpEmu.ShaderCompiler.Metal.Tests.MetalNative;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(2u)] [InlineData(3u)]
    public void R16SelectedNativeHalfStoreLoadBoundsPreserveAllOtherMipsAndTail(uint selected)
    {
        if (!RequireDevice()) return; // Explicit opt-in execution, not a native pass when disabled.
        using var observation = new R16SelectedObservationScope();
        var (guest, expected) = R16SelectedMipOracle.Seed(); var original = guest.ToArray();
        var memory = new R16SelectedMemory(guest);
        var snapshots = R16SelectedSnapshots(memory, Enumerable.Range(0, 4).Select(m => R16SelectedBinding((uint)m)).ToArray());
        Assert.Equal(4, snapshots.Count);
        nint view = 0, storeLibrary = 0, loadLibrary = 0;
        var textures = new nint[4];
        try
        {
            for (var mip = 0; mip < 4; mip++)
            {
                var image = Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [Device.Value, snapshots[mip]])!;
                textures[mip] = (nint)image.GetType().GetField("Texture")!.GetValue(image)!;
                var width = 2048u >> mip;
                Assert.Equal(25UL, ReadInteger(textures[mip], Selector("pixelFormat"))); // Exact R16Float.
                Assert.Equal(2UL, ReadInteger(textures[mip], Selector("textureType")));
                Assert.Equal((ulong)width, ReadInteger(textures[mip], Selector("width")));
                Assert.Equal((ulong)width, ReadInteger(textures[mip], Selector("height")));
                Assert.Equal(1UL, ReadInteger(textures[mip], Selector("mipmapLevelCount")));
                Assert.Equal(expected[mip], ReadNativeRows(textures[mip], width, width, 2));
            }
            Assert.Equal(4, textures.Distinct().Count());
            output.WriteLine($"R16 native selected{selected}: stage=all-four-seeds-passed bytes=11141120 actualFormat=25 type=2.");
            var selectedWidth = 2048u >> (int)selected;
            var texture = textures[selected];
            view = NewSwizzledVolumeView(texture, Selector("newTextureViewWithPixelFormat:textureType:levels:slices:swizzle:"),
                25, 2, new NativeRange { Location = 0, Length = 1 }, new NativeRange { Location = 0, Length = 1 },
                new NativeSwizzle { Red = 2, Green = 0, Blue = 0, Alpha = 1 });
            Assert.NotEqual((nint)0, view);

            // In-bounds last texel, first out-of-width, and signed negative X.
            // Every operation is a guest MIMG instruction translated by the
            // actual compiler, then executed against production materialization.
            foreach (var x in new[] { selectedWidth - 1, selectedWidth, uint.MaxValue })
            {
                var y = selectedWidth - 1;
                Assert.True(Gen5ImageFixtures.TryCompile("ImageStore", 9, out var store, out var error,
                    dmask: 1, dstSelect: 0x204, unifiedFormat: 13, addressValues: [x, y],
                    storeValues: [BitConverter.SingleToUInt32Bits(42.5f), 0, 0, 0]), error);
                Assert.Contains("texture2d<float", store!.Source); Assert.Contains("get_width()", store.Source);
                Assert.True(DeviceShader.TryCompileLibrary(store.Source, out storeLibrary, out error), error);
                Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(storeLibrary, store.EntryPoint, new byte[16],
                    new byte[PushData.ByteSize], store.ArgumentLayout!, 1, out _, out error, texture), error);
                SendVoid(storeLibrary, Selector("release")); storeLibrary = 0;
                Presenter.GetMethod("RecordStorageImageWrites", Methods)!.Invoke(null, [new[] { snapshots[checked((int)selected)] }]);
                if (x < selectedWidth)
                    BinaryPrimitives.WriteUInt16LittleEndian(expected[selected].AsSpan(
                        checked((int)((y * selectedWidth + x) * 2)), 2), BitConverter.HalfToUInt16Bits((Half)42.5f));
                for (var mip = 0; mip < 4; mip++)
                {
                    var width = 2048u >> mip;
                    Assert.Equal(expected[mip], ReadNativeRows(textures[mip], width, width, 2));
                }
                output.WriteLine($"R16 native selected{selected}: stage=store-full-byte-comparison-passed x={x} y={y} inBounds={x < selectedWidth} width={selectedWidth}.");
                Assert.True(Gen5ImageFixtures.TryCompile("ImageLoad", 9, out var load, out error,
                    dstSelect: 0x204, unifiedFormat: 13, addressValues: [x, y], storeResults: true), error);
                Assert.True(DeviceShader.TryCompileLibrary(load!.Source, out loadLibrary, out error), error);
                Assert.True(DeviceShader.TryExecuteWithArgumentBuffer(loadLibrary, load.EntryPoint, new byte[16],
                    new byte[PushData.ByteSize], load.ArgumentLayout!, 1, out var result, out error, view), error);
                SendVoid(loadLibrary, Selector("release")); loadLibrary = 0;
                // Store bounds are proved by unchanged complete native bytes above.
                // This separate sampled read uses an explicit X001 view: invalid
                // coordinates supply zero R data while its constant alpha remains 1.
                uint[] channels = [x < selectedWidth ? BitConverter.SingleToUInt32Bits(42.5f) : 0, 0, 0, 0x3F800000];
                var actualChannels = Enumerable.Range(0, 4).Select(c => BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(c * 4))).ToArray();
                output.WriteLine($"R16 native selected{selected}: stage=sampled-load x={x} y={y} inBounds={x < selectedWidth} format=25 swizzle=X001 expected=[{string.Join(",", channels.Select(c => $"{c:X8}"))}] actual=[{string.Join(",", actualChannels.Select(c => $"{c:X8}"))}].");
                if (x >= selectedWidth) output.WriteLine("R16 translated load-only sampled MSL (not storage ImageLoad):\n" + load.Source);
                for (var c = 0; c < 4; c++) Assert.True(channels[c] == actualChannels[c],
                    $"R16 sampled-load selected={selected} x={x} y={y} component={c} expected={channels[c]:X8} actual={actualChannels[c]:X8}; store bounds and sampled view constants are separate semantics.");
            }
            var reads = memory.Reads.Count;
            var next = Assert.Single(R16SelectedSnapshots(memory, R16SelectedBinding(selected)));
            Assert.Empty(next.RgbaPixels); Assert.Equal(reads, memory.Reads.Count);
            var again = Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [Device.Value, next])!;
            Assert.Equal(texture, (nint)again.GetType().GetField("Texture")!.GetValue(again)!);
            Assert.Equal(expected[selected], ReadNativeRows(texture, selectedWidth, selectedWidth, 2));
            Assert.Equal(original, guest); // Other guest mips and full64K tail padding remain untouched.
            Assert.False(((IDictionary)Presenter.GetField("_guestImages", Methods)!.GetValue(null)!).Contains(R16SelectedAddress));
            output.WriteLine($"R16Float selected{selected}: actual production materialization of all4 independent views; every11,141,120 native seed/post-store byte compared, translated half Float writes at last/out-of-width/negative coordinates; sampled X001 reads retain alpha1 including invalid coordinates; all other mips and guest0xAB0000 untouched; live reuse no CPU reread. Load-only class uses explicit X001 sampled native view, not production sampled-mip admission or storage ImageLoad/coherence proof.");
        }
        finally
        {
            if (loadLibrary != 0) SendVoid(loadLibrary, Selector("release"));
            if (storeLibrary != 0) SendVoid(storeLibrary, Selector("release"));
            if (view != 0) SendVoid(view, Selector("release"));
            Presenter.GetMethod("ReleaseSelectedStorageMipImages", Methods)!.Invoke(null, null);
        }
    }
}
