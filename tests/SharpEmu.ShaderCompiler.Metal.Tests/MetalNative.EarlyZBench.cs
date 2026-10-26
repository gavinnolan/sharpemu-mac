// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// A timed overdraw pass for the early-Z measurement: full-screen triangles drawn nearest
/// first against a Depth32Float target with a LESS test, so every layer after the first fails
/// the depth test. The fragment function stores to a device buffer and may discard, the shape
/// of a translated pixel shader with buffer stores, which Metal does not test early on its own.
/// </summary>
internal static partial class MetalNative
{
    /// <summary>GPU milliseconds (command buffer GPUStartTime to GPUEndTime) of the best of
    /// <paramref name="passes"/> runs of <paramref name="layers"/> overdraw layers.</summary>
    public static bool TryBenchmarkOverdraw(
        string source,
        uint width,
        uint height,
        uint layers,
        uint passes,
        out double gpuMilliseconds,
        out string error)
    {
        gpuMilliseconds = 0;
        if (!TryCompileLibrary(source, out var library, out error))
        {
            return false;
        }

        var vertexFunction = Send(library, Selector("newFunctionWithName:"), NsString("bench_vs"));
        var fragmentFunction = Send(library, Selector("newFunctionWithName:"), NsString("bench_fs"));
        if (vertexFunction == 0 || fragmentFunction == 0)
        {
            error = "bench entry points not found";
            return false;
        }

        const nuint rgba8 = 70;
        var descriptor = Send(Send(objc_getClass("MTLRenderPipelineDescriptor"), Selector("alloc")), Selector("init"));
        SendVoid(descriptor, Selector("setVertexFunction:"), vertexFunction);
        SendVoid(descriptor, Selector("setFragmentFunction:"), fragmentFunction);
        var attachment = SendIndexed(
            Send(descriptor, Selector("colorAttachments")), Selector("objectAtIndexedSubscript:"), 0);
        SendInteger(attachment, Selector("setPixelFormat:"), rgba8);
        SendInteger(descriptor, Selector("setDepthAttachmentPixelFormat:"), Depth32FloatPixelFormat);
        nint nsError = 0;
        var pipeline = SendRenderPipeline(
            Device.Value, Selector("newRenderPipelineStateWithDescriptor:error:"), descriptor, ref nsError);
        if (pipeline == 0)
        {
            error = $"render pipeline creation failed: {DescribeError(nsError)}";
            return false;
        }

        var depthDescriptor = Send(Send(objc_getClass("MTLDepthStencilDescriptor"), Selector("alloc")), Selector("init"));
        SendInteger(depthDescriptor, Selector("setDepthCompareFunction:"), 1); // MTLCompareFunctionLess
        SendVoidBool(depthDescriptor, Selector("setDepthWriteEnabled:"), true);
        var depthState = Send(Device.Value, Selector("newDepthStencilStateWithDescriptor:"), depthDescriptor);

        var color = CreateRenderTarget(width, height, pixelFormat: rgba8);
        var textureDescriptor = Send(Send(objc_getClass("MTLTextureDescriptor"), Selector("alloc")), Selector("init"));
        SendInteger(textureDescriptor, Selector("setPixelFormat:"), Depth32FloatPixelFormat);
        SendInteger(textureDescriptor, Selector("setTextureType:"), 2);
        SendInteger(textureDescriptor, Selector("setWidth:"), width);
        SendInteger(textureDescriptor, Selector("setHeight:"), height);
        SendInteger(textureDescriptor, Selector("setStorageMode:"), 2); // Private
        SendInteger(textureDescriptor, Selector("setUsage:"), 4); // RenderTarget
        var depth = Send(Device.Value, Selector("newTextureWithDescriptor:"), textureDescriptor);
        var output = SharedBuffer(new byte[width * height * sizeof(uint)]);
        if (color == 0 || depth == 0 || depthState == 0 || output == 0)
        {
            error = "failed to create the render targets";
            return false;
        }

        var zBuffers = new nint[layers];
        for (var layer = 0; layer < layers; layer++)
        {
            zBuffers[layer] = SharedBuffer(BitConverter.GetBytes(0.05f + (0.08f * layer)));
        }

        var queue = Send(Device.Value, Selector("newCommandQueue"));
        var best = double.MaxValue;
        for (var pass = 0u; pass <= passes; pass++)
        {
            var passDescriptor = Send(objc_getClass("MTLRenderPassDescriptor"), Selector("renderPassDescriptor"));
            var passAttachment = SendIndexed(
                Send(passDescriptor, Selector("colorAttachments")), Selector("objectAtIndexedSubscript:"), 0);
            SendVoid(passAttachment, Selector("setTexture:"), color);
            SendInteger(passAttachment, Selector("setLoadAction:"), 2);
            SendInteger(passAttachment, Selector("setStoreAction:"), 1);
            SendClearColor(passAttachment, Selector("setClearColor:"), default);
            var depthAttachment = Send(passDescriptor, Selector("depthAttachment"));
            SendVoid(depthAttachment, Selector("setTexture:"), depth);
            SendInteger(depthAttachment, Selector("setLoadAction:"), 2);
            SendInteger(depthAttachment, Selector("setStoreAction:"), 0); // DontCare
            SendVoidDouble(depthAttachment, Selector("setClearDepth:"), 1.0);

            var commandBuffer = Send(queue, Selector("commandBuffer"));
            var encoder = Send(commandBuffer, Selector("renderCommandEncoderWithDescriptor:"), passDescriptor);
            SendVoid(encoder, Selector("setRenderPipelineState:"), pipeline);
            SendVoid(encoder, Selector("setDepthStencilState:"), depthState);
            SendSetBuffer(encoder, Selector("setFragmentBuffer:offset:atIndex:"), output, 0, 0);
            for (var layer = 0; layer < layers; layer++)
            {
                SendSetBuffer(encoder, Selector("setVertexBuffer:offset:atIndex:"), zBuffers[layer], 0, 0);
                SendDrawPrimitives(encoder, Selector("drawPrimitives:vertexStart:vertexCount:"), 3, 0, 3);
            }

            SendVoid(encoder, Selector("endEncoding"));
            SendVoid(commandBuffer, Selector("commit"));
            SendVoid(commandBuffer, Selector("waitUntilCompleted"));
            if (Send(commandBuffer, Selector("error")) != 0)
            {
                error = "GPU execution failed: " + DescribeError(Send(commandBuffer, Selector("error")));
                return false;
            }

            if (pass == 0)
            {
                continue; // warm-up
            }

            var elapsed = SendDouble(commandBuffer, Selector("GPUEndTime")) -
                SendDouble(commandBuffer, Selector("GPUStartTime"));
            best = Math.Min(best, elapsed * 1000.0);
        }

        gpuMilliseconds = best;
        error = string.Empty;
        return true;
    }
}
