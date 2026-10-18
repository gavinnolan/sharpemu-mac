// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Gpu.Metal.V2;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Gpu.Metal.Tests;

/// <summary>
/// The title's binary archive: a library and a pipeline taken from it compute and draw
/// what the ones compiled from source do, a damaged or mismatched file is discarded
/// instead of used, and what is written reads back. Device work is behind the exclusive
/// device gate.
/// </summary>
public sealed class MetalBinaryArchiveTests(ITestOutputHelper output) : IDisposable
{
    private const nuint PixelFormatRgba8Unorm = 70;
    private const int Side = 32;
    private const uint Threads = 256;
    private const long Budget = 64L << 20;

    private const string ComputeSource = """
        #include <metal_stdlib>
        using namespace metal;
        kernel void gen5_cs(device uint* out [[buffer(0)]], uint id [[thread_position_in_grid]])
        {
            float x = float(id) * 0.37f;
            out[id] = as_type<uint>(sin(x) * float(id) + cos(x * 1.7f)) ^ (id * 2654435761u);
        }
        """;

    private const string VertexSource = """
        #include <metal_stdlib>
        using namespace metal;
        struct Gen5VsOut { float4 position [[position]]; float4 attr0 [[user(locn0)]]; };
        vertex Gen5VsOut gen5_vs(uint id [[vertex_id]])
        {
            const float2 corners[3] = { float2(-0.9f, -0.8f), float2(0.85f, -0.6f), float2(-0.2f, 0.9f) };
            Gen5VsOut out;
            out.position = float4(corners[id], 0.0f, 1.0f);
            out.attr0 = float4(corners[id] * 0.5f + 0.5f, 0.25f, 1.0f);
            return out;
        }
        """;

    private const string PixelSource = """
        #include <metal_stdlib>
        using namespace metal;
        struct Gen5PsIn { float4 position [[position]]; float4 attr0 [[user(locn0)]]; };
        fragment float4 gen5_ps(Gen5PsIn in [[stage_in]])
        {
            return float4(sin(in.attr0.xyz * 3.1f) * 0.5f + 0.5f, 1.0f);
        }
        """;

    private static readonly Lazy<nint> Device = new(MetalNative.MTLCreateSystemDefaultDevice);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sharpemu-binary-archive-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_root))
        {
            System.IO.Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ALibraryAndComputePipelineFromTheArchiveComputeWhatTheCompiledOnesDo()
    {
        if (!RequireDevice()) return;
        var device = Device.Value;
        var key = MetalBinaryArchive.LibraryKey(ComputeSource, usesPerVertexInputs: false);
        var compiledLibrary = Compile(ComputeSource);
        var compiledFunction = Function(compiledLibrary, "gen5_cs");
        nint error = 0;
        var compiled = MetalNative.Send(device, MetalNative.Selector("newComputePipelineStateWithFunction:error:"), compiledFunction, ref error);
        Assert.NotEqual(0, compiled);
        // A miss fails even though macOS's cache has this pipeline now: a state made with
        // the option below came from the archive.
        var emptyDescriptor = New("MTLBinaryArchiveDescriptor");
        var empty = MetalNative.Send(device, MetalNative.Selector("newBinaryArchiveWithDescriptor:error:"), emptyDescriptor, ref error);
        Assert.NotEqual(0, empty);
        Assert.Equal(0, NewComputeFromArchive(compiledFunction, empty));
        Release(empty, emptyDescriptor);
        using (var writing = MetalBinaryArchive.Open(_root, device, "translator-a", Budget))
        {
            Assert.Equal(0, writing.TryLoadLibrary(key));
            writing.CaptureCompute(key, compiledFunction);
            writing.Flush(wait: true);
            Assert.Equal(1, writing.Counters.LibrariesWritten);
        }

        using var reading = MetalBinaryArchive.Open(_root, device, "translator-a", Budget);
        var storedLibrary = reading.TryLoadLibrary(key);
        Assert.NotEqual(0, storedLibrary);
        var storedFunction = Function(storedLibrary, "gen5_cs");
        var archive = reading.OpenLibraryArchive(key);
        Assert.NotEqual(0, archive);
        var stored = NewComputeFromArchive(storedFunction, archive);
        Assert.NotEqual(0, stored);

        var expected = RunCompute(compiled);
        var actual = RunCompute(stored);
        Assert.Contains(expected, static value => value != 0);
        Assert.Equal(expected, actual);
        Assert.Equal(1, reading.Counters.LibraryHits);
        Release(stored, archive, storedFunction, storedLibrary, compiled, compiledFunction, compiledLibrary);
    }

    [Fact]
    public void ARenderPipelineFromTheArchiveDrawsThePixelsTheCompiledOneDoes()
    {
        if (!RequireDevice()) return;
        var device = Device.Value;
        var vertexKey = MetalBinaryArchive.LibraryKey(VertexSource, usesPerVertexInputs: false);
        var pixelKey = MetalBinaryArchive.LibraryKey(PixelSource, usesPerVertexInputs: false);
        var vertexLibrary = Compile(VertexSource);
        var pixelLibrary = Compile(PixelSource);
        var descriptor = RenderDescriptor(Function(vertexLibrary, "gen5_vs"), Function(pixelLibrary, "gen5_ps"));
        var compiled = NewRender(descriptor, 0, 0);
        Assert.NotEqual(0, compiled);
        using (var writing = MetalBinaryArchive.Open(_root, device, "translator-a", Budget))
        {
            Assert.True(writing.WaitLoaded(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, writing.AcquirePipelineArchives());
            writing.NoteRenderPipeline(descriptor, MetalBinaryArchive.PipelineKind.Render, vertexKey, pixelKey, missed: true);
            writing.Flush(wait: true);
            Assert.Equal(1, writing.Counters.PipelinesAdded);
            Assert.Equal(2, writing.Counters.LibrariesWritten);
        }

        using var reading = MetalBinaryArchive.Open(_root, device, "translator-a", Budget);
        Assert.True(reading.WaitLoaded(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, reading.Counters.LoadedSegments);
        var storedVertexLibrary = reading.TryLoadLibrary(vertexKey);
        var storedPixelLibrary = reading.TryLoadLibrary(pixelKey);
        Assert.NotEqual(0, storedVertexLibrary);
        Assert.NotEqual(0, storedPixelLibrary);
        var storedDescriptor = RenderDescriptor(Function(storedVertexLibrary, "gen5_vs"), Function(storedPixelLibrary, "gen5_ps"));
        var archives = reading.AcquirePipelineArchives();
        Assert.NotEqual(0, archives);
        // A miss fails instead of compiling: the state below came from the segment.
        var stored = NewRender(storedDescriptor, archives, MetalBinaryArchive.FailOnBinaryArchiveMiss);
        Assert.NotEqual(0, stored);

        var expected = Draw(compiled);
        var actual = Draw(stored);
        Assert.Contains(expected, static value => value != 0);
        Assert.Contains(expected, static value => value == 0);
        Assert.Equal(expected, actual);
        Release(stored, archives, storedDescriptor, storedPixelLibrary, storedVertexLibrary, compiled, descriptor, pixelLibrary, vertexLibrary);
    }

    [Fact]
    public void ADamagedLibraryFileIsDeletedInsteadOfLoaded()
    {
        if (!RequireDevice()) return;
        var device = Device.Value;
        var key = MetalBinaryArchive.LibraryKey(ComputeSource, usesPerVertexInputs: false);
        WriteComputeLibrary(device, key);
        string path;
        using (var reading = MetalBinaryArchive.Open(_root, device, "translator-a", Budget))
        {
            path = Path.Combine(reading.LibraryDirectory, key.ToString("x32") + MetalBinaryArchive.Extension);
        }

        var bytes = File.ReadAllBytes(path);
        // Metal itself opens this one as a library: the AIR slice comes first and is whole.
        File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length / 2).ToArray());
        using (var truncated = MetalBinaryArchive.Open(_root, device, "translator-a", Budget))
        {
            Assert.Equal(0, truncated.TryLoadLibrary(key));
            Assert.Equal(1, truncated.Counters.Rejected);
            Assert.False(File.Exists(path));
        }

        WriteComputeLibrary(device, key);
        bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 3] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        using var flipped = MetalBinaryArchive.Open(_root, device, "translator-a", Budget);
        Assert.Equal(0, flipped.TryLoadLibrary(key));
        Assert.Equal(1, flipped.Counters.Rejected);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AnArchiveOfAnotherTranslatorOrDeviceIsDiscarded()
    {
        if (!RequireDevice()) return;
        var device = Device.Value;
        var vertexLibrary = Compile(VertexSource);
        var pixelLibrary = Compile(PixelSource);
        var descriptor = RenderDescriptor(Function(vertexLibrary, "gen5_vs"), Function(pixelLibrary, "gen5_ps"));
        var other = Path.Combine(_root, "0123456789abcdef");
        string directory;
        using (var writing = MetalBinaryArchive.Open(_root, device, "translator-a", Budget))
        {
            directory = writing.Directory;
            Assert.True(writing.WaitLoaded(TimeSpan.FromSeconds(10)));
            writing.NoteRenderPipeline(descriptor, MetalBinaryArchive.PipelineKind.Render, default, default, missed: true);
            writing.Flush(wait: true);
        }

        var segments = System.IO.Directory.GetFiles(directory, MetalBinaryArchive.SegmentPrefix + "*");
        Assert.Single(segments);
        // What another device, OS or format wrote sits next to this one's directory.
        System.IO.Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "stale" + MetalBinaryArchive.Extension), [1, 2, 3]);

        using var reading = MetalBinaryArchive.Open(_root, device, "translator-b", Budget);
        Assert.True(reading.WaitLoaded(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, reading.Counters.LoadedSegments);
        Assert.Equal(0, reading.AcquirePipelineArchives());
        Assert.False(File.Exists(segments[0]));
        Assert.False(System.IO.Directory.Exists(other));
        Release(descriptor, pixelLibrary, vertexLibrary);
    }

    [Fact]
    public void TheTrailerRoundTripsAndRejectsAnyChange()
    {
        System.IO.Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "payload" + MetalBinaryArchive.Extension);
        var payload = new byte[4096];
        new Random(7).NextBytes(payload);
        File.WriteAllBytes(path, payload);

        MetalBinaryArchive.AppendTrailer(path);

        Assert.True(MetalBinaryArchive.TryVerify(path, out var length));
        Assert.Equal(payload.Length + MetalBinaryArchive.TrailerBytes, length);
        Assert.Equal(payload, File.ReadAllBytes(path).AsSpan(0, payload.Length).ToArray());
        var bytes = File.ReadAllBytes(path);
        bytes[100] ^= 1;
        File.WriteAllBytes(path, bytes);
        Assert.False(MetalBinaryArchive.TryVerify(path, out _));
        File.WriteAllBytes(path, payload);
        Assert.False(MetalBinaryArchive.TryVerify(path, out _));
        File.WriteAllBytes(path, []);
        Assert.False(MetalBinaryArchive.TryVerify(path, out _));
    }

    private void WriteComputeLibrary(nint device, UInt128 key)
    {
        var library = Compile(ComputeSource);
        var function = Function(library, "gen5_cs");
        using var writing = MetalBinaryArchive.Open(_root, device, "translator-a", Budget);
        writing.CaptureCompute(key, function);
        writing.Flush(wait: true);
        Release(function, library);
    }

    private static nint Compile(string source)
    {
        var library = MetalRenderHost.TryCompileLibrary(Device.Value, source, usesPerVertexInputs: false, out var error);
        Assert.True(library != 0, error);
        return library;
    }

    private static nint Function(nint library, string name)
    {
        var function = MetalNative.Send(library, MetalNative.Selector("newFunctionWithName:"), MetalNative.NsString(name));
        Assert.NotEqual(0, function);
        return function;
    }

    private static nint NewComputeFromArchive(nint function, nint archive)
    {
        var descriptor = New("MTLComputePipelineDescriptor");
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setComputeFunction:"), function);
        MetalNative.SendVoid(
            descriptor,
            MetalNative.Selector("setBinaryArchives:"),
            MetalNative.Send(MetalNative.Class("NSArray"), MetalNative.Selector("arrayWithObject:"), archive));
        nint error = 0;
        var state = MetalNative.SendNewMeshPipeline(
            Device.Value,
            MetalNative.Selector("newComputePipelineStateWithDescriptor:options:reflection:error:"),
            descriptor,
            MetalBinaryArchive.FailOnBinaryArchiveMiss,
            0,
            ref error);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("release"));
        return state;
    }

    private static nint RenderDescriptor(nint vertex, nint fragment)
    {
        var descriptor = New("MTLRenderPipelineDescriptor");
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setVertexFunction:"), vertex);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setFragmentFunction:"), fragment);
        var attachment = MetalNative.SendAtIndex(
            MetalNative.Send(descriptor, MetalNative.Selector("colorAttachments")), MetalNative.Selector("objectAtIndexedSubscript:"), 0);
        MetalNative.SendVoidNUInt(attachment, MetalNative.Selector("setPixelFormat:"), PixelFormatRgba8Unorm);
        return descriptor;
    }

    private static nint NewRender(nint descriptor, nint archives, nuint options)
    {
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setBinaryArchives:"), archives);
        nint error = 0;
        var state = MetalNative.SendNewMeshPipeline(
            Device.Value,
            MetalNative.Selector("newRenderPipelineStateWithDescriptor:options:reflection:error:"),
            descriptor,
            options,
            0,
            ref error);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setBinaryArchives:"), 0);
        return state;
    }

    private static uint[] RunCompute(nint state)
    {
        var device = Device.Value;
        var queue = MetalNative.Send(device, MetalNative.Selector("newCommandQueue"));
        var buffer = MetalNative.SendNewBuffer(device, MetalNative.Selector("newBufferWithLength:options:"), Threads * sizeof(uint), 0);
        var commands = MetalNative.Send(queue, MetalNative.Selector("commandBuffer"));
        var encoder = MetalNative.Send(commands, MetalNative.Selector("computeCommandEncoder"));
        MetalNative.SendVoid(encoder, MetalNative.Selector("setComputePipelineState:"), state);
        MetalNative.SendSetBuffer(encoder, MetalNative.Selector("setBuffer:offset:atIndex:"), buffer, 0, 0);
        MetalNative.SendDispatch(
            encoder,
            MetalNative.Selector("dispatchThreads:threadsPerThreadgroup:"),
            new MtlSize { Width = Threads, Height = 1, Depth = 1 },
            new MtlSize { Width = 64, Height = 1, Depth = 1 });
        MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
        MetalNative.SendVoid(commands, MetalNative.Selector("commit"));
        MetalNative.SendVoid(commands, MetalNative.Selector("waitUntilCompleted"));
        uint[] result;
        unsafe
        {
            result = new ReadOnlySpan<uint>((void*)MetalNative.Send(buffer, MetalNative.Selector("contents")), (int)Threads).ToArray();
        }

        Release(buffer, queue);
        return result;
    }

    private static byte[] Draw(nint state)
    {
        var device = Device.Value;
        var queue = MetalNative.Send(device, MetalNative.Selector("newCommandQueue"));
        var textureDescriptor = MetalNative.SendTextureDescriptor(
            MetalNative.Class("MTLTextureDescriptor"),
            MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
            PixelFormatRgba8Unorm,
            Side,
            Side,
            false);
        MetalNative.SendVoidNUInt(textureDescriptor, MetalNative.Selector("setUsage:"), 4 | 1);
        MetalNative.SendVoidNUInt(textureDescriptor, MetalNative.Selector("setStorageMode:"), 0);
        var texture = MetalNative.Send(device, MetalNative.Selector("newTextureWithDescriptor:"), textureDescriptor);
        var pass = MetalNative.Send(MetalNative.Class("MTLRenderPassDescriptor"), MetalNative.Selector("renderPassDescriptor"));
        var attachment = MetalNative.SendAtIndex(
            MetalNative.Send(pass, MetalNative.Selector("colorAttachments")), MetalNative.Selector("objectAtIndexedSubscript:"), 0);
        MetalNative.SendVoid(attachment, MetalNative.Selector("setTexture:"), texture);
        MetalNative.SendVoidNUInt(attachment, MetalNative.Selector("setLoadAction:"), 2);
        MetalNative.SendVoidClearColor(attachment, MetalNative.Selector("setClearColor:"), default);
        MetalNative.SendVoidNUInt(attachment, MetalNative.Selector("setStoreAction:"), 1);
        var commands = MetalNative.Send(queue, MetalNative.Selector("commandBuffer"));
        var encoder = MetalNative.Send(commands, MetalNative.Selector("renderCommandEncoderWithDescriptor:"), pass);
        MetalNative.SendVoid(encoder, MetalNative.Selector("setRenderPipelineState:"), state);
        MetalNative.SendDrawPrimitivesInstanced(encoder, MetalNative.Selector("drawPrimitives:vertexStart:vertexCount:instanceCount:"), 3, 0, 3, 1);
        MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
        MetalNative.SendVoid(commands, MetalNative.Selector("commit"));
        MetalNative.SendVoid(commands, MetalNative.Selector("waitUntilCompleted"));
        var pixels = new byte[Side * Side * 4];
        unsafe
        {
            fixed (byte* bytes = pixels)
            {
                SendGetBytes(
                    texture,
                    MetalNative.Selector("getBytes:bytesPerRow:fromRegion:mipmapLevel:"),
                    (nint)bytes,
                    Side * 4,
                    new MtlRegion { Width = Side, Height = Side, Depth = 1 },
                    0);
            }
        }

        Release(texture, queue);
        return pixels;
    }

    private static nint New(string className) =>
        MetalNative.Send(MetalNative.Send(MetalNative.Class(className), MetalNative.Selector("alloc")), MetalNative.Selector("init"));

    private static void Release(params nint[] objects)
    {
        foreach (var value in objects)
        {
            if (value != 0)
            {
                MetalNative.SendVoid(value, MetalNative.Selector("release"));
            }
        }
    }

    private bool RequireDevice()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Archive pipelines are built on the device only behind the exclusive device gate.");
            return false;
        }

        Assert.True(OperatingSystem.IsMacOS());
        Assert.NotEqual(0, Device.Value);
        return true;
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendGetBytes(
        nint receiver, nint selector, nint bytes, nuint bytesPerRow, MtlRegion region, nuint mipmapLevel);
}
