// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Mesh render pipelines over the same objc_msgSend harness: build an
/// MTLMeshRenderPipelineDescriptor from a translated primitive shader plus a fragment
/// function, launch it with drawMeshThreadgroups into a small float render target and read
/// the pixels back. Same dependency-free style as the compute side; lifetimes lean on
/// process teardown, which is fine for a test host.
/// </summary>
internal static partial class MetalNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MtlClearColor
    {
        public double Red, Green, Blue, Alpha;
    }

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint SendMeshPipeline(nint receiver, nint selector, nint descriptor,
        nuint options, nint reflection, ref nint error);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint SendIndexed(nint receiver, nint selector, nuint index);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendClearColor(nint receiver, nint selector, MtlClearColor color);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendDrawMesh(nint receiver, nint selector, MtlSize threadgroups,
        MtlSize threadsPerObjectThreadgroup, MtlSize threadsPerMeshThreadgroup);

    // MTLPixelFormatRGBA32Float, so the readback is exact.
    private const nuint ColorPixelFormat = 125;

    /// <summary>Renders one mesh draw into a width x height RGBA32Float target and returns
    /// its texels. <paramref name="meshBuffers"/> is bound to the mesh stage at the given
    /// buffer indices; each entry is raw bytes the helper uploads.</summary>
    public static bool TryRenderMesh(
        nint library,
        string meshEntryPoint,
        string fragmentEntryPoint,
        IReadOnlyList<(byte[] Bytes, uint Index)> meshBuffers,
        uint threadgroups,
        uint threadsPerThreadgroup,
        uint width,
        uint height,
        out float[] pixels,
        out string error)
    {
        pixels = [];
        error = string.Empty;

        var meshFunction = Send(library, Selector("newFunctionWithName:"), NsString(meshEntryPoint));
        var fragmentFunction = Send(library, Selector("newFunctionWithName:"), NsString(fragmentEntryPoint));
        if (meshFunction == 0 || fragmentFunction == 0)
        {
            error = $"entry points '{meshEntryPoint}'/'{fragmentEntryPoint}' not both found in the library";
            return false;
        }

        var descriptorClass = objc_getClass("MTLMeshRenderPipelineDescriptor");
        if (descriptorClass == 0)
        {
            error = "MTLMeshRenderPipelineDescriptor is unavailable on this OS";
            return false;
        }

        var descriptor = Send(Send(descriptorClass, Selector("alloc")), Selector("init"));
        SendVoid(descriptor, Selector("setMeshFunction:"), meshFunction);
        SendVoid(descriptor, Selector("setFragmentFunction:"), fragmentFunction);
        var attachments = Send(descriptor, Selector("colorAttachments"));
        var attachment = SendIndexed(attachments, Selector("objectAtIndexedSubscript:"), 0);
        SendInteger(attachment, Selector("setPixelFormat:"), ColorPixelFormat);
        SendInteger(descriptor, Selector("setMaxTotalThreadsPerMeshThreadgroup:"), threadsPerThreadgroup);

        nint nsError = 0;
        var pipeline = SendMeshPipeline(
            Device.Value,
            Selector("newRenderPipelineStateWithMeshDescriptor:options:reflection:error:"),
            descriptor,
            0,
            0,
            ref nsError);
        if (pipeline == 0)
        {
            error = $"mesh pipeline creation failed: {DescribeError(nsError)}";
            return false;
        }

        var target = CreateRenderTarget(width, height);
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
        var residents = new List<nint>();
        foreach (var (bytes, index) in meshBuffers)
        {
            var buffer = SharedBuffer(bytes.Length == 0 ? new byte[4] : bytes);
            if (buffer == 0)
            {
                error = $"failed to create the mesh buffer at index {index}";
                return false;
            }

            residents.Add(buffer);
            SendSetBuffer(encoder, Selector("setMeshBuffer:offset:atIndex:"), buffer, 0, index);
        }

        foreach (var resident in residents)
        {
            SendUseResource(encoder, Selector("useResource:usage:"), resident, ReadWriteUsage);
        }

        var one = new MtlSize { Width = 1, Height = 1, Depth = 1 };
        SendDrawMesh(
            encoder,
            Selector("drawMeshThreadgroups:threadsPerObjectThreadgroup:threadsPerMeshThreadgroup:"),
            new MtlSize { Width = threadgroups, Height = 1, Depth = 1 },
            one,
            new MtlSize { Width = threadsPerThreadgroup, Height = 1, Depth = 1 });
        SendVoid(encoder, Selector("endEncoding"));
        SendVoid(commandBuffer, Selector("commit"));
        SendVoid(commandBuffer, Selector("waitUntilCompleted"));

        var commandError = Send(commandBuffer, Selector("error"));
        if (commandError != 0)
        {
            error = "GPU execution failed: " + DescribeError(commandError);
            return false;
        }

        var bytesOut = ReadFloatTexture(target, width, height, 1, 1);
        pixels = new float[width * height * 4];
        Buffer.BlockCopy(bytesOut, 0, pixels, 0, bytesOut.Length);
        return true;
    }

    private static nint CreateRenderTarget(uint width, uint height)
    {
        var descriptor = Send(Send(objc_getClass("MTLTextureDescriptor"), Selector("alloc")), Selector("init"));
        SendInteger(descriptor, Selector("setPixelFormat:"), ColorPixelFormat);
        SendInteger(descriptor, Selector("setTextureType:"), 2); // MTLTextureType2D
        SendInteger(descriptor, Selector("setWidth:"), width);
        SendInteger(descriptor, Selector("setHeight:"), height);
        SendInteger(descriptor, Selector("setStorageMode:"), 0); // Shared, for readback
        SendInteger(descriptor, Selector("setUsage:"), 5); // RenderTarget | ShaderRead
        return Send(Device.Value, Selector("newTextureWithDescriptor:"), descriptor);
    }
}
