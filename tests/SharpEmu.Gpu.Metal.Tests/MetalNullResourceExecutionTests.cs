// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalizedReadonlyNullImageReturnsZeroFromActualMetalShader(bool storage)
    {
        if (!RequireDevice()) return;
        var host = Presenter.Assembly.GetTypes().Single(static type => type.Name == "MetalCommandStreamHost");
        var resource = new ImageResource
        {
            ResourceClass = storage ? ImageResourceClass.Storage : ImageResourceClass.Sampled,
            NumericClass = ImageNumericClass.Float,
            Dimension = ImageDimension.Dim2D,
            Read = true,
        };
        var guest = host.GetMethod("CreateUndecodableNullImage", Methods)!.Invoke(null, [new uint[8], resource])!;
        const string shader = """
            #include <metal_stdlib>
            using namespace metal;
            kernel void read_null(texture2d<float, access::read> source [[texture(0)]],
                                  device float4* result [[buffer(0)]]) {
                result[0] = source.read(uint2(0, 0));
            }
            """;
        var pool = NullSend(NullSend(NullClass("NSAutoreleasePool"), Selector("alloc")), Selector("init"));
        nint texture = 0, library = 0, function = 0, pipeline = 0, buffer = 0, queue = 0;
        try
        {
            texture = Create(guest);
            var source = NullString(NullClass("NSString"), Selector("stringWithUTF8String:"), shader);
            library = NullLibrary(Device.Value, Selector("newLibraryWithSource:options:error:"), source, 0, out var error);
            Assert.NotEqual(0, library);
            Assert.Equal(0, error);
            function = NullSendArgument(library, Selector("newFunctionWithName:"),
                NullString(NullClass("NSString"), Selector("stringWithUTF8String:"), "read_null"));
            Assert.NotEqual(0, function);
            pipeline = NullPipeline(Device.Value, Selector("newComputePipelineStateWithFunction:error:"), function, out error);
            Assert.NotEqual(0, pipeline);
            Assert.Equal(0, error);
            buffer = NullBuffer(Device.Value, Selector("newBufferWithLength:options:"), 16, 0);
            Assert.NotEqual(0, buffer);
            var contents = NullSend(buffer, Selector("contents"));
            Assert.NotEqual(0, contents);
            Marshal.Copy(new float[] { 91, 92, 93, 94 }, 0, contents, 4);
            queue = NullSend(Device.Value, Selector("newCommandQueue"));
            Assert.NotEqual(0, queue);
            var command = NullSend(queue, Selector("commandBuffer"));
            var encoder = NullSend(command, Selector("computeCommandEncoder"));
            Assert.NotEqual(0, encoder);
            NullSetObject(encoder, Selector("setComputePipelineState:"), pipeline);
            NullSetTexture(encoder, Selector("setTexture:atIndex:"), texture, 0);
            NullSetBuffer(encoder, Selector("setBuffer:offset:atIndex:"), buffer, 0, 0);
            NullDispatch(encoder, Selector("dispatchThreadgroups:threadsPerThreadgroup:"),
                new NullSize { Width = 1, Height = 1, Depth = 1 }, new NullSize { Width = 1, Height = 1, Depth = 1 });
            SendVoid(encoder, Selector("endEncoding"));
            SendVoid(command, Selector("commit"));
            SendVoid(command, Selector("waitUntilCompleted"));
            Assert.Equal(4UL, ReadInteger(command, Selector("status")));
            var result = new float[4];
            Marshal.Copy(contents, result, 0, result.Length);
            Assert.Equal(new float[4], result);
        }
        finally
        {
            foreach (var owned in new[] { queue, buffer, pipeline, function, library, texture })
                if (owned != 0) SendVoid(owned, Selector("release"));
            if (pool != 0) SendVoid(pool, Selector("drain"));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NullSize { public nuint Width, Height, Depth; }
    private const string NullObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(NullObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint NullClass(string name);
    [LibraryImport(NullObjC, EntryPoint = "objc_msgSend")]
    private static partial nint NullSend(nint receiver, nint selector);
    [LibraryImport(NullObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint NullString(nint receiver, nint selector, string value);
    [LibraryImport(NullObjC, EntryPoint = "objc_msgSend")]
    private static partial nint NullSendArgument(nint receiver, nint selector, nint argument);
    [LibraryImport(NullObjC, EntryPoint = "objc_msgSend")]
    private static partial nint NullLibrary(nint receiver, nint selector, nint source, nint options, out nint error);
    [LibraryImport(NullObjC, EntryPoint = "objc_msgSend")]
    private static partial nint NullPipeline(nint receiver, nint selector, nint function, out nint error);
    [LibraryImport(NullObjC, EntryPoint = "objc_msgSend")]
    private static partial nint NullBuffer(nint receiver, nint selector, nuint length, nuint options);
    [LibraryImport(NullObjC, EntryPoint = "objc_msgSend")]
    private static partial void NullSetObject(nint receiver, nint selector, nint value);
    [LibraryImport(NullObjC, EntryPoint = "objc_msgSend")]
    private static partial void NullSetTexture(nint receiver, nint selector, nint texture, nuint index);
    [LibraryImport(NullObjC, EntryPoint = "objc_msgSend")]
    private static partial void NullSetBuffer(nint receiver, nint selector, nint buffer, nuint offset, nuint index);
    [LibraryImport(NullObjC, EntryPoint = "objc_msgSend")]
    private static partial void NullDispatch(nint receiver, nint selector, NullSize groups, NullSize threads);
}
