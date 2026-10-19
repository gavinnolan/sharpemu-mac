// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed class MetalMeshletBuilderTests
{
    // Rebuilds the index stream from the buffer the way the mesh prologue reads it.
    private static uint[] Reassemble(MeshletBuilder.Result result, uint maxVertices, uint maxPrimitives)
    {
        var indices = new List<uint>();
        var nextTriangle = 0u;
        for (var meshlet = 0u; meshlet < result.MeshletCount; meshlet++)
        {
            var vertexOffset = result.Words[(meshlet * 4) + 0];
            var primitiveOffset = result.Words[(meshlet * 4) + 1];
            var firstTriangle = result.Words[(meshlet * 4) + 2];
            var counts = result.Words[(meshlet * 4) + 3];
            var vertexCount = counts & 0xFFFFu;
            var primitiveCount = counts >> 16;

            Assert.True(vertexCount <= maxVertices, $"meshlet {meshlet} has {vertexCount} vertices");
            Assert.True(primitiveCount <= maxPrimitives, $"meshlet {meshlet} has {primitiveCount} triangles");
            Assert.Equal(nextTriangle, firstTriangle);
            nextTriangle += primitiveCount;

            for (var primitive = 0u; primitive < primitiveCount; primitive++)
            {
                var packed = result.Words[result.PrimitiveBase + primitiveOffset + primitive];
                foreach (var shift in new[] { 0, 8, 16 })
                {
                    var local = (packed >> shift) & 0xFFu;
                    Assert.True(local < vertexCount);
                    indices.Add(result.Words[result.VertexBase + vertexOffset + local]);
                }
            }
        }

        return [.. indices];
    }

    [Theory]
    [InlineData(30u, 10u)]
    [InlineData(32u, 32u)]
    [InlineData(64u, 64u)]
    [InlineData(255u, 255u)]
    public void RandomTrianglesReassembleToTheInputInOrder(uint maxVertices, uint maxPrimitives)
    {
        var random = new Random(1234);
        var indices = new uint[3 * 5000];
        for (var i = 0; i < indices.Length; i++)
        {
            // A small vertex range makes reuse (and degenerate triangles) common.
            indices[i] = (uint)random.Next(0, 200);
        }

        var result = MeshletBuilder.Build(indices, maxVertices, maxPrimitives);
        Assert.Equal(indices, Reassemble(result, maxVertices, maxPrimitives));
    }

    [Fact]
    public void DegenerateTrianglesAreKept()
    {
        uint[] indices = [3, 3, 3, 1, 2, 2, 5, 6, 7];
        var result = MeshletBuilder.Build(indices, 30, 10);
        Assert.Equal(indices, Reassemble(result, 30, 10));
        Assert.Equal(1u, result.MeshletCount);
    }

    [Fact]
    public void ARaggedIndexCountDropsTheIncompleteTriangle()
    {
        uint[] indices = [0, 1, 2, 3, 4];
        var result = MeshletBuilder.Build(indices, 30, 10);
        Assert.Equal<uint>([0, 1, 2], Reassemble(result, 30, 10));
    }

    [Fact]
    public void AnEmptyDrawBuildsNoMeshlets()
    {
        Assert.Equal(0u, MeshletBuilder.Build([], 30, 10).MeshletCount);
    }

    [Theory]
    [InlineData(32u, 32u)]
    [InlineData(30u, 10u)]
    public void AGridPacksNearThePrimitiveLimit(uint maxVertices, uint maxPrimitives)
    {
        const int Cells = 64;
        var indices = new List<uint>();
        for (var y = 0; y < Cells; y++)
        {
            for (var x = 0; x < Cells; x++)
            {
                var a = (uint)((y * (Cells + 1)) + x);
                var b = a + 1;
                var c = a + Cells + 1;
                var d = c + 1;
                indices.AddRange([a, b, c, b, d, c]);
            }
        }

        var result = MeshletBuilder.Build([.. indices], maxVertices, maxPrimitives);
        Assert.Equal([.. indices], Reassemble(result, maxVertices, maxPrimitives));

        var triangles = indices.Count / 3;
        var ideal = (triangles + (int)maxPrimitives - 1) / (int)maxPrimitives;
        if (maxVertices == 32)
        {
            Assert.True(
                result.MeshletCount <= ideal * 1.2,
                $"{result.MeshletCount} meshlets for {triangles} triangles (ideal {ideal})");
        }
        else
        {
            // 10-triangle meshlets are bound by the triangle limit; the vertex limit never bites.
            Assert.True(result.MeshletCount <= ideal * 1.2);
        }
    }
}
