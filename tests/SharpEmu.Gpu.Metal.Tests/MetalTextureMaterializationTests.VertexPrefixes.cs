// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Reflection;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void IndexedVertexPrefixNativeStageInMatchesFullInputsAndAllSparseInstanceResults(bool index32, bool submitted)
    {
        if (!RequireDevice()) return;
        const ulong address = 0x71F7_A000_00;
        var vertices = new byte[256];
        var instances = new byte[32];
        Array.Fill(vertices, (byte)0xE9);
        Array.Fill(instances, (byte)0xDA);
        for (var record = 0; record < 8; record++)
            WritePrefixFloats(vertices.AsSpan(record * 32), [record + 0.125f, record + 0.25f,
                100 + record, 200 + record, 300 + record, 400 + record]);
        for (var record = 0; record < 2; record++)
            WritePrefixFloats(instances.AsSpan(record * 16), [10 + record, 20 + record]);
        var expected = Enumerable.Repeat(-777f, 128).ToArray();
        foreach (var instance in new[] { 0, 1 })
            foreach (var vertex in new[] { 0, 2, 5 })
                new[] { vertex + 0.125f, vertex + 0.25f, 100 + vertex, 200 + vertex,
                    300 + vertex, 400 + vertex, 10 + instance, 20 + instance }
                    .CopyTo(expected, (instance * 8 + vertex) * 8);

        byte[] fullVertices, fullInstances, prefixVertices, prefixInstances;
        if (submitted)
        {
            var source = new PrefixNativeMemory(address, vertices, instances);
            var input = new VertexInputInfo { Buffers = [new(address, 32, 8), new(address + 4096, 16, 2, true)] };
            Assert.True(SubmittedVertexData.TryCapture(source, input, 288, out var snapshot));
            Array.Fill(vertices, (byte)0xA5); Array.Fill(instances, (byte)0xA6);
            source.RejectReads = true;
            fullVertices = snapshot!.CopyBuffer(0); fullInstances = snapshot.CopyBuffer(1);
            prefixVertices = snapshot.CopyBufferPrefix(0, 184); prefixInstances = snapshot.CopyBufferPrefix(1, 24);
            Assert.Equal(288, snapshot.ByteCount);
            Assert.Equal(2, source.Reads);
        }
        else
        {
            fullVertices = (byte[])vertices.Clone(); fullInstances = (byte[])instances.Clone();
            prefixVertices = vertices.AsSpan(0, 184).ToArray(); prefixInstances = instances.AsSpan(0, 24).ToArray();
            Array.Fill(vertices, (byte)0xA5); Array.Fill(instances, (byte)0xA6);
        }
        var full = RunPrefixStageIn(address, fullVertices, fullInstances, index32);
        var prefix = RunPrefixStageIn(address, prefixVertices, prefixInstances, index32);
        Assert.Equal(expected, full);
        Assert.Equal(expected, prefix); // All 128 floats, including every uninvoked sentinel record.
        // Distinct full lengths at the same address/stride are distinct native slots.
        // A candidate184/184 prefix would merge them and read the wrong attribute bytes.
        var distinct = new byte[512];
        Array.Fill(distinct, (byte)0xEB);
        for (var record = 0; record < 16; record++)
            WritePrefixFloats(distinct.AsSpan(record * 32 + 8), [1000 + record, 2000 + record, 3000 + record, 4000 + record]);
        AssertNativePrefixCollisionDeclined(address, index32);
        var collisionExpected = (float[])expected.Clone();
        foreach (var instance in new[] { 0, 1 })
            foreach (var vertex in new[] { 0, 2, 5 })
            {
                var offset = (instance * 8 + vertex) * 8;
                collisionExpected[offset + 2] = 1000 + vertex; collisionExpected[offset + 3] = 2000 + vertex;
                collisionExpected[offset + 4] = 3000 + vertex; collisionExpected[offset + 5] = 4000 + vertex;
            }
        Assert.Equal(collisionExpected, RunPrefixStageIn(address, fullVertices, fullInstances, index32, distinct));
        output.WriteLine($"Actual indexed native stage_in Uint{(index32 ? 32 : 16)} submitted={submitted}: production MTLVertexDescriptor; full256/32B vs prefix184/24B; sparse indices0,2,5 × two instances; all128 output floats and untouched sentinels matched; native float4 all four lanes fetched despite DTO ComponentCount1. Old distinct full-length256/512B slot partition preserved after real prefix predicate declined new184/184B collision; independent second-attribute oracle matched. Three completed native commands per case. Handwritten MSL oracle/direct native draw, no ISA/Drain/game/performance claim.");
    }

    private static void AssertNativePrefixCollisionDeclined(ulong address, bool index32)
    {
        var type = Presenter.Assembly.GetTypes().Single(static value => value.Name == "MetalCommandStreamHost");
        var host = RuntimeHelpers.GetUninitializedObject(type);
        const BindingFlags instanceMembers = BindingFlags.Instance | BindingFlags.NonPublic;
        type.GetField("_vertexInput", instanceMembers)!.SetValue(host, new VertexInputInfo
        {
            Buffers = [new(address, 32, 8), new(address, 32, 16), new(address + 4096, 16, 2, true)],
        });
        var bytes = new byte[index32 ? 12 : 6];
        for (var index = 0; index < 3; index++)
        {
            var value = new uint[] { 0, 2, 5 }[index];
            if (index32) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), value);
            else BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * 2), (ushort)value);
        }
        // The actual bounds/partition predicate is a structural control. Native
        // execution below separately proves that the untrimmed partition matters.
        Assert.Equal(false, type.GetMethod("TryFinishIndexedVertexPrefixes", instanceMembers)!.Invoke(host,
            [new GuestIndexBuffer(bytes, bytes.Length, index32, false), 3u, 2u, new[] { 24, 24, 8 }]));
    }

    private static void WritePrefixFloats(Span<byte> destination, float[] values)
    {
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(destination[(index * 4)..], BitConverter.SingleToUInt32Bits(values[index]));
    }

    private static float[] RunPrefixStageIn(ulong address, byte[] vertices, byte[] instances, bool index32, byte[]? distinctAttributeData = null)
    {
        const string msl = """
            #include <metal_stdlib>
            using namespace metal;
            struct Input {
                float2 xy [[attribute(0)]];
                float4 rgba [[attribute(1)]];
                float2 per_instance [[attribute(2)]];
            };
            vertex void vertex_prefix_probe(Input input [[stage_in]], uint vertex_index [[vertex_id]],
                                               uint instance_index [[instance_id]], device float* result [[buffer(0)]]) {
                uint offset = (instance_index * 8 + vertex_index) * 8;
                result[offset+0] = input.xy.x; result[offset+1] = input.xy.y;
                result[offset+2] = input.rgba.x; result[offset+3] = input.rgba.y;
                result[offset+4] = input.rgba.z; result[offset+5] = input.rgba.w;
                result[offset+6] = input.per_instance.x; result[offset+7] = input.per_instance.y;
            }
            """;
        var pool = NullSend(NullSend(NullClass("NSAutoreleasePool"), Selector("alloc")), Selector("init"));
        nint library = 0, function = 0, pipelineDescriptor = 0, pipeline = 0, vertexBuffer = 0, distinctBuffer = 0,
            instanceBuffer = 0, indexBuffer = 0, resultBuffer = 0, queue = 0, command = 0, encoder = 0;
        var encoderEnded = false;
        var commitAttempted = false;
        try
        {
            library = NullLibrary(Device.Value, Selector("newLibraryWithSource:options:error:"),
                NullString(NullClass("NSString"), Selector("stringWithUTF8String:"), msl), 0, out var error);
            RequirePrefixNativeHandle(library, "MSL library", error); Assert.Equal(0, error);
            function = NullSendArgument(library, Selector("newFunctionWithName:"),
                NullString(NullClass("NSString"), Selector("stringWithUTF8String:"), "vertex_prefix_probe"));
            RequirePrefixNativeHandle(function, "vertex function");
            pipelineDescriptor = NullSend(NullSend(NullClass("MTLRenderPipelineDescriptor"), Selector("alloc")), Selector("init"));
            RequirePrefixNativeHandle(pipelineDescriptor, "render pipeline descriptor");
            NullSetObject(pipelineDescriptor, Selector("setVertexFunction:"), function);
            PrefixSetBool(pipelineDescriptor, Selector("setRasterizationEnabled:"), 0);
            PrefixSetUnsigned(pipelineDescriptor, Selector("setRasterSampleCount:"), 1);
            var pipelineSamples = ReadInteger(pipelineDescriptor, Selector("rasterSampleCount"));
            Assert.Equal(1UL, pipelineSamples);
            var attributeData = distinctAttributeData ?? vertices;
            GuestVertexBuffer[] streams = [
                new(0, 2, 11, 7, address, 32, 0, vertices, vertices.Length, false),
                new(1, 1, 14, 7, address, 32, 8, attributeData, attributeData.Length, false),
                new(2, 2, 11, 7, address + 4096, 16, 0, instances, instances.Length, false, true),
            ];
            // Actual production format/offset/stride/slot/PerInstance descriptor.
            var vertexDescriptor = (nint)Presenter.GetMethod("CreateVertexDescriptor", Methods)!.Invoke(null, [streams])!;
            RequirePrefixNativeHandle(vertexDescriptor, "production vertex descriptor");
            NullSetObject(pipelineDescriptor, Selector("setVertexDescriptor:"), vertexDescriptor);
            pipeline = NullPipeline(Device.Value, Selector("newRenderPipelineStateWithDescriptor:error:"), pipelineDescriptor, out error);
            RequirePrefixNativeHandle(pipeline, "render pipeline", error); Assert.Equal(0, error);
            Console.Error.WriteLine($"[VERTEX-PREFIX-NATIVE] pipeline_created rasterization=false pipeline_samples={pipelineSamples}");
            vertexBuffer = PrefixNativeBuffer(vertices);
            if (distinctAttributeData is not null) distinctBuffer = PrefixNativeBuffer(distinctAttributeData);
            instanceBuffer = PrefixNativeBuffer(instances);
            var indexBytes = new byte[index32 ? 12 : 6];
            for (var index = 0; index < 3; index++)
            {
                var value = new uint[] { 0, 2, 5 }[index];
                if (index32) BinaryPrimitives.WriteUInt32LittleEndian(indexBytes.AsSpan(index * 4), value);
                else BinaryPrimitives.WriteUInt16LittleEndian(indexBytes.AsSpan(index * 2), (ushort)value);
            }
            indexBuffer = PrefixNativeBuffer(indexBytes);
            resultBuffer = NullBuffer(Device.Value, Selector("newBufferWithLength:options:"), 512, 0);
            RequirePrefixNativeHandle(resultBuffer, "result buffer");
            var contents = NullSend(resultBuffer, Selector("contents")); RequirePrefixNativeHandle(contents, "result buffer contents");
            Marshal.Copy(Enumerable.Repeat(-777f, 128).ToArray(), 0, contents, 128);
            queue = NullSend(Device.Value, Selector("newCommandQueue")); RequirePrefixNativeHandle(queue, "command queue");
            command = NullSend(queue, Selector("commandBuffer")); RequirePrefixNativeHandle(command, "command buffer");
            _ = NullSend(command, Selector("retain"));
            var pass = NullSend(NullClass("MTLRenderPassDescriptor"), Selector("renderPassDescriptor")); RequirePrefixNativeHandle(pass, "render pass descriptor");
            PrefixSetUnsigned(pass, Selector("setRenderTargetWidth:"), 1);
            PrefixSetUnsigned(pass, Selector("setRenderTargetHeight:"), 1);
            // Attachmentless passes have an explicit sample-count contract;
            // do not infer it from the render pipeline's independent default.
            PrefixSetUnsigned(pass, Selector("setDefaultRasterSampleCount:"), 1);
            var passSamples = ReadInteger(pass, Selector("defaultRasterSampleCount"));
            Assert.Equal(1UL, passSamples);
            Console.Error.WriteLine($"[VERTEX-PREFIX-NATIVE] before_encoder attachmentless=true width=1 height=1 pass_samples={passSamples} pipeline_samples={pipelineSamples}");
            encoder = NullSendArgument(command, Selector("renderCommandEncoderWithDescriptor:"), pass); RequirePrefixNativeHandle(encoder, "render encoder");
            Console.Error.WriteLine("[VERTEX-PREFIX-NATIVE] encoder_created");
            NullSetObject(encoder, Selector("setRenderPipelineState:"), pipeline);
            NullSetBuffer(encoder, Selector("setVertexBuffer:offset:atIndex:"), resultBuffer, 0, 0);
            NullSetBuffer(encoder, Selector("setVertexBuffer:offset:atIndex:"), vertexBuffer, 0, 26);
            if (distinctBuffer != 0) NullSetBuffer(encoder, Selector("setVertexBuffer:offset:atIndex:"), distinctBuffer, 0, 27);
            NullSetBuffer(encoder, Selector("setVertexBuffer:offset:atIndex:"), instanceBuffer, 0, distinctBuffer != 0 ? 28u : 27u);
            PrefixDrawIndexed(encoder, Selector("drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:instanceCount:"),
                0, 3, index32 ? 1u : 0u, indexBuffer, 0, 2); // MTLPrimitiveType.point, unique sparse indices.
            SendVoid(encoder, Selector("endEncoding")); encoderEnded = true;
            commitAttempted = true; SendVoid(command, Selector("commit"));
            SendVoid(command, Selector("waitUntilCompleted"));
            Assert.Equal(4UL, ReadInteger(command, Selector("status")));
            Console.Error.WriteLine("[VERTEX-PREFIX-NATIVE] command_completed status=4");
            var result = new float[128]; Marshal.Copy(contents, result, 0, result.Length);
            return result;
        }
        finally
        {
            try
            {
                if (encoder != 0 && !encoderEnded) SendVoid(encoder, Selector("endEncoding"));
                if (command != 0 && commitAttempted) SendVoid(command, Selector("waitUntilCompleted"));
            }
            finally
            {
                foreach (var owned in new[] { command, queue, resultBuffer, indexBuffer, instanceBuffer, distinctBuffer, vertexBuffer,
                    pipeline, pipelineDescriptor, function, library })
                    if (owned != 0) SendVoid(owned, Selector("release"));
                if (pool != 0) SendVoid(pool, Selector("drain"));
            }
        }
    }

    private static void RequirePrefixNativeHandle(nint handle, string stage, nint error = 0)
    {
        if (handle != 0) return;
        string? description = null;
        if (error != 0)
        {
            var value = NullSend(error, Selector("localizedDescription"));
            var utf8 = value != 0 ? NullSend(value, Selector("UTF8String")) : 0;
            if (utf8 != 0) description = Marshal.PtrToStringUTF8(utf8);
        }
        Assert.True(handle != 0, $"Indexed stage_in native {stage} returned zero; NSError=0x{error:x}; {description ?? "no native error description"}");
    }

    private static nint PrefixNativeBuffer(byte[] bytes)
    {
        var buffer = NullBuffer(Device.Value, Selector("newBufferWithLength:options:"), (nuint)bytes.Length, 0);
        RequirePrefixNativeHandle(buffer, "input buffer");
        try
        {
            var contents = NullSend(buffer, Selector("contents")); RequirePrefixNativeHandle(contents, "input buffer contents");
            Marshal.Copy(bytes, 0, contents, bytes.Length);
            return buffer;
        }
        catch { SendVoid(buffer, Selector("release")); throw; }
    }

    private sealed class PrefixNativeMemory(ulong address, byte[] vertices, byte[] instances) : ICpuMemory
    {
        public int Reads;
        public bool RejectReads;
        public bool TryRead(ulong source, Span<byte> destination)
        {
            if (RejectReads) throw new InvalidOperationException("Native prefix attempted a late guest read.");
            Reads++;
            var data = source == address ? vertices : source == address + 4096 ? instances : null;
            if (data is null || destination.Length != data.Length) return false;
            data.CopyTo(destination); return true;
        }
        public bool TryWrite(ulong source, ReadOnlySpan<byte> value) => false;
    }

    [LibraryImport(NullObjC, EntryPoint = "objc_msgSend")]
    private static partial void PrefixSetBool(nint receiver, nint selector, byte value);
    [LibraryImport(NullObjC, EntryPoint = "objc_msgSend")]
    private static partial void PrefixSetUnsigned(nint receiver, nint selector, nuint value);
    [LibraryImport(NullObjC, EntryPoint = "objc_msgSend")]
    private static partial void PrefixDrawIndexed(nint receiver, nint selector, nuint primitive, nuint indexCount,
        nuint indexType, nint indexBuffer, nuint indexOffset, nuint instanceCount);
}
