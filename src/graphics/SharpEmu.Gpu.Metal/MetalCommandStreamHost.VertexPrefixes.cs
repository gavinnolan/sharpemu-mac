// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Gpu.Metal;

internal sealed partial class MetalCommandStreamHost
{
    // Existing Metal binding layout reserves0..25 for stage resources, leaving
    // vertex slots26..30 (five streams); vertex attribute indices are0..30.
    // Larger inputs retain the old path and its existing failure behavior.
    private const int MaximumPrefixStreams = 5;
    private const int MaximumPrefixAttributes = 31;

    private bool TryPrepareIndexedVertexPrefixes(GraphicsPipelineRecord pipeline, uint indexCount, uint instanceCount, out int[]? prefixes)
    {
        prefixes = null;
        if (!_directIndexedVertexPrefix || indexCount == 0 || instanceCount == 0 ||
            pipeline.Parameters.PrimitiveRestartEnable ||
            pipeline.Parameters.Topology is not (GpuPrimitiveTopology.PointList or GpuPrimitiveTopology.LineList or
                GpuPrimitiveTopology.LineStrip or GpuPrimitiveTopology.TriangleList or GpuPrimitiveTopology.TriangleStrip or
                GpuPrimitiveTopology.TriangleFan) ||
            _vertexInput is not { } input || input.Buffers.Length is 0 or > MaximumPrefixStreams ||
            input.Attributes.Length is 0 or > MaximumPrefixAttributes ||
            _indexType is not (GpuIndexType.Uint16 or GpuIndexType.Uint32) || _indexBinding is not { } indexBinding ||
            !_buffers.TryGetValue(indexBinding.Handle, out var indexBound)) return false;

        var indexBytes = (ulong)indexCount * (_indexType == GpuIndexType.Uint32 ? 4ul : 2ul);
        if (indexBytes > int.MaxValue || !ContainsBoundBytes(indexBound, indexBinding.Offset, indexBytes)) return false;

        // Require the original entire vertex copy to fit its obtained range. A prefix must
        // never conceal an invalid original extent, transient short read or arithmetic wrap.
        for (var slot = 0; slot < input.Buffers.Length; slot++)
        {
            var buffer = input.Buffers[slot];
            if (buffer.Stride == 0 || buffer.RecordCount == 0 || buffer.Size > int.MaxValue ||
                slot >= _vertexBindings.Length || !_buffers.TryGetValue(_vertexBindings[slot].Handle, out var bound) ||
                !ContainsBoundBytes(bound, _vertexBindings[slot].Offset, buffer.Size)) return false;
        }

        var ends = new int[input.Buffers.Length];
        foreach (var attribute in input.Attributes)
        {
            if ((uint)attribute.BufferIndex >= (uint)ends.Length || attribute.Descriptor.AddThreadId ||
                attribute.Descriptor.SwizzleEnabled || !TryGetExactVertexFetchWidth(attribute.Descriptor.Format, out var width)) return false;
            var end = (ulong)attribute.OffsetBytes + width;
            // Inter-record overlap and the presenter's out-of-length offset normalization
            // are deliberately excluded from this first bounded optimization.
            if (end > input.Buffers[attribute.BufferIndex].Stride) return false;
            ends[attribute.BufferIndex] = Math.Max(ends[attribute.BufferIndex], (int)end);
        }
        if (Array.Exists(ends, static end => end == 0)) return false;
        prefixes = ends;
        return true;
    }

    private static bool ContainsBoundBytes(BoundBuffer bound, ulong offset, ulong size) =>
        offset <= bound.Size && size <= bound.Size - offset &&
        (bound.Bytes is { } bytes
            ? offset <= (ulong)bytes.Length && size <= (ulong)bytes.Length - offset
            : bound.Address != 0 && offset <= ulong.MaxValue - bound.Address && size <= ulong.MaxValue - (bound.Address + offset));

    private bool TryFinishIndexedVertexPrefixes(GuestIndexBuffer indices, uint indexCount, uint instanceCount, int[] prefixes)
    {
        var width = indices.Is32Bit ? 4 : 2;
        if ((ulong)indices.Length != (ulong)indexCount * (uint)width || indices.Length != indices.Data.Length) return false;
        uint maximumIndex = 0;
        for (var offset = 0; offset < indices.Length; offset += width)
        {
            var value = indices.Is32Bit
                ? BinaryPrimitives.ReadUInt32LittleEndian(indices.Data.AsSpan(offset, 4))
                : BinaryPrimitives.ReadUInt16LittleEndian(indices.Data.AsSpan(offset, 2));
            maximumIndex = Math.Max(maximumIndex, value);
        }
        var buffers = _vertexInput!.Buffers;
        for (var slot = 0; slot < buffers.Length; slot++)
        {
            var buffer = buffers[slot];
            var lastRecord = buffer.PerInstance ? instanceCount - 1 : maximumIndex;
            if (lastRecord >= buffer.RecordCount) return false;
            var bytes = (ulong)lastRecord * buffer.Stride + (uint)prefixes[slot];
            if (bytes > buffer.Size || bytes > int.MaxValue) return false;
            prefixes[slot] = (int)bytes;
        }
        // Native slot assignment includes Length. Keep its original sharing partition:
        // shortening must neither split an existing slot nor merge two old slots.
        for (var left = 0; left < buffers.Length; left++)
            for (var right = left + 1; right < buffers.Length; right++)
            {
                var a = buffers[left];
                var b = buffers[right];
                if (a.Address == 0 || a.Address != b.Address || a.Stride != b.Stride) continue;
                if (a.Size == b.Size)
                {
                    if (a.PerInstance != b.PerInstance) return false;
                    prefixes[left] = prefixes[right] = Math.Max(prefixes[left], prefixes[right]);
                }
            }
        for (var left = 0; left < buffers.Length; left++)
            for (var right = left + 1; right < buffers.Length; right++)
                if (buffers[left].Address != 0 && buffers[left].Address == buffers[right].Address &&
                    buffers[left].Stride == buffers[right].Stride && buffers[left].Size != buffers[right].Size &&
                    prefixes[left] == prefixes[right]) return false;
        return true;
    }

    // Exact fixed MTLVertexFormat widths from the presenter's explicit mapping. Unknown
    // formats (including its component-count fallback) retain the original full copy.
    private static bool TryGetExactVertexFetchWidth(uint format, out uint width)
    {
        uint data, number;
        if (format == AcceptedSignedShortPairFormat) (data, number) = (DataFormat32x4, NumberFormatFloat);
        else if (format == AcceptedHalfPairFormat) (data, number) = (DataFormat16x2, NumberFormatFloat);
        else if (!Gfx10UnifiedFormat.TryDecode(format, out data, out number)) { width = 0; return false; }
        width = (data, number) switch
        {
            (1, 0 or 1 or 4 or 5) => 1,
            (2, 0 or 1 or 4 or 5 or 7) or (3, 0 or 1 or 4 or 5) => 2,
            (4, 4 or 5 or 7) or (5, 0 or 1 or 4 or 5 or 7) or
                (6 or 7, 7) or (8 or 9, 0 or 1) or (10, 0 or 1 or 4 or 5) or (34, 7) => 4,
            (11, 4 or 5 or 7) or (12, 0 or 1 or 4 or 5 or 6 or 7) => 8,
            (13, 4 or 5 or 7) => 12,
            (14, 4 or 5 or 7) => 16,
            _ => 0,
        };
        return width != 0;
    }
}
