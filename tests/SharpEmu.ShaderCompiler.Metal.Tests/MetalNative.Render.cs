// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Classic vertex/fragment render pipelines over the same objc_msgSend harness: a
/// hand-written vertex function paired with a translated pixel shader, drawn into a small
/// render target whose texels are read back. Lifetimes lean on process teardown.
/// </summary>
internal static partial class MetalNative
{
    // MTLPixelFormatRGBA32Uint: an integer target stores exported register bits unchanged.
    public const nuint Rgba32UintPixelFormat = 123;

    // MTLPixelFormatRGBA32Float.
    public const nuint Rgba32FloatPixelFormat = ColorPixelFormat;

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool SendBool(nint receiver, nint selector, nint argument);

    // MTLGPUFamilyApple10: the only family the feature set tables list for pre-raster
    // per-vertex values (metal::vertex_value).
    private const nint GpuFamilyApple10 = 1010;

    /// <summary>Whether this device reads per-vertex pixel inputs: MSL 4.0 on an Apple10 GPU.</summary>
    public static bool SupportsPerVertexPixelInputs =>
        IsAvailable &&
        OperatingSystem.IsMacOSVersionAtLeast(26) &&
        SendBool(Device.Value, Selector("supportsFamily:"), GpuFamilyApple10);

    /// <summary>Compiles MSL source at an explicit language version, for source that needs a
    /// newer one than the backend's default (per-vertex pixel inputs are MSL 4.0).</summary>
    public static bool TryCompileLibrary(string source, nuint languageVersion, out nint library, out string error)
    {
        library = 0;
        error = string.Empty;
        var options = Send(Send(objc_getClass("MTLCompileOptions"), Selector("alloc")), Selector("init"));
        SendVoidBool(options, Selector("setFastMathEnabled:"), false);
        SendVoidUInt(options, Selector("setLanguageVersion:"), languageVersion);
        nint nsError = 0;
        library = Send(Device.Value, Selector("newLibraryWithSource:options:error:"), NsString(source), options, ref nsError);
        if (library == 0)
        {
            error = DescribeError(nsError);
            return false;
        }

        return true;
    }

    /// <summary>Draws <paramref name="vertexCount"/> vertices as a triangle list into a
    /// width x height target of <paramref name="pixelFormat"/> (16 bytes per texel) and
    /// returns its texels as 32-bit words.</summary>
    public static bool TryRenderTriangles(
        nint vertexLibrary,
        string vertexEntryPoint,
        nint fragmentLibrary,
        string fragmentEntryPoint,
        uint vertexCount,
        uint width,
        uint height,
        nuint pixelFormat,
        out uint[] texels,
        out string error)
    {
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
        var attachment = SendIndexed(Send(descriptor, Selector("colorAttachments")), Selector("objectAtIndexedSubscript:"), 0);
        SendInteger(attachment, Selector("setPixelFormat:"), pixelFormat);
        nint nsError = 0;
        var pipeline = Send(Device.Value, Selector("newRenderPipelineStateWithDescriptor:error:"), descriptor, ref nsError);
        if (pipeline == 0)
        {
            error = $"render pipeline creation failed: {DescribeError(nsError)}";
            return false;
        }

        var textureDescriptor = Send(Send(objc_getClass("MTLTextureDescriptor"), Selector("alloc")), Selector("init"));
        SendInteger(textureDescriptor, Selector("setPixelFormat:"), pixelFormat);
        SendInteger(textureDescriptor, Selector("setTextureType:"), 2); // MTLTextureType2D
        SendInteger(textureDescriptor, Selector("setWidth:"), width);
        SendInteger(textureDescriptor, Selector("setHeight:"), height);
        SendInteger(textureDescriptor, Selector("setStorageMode:"), 0); // Shared, for readback
        SendInteger(textureDescriptor, Selector("setUsage:"), 5); // RenderTarget | ShaderRead
        var target = Send(Device.Value, Selector("newTextureWithDescriptor:"), textureDescriptor);
        if (target == 0)
        {
            error = "failed to create the render target";
            return false;
        }

        var pass = Send(objc_getClass("MTLRenderPassDescriptor"), Selector("renderPassDescriptor"));
        var passAttachment = SendIndexed(Send(pass, Selector("colorAttachments")), Selector("objectAtIndexedSubscript:"), 0);
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
        SendDrawPrimitives(encoder, Selector("drawPrimitives:vertexStart:vertexCount:"), 3, 0, vertexCount); // Triangle
        SendVoid(encoder, Selector("endEncoding"));
        SendVoid(commandBuffer, Selector("commit"));
        SendVoid(commandBuffer, Selector("waitUntilCompleted"));
        var commandError = Send(commandBuffer, Selector("error"));
        if (commandError != 0)
        {
            error = "GPU execution failed: " + DescribeError(commandError);
            return false;
        }

        var bytes = ReadFloatTexture(target, width, height, 1, 1);
        texels = new uint[bytes.Length / sizeof(uint)];
        Buffer.BlockCopy(bytes, 0, texels, 0, bytes.Length);
        return true;
    }
}
