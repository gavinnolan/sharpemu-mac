// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

// What VGT_SHADER_STAGES_EN selects, read the same way by the executor (which decides
// whether a draw runs at all) and by the pipeline cache (which decides how its vertex
// program is compiled). Those two must agree: a program compiled as the merged ES/GS stage
// for a draw the executor would not admit as one has no pipeline to run on, and a draw
// admitted as one whose program was compiled per-vertex has no mesh function.
public static class VgtShaderStages
{
    // GS_W32_EN (bit 22) and VS_W32_EN (bit 23) only select the wave size; graphics stages
    // always compile as wave32, so they never change the path.
    public const uint WaveSizeBits = (1u << 22) | (1u << 23);

    /// <summary>GS_W32_EN (bit 22): set selects wave32, clear selects wave64 for the
    /// merged ES/GS stage.</summary>
    public const uint GeometryWave32Bit = 1u << 22;

    public static bool IsGeometryWave64(uint stages) => (stages & GeometryWave32Bit) == 0;

    /// <summary>PRIMGEN_PASSTHRU_EN (bit 25): NGG passthrough mode, where the primitive
    /// shader's v0 is the packed primitive export rather than the GS vertex offsets.</summary>
    public const uint PrimitivePassthroughBit = 1u << 25;

    public static bool IsPrimitivePassthrough(uint stages) => (stages & PrimitivePassthroughBit) != 0;

    /// <summary>GS_EN (bit 5): the merged stage runs its GS half, in NGG mode the subgroup
    /// that emits the output vertices and primitives.</summary>
    public const uint GeometryEnableBit = 1u << 5;

    // SHARPEMU_NGG_AMPLIFIED=0 (diagnostic): run amplified subgroups the old way, as narrow
    // primitive shaders, for A/B measurements of what drawing every cluster costs.
    private static readonly bool AmplifiedSubgroups = Environment.GetEnvironmentVariable("SHARPEMU_NGG_AMPLIFIED") != "0";

    /// <summary>VGT_GS_MAX_VERT_OUT when the draw is an amplified merged ES/GS subgroup of a
    /// single input vertex - Prospero's mesh shader, see
    /// <see cref="ShaderCompiler.Gen5NggStageInfo.AmplifiedVerticesOut"/> - else zero. Black
    /// Myth: Wukong's Nanite rasterizer arrives as 0x00002030 with PRIM_AMP_FACTOR 128,
    /// ES_VERTS_PER_SUBGRP 1 and VGT_GS_MAX_VERT_OUT 256; GS_FAST_LAUNCH stays clear.</summary>
    public static uint AmplifiedVerticesOut(uint stages, ShaderInterfaceRegisters shaderInterface) =>
        AmplifiedSubgroups && (stages & GeometryEnableBit) != 0 &&
        (shaderInterface.PrimitiveShaderSubgroupControl & 0x1FFu) > 1 &&
        shaderInterface.ExportVerticesPerSubgroup == 1
            ? shaderInterface.GeometryMaxVerticesOut
            : 0;

    // Bits 0-1 (LS_EN), 2 (HS_EN) and 6-7 (VS_EN) select stages no primitive shader
    // implements: tessellation and the legacy vertex stage.
    public const uint ForeignStageBits = 0x7u | (0x3u << 6);

    /// <summary>True for the compact primitive-shader masks Prospero emits when the
    /// task/mesh front end has already folded the upper enable bits away. Little Nightmares
    /// also emits 0x2030 for a primitive path with no geometry shader; the remaining bits
    /// describe the fixed NGG setup. These are the masks the per-vertex path admits.</summary>
    public static bool IsPrimitiveShader(uint stages) =>
        (stages & ~WaveSizeBits) is 0x02002000 or 0x00002000 or 0x00002030;

    /// <summary>True when the mask selects the NGG primitive-shader path and nothing else,
    /// so a backend with mesh pipelines can run the draw as the merged ES/GS workgroup
    /// stage. Everything the register can still select (the ES/GS enables, the primitive
    /// group size, primitive generation and its passthrough mode) is NGG setup the primitive
    /// shader performs itself. Void Terrarium's culling draws arrive as 0x00402020, which
    /// <see cref="IsPrimitiveShader"/> does not cover.
    ///
    /// A zero mask is the plain vertex/pixel path, not a primitive-shader one, so it is
    /// excluded: compiling an ordinary vertex program as a mesh function would size its
    /// workgroup from geometry-engine registers the draw never programmed.</summary>
    public static bool SelectsPrimitiveShaderOnly(uint stages) =>
        stages != 0 && (stages & ~WaveSizeBits & ForeignStageBits) == 0;
}
