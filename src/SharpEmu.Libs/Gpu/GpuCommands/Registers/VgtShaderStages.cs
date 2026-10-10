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
