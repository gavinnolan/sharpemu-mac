// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Classic vertex + fragment render pipelines over the same objc_msgSend harness: a
/// hand-written vertex function places triangles from a position buffer, a fragment
/// function from the same library shades them into a small 16-byte-per-texel target, and
/// the texels are read back. Lifetimes lean on process teardown like the rest of the
/// harness.
/// </summary>
internal static partial class MetalNative
{
    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint SendRenderPipeline(nint receiver, nint selector, nint descriptor, ref nint error);

    /// <summary>Draws <paramref name="vertexCount"/> vertices as a triangle list into a
    /// width x height target of <paramref name="pixelFormat"/> (16 bytes a texel) cleared
    /// to zero and returns its raw texels. <paramref name="vertexBuffers"/> and
    /// <paramref name="fragmentBuffers"/> are raw bytes bound at the given indices of each
    /// stage.</summary>
    public static bool TryRenderTriangles(
        nint library,
        string vertexEntryPoint,
        string fragmentEntryPoint,
        IReadOnlyList<(byte[] Bytes, uint Index)> vertexBuffers,
        IReadOnlyList<(byte[] Bytes, uint Index)> fragmentBuffers,
        uint vertexCount,
        uint width,
        uint height,
        nuint pixelFormat,
        out byte[] texels,
        out string error)
    {
        texels = [];
        error = string.Empty;

        var vertexFunction = Send(library, Selector("newFunctionWithName:"), NsString(vertexEntryPoint));
        var fragmentFunction = Send(library, Selector("newFunctionWithName:"), NsString(fragmentEntryPoint));
        if (vertexFunction == 0 || fragmentFunction == 0)
        {
            error = $"entry points '{vertexEntryPoint}'/'{fragmentEntryPoint}' not both found in the library";
            return false;
        }

        var descriptor = Send(
            Send(objc_getClass("MTLRenderPipelineDescriptor"), Selector("alloc")), Selector("init"));
        SendVoid(descriptor, Selector("setVertexFunction:"), vertexFunction);
        SendVoid(descriptor, Selector("setFragmentFunction:"), fragmentFunction);
        var attachment = SendIndexed(
            Send(descriptor, Selector("colorAttachments")), Selector("objectAtIndexedSubscript:"), 0);
        SendInteger(attachment, Selector("setPixelFormat:"), pixelFormat);

        nint nsError = 0;
        var pipeline = SendRenderPipeline(
            Device.Value, Selector("newRenderPipelineStateWithDescriptor:error:"), descriptor, ref nsError);
        if (pipeline == 0)
        {
            error = $"render pipeline creation failed: {DescribeError(nsError)}";
            return false;
        }

        var target = CreateRenderTarget(width, height, pixelFormat: pixelFormat);
        if (target == 0)
        {
            error = "failed to create the render target";
            return false;
        }

        var pass = Send(objc_getClass("MTLRenderPassDescriptor"), Selector("renderPassDescriptor"));
        var passAttachment = SendIndexed(
            Send(pass, Selector("colorAttachments")), Selector("objectAtIndexedSubscript:"), 0);
        SendVoid(passAttachment, Selector("setTexture:"), target);
        SendInteger(passAttachment, Selector("setLoadAction:"), 2); // MTLLoadActionClear
        SendInteger(passAttachment, Selector("setStoreAction:"), 1); // MTLStoreActionStore
        SendClearColor(passAttachment, Selector("setClearColor:"), default);

        var queue = Send(Device.Value, Selector("newCommandQueue"));
        var commandBuffer = Send(queue, Selector("commandBuffer"));
        var encoder = Send(commandBuffer, Selector("renderCommandEncoderWithDescriptor:"), pass);
        if (encoder == 0)
        {
            error = "failed to create the render command encoder";
            return false;
        }

        SendVoid(encoder, Selector("setRenderPipelineState:"), pipeline);
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
        SendVoid(commandBuffer, Selector("commit"));
        SendVoid(commandBuffer, Selector("waitUntilCompleted"));

        var commandError = Send(commandBuffer, Selector("error"));
        if (commandError != 0)
        {
            error = "GPU execution failed: " + DescribeError(commandError);
            return false;
        }

        texels = ReadFloatTexture(target, width, height, 1, 1);
        return true;
    }
}
