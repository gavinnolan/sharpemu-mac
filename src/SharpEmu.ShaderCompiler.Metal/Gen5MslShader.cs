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
    bool ReadsIndexBuffer)
{
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
    bool WritesViewportIndex = false);
