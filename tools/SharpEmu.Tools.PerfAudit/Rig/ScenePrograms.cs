// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal.Tests.Scenes;

/// <summary>
/// The scenes' RDNA2 programs, assembled with
/// <c>llvm-mc -triple=amdgcn -mcpu=gfx1030 -show-encoding</c>; each word carries its
/// source line. Vertex programs run on the ES/GS (NGG-merged) stage, so the vertex id is
/// v5, the instance id v8 and user SGPRs start at s8. Pixel programs take user SGPRs from
/// s0, the primitive mask after them, and PERSP_CENTER barycentrics in v0/v1.
/// </summary>
internal static class ScenePrograms
{
    /// <summary>
    /// Position from the V# in s[8:11] and PARAM0 from the V# in s[12:15], both 16-byte
    /// records indexed by the vertex id.
    /// </summary>
    public static readonly uint[] FetchVertex =
    [
        0xE0382000, 0x80020005,    // buffer_load_dwordx4 v[0:3], v5, s[8:11], 0 idxen
        0xE0382000, 0x80030A05,    // buffer_load_dwordx4 v[10:13], v5, s[12:15], 0 idxen
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0xF80008CF, 0x03020100,    // exp pos0 v0, v1, v2, v3 done
        0xF800020F, 0x0D0C0B0A,    // exp param0 v10, v11, v12, v13
        0xBF810000,                // s_endpgm
    ];

    /// <summary>MRT0 = interpolated PARAM0 (no user SGPRs, so the primitive mask is s0).</summary>
    public static readonly uint[] InterpolatedColor =
    [
        0xBEFC0300,                // s_mov_b32 m0, s0
        0xC8080000,                // v_interp_p1_f32_e32 v2, v0, attr0.x
        0xC8090001,                // v_interp_p2_f32_e32 v2, v1, attr0.x
        0xC80C0100,                // v_interp_p1_f32_e32 v3, v0, attr0.y
        0xC80D0101,                // v_interp_p2_f32_e32 v3, v1, attr0.y
        0xC8100200,                // v_interp_p1_f32_e32 v4, v0, attr0.z
        0xC8110201,                // v_interp_p2_f32_e32 v4, v1, attr0.z
        0xC8140300,                // v_interp_p1_f32_e32 v5, v0, attr0.w
        0xC8150301,                // v_interp_p2_f32_e32 v5, v1, attr0.w
        0xF800180F, 0x05040302,    // exp mrt0 v2, v3, v4, v5 done vm
        0xBF810000,                // s_endpgm
    ];

    /// <summary>
    /// MRT0 = the 2D image T# s[0:7] sampled through S# s[8:11] at the interpolated
    /// PARAM0.xy (twelve user SGPRs, so the primitive mask is s12).
    /// </summary>
    public static readonly uint[] SampledTexture =
    [
        0xBEFC030C,                // s_mov_b32 m0, s12
        0xC8080000,                // v_interp_p1_f32_e32 v2, v0, attr0.x
        0xC8090001,                // v_interp_p2_f32_e32 v2, v1, attr0.x
        0xC80C0100,                // v_interp_p1_f32_e32 v3, v0, attr0.y
        0xC80D0101,                // v_interp_p2_f32_e32 v3, v1, attr0.y
        0xF0800F08, 0x00400402,    // image_sample v[4:7], v[2:3], s[0:7], s[8:11] dmask:0xf dim:SQ_RSRC_IMG_2D
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0xF800180F, 0x07060504,    // exp mrt0 v4, v5, v6, v7 done vm
        0xBF810000,                // s_endpgm
    ];

    /// <summary>
    /// 64-thread groups; thread g writes (g ^ 0x5A5A) to record g of the V# in s[0:3]
    /// (the group id X is s4).
    /// </summary>
    public static readonly uint[] StoreGlobalId =
    [
        0x8F058604,                // s_lshl_b32 s5, s4, 6
        0x4A020005,                // v_add_nc_u32_e32 v1, s5, v0
        0x3A0402FF, 0x00005A5A,    // v_xor_b32_e32 v2, 0x5a5a, v1
        0xE0702000, 0x80000201,    // buffer_store_dword v2, v1, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];
}
