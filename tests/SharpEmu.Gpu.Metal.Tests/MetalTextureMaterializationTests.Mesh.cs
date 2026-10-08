// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Fact]
    public void ActualNativeMeshTransportEmitsBothQuadPrimitivesAndEveryPixel()
    {
        if (!RequireDevice()) return;
        const string source = """
            #include <metal_stdlib>
            using namespace metal;
            struct V { float4 position [[position]]; };
            struct P { float4 color [[flat]]; };
            using Quad = mesh<V, P, 4, 2, topology::triangle>;
            [[mesh, max_total_threads_per_threadgroup(32)]]
            void quad(Quad geometry, uint lane [[thread_index_in_threadgroup]]) {
                const float2 positions[4] = { float2(-1,-1), float2(1,-1), float2(-1,1), float2(1,1) };
                const uint indices[6] = { 0,1,2,1,3,2 };
                if (lane < 4) geometry.set_vertex(lane, V{float4(positions[lane], 0, 1)});
                if (lane < 6) geometry.set_index(lane, indices[lane]);
                if (lane < 2) geometry.set_primitive(lane, P{lane == 0 ? float4(1,0,0,1) : float4(0,1,0,1)});
                if (lane == 0) geometry.set_primitive_count(2);
            }
            fragment float4 color(P primitive [[stage_in]]) { return primitive.color; }
            """;
        var pool = MeshPoolPush();
        nint library = 0, mesh = 0, fragment = 0, pipeline = 0, texture = 0, queue = 0, descriptor = 0;
        Exception? failure = null;
        try
        {
            var compiled = new object?[] { Device.Value, source, (nint)0, "", false };
            Assert.True((bool)Presenter.GetMethod("TryCompileLibrary", Methods)!.Invoke(null, compiled)!, (string)compiled[3]!);
            library = (nint)compiled[2]!;
            var native = Presenter.Assembly.GetType("SharpEmu.Gpu.Metal.MetalNative")!;
            nint String(string value) => (nint)native.GetMethod("NsString", Methods)!.Invoke(null, [value])!;
            mesh = MeshSend(library, Selector("newFunctionWithName:"), String("quad"));
            fragment = MeshSend(library, Selector("newFunctionWithName:"), String("color"));
            Assert.NotEqual(0, mesh); Assert.NotEqual(0, fragment);
            var transport = Presenter.Assembly.GetType("SharpEmu.Gpu.Metal.MetalMeshPipeline")!;
            pipeline = (nint)transport.GetMethod("Create", Methods)!.Invoke(null, [Device.Value, mesh, fragment, 70u, (nuint)32])!;
            descriptor = MeshSend0(MeshSend0(MeshClass("MTLTextureDescriptor"), Selector("alloc")), Selector("init"));
            MeshSend(descriptor, Selector("setTextureType:"), 2);
            MeshSend(descriptor, Selector("setPixelFormat:"), 70);
            MeshSend(descriptor, Selector("setWidth:"), 16);
            MeshSend(descriptor, Selector("setHeight:"), 16);
            MeshSend(descriptor, Selector("setStorageMode:"), 0);
            MeshSend(descriptor, Selector("setUsage:"), 4);
            texture = MeshSend(Device.Value, Selector("newTextureWithDescriptor:"), descriptor);
            Assert.NotEqual(0, texture);
            queue = MeshSend0(Device.Value, Selector("newCommandQueue")); Assert.NotEqual(0, queue);
            var command = MeshSend0(queue, Selector("commandBuffer")); Assert.NotEqual(0, command);
            var pass = MeshSend0(MeshClass("MTLRenderPassDescriptor"), Selector("renderPassDescriptor"));
            var colors = MeshSend0(pass, Selector("colorAttachments"));
            var attachment = MeshSend(colors, Selector("objectAtIndexedSubscript:"), 0);
            MeshSend(attachment, Selector("setTexture:"), texture);
            MeshSend(attachment, Selector("setLoadAction:"), 2);
            MeshSend(attachment, Selector("setStoreAction:"), 1);
            var encoder = MeshSend(command, Selector("renderCommandEncoderWithDescriptor:"), pass); Assert.NotEqual(0, encoder);
            Exception? encodingFailure = null;
            try { transport.GetMethod("Encode", Methods)!.Invoke(null, [encoder, pipeline, (nuint)1, (nuint)32]); }
            catch (Exception exception) { encodingFailure = exception; }
            try { SendVoid(encoder, Selector("endEncoding")); }
            catch (Exception exception) { PreserveMeshFailure(ref encodingFailure, exception); }
            if (encodingFailure is not null) ExceptionDispatchInfo.Capture(encodingFailure).Throw();
            SendVoid(command, Selector("commit")); SendVoid(command, Selector("waitUntilCompleted"));
            Assert.Equal(4UL, ReadInteger(command, Selector("status")));
            var bytes = ReadNativeRows(texture, 16, 16, 4);
            for (var y = 0; y < 16; y++)
                for (var x = 0; x < 16; x++)
                {
                    var offset = (y * 16 + x) * 4;
                    Assert.Equal((byte)0, bytes[offset + 2]); Assert.Equal((byte)255, bytes[offset + 3]);
                    if (x == y) Assert.True((bytes[offset] == 255 && bytes[offset + 1] == 0) || (bytes[offset] == 0 && bytes[offset + 1] == 255));
                    else
                    {
                        Assert.Equal((byte)(x < y ? 255 : 0), bytes[offset]);
                        Assert.Equal((byte)(x > y ? 255 : 0), bytes[offset + 1]);
                    }
                }
            output.WriteLine("mesh-native status=4 primitives=2 vertices=4 full-pixels=256 bytes=1024; both primitive colors and complete coverage verified");
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            ReleaseMeshHandle(ref queue, ref failure);
            ReleaseMeshHandle(ref texture, ref failure);
            ReleaseMeshHandle(ref descriptor, ref failure);
            ReleaseMeshHandle(ref pipeline, ref failure);
            ReleaseMeshHandle(ref fragment, ref failure);
            ReleaseMeshHandle(ref mesh, ref failure);
            ReleaseMeshHandle(ref library, ref failure);
            var ownedPool = pool; pool = 0;
            try { MeshPoolPop(ownedPool); }
            catch (Exception exception) { PreserveMeshFailure(ref failure, exception); }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void ReleaseMeshHandle(ref nint handle, ref Exception? failure)
    {
        var owned = handle; handle = 0;
        if (owned == 0) return;
        try { SendVoid(owned, Selector("release")); }
        catch (Exception exception) { PreserveMeshFailure(ref failure, exception); }
    }

    private static void PreserveMeshFailure(ref Exception? failure, Exception cleanup)
    {
        if (failure is null) failure = cleanup;
        else
        {
            try { failure.Data["mesh.cleanup"] = failure.Data["mesh.cleanup"] is Exception previous ? new AggregateException(previous, cleanup) : cleanup; }
            catch (Exception) { }
        }
    }

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_autoreleasePoolPush")]
    private static partial nint MeshPoolPush();
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_autoreleasePoolPop")]
    private static partial void MeshPoolPop(nint pool);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint MeshClass(string name);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nint MeshSend0(nint receiver, nint selector);
    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nint MeshSend(nint receiver, nint selector, nint argument);
}
