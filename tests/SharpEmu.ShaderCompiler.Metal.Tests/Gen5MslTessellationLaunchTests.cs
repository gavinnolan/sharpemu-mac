// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.RegularExpressions;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// A tessellated draw on a host without a tessellator (Gen5TessellationLaunch): the merged local/hull
// program starts as the hardware's hull stage would start it, one guest wave64 per threadgroup of whole
// patches, and the domain program per vertex at the corners of each patch. Grand Theft Auto V's palm
// trunks: 3 control points, 21 patches a group, 32 KB of the off-chip buffer and 16 factor bytes a patch.
public sealed class Gen5MslTessellationLaunchTests
{
    private static readonly uint[] EndProgram =
    [
        0xBF810000,             // s_endpgm
    ];

    [Fact]
    public void TheHullKernelStartsEachLaneOnItsPatchAndControlPoint()
    {
        var source = Compile(ShaderStage.Compute,
            new Gen5TessellationLaunch(Gen5TessellationRole.Hull, 21, 3, 0x8000, 16));

        Assert.Contains("constant uint* sharpemu_hull_launch [[buffer(2)]]", source, StringComparison.Ordinal);
        Assert.Contains("device const uint* sharpemu_hull_vertices [[buffer(3)]]", source, StringComparison.Ordinal);
        // The group's first patch, and only the patches the draw has in its last group.
        Assert.Contains("uint sharpemu_hull_first = sharpemu_group_id.x * 21u;", source, StringComparison.Ordinal);
        Assert.Contains("? min(21u, sharpemu_hull_launch[0] - sharpemu_hull_first) : 0u;", source, StringComparison.Ordinal);
        // Off-chip offset, merged wave info (local and hull counts), factor offset.
        Assert.Contains("s2 = sharpemu_group_id.x * 32768u;", source, StringComparison.Ordinal);
        Assert.Contains("s3 = sharpemu_hull_points | (sharpemu_hull_points << 8);", source, StringComparison.Ordinal);
        Assert.Contains("s4 = sharpemu_hull_first * 16u;", source, StringComparison.Ordinal);
        // Relative patch [7:0], control point [12:8]; the local half's vertex from the host's ids.
        Assert.Contains("v1 = (sharpemu_hull_lane / 3u) | ((sharpemu_hull_lane % 3u) << 8);", source, StringComparison.Ordinal);
        Assert.Contains("? sharpemu_hull_vertices[sharpemu_hull_first * 3u + sharpemu_hull_lane] : 0u;", source, StringComparison.Ordinal);
        Assert.Contains("v5 = sharpemu_hull_launch[1];", source, StringComparison.Ordinal);
        // Not the ordinary compute launch.
        Assert.DoesNotContain("v0 = sharpemu_local_id.x;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ADomainVertexIsOneCornerOfOnePatch()
    {
        var source = Compile(ShaderStage.Vertex,
            new Gen5TessellationLaunch(Gen5TessellationRole.Domain, 21, 3, 0x8000, 16));

        Assert.Contains("uint sharpemu_domain_patch = sharpemu_vertex_id / 3u;", source, StringComparison.Ordinal);
        // u and v: corners (1, 0), (0, 1) and (0, 0).
        Assert.Contains("v5 = as_type<uint>(sharpemu_domain_corner == 0u ? 1.0f : 0.0f);", source, StringComparison.Ordinal);
        Assert.Contains("v6 = as_type<uint>(sharpemu_domain_corner == 1u ? 1.0f : 0.0f);", source, StringComparison.Ordinal);
        // The patch within its hull group, the patch, and that group's off-chip offset.
        Assert.Contains("v7 = sharpemu_domain_patch % 21u;", source, StringComparison.Ordinal);
        Assert.Contains("v8 = sharpemu_domain_patch;", source, StringComparison.Ordinal);
        Assert.Contains("s4 = (sharpemu_domain_patch / 21u) * 32768u;", source, StringComparison.Ordinal);
    }

    private static string Compile(ShaderStage stage, Gen5TessellationLaunch tessellation)
    {
        var program = Gen5ComputeFixtures.DecodeOrThrow(new Gen5ComputeFixture("tessellation", EndProgram, 0, 0));
        var request = Gen5ComputeFixtures.RequestOrThrow(program, stage,
            waveLaneCount: 64, localSizeX: 64, tessellation: tessellation);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        // Registers the program never indexes are emitted as locals (s2) rather than file entries (s[2]).
        return Regex.Replace(shader.Source, @"\b([sv])\[(\d+)\]", "$1$2");
    }
}
