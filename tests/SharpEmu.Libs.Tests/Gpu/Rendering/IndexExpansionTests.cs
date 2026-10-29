// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

// The index rewrites of a draw (fans and quad lists as triangle lists, 8-bit widening, the custom
// restart index) write into reused scratch; each must produce what the straightforward allocating form
// did, whatever the scratch held before.
public sealed class IndexExpansionTests
{
    // The allocating forms the executor used before the scratch.
    private static uint[] ReferenceTriangulate(GuestPrimitiveType type, uint[] vertices, uint? restartIndex)
    {
        if (type == GuestPrimitiveType.QuadListLegacy)
        {
            var quads = new uint[checked((vertices.Length / 4) * 6)];
            for (int quad = 0, output = 0; quad < vertices.Length; quad += 4, output += 6)
            {
                quads[output] = vertices[quad];
                quads[output + 1] = vertices[quad + 1];
                quads[output + 2] = vertices[quad + 2];
                quads[output + 3] = vertices[quad];
                quads[output + 4] = vertices[quad + 2];
                quads[output + 5] = vertices[quad + 3];
            }

            return quads;
        }

        var triangles = new List<uint>(Math.Max(0, (vertices.Length - 2) * 3));
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
                triangles.Add(vertices[index - 1]);
                triangles.Add(vertices[index]);
                triangles.Add(vertices[hub]);
            }
        }

        return [.. triangles];
    }

    private static byte[] ReferenceListBytes(uint[] list, bool wide)
    {
        var bytes = new byte[list.Length * (wide ? sizeof(uint) : sizeof(ushort))];
        for (var index = 0; index < list.Length; index++)
        {
            if (wide)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), list[index]);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * sizeof(ushort)), checked((ushort)list[index]));
            }
        }

        return bytes;
    }

    private static byte[] ReferenceIndex8(byte[] source, bool primitiveRestart, uint restartIndex)
    {
        var expanded = new byte[source.Length * 2];
        for (var i = 0; i < source.Length; i++)
        {
            var value = source[i];
            ushort index = primitiveRestart && value == (restartIndex & 0xFF) ? (ushort)0xFFFF : value;
            expanded[i * 2] = (byte)index;
            expanded[(i * 2) + 1] = (byte)(index >> 8);
        }

        return expanded;
    }

    private static byte[] ReferenceRestart(byte[] source, int stride, uint restartIndex)
    {
        var count = source.Length / stride;
        var converted = new byte[count * sizeof(uint)];
        for (var index = 0; index < count; index++)
        {
            var value = stride == sizeof(ushort)
                ? BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(index * stride))
                : BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(index * stride));
            BinaryPrimitives.WriteUInt32LittleEndian(converted.AsSpan(index * sizeof(uint)),
                value == restartIndex ? uint.MaxValue : value);
        }

        return converted;
    }

    private static uint[] Vertices(System.Random random, int count, uint? restart, uint ceiling)
    {
        var vertices = new uint[count];
        for (var index = 0; index < count; index++)
        {
            vertices[index] = restart is { } r && random.Next(5) == 0 ? r : (uint)random.NextInt64(0, ceiling);
        }

        return vertices;
    }

    // The scratch the executor holds is dirty with a longer, different draw before each case.
    private static void Dirty(Span<uint> list, Span<byte> bytes)
    {
        list.Fill(0xDEAD_BEEF);
        bytes.Fill(0xA5);
    }

    [Theory]
    [InlineData(GuestPrimitiveType.TriangleFan, false)]
    [InlineData(GuestPrimitiveType.Polygon, false)]
    [InlineData(GuestPrimitiveType.TriangleFan, true)]
    [InlineData(GuestPrimitiveType.QuadListLegacy, false)]
    public void TriangulateFans_MatchesTheAllocatingForm(GuestPrimitiveType type, bool withRestart)
    {
        var random = new System.Random(1234 + (int)type);
        foreach (var count in new[] { 0, 1, 2, 3, 4, 5, 8, 9, 64, 1000, 5001 })
        {
            if (type == GuestPrimitiveType.QuadListLegacy && (count & 3) != 0)
            {
                continue;
            }

            uint? restart = withRestart ? 0xFFFFu : null;
            var vertices = Vertices(random, count, restart, 60_000);
            var expected = ReferenceTriangulate(type, vertices, restart);
            var capacity = IndexExpansion.MaxTriangleListCount(type, count);
            Assert.True(expected.Length <= capacity);

            var output = new uint[capacity + 7];
            Dirty(output, []);
            var written = IndexExpansion.TriangulateFans(type, vertices, restart, output.AsSpan(0, capacity));

            Assert.Equal(expected, output.AsSpan(0, written).ToArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListIndexBytes_MatchTheAllocatingForm(bool wide)
    {
        var random = new System.Random(77);
        foreach (var count in new[] { 0, 3, 6, 255, 4096 })
        {
            var list = Vertices(random, count, null, wide ? 4_000_000_000u : 65_536u);
            var expected = ReferenceListBytes(list, wide);
            var bytes = new byte[expected.Length + 16];
            Dirty([], bytes);

            Assert.True(IndexExpansion.WriteListIndexBytes(list, wide, bytes));
            Assert.Equal(expected, bytes.AsSpan(0, expected.Length).ToArray());
        }
    }

    [Fact]
    public void ListIndexBytes_RefuseAnAllOnesIndexAndANarrowOverflow()
    {
        Assert.False(IndexExpansion.WriteListIndexBytes([1u, uint.MaxValue], wide: true, new byte[8]));
        Assert.Throws<OverflowException>(() => IndexExpansion.WriteListIndexBytes([0x1_0000u], wide: false, new byte[2]));
    }

    [Theory]
    [InlineData(false, 0xFFu)]
    [InlineData(true, 0xFFu)]
    [InlineData(true, 0x1_0007u)]
    public void ExpandIndex8_MatchesTheAllocatingForm(bool primitiveRestart, uint restartIndex)
    {
        var random = new System.Random(5);
        foreach (var count in new[] { 0, 1, 7, 256, 3000 })
        {
            var source = new byte[count];
            random.NextBytes(source);
            if (count > 3)
            {
                source[1] = (byte)(restartIndex & 0xFF);
            }

            var expected = ReferenceIndex8(source, primitiveRestart, restartIndex);
            var destination = new byte[expected.Length + 5];
            Dirty([], destination);

            IndexExpansion.ExpandIndex8(source, primitiveRestart, restartIndex, destination);

            Assert.Equal(expected, destination.AsSpan(0, expected.Length).ToArray());
        }
    }

    [Theory]
    [InlineData(2, 0xFFFEu)]
    [InlineData(2, 0x7u)]
    [InlineData(4, 0xFFFF_FFFEu)]
    [InlineData(4, 0x12u)]
    public void ConvertRestart_MatchesTheAllocatingForm(int stride, uint restartIndex)
    {
        var random = new System.Random(99);
        foreach (var count in new[] { 0, 1, 5, 400 })
        {
            var source = new byte[count * stride];
            for (var index = 0; index < count; index++)
            {
                var value = random.Next(3) == 0 ? restartIndex : (uint)random.NextInt64(0, stride == 2 ? 0xFFFF : 0xFFFF_FFFEL);
                if (stride == 2)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(index * 2), (ushort)value);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(index * 4), value);
                }
            }

            var expected = ReferenceRestart(source, stride, restartIndex);
            var destination = new byte[expected.Length + 9];
            Dirty([], destination);

            Assert.True(IndexExpansion.ConvertRestart(source, stride, restartIndex, destination));
            Assert.Equal(expected, destination.AsSpan(0, expected.Length).ToArray());
        }
    }

    [Fact]
    public void ConvertRestart_RefusesAnIndexThatIsAlreadyAllOnes()
    {
        var source = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(4), uint.MaxValue);
        Assert.False(IndexExpansion.ConvertRestart(source, sizeof(uint), 7, new byte[8]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Widen_ReadsLittleEndianIndicesOfEachStride(int stride)
    {
        var random = new System.Random(stride);
        var count = 300;
        var source = new byte[count * stride];
        random.NextBytes(source);
        var widened = new uint[count + 3];
        widened.AsSpan().Fill(0xFFFF_FFFF);

        IndexExpansion.Widen(source, stride, widened.AsSpan(0, count));

        for (var index = 0; index < count; index++)
        {
            var expected = stride switch
            {
                1 => source[index],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(index * 2)),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(index * 4)),
            };
            Assert.Equal(expected, widened[index]);
        }

        Assert.Equal(0xFFFF_FFFFu, widened[count]);
    }

    [Fact]
    public void TheScratchGrowsAndKeepsItsBuffersUnlessAnOversizedDraw()
    {
        var scratch = new IndexExpansionScratch();
        var small = scratch.Vertices(100);
        Assert.Same(small, scratch.Vertices(50));
        var grown = scratch.Vertices(1000);
        Assert.True(grown.Length >= 1000);
        Assert.Same(grown, scratch.Vertices(1000));

        // A request past the retention limit is served and then forgotten, so one huge draw pins nothing.
        var huge = scratch.Vertices(4 << 20);
        Assert.True(huge.Length >= 4 << 20);
        Assert.Same(grown, scratch.Vertices(1000));
    }
}
