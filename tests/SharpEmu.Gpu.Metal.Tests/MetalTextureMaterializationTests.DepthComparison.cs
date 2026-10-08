// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using NativeFixture = SharpEmu.ShaderCompiler.Metal.Tests.MetalNative;
using SharpEmu.ShaderCompiler.Metal.Tests;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Theory]
    [InlineData(10u, 4u, 7u)]
    [InlineData(9u, 14u, 7u)]
    [InlineData(9u, 4u, 4u)]
    public void NativeDepthComparisonRejectsUnqualifiedShapeOrFormatBeforeNativeAllocation(uint type, uint format, uint number)
    {
        var guest = (GuestDrawTexture)Guest(type, 1, 1, false, 4, new byte[256], extra: new()
        {
            ["Format"] = format, ["NumberType"] = number,
            ["Shape"] = new ShaderImageShape(false, false, false, false, false, TextureNumericClass.Float, DepthCompare: true),
        });
        var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            Presenter.GetMethod("CreateDrawTexture", Methods)!.Invoke(null, [nint.Zero, (nint)1, guest, false]));
        Assert.IsType<NotSupportedException>(failure.InnerException);
    }

    [Theory]
    [InlineData("uint")]
    [InlineData("storage")]
    [InlineData("volume")]
    [InlineData("array-disagreement")]
    [InlineData("one-dimensional")]
    [InlineData("unknown-type")]
    [InlineData("cube-disagreement")]
    public void NativeDepthComparisonRejectsCompiledShapeDescriptorDisagreementBeforeNativeAllocation(string mismatch)
    {
        var guest = ComparisonGuest(4, 7, false, 1, 1);
        guest = mismatch switch
        {
            "uint" => guest with { Shape = guest.Shape with { NumericClass = TextureNumericClass.Uint } },
            "storage" => guest with { Shape = guest.Shape with { Storage = true } },
            "volume" => guest with { Shape = guest.Shape with { Volume = true } },
            "array-disagreement" => guest with { Shape = guest.Shape with { Arrayed = true } },
            "one-dimensional" => guest with { Type = 8 },
            "unknown-type" => guest with { Type = 123 },
            _ => guest with { Shape = guest.Shape with { Cube = true } },
        };
        var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            Presenter.GetMethod("CreateDrawTexture", Methods)!.Invoke(null, [nint.Zero, (nint)1, guest, false]));
        Assert.IsType<NotSupportedException>(failure.InnerException);
    }

    [Fact]
    public void NativeDepthComparisonRequiresConsumingCommandBufferBeforeMaterialization()
    {
        var guest = ComparisonGuest(4, 7, false, 1, 1);
        var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            Presenter.GetMethod("CreateDrawTexture", Methods)!.Invoke(null, [nint.Zero, nint.Zero, guest, false]));
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.Contains("consuming command buffer", failure.InnerException!.Message);
    }

    [Fact]
    public void ActualNativeDepthComparisonRefusesUnretainedReferencesBeforeMaterialization()
    {
        if (!RequireDevice()) return;
        var queue = NullSend(Device.Value, Selector("newCommandQueue")); Assert.NotEqual(0, queue);
        try
        {
            var command = NullSend(queue, Selector("commandBufferWithUnretainedReferences")); Assert.NotEqual(0, command);
            var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                Presenter.GetMethod("CreateDrawTexture", Methods)!.Invoke(null, [Device.Value, command, ComparisonGuest(4, 7, false, 1, 1), false]));
            Assert.IsType<NotSupportedException>(failure.InnerException);
            Assert.Contains("retained references", failure.InnerException!.Message);
        }
        finally { SendVoid(queue, Selector("release")); }
    }

    [Theory]
    [InlineData(0u, 0f)]
    [InlineData(1u, 0.5f)]
    [InlineData(2u, 0f)]
    [InlineData(3u, 0.5f)]
    [InlineData(4u, 0.5f)]
    [InlineData(5u, 1f)]
    [InlineData(6u, 0.5f)]
    [InlineData(7u, 1f)]
    public void ActualNativeDepthPcfUsesGuestComparisonFunctionBeforeLinearFiltering(uint function, float expected)
    {
        if (!RequireDevice()) return;
        RunDepthComparison(ComparisonGuest(4, 7, false, 1, 1), function,
            "ImageSampleCLz", [0.5f, 0.5f, 0.5f], [expected, expected, expected, 1f]);
    }

    [Fact]
    public void ActualNativeDepthPcfConvertsR16UnormBeforeComparison()
    {
        if (!RequireDevice()) return;
        RunDepthComparison(ComparisonGuest(2, 0, false, 1, 1), 3,
            "ImageSampleCLz", [0.5f, 0.5f, 0.5f], [0.5f, 0.5f, 0.5f, 1f], unifiedFormat: 7);
    }

    [Fact]
    public void ActualNativeDepthPcfPreservesArrayLayerAndExplicitMip()
    {
        if (!RequireDevice()) return;
        // Layer0 is all zero, layer1 base is checkerboard but selected mip1
        // is all one. A wrong slice or missing mip cannot produce this result.
        RunDepthComparison(ComparisonGuest(4, 7, true, 2, 2), 3,
            "ImageSampleCL", [0.5f, 0.5f, 0.5f, 1f, 1f], [1f, 1f, 1f, 1f]);
    }

    [Fact]
    public void ActualNativeDepthPcfPreservesCubeFaceResolvedAsArrayLayer()
    {
        if (!RequireDevice()) return;
        var guest = ComparisonGuest(4, 7, true, 6, 2);
        guest = guest with { Type = 11, Shape = guest.Shape with { Cube = true } };
        RunDepthComparison(guest, 3, "ImageSampleCL", [0.5f, 0.5f, 0.5f, 1f, 1f], [1f, 1f, 1f, 1f]);
    }

    [Fact]
    public void ActualNativeDepthGatherComparesEachTexelWithGuestFunction()
    {
        if (!RequireDevice()) return;
        RunDepthComparison(ComparisonGuest(4, 7, false, 1, 1), 3,
            "ImageGather4C", [0.5f, 0.5f, 0.5f], [1f, 0f, 1f, 0f]);
    }

    private static GuestDrawTexture ComparisonGuest(uint format, uint number, bool arrayed, uint layers, uint mips)
    {
        var bytesPerPixel = format == 2 ? 2 : 4;
        var data = new List<byte>();
        var uploads = new List<GuestTextureMipUpload>();
        for (uint mip = 0; mip < mips; mip++)
        {
            var width = 4u >> (int)mip;
            uploads.Add(new((ulong)data.Count, mip, width, width, width));
            for (uint layer = 0; layer < layers; layer++)
                for (uint y = 0; y < width; y++)
                    for (uint x = 0; x < width; x++)
                    {
                        var one = layers == 1 ? ((x + y) & 1) != 0 : layer == 1 && (mip != 0 || ((x + y) & 1) != 0);
                        data.AddRange(bytesPerPixel == 2 ? BitConverter.GetBytes((ushort)(one ? ushort.MaxValue : 0)) : BitConverter.GetBytes(one ? 1f : 0f));
                    }
        }
        return (GuestDrawTexture)Guest(arrayed ? 13u : 9u, layers, 1, arrayed, 4, data.ToArray(), extra: new()
        {
            ["Format"] = format, ["NumberType"] = number, ["DstSelect"] = 0x204u,
            ["MipUploads"] = uploads.ToArray(), ["MipLevels"] = mips, ["ResourceMipLevels"] = mips,
            ["Shape"] = new ShaderImageShape(false, arrayed, false, false, false, TextureNumericClass.Float, DepthCompare: true),
        });
    }

    private void RunDepthComparison(GuestDrawTexture guest, uint function, string opcode,
        float[] addresses, float[] expected, uint unifiedFormat = 22)
    {
        var pending = (IList)Presenter.GetField("_pendingSnapshotResources", Methods)!.GetValue(null)!;
        var pendingStart = pending.Count;
        nint queue = 0, command = 0, library = 0, sampler = 0;
        var samplerKey = new GuestSampler(function << 12, 0x0FFF000u, (1u << 20) | (1u << 22) | (1u << 26), 0);
        Exception? failure = null;
        try
        {
            queue = NullSend(Device.Value, Selector("newCommandQueue")); Assert.NotEqual(0, queue);
            command = NullSend(queue, Selector("commandBuffer")); Assert.NotEqual(0, command);
            var arguments = new object?[] { Device.Value, command, guest, false };
            var depth = (nint)Presenter.GetMethod("CreateDrawTexture", Methods)!.Invoke(null, arguments)!;
            Assert.False((bool)arguments[3]!); Assert.NotEqual(0, depth);
            Assert.Equal(252UL, ReadInteger(depth, Selector("pixelFormat")));
            Assert.Equal(guest.ArrayedView ? 3UL : 2UL, ReadInteger(depth, Selector("textureType")));
            Assert.Equal((ulong)guest.ArrayLayers, ReadInteger(depth, Selector("arrayLength")));
            Assert.Equal((ulong)guest.MipLevels, ReadInteger(depth, Selector("mipmapLevelCount")));
            SendVoid(command, Selector("commit")); SendVoid(command, Selector("waitUntilCompleted"));
            Assert.Equal(4UL, ReadInteger(command, Selector("status")));
            Assert.True(Gen5ImageFixtures.TryCompile(opcode, guest.Type, out var shader, out var translationError,
                unifiedFormat: unifiedFormat, addressValues: addresses.Select(BitConverter.SingleToUInt32Bits).ToArray(), storeResults: true), translationError);
            Assert.Contains(opcode.Contains("Gather", StringComparison.Ordinal) ? ".gather_compare(" : ".sample_compare(", shader!.Source);
            Assert.True(NativeFixture.TryCompileLibrary(shader.Source, out library, out var error), error + "\n" + shader.Source);
            sampler = (nint)Presenter.GetMethod("GetOrCreateSampler", Methods)!.Invoke(null, [Device.Value, samplerKey])!;
            Assert.True(NativeFixture.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, new byte[16], [],
                shader.ArgumentLayout!, 1, out var result, out error, depth, sampler), error);
            for (var component = 0; component < 4; component++)
                Assert.Equal(expected[component], BitConverter.ToSingle(result, component * 4), precision: 5);
            output.WriteLine($"Actual native depth PCF: format={guest.Format}/{guest.NumberType} array={guest.ArrayedView} mips={guest.MipLevels} compare={function} opcode={opcode}; result={string.Join(',', expected)}. Conversion status4, generated guest shader executed against production depth/sampler.");
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
                    else { try { failure.Data["Depth fixture cleanup " + Guid.NewGuid()] = ex; } catch (Exception) { } }
                }
            }
            if (library != 0) Cleanup(() => SendVoid(library, Selector("release")));
            if (queue != 0) Cleanup(() => SendVoid(queue, Selector("release")));
            // Normal execution waits conversion and consumer completion. Before
            // commit, encoded resources are retained by the unsent command buffer.
            while (pending.Count > pendingStart)
            {
                var last = pending[pending.Count - 1]!;
                var handle = (nint)last.GetType().GetField("Handle")!.GetValue(last)!;
                pending.RemoveAt(pending.Count - 1);
                Cleanup(() => SendVoid(handle, Selector("release")));
            }
            if (sampler != 0) Cleanup(() =>
            {
                var cache = (IDictionary)Presenter.GetField("_samplerCache", Methods)!.GetValue(null)!;
                if (cache.Contains(samplerKey) && (nint)cache[samplerKey]! == sampler) { cache.Remove(samplerKey); SendVoid(sampler, Selector("release")); }
            });
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
