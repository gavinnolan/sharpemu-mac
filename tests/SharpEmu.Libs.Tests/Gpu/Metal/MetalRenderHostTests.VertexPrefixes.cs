// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

public sealed partial class MetalRenderHostTests
{
    private static readonly FieldInfo PrefixInvocation = typeof(MetalCommandStreamHost)
        .GetField("_directIndexedVertexPrefix", Members)!;

    private VertexInputInfo PrefixInput(TwoStageProvider provider, uint stride = 32, uint records = 64)
    {
        var original = provider.GetGraphicsPrograms(_banks.Shader.Vertex, _banks.Shader.Pixel,
            _banks.Context.ShaderInterface, _banks.Context, [], true, false).VertexInput;
        return new VertexInputInfo
        {
            Buffers = [new(VertexBase, stride, records)],
            Attributes = [original.Attributes[0] with
            {
                Descriptor = original.Attributes[0].Descriptor with { Word1 = (uint)(VertexBase >> 32) | (stride << 16), Word2 = records, Word3 = 77u << 12 },
                RegisterCount = 2,
            }],
            Stage = original.Stage,
        };
    }

    // Exercise actual executor→SubmitDraw→CopyVertexBuffers→recording backend. The
    // private invocation scalar is supplied independently here; packet eligibility
    // itself is covered below rather than pretending these are interpreter tests.
    private RecordedDraw PrefixDraw(bool index32, uint maximumIndex, uint instances = 1,
        bool eligibleInvocation = true, int baseVertex = 0)
    {
        var executor = Executor(out var provider);
        var input = PrefixInput(provider);
        provider.Vertex.VertexFetchComponents[0] = 1;
        provider.VertexInputOverride = input;
        _memory.TryWrite(VertexBase, Pattern(2048, 17));
        byte[] indices = new byte[index32 ? 12 : 6];
        for (var slot = 0; slot < 3; slot++)
        {
            var value = slot == 1 ? maximumIndex : 0;
            if (index32) BinaryPrimitives.WriteUInt32LittleEndian(indices.AsSpan(slot * 4), value);
            else BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(slot * 2), checked((ushort)value));
        }
        _memory.TryWrite(IndexBase, indices);
        PrefixInvocation.SetValue(_host, eligibleInvocation);
        try
        {
            executor.DrawIndexed(1, _banks, new DrawIndexedArguments(0, 0, 3, IndexBase,
                index32 ? 1u : 0u, instances, baseVertex, 0, DrawOffsetSource.Packet));
        }
        finally { PrefixInvocation.SetValue(_host, false); }
        var draw = Assert.Single(_backend.Draws);
        Assert.Equal(indices, draw.IndexBuffer!.Data);
        return draw;
    }

    [Theory]
    [InlineData(false, 0u)]
    [InlineData(false, 5u)]
    [InlineData(true, 0u)]
    [InlineData(true, 5u)]
    public void IndexedPrefixUsesSparseMaximumAndFullFloat4Width(bool index32, uint maximumIndex)
    {
        var draw = PrefixDraw(index32, maximumIndex);
        var stream = Assert.Single(draw.VertexBuffers!);
        var expectedLength = checked((int)maximumIndex * 32 + 16);
        Assert.Equal(expectedLength, stream.Length);
        Assert.Equal(Pattern(2048, 17)[..expectedLength], stream.Data);
        Assert.False(stream.Pooled);
        // The compiled attribute only consumes one component; native float4 fetch
        // still needs all sixteen bytes, not the compiler's four used bytes.
        Assert.Equal(1u, stream.ComponentCount);
        Assert.Equal(14u, stream.DataFormat);
    }

    [Theory]
    [InlineData(false, 64u, true, 0)]
    [InlineData(true, uint.MaxValue, true, 0)]
    [InlineData(false, 5u, false, 0)]
    [InlineData(false, 5u, true, 1)]
    public void IndexedPrefixFallsBackForOutOfBoundsOrUnqualifiedInvocation(bool index32, uint maximumIndex,
        bool eligibleInvocation, int baseVertex)
    {
        var stream = Assert.Single(PrefixDraw(index32, maximumIndex, eligibleInvocation: eligibleInvocation,
            baseVertex: baseVertex).VertexBuffers!);
        Assert.Equal(2048, stream.Length);
        Assert.Equal(Pattern(2048, 17), stream.Data);
    }

    [Theory]
    [InlineData(0u, 1u, true)]
    [InlineData(1u, 6u, true)]
    [InlineData(2u, 4u, false)]
    [InlineData(0u, 7u, false)]
    [InlineData(0u, 19u, false)]
    public void OriginalPacketEligibilityExcludesExpansionAndLegacyTopology(uint indexType, uint primitive, bool expected)
    {
        var args = new DrawIndexedArguments(0, 0, 3, IndexBase, indexType, 1, 0, 0, DrawOffsetSource.Packet);
        var method = typeof(MetalCommandStreamHost).GetMethod("IsDirectIndexedVertexPrefix", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expected, method.Invoke(null, [args, primitive, 0u]));
        foreach (var rejected in new[] { args with { OffsetSource = DrawOffsetSource.IndirectArguments },
            args with { IndirectArgumentsAddress = 64 }, args with { UnboundedIndexBuffer = true },
            args with { BaseVertex = -1 }, args with { FirstInstance = 1 } })
            Assert.Equal(false, method.Invoke(null, [rejected, primitive, 0u]));
        Assert.Equal(false, method.Invoke(null, [args, primitive, 1u]));
    }

    [Theory]
    [InlineData(3u, true)]
    [InlineData(21u, false)]
    public void IndexedPrefixUnionsInterleavedAttributesAndBoundsInstancesSeparately(uint instanceCount, bool fitsInstances)
    {
        var executor = Executor(out var provider);
        var first = PrefixInput(provider);
        var attributes = new[]
        {
            first.Attributes[0] with { Descriptor = first.Attributes[0].Descriptor with { Word3 = 64u << 12 } },
            first.Attributes[0] with { OffsetBytes = 8, RegisterStart = 4, AttributeId = 1 },
            first.Attributes[0] with { BufferIndex = 1, AttributeId = 2, RegisterStart = 8,
                Descriptor = first.Attributes[0].Descriptor with { Word3 = 64u << 12 } },
        };
        provider.VertexInputOverride = new VertexInputInfo
        {
            Buffers = [new(VertexBase, 32, 64), new(VertexBase + 4096, 16, 20, true)],
            Attributes = attributes,
            Stage = first.Stage,
        };
        _memory.TryWrite(VertexBase, Pattern(2048, 17));
        _memory.TryWrite(VertexBase + 4096, Pattern(320, 29));
        _memory.TryWrite(IndexBase, new byte[] { 5, 0, 0, 0, 2, 0 });
        PrefixInvocation.SetValue(_host, true);
        try { executor.DrawIndexed(1, _banks, new DrawIndexedArguments(0, 0, 3, IndexBase, 0, instanceCount, 0, 0, DrawOffsetSource.Packet)); }
        finally { PrefixInvocation.SetValue(_host, false); }
        var streams = Assert.Single(_backend.Draws).VertexBuffers!;
        Assert.Equal(fitsInstances ? 184 : 2048, streams[0].Length); // 5*32 + max(8,8+16)
        Assert.Same(streams[0].Data, streams[1].Data);
        Assert.Equal(fitsInstances ? 40 : 320, streams[2].Length); // (3-1)*16 + native float2 width8; otherwise full fallback
        Assert.Equal(Pattern(320, 29)[..(fitsInstances ? 40 : 320)], streams[2].Data);
    }
    [Theory]
    [InlineData(0u, 77u, 0u)]
    [InlineData(8u, 77u, 0u)]
    [InlineData(32u, 58u, 0u)]
    [InlineData(32u, 77u, 24u)]
    public void UnsafeStrideUnknownNativeFormatOrAttributeOverlapRetainsFullCopy(uint stride, uint format, uint offset)
    {
        var executor = Executor(out var provider);
        var input = PrefixInput(provider, stride, 64);
        provider.VertexInputOverride = new VertexInputInfo
        {
            Buffers = input.Buffers,
            Attributes = [input.Attributes[0] with
            {
                OffsetBytes = offset,
                Descriptor = input.Attributes[0].Descriptor with { Word3 = format << 12 },
            }],
            Stage = input.Stage,
        };
        var expected = Pattern(checked((int)input.Buffers[0].Size), 17);
        _memory.TryWrite(VertexBase, expected);
        _memory.TryWrite(IndexBase, new byte[] { 0, 0, 1, 0, 2, 0 });
        PrefixInvocation.SetValue(_host, true);
        try { executor.DrawIndexed(1, _banks, new DrawIndexedArguments(0, 0, 3, IndexBase, 0, 1, 0, 0, DrawOffsetSource.Packet)); }
        finally { PrefixInvocation.SetValue(_host, false); }
        var stream = Assert.Single(Assert.Single(_backend.Draws).VertexBuffers!);
        Assert.Equal(expected.Length, stream.Length);
        Assert.Equal(expected, stream.Data);
    }

    [Fact]
    public void IndexedPrefixUsesFullSubmittedCaptureAfterGuestMutationAndKeepsReplayPrivate()
    {
        var executor = Executor(out var provider);
        var input = PrefixInput(provider);
        provider.VertexInputOverride = input;
        var original = Pattern(2048, 17);
        _memory.TryWrite(VertexBase, original);
        _memory.TryWrite(IndexBase, new byte[] { 5, 0, 0, 0, 2, 0 });
        Assert.True(SubmittedVertexData.TryCapture(_memory, input, original.Length, out var snapshot));
        var registers = _host.Queue.GetInterpreter(0).Registers.Shader.Select(pair => (pair.Key, pair.Value)).ToArray();
        var geometry = AgcExports.CreateGeometrySnapshotsForTests(0, 0, IndexBase, 3, 1, registers, snapshot, 0x1000);
        _host.BeginSubmission(0, 2, geometry);
        var translation = (AgcExports.CommandStreamTranslation)typeof(AgcExports.TranslatingCommandStreamHost)
            .GetProperty("Translation", Members)!.GetValue(_host)!;
        var args = new DrawIndexedArguments(0, 0, 3, IndexBase, 0, 1, 0, 0, DrawOffsetSource.Packet);
        translation.RecordIndexedDrawState(in args);
        _memory.TryWrite(VertexBase, new byte[2048]);
        PrefixInvocation.SetValue(_host, true);
        try { executor.DrawIndexed(2, _banks, in args); }
        finally { PrefixInvocation.SetValue(_host, false); }
        var stream = Assert.Single(Assert.Single(_backend.Draws).VertexBuffers!);
        Assert.Equal(original[..176], stream.Data);
        stream.Data[0] = 201;
        Assert.Equal(original, snapshot!.CopyBuffer(0));
        Assert.Equal(original[..176], snapshot.CopyBufferPrefix(0, 176));
        Assert.Equal(2048, snapshot.ByteCount);
    }

    [Theory]
    [InlineData(113u, 16, 14u)]
    [InlineData(121u, 4, 5u)]
    public void AcceptedVertexFormatOverridesUseTheirActualNativeWidth(uint format, int width, uint dataFormat)
    {
        var executor = Executor(out var provider);
        var input = PrefixInput(provider);
        provider.VertexInputOverride = new VertexInputInfo
        {
            Buffers = input.Buffers,
            Attributes = [input.Attributes[0] with { Descriptor = input.Attributes[0].Descriptor with { Word3 = format << 12 } }],
            Stage = input.Stage,
        };
        _memory.TryWrite(VertexBase, Pattern(2048, 17));
        _memory.TryWrite(IndexBase, new byte[] { 0, 0, 2, 0, 1, 0 });
        PrefixInvocation.SetValue(_host, true);
        try { executor.DrawIndexed(1, _banks, new DrawIndexedArguments(0, 0, 3, IndexBase, 0, 1, 0, 0, DrawOffsetSource.Packet)); }
        finally { PrefixInvocation.SetValue(_host, false); }
        var stream = Assert.Single(Assert.Single(_backend.Draws).VertexBuffers!);
        Assert.Equal(64 + width, stream.Length);
        Assert.Equal(dataFormat, stream.DataFormat);
        Assert.Equal(7u, stream.NumberFormat);
    }

    [Theory]
    [InlineData(31, 0ul, 6)]
    [InlineData(64, 32ul, 6)]
    [InlineData(64, 0ul, 2)]
    public void TransientShortOrOffsetExtentCannotBeConcealedByPrefix(int vertexBytes, ulong offset, int indexBytes)
    {
        var executor = Executor(out var provider);
        _ = executor;
        var input = PrefixInput(provider, 16, 4);
        var render = (IRenderHost)_host;
        var vertexBinding = render.UploadTransient(Pattern(vertexBytes, 17), 16);
        render.BindVertexBuffers([vertexBinding with { Offset = offset }], input);
        render.BindIndexBuffer(render.UploadTransient(new byte[indexBytes], 16), GpuIndexType.Uint16);
        var type = typeof(MetalCommandStreamHost).GetNestedType("GraphicsPipelineRecord", BindingFlags.NonPublic)!;
        var pipeline = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [Shader("prefix", Gen5MslStage.Vertex), null, provider.Vertex, 0u,
                new PipelineStaticParameters { Topology = GpuPrimitiveTopology.TriangleList }], null)!;
        PrefixInvocation.SetValue(_host, true);
        try
        {
            var args = new object?[] { pipeline, 3u, 1u, null };
            Assert.Equal(false, typeof(MetalCommandStreamHost).GetMethod("TryPrepareIndexedVertexPrefixes", Members)!.Invoke(_host, args));
            Assert.Null(args[3]);
            var copies = (IReadOnlyList<GuestVertexBuffer>)typeof(MetalCommandStreamHost)
                .GetMethod("CopyVertexBuffers", Members)!.Invoke(_host, [pipeline, null, default(MetalVertexPrefixProfile.CopyScope)])!;
            // The original full-copy path's transient clipping remains observable.
            Assert.Equal(Math.Min(64, vertexBytes - (int)offset), Assert.Single(copies).Length);
        }
        finally { PrefixInvocation.SetValue(_host, false); }
    }

    private sealed class PrefixReadMemory(FakeCpuMemory inner) : SharpEmu.HLE.ICpuMemory
    {
        public List<(ulong Address, int Bytes)> Reads { get; } = [];
        public bool TryRead(ulong address, Span<byte> destination)
        {
            Reads.Add((address, destination.Length));
            return inner.TryRead(address, destination);
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => inner.TryWrite(address, source);
        public bool CanRead(ulong address, ulong count) => inner.CanRead(address, count);
    }

    [Theory]
    [InlineData(false, 2u, 64ul, false, 0u, 0u)]
    [InlineData(true, 2u, 64ul, true, 0u, 0u)]
    [InlineData(true, 9u, 64ul, true, 0u, 0u)]
    [InlineData(true, 2u, 32ul, false, 0u, 0u)]
    [InlineData(true, 2u, 64ul, false, 1u, 0u)]
    [InlineData(true, 2u, 64ul, false, 0u, 1u)]
    public void ActualSubmitDrawCapturesIndexOnceAndPreservesMetadataFallbackOrder(bool invocation, uint maximumIndex,
        ulong obtainedVertexBytes, bool expectedIndexFirst, uint firstIndex, uint firstInstance)
    {
        var trace = new PrefixReadMemory(_memory);
        var host = new MetalCommandStreamHost(trace, _backend, _backend);
        host.AttachQueue(new CommandStreamQueue(host));
        host.BeginSubmission(0, 1, null);
        var render = (IRenderHost)host;
        var descriptor = new BufferDescriptorWords(unchecked((uint)VertexBase), (uint)(VertexBase >> 32) | 16u << 16, 4, 77u << 12);
        var input = new VertexInputInfo
        {
            Buffers = [new(VertexBase, 16, 4)],
            Attributes = [new(descriptor, 0, 1, 0, 0, 0, 0)],
        };
        _memory.TryWrite(VertexBase, Pattern(64, 17));
        _memory.TryWrite(IndexBase, new byte[] { 0, 0, (byte)maximumIndex, 0, 1, 0 });
        render.BindVertexBuffers([render.ObtainBuffer(VertexBase, obtainedVertexBytes, false)], input);
        render.BindIndexBuffer(render.ObtainBuffer(IndexBase, 6, false), GpuIndexType.Uint16);
        var type = typeof(MetalCommandStreamHost).GetNestedType("GraphicsPipelineRecord", BindingFlags.NonPublic)!;
        var pipeline = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [Shader("prefix", Gen5MslStage.Vertex), Shader("pixel", Gen5MslStage.Pixel), new ShaderProgramInfo(), 0u,
                new PipelineStaticParameters { Topology = GpuPrimitiveTopology.TriangleList }], null)!;
        typeof(MetalCommandStreamHost).GetField("_boundPipeline", Members)!.SetValue(host, pipeline);
        PrefixInvocation.SetValue(host, invocation);
        try { render.DrawIndexed(3, 1, firstIndex, 0, firstInstance); }
        finally { PrefixInvocation.SetValue(host, false); }
        Assert.Equal(2, trace.Reads.Count);
        Assert.Equal(expectedIndexFirst ? IndexBase : VertexBase, trace.Reads[0].Address);
        Assert.Equal(expectedIndexFirst ? VertexBase : IndexBase, trace.Reads[1].Address);
        Assert.Equal(6, Assert.Single(trace.Reads.Where(read => read.Address == IndexBase)).Bytes);
        Assert.Equal(invocation && expectedIndexFirst && maximumIndex < 4 ? checked((int)maximumIndex * 16 + 16) : 64,
            Assert.Single(trace.Reads.Where(read => read.Address == VertexBase)).Bytes);
    }

    [Theory]
    [InlineData(true, false, 184, 184)]
    [InlineData(false, false, 2048, 1024)]
    [InlineData(true, true, 2048, 2048)]
    public void PrefixLengthPreservesOriginalNativeSlotSharing(bool sameFullSize, bool ambiguousInstanceStep,
        int firstLength, int secondLength)
    {
        var executor = Executor(out var provider);
        var first = PrefixInput(provider);
        provider.VertexInputOverride = new VertexInputInfo
        {
            Buffers = [new(VertexBase, 32, 64), new(VertexBase, 32, sameFullSize ? 64u : 32u, ambiguousInstanceStep)],
            Attributes = [first.Attributes[0], first.Attributes[0] with
            {
                BufferIndex = 1, AttributeId = 1, RegisterStart = 4,
                OffsetBytes = sameFullSize ? 8u : 0u,
            }],
            Stage = first.Stage,
        };
        _memory.TryWrite(VertexBase, Pattern(2048, 17));
        _memory.TryWrite(IndexBase, new byte[] { 5, 0, 0, 0, 2, 0 });
        PrefixInvocation.SetValue(_host, true);
        try { executor.DrawIndexed(1, _banks, new DrawIndexedArguments(0, 0, 3, IndexBase, 0, 1, 0, 0, DrawOffsetSource.Packet)); }
        finally { PrefixInvocation.SetValue(_host, false); }
        var streams = Assert.Single(_backend.Draws).VertexBuffers!;
        Assert.Equal(firstLength, streams[0].Length);
        Assert.Equal(secondLength, streams[1].Length);
        Assert.Equal(Pattern(2048, 17)[..firstLength], streams[0].Data);
        Assert.Equal(Pattern(2048, 17)[..secondLength], streams[1].Data);
    }

    [Theory]
    [InlineData(6, 6)]
    [InlineData(1, 32)]
    public void InputsBeyondFiniteNativePrefixCapRetainOriginalFullCopies(int streamCount, int attributeCount)
    {
        var executor = Executor(out var provider);
        var first = PrefixInput(provider, 16, 8);
        var buffers = Enumerable.Range(0, streamCount).Select(slot => new VertexInputBuffer(VertexBase + (ulong)slot * 256, 16, 8)).ToArray();
        var attributes = Enumerable.Range(0, attributeCount).Select(slot => first.Attributes[0] with
        {
            BufferIndex = slot % streamCount, AttributeId = slot, RegisterStart = slot * 4,
            Descriptor = first.Attributes[0].Descriptor.WithAddress(buffers[slot % streamCount].Address),
        }).ToArray();
        provider.VertexInputOverride = new VertexInputInfo { Buffers = buffers, Attributes = attributes, Stage = first.Stage };
        foreach (var buffer in buffers) _memory.TryWrite(buffer.Address, Pattern(128, 17));
        _memory.TryWrite(IndexBase, new byte[] { 0, 0, 1, 0, 2, 0 });
        PrefixInvocation.SetValue(_host, true);
        try { executor.DrawIndexed(1, _banks, new DrawIndexedArguments(0, 0, 3, IndexBase, 0, 1, 0, 0, DrawOffsetSource.Packet)); }
        finally { PrefixInvocation.SetValue(_host, false); }
        var streams = Assert.Single(_backend.Draws).VertexBuffers!;
        Assert.Equal(attributeCount, streams.Count);
        Assert.All(streams, stream => Assert.Equal(128, stream.Length));
    }

}
