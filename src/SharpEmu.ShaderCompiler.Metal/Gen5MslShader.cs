// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Metal;

// MSL-specific shader artifact types. These stay beside the MSL emitter (not in the
// backend-neutral SharpEmu.ShaderCompiler project): each codegen owns its own
// compiled-shader shape, mirroring Gen5SpirvShader on the Vulkan side.
public enum Gen5MslStage
{
    Vertex,
    Pixel,
    Compute,

    // An NGG primitive shader (the merged ES/GS hardware stage) emitted as an MSL
    // [[mesh]] function. One Metal threadgroup is one NGG subgroup.
    Mesh,
}

public enum Gen5MslMeshTopology
{
    Point,
    Line,
    Triangle,
}

// The draw topology a fused program's GS half assembles its input primitives from. The
// values are what the host writes into the draw-info block's gs_input_topology.
public enum Gen5MslMeshInputTopology : uint
{
    TriangleList = 0,
    TriangleStrip = 1,
    PointList = 2,
}

// Everything the render host needs to build an MTLMeshRenderPipelineDescriptor around a
// translated primitive shader and to launch it, plus the buffer slots the mesh entry reads
// its NGG system values from. docs/mac/ngg-mesh-shaders.md is the host-side contract.
public sealed record Gen5MslMeshInfo(
    uint ThreadsPerThreadgroup,
    uint WavesPerThreadgroup,
    uint MaxVertices,
    uint MaxPrimitives,
    uint VerticesPerPrimitive,
    Gen5MslMeshTopology Topology,
    uint ThreadgroupMemoryBytes,
    uint DrawInfoBufferIndex,
    uint IndexBufferIndex,
    bool ReadsIndexBuffer,
    uint GeometryVerticesOut = 0)
{
    /// <summary>Whether the program has a GS half that assembles its input primitives
    /// from the draw's topology; see <see cref="InputPrimitivesPerSubgroup"/>.</summary>
    public bool HasGeometryHalf => GeometryVerticesOut != 0;

    /// <summary>
    /// The input primitives one subgroup of a fused (ES+GS) draw takes. Every lane is one
    /// ES vertex and one GS primitive, so the input vertices the topology needs must fit
    /// the vertex extent, and what the GS half can emit for them - up to
    /// <see cref="GeometryVerticesOut"/> vertices per primitive, as a strip - must fit the
    /// mesh output. The host splits the draw into subgroups of this size and the mesh
    /// prologue assembles the GS vertex offsets from the same number.
    /// </summary>
    public uint InputPrimitivesPerSubgroup(Gen5MslMeshInputTopology input)
    {
        var byInput = input switch
        {
            Gen5MslMeshInputTopology.TriangleStrip => MaxVertices > 2 ? MaxVertices - 2 : 1,
            Gen5MslMeshInputTopology.PointList => MaxVertices,
            _ => Math.Max(MaxVertices / 3, 1),
        };
        var emitted = Math.Max(GeometryVerticesOut, 1);
        var byOutputVertices = Math.Max(MaxVertices / emitted, 1);
        var byOutputPrimitives = Math.Max(MaxPrimitives / (emitted > 2 ? emitted - 2 : 1), 1);
        return Math.Min(Math.Min(byInput, MaxPrimitives), Math.Min(byOutputVertices, byOutputPrimitives));
    }

    /// <summary>The input primitives a fused draw of <paramref name="count"/> vertices or
    /// indices assembles.</summary>
    public static uint InputPrimitiveCount(Gen5MslMeshInputTopology input, uint count) => input switch
    {
        Gen5MslMeshInputTopology.TriangleStrip => count > 2 ? count - 2 : 0,
        Gen5MslMeshInputTopology.PointList => count,
        _ => count / 3,
    };

    /// <summary>Byte size of the Gen5MeshDrawInfo block the host fills at
    /// <see cref="DrawInfoBufferIndex"/>; see the generated struct of the same name.</summary>
    public const uint DrawInfoByteSize = 64;

    /// <summary>Dword count of that block, for a host that fills it as words.</summary>
    public const uint DrawInfoDwordCount = DrawInfoByteSize / 4;
}

// Compiled source, stage interface counts, and the argument-buffer layout.
public sealed record Gen5MslShader(
    string Source,
    string EntryPoint,
    Gen5MslStage Stage,
    uint AttributeCount,
    uint ThreadgroupSizeX = 1,
    uint ThreadgroupSizeY = 1,
    uint ThreadgroupSizeZ = 1,
    Gen5MslArgumentLayout? ArgumentLayout = null,
    Gen5MslMeshInfo? MeshInfo = null,
    bool WritesLayer = false,
    bool WritesViewportIndex = false)
{
    /// <summary>MSL 4.0 (macOS 26), not the backend's usual version, compiles this source:
    /// a pixel shader declares metal::vertex_value inputs, which only that version has.</summary>
    public const nuint PerVertexInputsLanguageVersion = (4 << 16) | 0;

    /// <summary>The source declares per-vertex pixel inputs; see <see cref="PerVertexInputsLanguageVersion"/>.</summary>
    public bool UsesPerVertexInputs { get; init; }
}
