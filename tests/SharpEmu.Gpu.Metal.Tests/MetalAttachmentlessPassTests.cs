// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;
using Xunit.Abstractions;
using Native = SharpEmu.Gpu.Metal.MetalNative;

namespace SharpEmu.Gpu.Metal.Tests;

// A render pass with no attachment: the descriptor's default raster sample count is
// zero, and the x86-64 AGX driver divides by it while it sizes the pass's tiles, so
// encoding such a pass without a sample count ends the process with an integer
// divide fault (Demon's Souls at the movie-to-menu transition).
public sealed class MetalAttachmentlessPassTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(1u, 1u)]
    [InlineData(4u, 4u)]
    public void AnAttachmentlessPassCarriesTheSampleCountOfItsPipeline(uint samples, uint expected)
    {
        if (!RequireDevice())
        {
            return;
        }

        var pool = Native.objc_autoreleasePoolPush();
        try
        {
            var descriptor = Native.Send(
                Native.Send(Native.Class("MTLRenderPassDescriptor"), Native.Selector("alloc")),
                Native.Selector("init"));
            Assert.NotEqual(0, descriptor);
            Assert.Equal(0, Native.Send(descriptor, Native.Selector("defaultRasterSampleCount")));

            MetalRenderHost.ConfigureAttachmentlessPass(descriptor, samples);

            Assert.Equal((nint)expected, Native.Send(descriptor, Native.Selector("defaultRasterSampleCount")));
            Assert.Equal(1, Native.Send(descriptor, Native.Selector("renderTargetWidth")));
            Assert.Equal(1, Native.Send(descriptor, Native.Selector("renderTargetHeight")));

            // The encoder the guest's draw needs: before the sample count was set this
            // call did not return.
            var queue = Native.Send(Device.Value, Native.Selector("newCommandQueue"));
            var commandBuffer = Native.Send(queue, Native.Selector("commandBuffer"));
            var encoder = Native.Send(commandBuffer, Native.Selector("renderCommandEncoderWithDescriptor:"), descriptor);
            Assert.NotEqual(0, encoder);
            Native.SendVoid(encoder, Native.Selector("endEncoding"));
            Native.SendVoid(commandBuffer, Native.Selector("commit"));
            Native.SendVoid(commandBuffer, Native.Selector("waitUntilCompleted"));
            Native.SendVoid(descriptor, Native.Selector("release"));
            Native.SendVoid(queue, Native.Selector("release"));
        }
        finally
        {
            Native.objc_autoreleasePoolPop(pool);
        }
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("An attachmentless pass is encoded on the device only behind the exclusive device gate.");
            return false;
        }

        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        return true;
    }

    private static readonly Lazy<nint> Device = new(Native.MTLCreateSystemDefaultDevice);
}
