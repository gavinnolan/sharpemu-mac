// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// A render pass with a Depth32Float_Stencil8 attachment next to the color target: every
/// fragment passes the depth and stencil tests and replaces the stencil value with the
/// reference (the encoder's, or the one the fragment function exports). The stencil plane
/// is copied out through a blit, the color target read back directly.
/// </summary>
internal static partial class MetalNative
{
    // MTLPixelFormatDepth32Float_Stencil8.
    private const nuint Depth32FloatStencil8PixelFormat = 260;

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendCopyTextureToBufferWithOptions(nint receiver, nint selector, nint texture, nuint slice,
        nuint level, MtlOrigin origin, MtlSize size, nint buffer, nuint offset, nuint rowBytes, nuint imageBytes, nuint options);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendUInt32(nint receiver, nint selector, uint value);

    /// <summary>Draws a triangle list into a width x height color target of
    /// <paramref name="pixelFormat"/> (16 bytes a texel) and a Depth32Float_Stencil8 target
    /// whose stencil is cleared to 0 and replaced by <paramref name="stencilReference"/> (or
    /// the exported value) wherever a fragment survives. Returns the stencil bytes and the
    /// color texels.</summary>
    public static bool TryRenderTrianglesStencil(
        nint vertexLibrary,
        string vertexEntryPoint,
        nint fragmentLibrary,
        string fragmentEntryPoint,
        IReadOnlyList<(byte[] Bytes, uint Index)> fragmentBuffers,
        uint vertexCount,
        uint width,
        uint height,
        nuint pixelFormat,
        uint stencilReference,
        out byte[] stencil,
        out uint[] texels,
        out string error)
    {
        stencil = [];
        texels = [];
        error = string.Empty;
        var vertexFunction = Send(vertexLibrary, Selector("newFunctionWithName:"), NsString(vertexEntryPoint));
        var fragmentFunction = Send(fragmentLibrary, Selector("newFunctionWithName:"), NsString(fragmentEntryPoint));
        if (vertexFunction == 0 || fragmentFunction == 0)
        {
            error = $"entry points '{vertexEntryPoint}'/'{fragmentEntryPoint}' not both found";
            return false;
        }

        var descriptor = Send(Send(objc_getClass("MTLRenderPipelineDescriptor"), Selector("alloc")), Selector("init"));
        SendVoid(descriptor, Selector("setVertexFunction:"), vertexFunction);
        SendVoid(descriptor, Selector("setFragmentFunction:"), fragmentFunction);
        var attachment = SendIndexed(
            Send(descriptor, Selector("colorAttachments")), Selector("objectAtIndexedSubscript:"), 0);
        SendInteger(attachment, Selector("setPixelFormat:"), pixelFormat);
        SendInteger(descriptor, Selector("setDepthAttachmentPixelFormat:"), Depth32FloatStencil8PixelFormat);
        SendInteger(descriptor, Selector("setStencilAttachmentPixelFormat:"), Depth32FloatStencil8PixelFormat);
        nint nsError = 0;
        var pipeline = SendRenderPipeline(
            Device.Value, Selector("newRenderPipelineStateWithDescriptor:error:"), descriptor, ref nsError);
        if (pipeline == 0)
        {
            error = $"render pipeline creation failed: {DescribeError(nsError)}";
            return false;
        }

        var stencilDescriptor = Send(Send(objc_getClass("MTLStencilDescriptor"), Selector("alloc")), Selector("init"));
        SendInteger(stencilDescriptor, Selector("setStencilCompareFunction:"), 7); // MTLCompareFunctionAlways
        SendInteger(stencilDescriptor, Selector("setDepthStencilPassOperation:"), 2); // MTLStencilOperationReplace
        SendUInt32(stencilDescriptor, Selector("setReadMask:"), 0xFF);
        SendUInt32(stencilDescriptor, Selector("setWriteMask:"), 0xFF);
        var depthDescriptor = Send(Send(objc_getClass("MTLDepthStencilDescriptor"), Selector("alloc")), Selector("init"));
        SendInteger(depthDescriptor, Selector("setDepthCompareFunction:"), 7);
        SendVoid(depthDescriptor, Selector("setFrontFaceStencil:"), stencilDescriptor);
        SendVoid(depthDescriptor, Selector("setBackFaceStencil:"), stencilDescriptor);
        var depthState = Send(Device.Value, Selector("newDepthStencilStateWithDescriptor:"), depthDescriptor);

        var color = CreateRenderTarget(width, height, pixelFormat: pixelFormat);
        var textureDescriptor = Send(Send(objc_getClass("MTLTextureDescriptor"), Selector("alloc")), Selector("init"));
        SendInteger(textureDescriptor, Selector("setPixelFormat:"), Depth32FloatStencil8PixelFormat);
        SendInteger(textureDescriptor, Selector("setTextureType:"), 2); // MTLTextureType2D
        SendInteger(textureDescriptor, Selector("setWidth:"), width);
        SendInteger(textureDescriptor, Selector("setHeight:"), height);
        SendInteger(textureDescriptor, Selector("setStorageMode:"), 2); // Private
        SendInteger(textureDescriptor, Selector("setUsage:"), 4); // RenderTarget
        var depthStencil = Send(Device.Value, Selector("newTextureWithDescriptor:"), textureDescriptor);
        if (color == 0 || depthStencil == 0 || depthState == 0)
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
        SendVoid(depthAttachment, Selector("setTexture:"), depthStencil);
        SendInteger(depthAttachment, Selector("setLoadAction:"), 2);
        SendInteger(depthAttachment, Selector("setStoreAction:"), 1);
        SendVoidDouble(depthAttachment, Selector("setClearDepth:"), 1.0);
        var stencilAttachment = Send(pass, Selector("stencilAttachment"));
        SendVoid(stencilAttachment, Selector("setTexture:"), depthStencil);
        SendInteger(stencilAttachment, Selector("setLoadAction:"), 2);
        SendInteger(stencilAttachment, Selector("setStoreAction:"), 1);
        SendUInt32(stencilAttachment, Selector("setClearStencil:"), 0);

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
        SendUInt32(encoder, Selector("setStencilReferenceValue:"), stencilReference);
        foreach (var (bytes, index) in fragmentBuffers)
        {
            SendSetBuffer(encoder, Selector("setFragmentBuffer:offset:atIndex:"),
                SharedBuffer(bytes.Length == 0 ? new byte[4] : bytes), 0, index);
        }

        SendDrawPrimitives(encoder, Selector("drawPrimitives:vertexStart:vertexCount:"), 3, 0, vertexCount);
        SendVoid(encoder, Selector("endEncoding"));

        var rowBytes = (nuint)width;
        var readback = SendNewBuffer(Device.Value, Selector("newBufferWithLength:options:"), rowBytes * height, 0);
        var blit = Send(commandBuffer, Selector("blitCommandEncoder"));
        SendCopyTextureToBufferWithOptions(
            blit,
            Selector("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:options:"),
            depthStencil, 0, 0, default, new MtlSize { Width = width, Height = height, Depth = 1 },
            readback, 0, rowBytes, rowBytes * height, 2); // MTLBlitOptionStencilFromDepthStencil
        SendVoid(blit, Selector("endEncoding"));
        SendVoid(commandBuffer, Selector("commit"));
        SendVoid(commandBuffer, Selector("waitUntilCompleted"));
        var commandError = Send(commandBuffer, Selector("error"));
        if (commandError != 0)
        {
            error = "GPU execution failed: " + DescribeError(commandError);
            return false;
        }

        stencil = new byte[width * height];
        Marshal.Copy(Send(readback, Selector("contents")), stencil, 0, stencil.Length);
        var bytesOut = ReadFloatTexture(color, width, height, 1, 1);
        texels = new uint[bytesOut.Length / sizeof(uint)];
        Buffer.BlockCopy(bytesOut, 0, texels, 0, bytesOut.Length);
        return true;
    }
}
