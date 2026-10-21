// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// A render pass with a Depth32Float attachment next to the color target: the depth is
/// cleared, every fragment passes the depth test and writes, and the depth texels are
/// copied out through a blit because a depth texture cannot be shared with the CPU.
/// </summary>
internal static partial class MetalNative
{
    // MTLPixelFormatDepth32Float.
    private const nuint Depth32FloatPixelFormat = 252;

    [StructLayout(LayoutKind.Sequential)]
    private struct MtlOrigin
    {
        public nuint X, Y, Z;
    }

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendVoidDouble(nint receiver, nint selector, double value);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint SendNewBuffer(nint receiver, nint selector, nuint length, nuint options);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendCopyTextureToBuffer(nint receiver, nint selector, nint texture, nuint slice,
        nuint level, MtlOrigin origin, MtlSize size, nint buffer, nuint offset, nuint rowBytes, nuint imageBytes);

    /// <summary>Draws a triangle list into a width x height color target of
    /// <paramref name="pixelFormat"/> and a Depth32Float target cleared to
    /// <paramref name="clearDepth"/> with the depth test always passing, and returns the
    /// depth texels.</summary>
    public static bool TryRenderTrianglesDepth(
        nint library,
        string vertexEntryPoint,
        string fragmentEntryPoint,
        IReadOnlyList<(byte[] Bytes, uint Index)> vertexBuffers,
        IReadOnlyList<(byte[] Bytes, uint Index)> fragmentBuffers,
        uint vertexCount,
        uint width,
        uint height,
        nuint pixelFormat,
        double clearDepth,
        out float[] depths,
        out string error)
    {
        depths = [];
        error = string.Empty;
        var vertexFunction = Send(library, Selector("newFunctionWithName:"), NsString(vertexEntryPoint));
        var fragmentFunction = Send(library, Selector("newFunctionWithName:"), NsString(fragmentEntryPoint));
        if (vertexFunction == 0 || fragmentFunction == 0)
        {
            error = $"entry points '{vertexEntryPoint}'/'{fragmentEntryPoint}' not both found in the library";
            return false;
        }

        var descriptor = Send(Send(objc_getClass("MTLRenderPipelineDescriptor"), Selector("alloc")), Selector("init"));
        SendVoid(descriptor, Selector("setVertexFunction:"), vertexFunction);
        SendVoid(descriptor, Selector("setFragmentFunction:"), fragmentFunction);
        var attachment = SendIndexed(
            Send(descriptor, Selector("colorAttachments")), Selector("objectAtIndexedSubscript:"), 0);
        SendInteger(attachment, Selector("setPixelFormat:"), pixelFormat);
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
        SendInteger(depthDescriptor, Selector("setDepthCompareFunction:"), 7); // MTLCompareFunctionAlways
        SendVoidBool(depthDescriptor, Selector("setDepthWriteEnabled:"), true);
        var depthState = Send(Device.Value, Selector("newDepthStencilStateWithDescriptor:"), depthDescriptor);

        var color = CreateRenderTarget(width, height, pixelFormat: pixelFormat);
        var textureDescriptor = Send(Send(objc_getClass("MTLTextureDescriptor"), Selector("alloc")), Selector("init"));
        SendInteger(textureDescriptor, Selector("setPixelFormat:"), Depth32FloatPixelFormat);
        SendInteger(textureDescriptor, Selector("setTextureType:"), 2); // MTLTextureType2D
        SendInteger(textureDescriptor, Selector("setWidth:"), width);
        SendInteger(textureDescriptor, Selector("setHeight:"), height);
        SendInteger(textureDescriptor, Selector("setStorageMode:"), 2); // Private
        SendInteger(textureDescriptor, Selector("setUsage:"), 4); // RenderTarget
        var depth = Send(Device.Value, Selector("newTextureWithDescriptor:"), textureDescriptor);
        if (color == 0 || depth == 0 || depthState == 0)
        {
            error = "failed to create the render targets";
            return false;
        }

        var pass = Send(objc_getClass("MTLRenderPassDescriptor"), Selector("renderPassDescriptor"));
        var passAttachment = SendIndexed(
            Send(pass, Selector("colorAttachments")), Selector("objectAtIndexedSubscript:"), 0);
        SendVoid(passAttachment, Selector("setTexture:"), color);
        SendInteger(passAttachment, Selector("setLoadAction:"), 2); // MTLLoadActionClear
        SendInteger(passAttachment, Selector("setStoreAction:"), 1); // MTLStoreActionStore
        SendClearColor(passAttachment, Selector("setClearColor:"), default);
        var depthAttachment = Send(pass, Selector("depthAttachment"));
        SendVoid(depthAttachment, Selector("setTexture:"), depth);
        SendInteger(depthAttachment, Selector("setLoadAction:"), 2);
        SendInteger(depthAttachment, Selector("setStoreAction:"), 1);
        SendVoidDouble(depthAttachment, Selector("setClearDepth:"), clearDepth);

        var queue = Send(Device.Value, Selector("newCommandQueue"));
        var commandBuffer = Send(queue, Selector("commandBuffer"));
        var encoder = Send(commandBuffer, Selector("renderCommandEncoderWithDescriptor:"), pass);
        if (encoder == 0)
        {
            error = "failed to create the render command encoder";
            return false;
        }

        SendVoid(encoder, Selector("setRenderPipelineState:"), pipeline);
        SendVoid(encoder, Selector("setDepthStencilState:"), depthState);
        foreach (var (bytes, index) in vertexBuffers)
        {
            SendSetBuffer(encoder, Selector("setVertexBuffer:offset:atIndex:"),
                SharedBuffer(bytes.Length == 0 ? new byte[4] : bytes), 0, index);
        }

        foreach (var (bytes, index) in fragmentBuffers)
        {
            SendSetBuffer(encoder, Selector("setFragmentBuffer:offset:atIndex:"),
                SharedBuffer(bytes.Length == 0 ? new byte[4] : bytes), 0, index);
        }

        SendDrawPrimitives(encoder, Selector("drawPrimitives:vertexStart:vertexCount:"), 3, 0, vertexCount);
        SendVoid(encoder, Selector("endEncoding"));

        var rowBytes = (nuint)(width * sizeof(float));
        var readback = SendNewBuffer(Device.Value, Selector("newBufferWithLength:options:"), rowBytes * height, 0);
        var blit = Send(commandBuffer, Selector("blitCommandEncoder"));
        SendCopyTextureToBuffer(
            blit,
            Selector("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:"),
            depth, 0, 0, default, new MtlSize { Width = width, Height = height, Depth = 1 },
            readback, 0, rowBytes, rowBytes * height);
        SendVoid(blit, Selector("endEncoding"));
        SendVoid(commandBuffer, Selector("commit"));
        SendVoid(commandBuffer, Selector("waitUntilCompleted"));
        var commandError = Send(commandBuffer, Selector("error"));
        if (commandError != 0)
        {
            error = "GPU execution failed: " + DescribeError(commandError);
            return false;
        }

        depths = new float[width * height];
        Marshal.Copy(Send(readback, Selector("contents")), depths, 0, depths.Length);
        return true;
    }
}
