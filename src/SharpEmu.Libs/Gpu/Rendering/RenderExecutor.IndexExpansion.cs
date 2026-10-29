// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Abstractions;

namespace SharpEmu.Libs.Gpu.Rendering;

// The index buffers a draw has to rewrite before the host can draw them: fans, polygons and quad lists as triangle
// lists (Metal has no fan primitive), 8-bit indices widened to 16, and a custom restart index mapped to the
// all-ones one. They used to allocate several arrays per draw (about 30 bytes per index, the large object heap
// above roughly 21k indices); the passes below write into per-thread scratch that the draw's upload consumes
// before the next draw, so a draw allocates nothing once the scratch has grown to its largest index count.
public sealed partial class RenderExecutor
{
    private byte[] ReadIndexBytes(ulong indexAddress, int byteCount, string what, uint indexCount)
    {
        var source = IndexExpansionScratch.Current.Read(byteCount);
        if (indexAddress == 0 || !_host.TryReadGuest(indexAddress, source.AsSpan(0, byteCount)))
        {
            throw _host.Fatal($"The {what} index data is unreadable: address=0x{indexAddress:X16} count={indexCount}.");
        }

        return source;
    }

    // The draw's guest indices, widened to 32 bits, in scratch that the next scratch call may overwrite.
    private ReadOnlySpan<uint> ReadGuestIndicesScratch(ulong indexAddress, uint indexCount, GuestIndexType type)
    {
        var stride = type switch
        {
            GuestIndexType.Index8 => 1,
            GuestIndexType.Index16 => 2,
            GuestIndexType.Index32 => 4,
            _ => throw _host.Fatal($"The index type and size is unknown: indexTypeAndSize={(uint)type}."),
        };
        var source = ReadIndexBytes(indexAddress, checked((int)indexCount * stride), "fan", indexCount);
        var indices = IndexExpansionScratch.Current.Vertices(checked((int)indexCount)).AsSpan(0, (int)indexCount);
        IndexExpansion.Widen(source, stride, indices);
        return indices;
    }

    // The vertices 0..count-1 of a non-indexed fan, in scratch.
    private static ReadOnlySpan<uint> SequentialVerticesScratch(uint vertexCount)
    {
        var vertices = IndexExpansionScratch.Current.Vertices(checked((int)vertexCount)).AsSpan(0, (int)vertexCount);
        for (var index = 0; index < vertices.Length; index++)
        {
            vertices[index] = (uint)index;
        }

        return vertices;
    }

    // The triangle list the primitive assembler makes of these vertices (see IndexExpansion.TriangulateFans),
    // in scratch separate from the vertices'.
    private ReadOnlySpan<uint> TriangulateFansScratch(GuestPrimitiveType type, ReadOnlySpan<uint> vertices, uint? restartIndex)
    {
        if (type == GuestPrimitiveType.QuadListLegacy && (vertices.Length & 0x3) != 0)
        {
            throw _host.Fatal($"A legacy quad list count is not a multiple of four: count={vertices.Length}.");
        }

        var capacity = IndexExpansion.MaxTriangleListCount(type, vertices.Length);
        var output = IndexExpansionScratch.Current.List(capacity).AsSpan(0, capacity);
        var written = IndexExpansion.TriangulateFans(type, vertices, restartIndex, output);
        return output[..written];
    }

    // The list's indices as little-endian bytes in scratch; the array is longer than the returned byte count.
    private byte[] ListIndexBytesScratch(ReadOnlySpan<uint> list, bool wide, out int byteCount)
    {
        byteCount = checked(list.Length * (wide ? sizeof(uint) : sizeof(ushort)));
        var bytes = IndexExpansionScratch.Current.Output(byteCount);
        if (!IndexExpansion.WriteListIndexBytes(list, wide, bytes))
        {
            throw _host.Fatal("A triangle fan converted to a triangle list has a 32-bit vertex index of 0xFFFFFFFF.");
        }

        return bytes;
    }

    private byte[] ConvertRestartIndicesScratch(ulong indexAddress, uint indexCount, GpuIndexType indexType, uint restartIndex, out int byteCount)
    {
        var sourceStride = indexType == GpuIndexType.Uint16 ? sizeof(ushort) : sizeof(uint);
        var source = ReadIndexBytes(indexAddress, checked((int)indexCount * sourceStride), "restart", indexCount);
        // Widen 16-bit indices so their maximum value remains an ordinary vertex.
        byteCount = checked((int)indexCount * sizeof(uint));
        var converted = IndexExpansionScratch.Current.Output(byteCount);
        if (!IndexExpansion.ConvertRestart(source.AsSpan(0, (int)indexCount * sourceStride), sourceStride, restartIndex, converted))
        {
            throw _host.Fatal("A custom restart index conflicts with a 32-bit vertex index of 0xFFFFFFFF.");
        }

        return converted;
    }

    private byte[] ExpandIndex8Scratch(ulong indexAddress, uint indexCount, bool primitiveRestart, uint restartIndex, out int byteCount)
    {
        if (indexAddress == 0)
        {
            throw _host.Fatal($"An 8-bit index draw has no index address: count={indexCount}.");
        }

        var source = ReadIndexBytes(indexAddress, checked((int)indexCount), "8-bit", indexCount);
        byteCount = checked((int)indexCount * 2);
        var expanded = IndexExpansionScratch.Current.Output(byteCount);
        IndexExpansion.ExpandIndex8(source.AsSpan(0, (int)indexCount), primitiveRestart, restartIndex, expanded);
        return expanded;
    }
}

/// <summary>The index rewrites as pure functions over spans, so a test can compare them with a straightforward form.</summary>
internal static class IndexExpansion
{
    /// <summary>Widens 8-, 16- or 32-bit little-endian indices to 32 bits.</summary>
    public static void Widen(ReadOnlySpan<byte> source, int stride, Span<uint> destination)
    {
        switch (stride)
        {
            case 1:
                for (var index = 0; index < destination.Length; index++)
                {
                    destination[index] = source[index];
                }

                break;
            case 2:
                for (var index = 0; index < destination.Length; index++)
                {
                    destination[index] = BinaryPrimitives.ReadUInt16LittleEndian(source[(index * 2)..]);
                }

                break;
            default:
                for (var index = 0; index < destination.Length; index++)
                {
                    destination[index] = BinaryPrimitives.ReadUInt32LittleEndian(source[(index * 4)..]);
                }

                break;
        }
    }

    /// <summary>The most indices <see cref="TriangulateFans"/> writes for this many vertices.</summary>
    public static int MaxTriangleListCount(GuestPrimitiveType type, int vertexCount) =>
        type == GuestPrimitiveType.QuadListLegacy
            ? checked((vertexCount / 4) * 6)
            : Math.Max(0, checked((vertexCount - 2) * 3));

    /// <summary>
    /// The triangle list the primitive assembler makes of these vertices, in its vertex order: fan
    /// triangle i is (v[i+1], v[i+2], v[0]), the order DI_PT_TRIFAN assembles (the first-vertex
    /// provoking vertex is v[i+1], as radv's VK fans report it), and a restart index ends the fan, the
    /// next index starting a new one. A polygon is one fan. Each quad of a quad list is (q0, q1, q2)
    /// and (q0, q2, q3), both taking q0 as their provoking vertex. Returns the index count written;
    /// the destination holds <see cref="MaxTriangleListCount"/> indices, and a quad count that is not
    /// a whole number of quads is the caller's to refuse.
    /// </summary>
    public static int TriangulateFans(GuestPrimitiveType type, ReadOnlySpan<uint> vertices, uint? restartIndex, Span<uint> output)
    {
        if (type == GuestPrimitiveType.QuadListLegacy)
        {
            var written = 0;
            for (var quad = 0; quad + 3 < vertices.Length; quad += 4, written += 6)
            {
                output[written] = vertices[quad];
                output[written + 1] = vertices[quad + 1];
                output[written + 2] = vertices[quad + 2];
                output[written + 3] = vertices[quad];
                output[written + 4] = vertices[quad + 2];
                output[written + 5] = vertices[quad + 3];
            }

            return written;
        }

        var count = 0;
        var hub = 0;
        for (var index = 0; index < vertices.Length; index++)
        {
            if (restartIndex is { } restart && vertices[index] == restart)
            {
                hub = index + 1;
                continue;
            }

            if (index - hub >= 2)
            {
                output[count] = vertices[index - 1];
                output[count + 1] = vertices[index];
                output[count + 2] = vertices[hub];
                count += 3;
            }
        }

        return count;
    }

    /// <summary>
    /// Writes the indices as 16- or 32-bit little-endian values. A 32-bit all-ones index is refused
    /// (false): Metal reserves it for primitive restart. A 16-bit value that does not fit throws.
    /// </summary>
    public static bool WriteListIndexBytes(ReadOnlySpan<uint> list, bool wide, Span<byte> destination)
    {
        for (var index = 0; index < list.Length; index++)
        {
            var value = list[index];
            if (wide)
            {
                if (value == uint.MaxValue)
                {
                    return false;
                }

                BinaryPrimitives.WriteUInt32LittleEndian(destination[(index * sizeof(uint))..], value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(destination[(index * sizeof(ushort))..], checked((ushort)value));
            }
        }

        return true;
    }

    /// <summary>8-bit indices as 16-bit ones, the restart value (compared on its low byte) becoming 0xFFFF.</summary>
    public static void ExpandIndex8(ReadOnlySpan<byte> source, bool primitiveRestart, uint restartIndex, Span<byte> destination)
    {
        var restart = restartIndex & 0xFF;
        for (var i = 0; i < source.Length; i++)
        {
            var value = source[i];
            var index = primitiveRestart && value == restart ? (ushort)0xFFFF : value;
            destination[i * 2] = (byte)index;
            destination[(i * 2) + 1] = (byte)(index >> 8);
        }
    }

    /// <summary>
    /// 16- or 32-bit indices as 32-bit ones with a custom restart index mapped to the all-ones one.
    /// False when a vertex index already is all-ones, which the mapping would turn into a restart.
    /// </summary>
    public static bool ConvertRestart(ReadOnlySpan<byte> source, int sourceStride, uint restartIndex, Span<byte> destination)
    {
        var count = source.Length / sourceStride;
        for (var index = 0; index < count; index++)
        {
            var value = sourceStride == sizeof(ushort)
                ? BinaryPrimitives.ReadUInt16LittleEndian(source[(index * sourceStride)..])
                : BinaryPrimitives.ReadUInt32LittleEndian(source[(index * sourceStride)..]);
            if (value == uint.MaxValue)
            {
                return false;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(destination[(index * sizeof(uint))..],
                value == restartIndex ? uint.MaxValue : value);
        }

        return true;
    }
}

/// <summary>
/// The buffers the index rewrites use, one set per thread. A buffer grows to the largest request and is
/// kept up to a limit; a larger one is allocated for its draw alone, so one huge draw does not pin memory.
/// Every request is valid until the next request for the same buffer on this thread.
/// </summary>
internal sealed class IndexExpansionScratch
{
    private const int RetainLimitBytes = 8 << 20;

    [ThreadStatic]
    private static IndexExpansionScratch? _current;

    public static IndexExpansionScratch Current => _current ??= new IndexExpansionScratch();

    private byte[] _read = [];
    private byte[] _output = [];
    private uint[] _vertices = [];
    private uint[] _list = [];

    public byte[] Read(int bytes) => Take(ref _read, bytes);

    public byte[] Output(int bytes) => Take(ref _output, bytes);

    public uint[] Vertices(int count) => Take(ref _vertices, count);

    public uint[] List(int count) => Take(ref _list, count);

    private static T[] Take<T>(ref T[] slot, int count) where T : unmanaged
    {
        if (slot.Length >= count)
        {
            return slot;
        }

        var elementBytes = (long)System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        var doubled = Math.Max((long)count, (long)slot.Length * 2);
        var size = doubled * elementBytes <= RetainLimitBytes ? doubled : count;
        var grown = new T[size];
        if (size * elementBytes <= RetainLimitBytes)
        {
            slot = grown;
        }

        return grown;
    }
}
