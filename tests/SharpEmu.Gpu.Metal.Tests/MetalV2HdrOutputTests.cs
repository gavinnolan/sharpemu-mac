// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal.V2;
using SharpEmu.Libs.VideoOut;
using Xunit;
using Xunit.Abstractions;
using Native = SharpEmu.Gpu.Metal.MetalNative;

namespace SharpEmu.Gpu.Metal.Tests;

// HDR output on the v2 presenter: which drawable a mode and a screen get, what the
// guest is told, and what the EDR present stage writes for SDR and PQ display buffers.
// The values are the ones the Vulkan presenter's scRGB path produces for the same
// buffers (SDR white at 1.0, PQ at nits / 80, BT.2020 primaries to BT.709).
public sealed class MetalV2HdrOutputTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(HostHdrMode.On, 1.0, false, true)]
    [InlineData(HostHdrMode.On, 4.0, true, true)]
    [InlineData(HostHdrMode.Auto, 4.0, true, true)]
    [InlineData(HostHdrMode.Auto, 4.0, false, false)]
    [InlineData(HostHdrMode.Auto, 1.0, true, false)]
    [InlineData(HostHdrMode.Off, 4.0, true, false)]
    public void TheEdrLayerFollowsTheModeTheScreenAndTheGuestBuffer(
        HostHdrMode mode, double potentialHeadroom, bool guestFlipsPq, bool expected)
    {
        Assert.Equal(expected, MetalV2HdrOutput.WantsEdrLayer(mode, potentialHeadroom, guestFlipsPq));
    }

    [Theory]
    [InlineData(HostHdrMode.On, 1.0, true)]
    [InlineData(HostHdrMode.On, 4.0, true)]
    [InlineData(HostHdrMode.Auto, 1.0, false)]
    [InlineData(HostHdrMode.Auto, 4.0, true)]
    [InlineData(HostHdrMode.Off, 4.0, false)]
    public void TheGuestIsToldHdrUnderOnAndUnderAutoOnAnEdrScreen(
        HostHdrMode mode, double potentialHeadroom, bool expected)
    {
        var options = new HostVideoOptions { HdrMode = mode };
        Assert.Equal(expected, MetalV2HdrOutput.GuestHdrSupported(options, potentialHeadroom));
    }

    [Fact]
    public void PresentParametersPlacePqReferenceWhiteAtSdrWhite()
    {
        Assert.Equal(16, Marshal.SizeOf<MetalV2HdrOutput.PresentHdrParameters>());
        var pq = MetalV2HdrOutput.Parameters(pqSource: true, currentHeadroom: 3.5);
        Assert.Equal(1u, pq.Pq);
        Assert.Equal(1f / 80f, pq.PqScale);
        Assert.Equal(3.5f, pq.Headroom);
        var sdr = MetalV2HdrOutput.Parameters(pqSource: false, currentHeadroom: 0.5);
        Assert.Equal(0u, sdr.Pq);
        Assert.Equal(1f, sdr.SdrScale);
        Assert.Equal(1f, sdr.Headroom);
        Assert.Equal(MtlFormat.Rgba16Float, MetalV2HdrOutput.PixelFormat);
    }

    [Fact]
    public void PqBuffersPresentAsLuminanceOverReferenceWhiteRolledIntoTheHeadroom()
    {
        if (!RequireDevice()) return;
        const double headroom = 16;
        // Pure BT.2020 red at 80 nits, then grey at 80, 1000 and 10000 nits.
        var codes = new[] { (PqCode(80), 0u, 0u), (PqCode(80), PqCode(80), PqCode(80)),
            (PqCode(1000), PqCode(1000), PqCode(1000)), (1023u, 1023u, 1023u) };
        var source = new uint[codes.Length];
        for (var index = 0; index < codes.Length; index++)
        {
            var (red, green, blue) = codes[index];
            source[index] = red | green << 10 | blue << 20 | 3u << 30;
        }

        var pixels = Render(MtlFormat.Rgb10A2Unorm, MemoryMarshal.AsBytes(source.AsSpan()).ToArray(), pq: true, headroom);
        var red80 = PqNits(codes[0].Item1) / 80;
        AssertPixel(pixels, 0, red80 * 1.660491, red80 * -0.124550, red80 * -0.018151);
        var grey80 = PqNits(codes[1].Item1) / 80;
        AssertPixel(pixels, 1, grey80, grey80, grey80);
        var grey1000 = FitHeadroom(PqNits(codes[2].Item1) / 80, headroom);
        Assert.True(grey1000 < PqNits(codes[2].Item1) / 80, "1000 nits sits above the knee and is rolled off");
        AssertPixel(pixels, 2, grey1000, grey1000, grey1000);
        var peak = FitHeadroom(10000.0 / 80, headroom);
        Assert.InRange(peak, 15.9, 16.0);
        AssertPixel(pixels, 3, peak, peak, peak);
    }

    [Fact]
    public void SdrBuffersPresentAsLinearWithWhiteAtOne()
    {
        if (!RequireDevice()) return;
        byte[] source = [0, 0, 0, 255, 128, 128, 128, 255, 255, 255, 255, 255, 10, 64, 200, 255];
        var pixels = Render(MtlFormat.Rgba8Unorm, source, pq: false, headroom: 16);
        for (var index = 0; index < 4; index++)
        {
            AssertPixel(
                pixels,
                index,
                SrgbToLinear(source[index * 4] / 255.0),
                SrgbToLinear(source[index * 4 + 1] / 255.0),
                SrgbToLinear(source[index * 4 + 2] / 255.0));
        }
    }

    private static void AssertPixel(Half[] pixels, int index, double red, double green, double blue)
    {
        AssertNear(red, (double)pixels[index * 4], $"pixel {index} red");
        AssertNear(green, (double)pixels[index * 4 + 1], $"pixel {index} green");
        AssertNear(blue, (double)pixels[index * 4 + 2], $"pixel {index} blue");
        AssertNear(1.0, (double)pixels[index * 4 + 3], $"pixel {index} alpha");
    }

    private static void AssertNear(double expected, double actual, string what) =>
        Assert.True(
            Math.Abs(expected - actual) <= 0.004 + Math.Abs(expected) * 0.004,
            $"{what}: expected {expected:F5}, got {actual:F5}");

    private const double M1 = 2610.0 / 16384;
    private const double M2 = 2523.0 / 4096 * 128;
    private const double C1 = 3424.0 / 4096;
    private const double C2 = 2413.0 / 4096 * 32;
    private const double C3 = 2392.0 / 4096 * 32;

    private static uint PqCode(double nits)
    {
        var y = Math.Pow(nits / 10000, M1);
        return (uint)Math.Round(Math.Pow((C1 + C2 * y) / (1 + C3 * y), M2) * 1023);
    }

    private static double PqNits(uint code)
    {
        var powered = Math.Pow(code / 1023.0, 1 / M2);
        return Math.Pow(Math.Max(powered - C1, 0) / (C2 - C3 * powered), 1 / M1) * 10000;
    }

    private static double FitHeadroom(double value, double headroom)
    {
        var knee = Math.Max(1, headroom * 0.75);
        return value <= knee ? value : knee + (headroom - knee) * (1 - Math.Exp(-(value - knee) / (headroom - knee)));
    }

    private static double SrgbToLinear(double encoded) =>
        encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);

    // Draws a 4x1 source through the EDR present stage into a 4x1 RGBA16Float target.
    private static unsafe Half[] Render(ulong sourceFormat, byte[] sourceBytes, bool pq, double headroom)
    {
        const uint width = 4;
        var device = Device.Value;
        var pool = Native.objc_autoreleasePoolPush();
        nint pipeline = 0, source = 0, target = 0, sampler = 0, buffer = 0, queue = 0;
        try
        {
            Assert.True(MetalV2Presenter.TryCreateHdrPresentPipeline(device, out pipeline, out var error), error);
            source = NewTexture(device, sourceFormat, width, usage: 1);
            fixed (byte* bytes = sourceBytes)
            {
                Native.SendReplaceRegion(
                    source,
                    Native.Selector("replaceRegion:mipmapLevel:withBytes:bytesPerRow:"),
                    new MtlRegion { Width = width, Height = 1, Depth = 1 },
                    0,
                    (nint)bytes,
                    (nuint)sourceBytes.Length);
            }

            target = NewTexture(device, MetalV2HdrOutput.PixelFormat, width, usage: 4);
            var samplerDescriptor = Native.Send(
                Native.Send(Native.Class("MTLSamplerDescriptor"), Native.Selector("alloc")), Native.Selector("init"));
            sampler = Native.Send(device, Native.Selector("newSamplerStateWithDescriptor:"), samplerDescriptor);
            Native.SendVoid(samplerDescriptor, Native.Selector("release"));
            buffer = Native.SendNewBuffer(device, Native.Selector("newBufferWithLength:options:"), width * 8, 0);
            queue = Native.Send(device, Native.Selector("newCommandQueue"));
            var command = Native.Send(queue, Native.Selector("commandBuffer"));

            var pass = Native.Send(Native.Class("MTLRenderPassDescriptor"), Native.Selector("renderPassDescriptor"));
            var attachment = Native.SendAtIndex(
                Native.Send(pass, Native.Selector("colorAttachments")), Native.Selector("objectAtIndexedSubscript:"), 0);
            Native.SendVoid(attachment, Native.Selector("setTexture:"), target);
            Native.SendVoidNUInt(attachment, Native.Selector("setLoadAction:"), (nuint)Mtl.LoadActionClear);
            Native.SendVoidNUInt(attachment, Native.Selector("setStoreAction:"), (nuint)Mtl.StoreActionStore);
            var encoder = Native.Send(command, Native.Selector("renderCommandEncoderWithDescriptor:"), pass);
            MetalV2Presenter.EncodeHdrPresentState(encoder, pipeline, pq, headroom);
            Native.SendSetAtIndex(encoder, Native.Selector("setFragmentTexture:atIndex:"), source, 0);
            Native.SendSetAtIndex(encoder, Native.Selector("setFragmentSamplerState:atIndex:"), sampler, 0);
            Native.SendDrawPrimitives(
                encoder, Native.Selector("drawPrimitives:vertexStart:vertexCount:"), Mtl.PrimitiveTriangle, 0, 3);
            Native.SendVoid(encoder, Native.Selector("endEncoding"));

            var blit = Native.Send(command, Native.Selector("blitCommandEncoder"));
            Native.SendCopyTextureToBuffer(
                blit,
                Native.Selector(
                    "copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:" +
                    "destinationOffset:destinationBytesPerRow:destinationBytesPerImage:"),
                target,
                0,
                0,
                default,
                new MtlSize { Width = width, Height = 1, Depth = 1 },
                buffer,
                0,
                width * 8,
                width * 8);
            Native.SendVoid(blit, Native.Selector("endEncoding"));
            Native.SendVoid(command, Native.Selector("commit"));
            Native.SendVoid(command, Native.Selector("waitUntilCompleted"));

            var result = new Half[width * 4];
            new ReadOnlySpan<Half>((void*)Native.Send(buffer, Native.Selector("contents")), result.Length)
                .CopyTo(result);
            return result;
        }
        finally
        {
            foreach (var handle in new[] { pipeline, source, target, sampler, buffer, queue })
            {
                if (handle != 0)
                {
                    Native.SendVoid(handle, Native.Selector("release"));
                }
            }

            Native.objc_autoreleasePoolPop(pool);
        }
    }

    private static nint NewTexture(nint device, ulong format, uint width, nuint usage)
    {
        var descriptor = Native.SendTextureDescriptor(
            Native.Class("MTLTextureDescriptor"),
            Native.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
            (nuint)format,
            width,
            1,
            mipmapped: false);
        Native.SendVoidNUInt(descriptor, Native.Selector("setUsage:"), usage);
        var texture = Native.Send(device, Native.Selector("newTextureWithDescriptor:"), descriptor);
        Assert.NotEqual(0, texture);
        return texture;
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("The HDR present stage runs on the device only behind the exclusive device gate.");
            return false;
        }

        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        return true;
    }

    private static readonly Lazy<nint> Device = new(Native.MTLCreateSystemDefaultDevice);
}
