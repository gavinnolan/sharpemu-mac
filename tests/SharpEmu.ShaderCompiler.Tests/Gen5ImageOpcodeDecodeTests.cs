// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

/// <summary>
/// Decoder coverage for every gfx10.3 MIMG opcode. Each row's words were produced by
/// disassembling a constructed word pair with llvm-mc (-mcpu=gfx1030) and assembling the
/// printed text back (-show-encoding); the round trip reproduced the words exactly. The
/// operands are vdata v4, vaddr v8, srsrc s[8:15] and (for sampler opcodes) ssamp s[4:7].
/// </summary>
public sealed class Gen5ImageOpcodeDecodeTests
{
    private const uint SEndpgm = 0xBF810000;

    public static TheoryData<uint, uint, string> LlvmCases = new()
    {
        { 0xF0000F08u, 0x00020408u, "image_load" }, // image_load v[4:7], v[8:9], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0040F08u, 0x00020408u, "image_load_mip" }, // image_load_mip v[4:7], v[8:10], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0080F08u, 0x00020408u, "image_load_pck" }, // image_load_pck v[4:7], v[8:9], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF00C0F08u, 0x00020408u, "image_load_pck_sgn" }, // image_load_pck_sgn v[4:7], v[8:9], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0100F08u, 0x00020408u, "image_load_mip_pck" }, // image_load_mip_pck v[4:7], v[8:10], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0140F08u, 0x00020408u, "image_load_mip_pck_sgn" }, // image_load_mip_pck_sgn v[4:7], v[8:10], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0200F08u, 0x00020408u, "image_store" }, // image_store v[4:7], v[8:9], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0240F08u, 0x00020408u, "image_store_mip" }, // image_store_mip v[4:7], v[8:10], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0280F08u, 0x00020408u, "image_store_pck" }, // image_store_pck v[4:7], v[8:9], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF02C0F08u, 0x00020408u, "image_store_mip_pck" }, // image_store_mip_pck v[4:7], v[8:10], s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0380F08u, 0x00020408u, "image_get_resinfo" }, // image_get_resinfo v[4:7], v8, s[8:15] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF03C0108u, 0x00020408u, "image_atomic_swap" }, // image_atomic_swap v4, v[8:9], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF0400308u, 0x00020408u, "image_atomic_cmpswap" }, // image_atomic_cmpswap v[4:5], v[8:9], s[8:15] dmask:0x3 dim:SQ_RSRC_IMG_2D
        { 0xF0440108u, 0x00020408u, "image_atomic_add" }, // image_atomic_add v4, v[8:9], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF0480108u, 0x00020408u, "image_atomic_sub" }, // image_atomic_sub v4, v[8:9], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF0500108u, 0x00020408u, "image_atomic_smin" }, // image_atomic_smin v4, v[8:9], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF0540108u, 0x00020408u, "image_atomic_umin" }, // image_atomic_umin v4, v[8:9], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF0580108u, 0x00020408u, "image_atomic_smax" }, // image_atomic_smax v4, v[8:9], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF05C0108u, 0x00020408u, "image_atomic_umax" }, // image_atomic_umax v4, v[8:9], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF0600108u, 0x00020408u, "image_atomic_and" }, // image_atomic_and v4, v[8:9], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF0640108u, 0x00020408u, "image_atomic_or" }, // image_atomic_or v4, v[8:9], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF0680108u, 0x00020408u, "image_atomic_xor" }, // image_atomic_xor v4, v[8:9], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF06C0108u, 0x00020408u, "image_atomic_inc" }, // image_atomic_inc v4, v[8:9], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF0700108u, 0x00020408u, "image_atomic_dec" }, // image_atomic_dec v4, v[8:9], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF0740308u, 0x00020408u, "image_atomic_fcmpswap" }, // image_atomic_fcmpswap v[4:5], v[8:9], s[8:15] dmask:0x3 dim:SQ_RSRC_IMG_2D
        { 0xF0780108u, 0x00020408u, "image_atomic_fmin" }, // image_atomic_fmin v4, v[8:9], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF07C0108u, 0x00020408u, "image_atomic_fmax" }, // image_atomic_fmax v4, v[8:9], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF0800F08u, 0x00220408u, "image_sample" }, // image_sample v[4:7], v[8:9], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0840F08u, 0x00220408u, "image_sample_cl" }, // image_sample_cl v[4:7], v[8:10], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0880F08u, 0x00220408u, "image_sample_d" }, // image_sample_d v[4:7], v[8:13], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF08C0F08u, 0x00220408u, "image_sample_d_cl" }, // image_sample_d_cl v[4:7], v[8:14], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0900F08u, 0x00220408u, "image_sample_l" }, // image_sample_l v[4:7], v[8:10], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0940F08u, 0x00220408u, "image_sample_b" }, // image_sample_b v[4:7], v[8:10], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0980F08u, 0x00220408u, "image_sample_b_cl" }, // image_sample_b_cl v[4:7], v[8:11], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF09C0F08u, 0x00220408u, "image_sample_lz" }, // image_sample_lz v[4:7], v[8:9], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0A00F08u, 0x00220408u, "image_sample_c" }, // image_sample_c v[4:7], v[8:10], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0A40F08u, 0x00220408u, "image_sample_c_cl" }, // image_sample_c_cl v[4:7], v[8:11], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0A80F08u, 0x00220408u, "image_sample_c_d" }, // image_sample_c_d v[4:7], v[8:14], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0AC0F08u, 0x00220408u, "image_sample_c_d_cl" }, // image_sample_c_d_cl v[4:7], v[8:15], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0B00F08u, 0x00220408u, "image_sample_c_l" }, // image_sample_c_l v[4:7], v[8:11], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0B40F08u, 0x00220408u, "image_sample_c_b" }, // image_sample_c_b v[4:7], v[8:11], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0B80F08u, 0x00220408u, "image_sample_c_b_cl" }, // image_sample_c_b_cl v[4:7], v[8:12], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0BC0F08u, 0x00220408u, "image_sample_c_lz" }, // image_sample_c_lz v[4:7], v[8:10], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0C00F08u, 0x00220408u, "image_sample_o" }, // image_sample_o v[4:7], v[8:10], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0C40F08u, 0x00220408u, "image_sample_cl_o" }, // image_sample_cl_o v[4:7], v[8:11], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0C80F08u, 0x00220408u, "image_sample_d_o" }, // image_sample_d_o v[4:7], v[8:14], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0CC0F08u, 0x00220408u, "image_sample_d_cl_o" }, // image_sample_d_cl_o v[4:7], v[8:15], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0D00F08u, 0x00220408u, "image_sample_l_o" }, // image_sample_l_o v[4:7], v[8:11], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0D40F08u, 0x00220408u, "image_sample_b_o" }, // image_sample_b_o v[4:7], v[8:11], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0D80F08u, 0x00220408u, "image_sample_b_cl_o" }, // image_sample_b_cl_o v[4:7], v[8:12], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0DC0F08u, 0x00220408u, "image_sample_lz_o" }, // image_sample_lz_o v[4:7], v[8:10], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0E00F08u, 0x00220408u, "image_sample_c_o" }, // image_sample_c_o v[4:7], v[8:11], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0E40F08u, 0x00220408u, "image_sample_c_cl_o" }, // image_sample_c_cl_o v[4:7], v[8:12], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0E80F08u, 0x00220408u, "image_sample_c_d_o" }, // image_sample_c_d_o v[4:7], v[8:15], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0EC0F08u, 0x00220408u, "image_sample_c_d_cl_o" }, // image_sample_c_d_cl_o v[4:7], v[8:16], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0F00F08u, 0x00220408u, "image_sample_c_l_o" }, // image_sample_c_l_o v[4:7], v[8:12], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0F40F08u, 0x00220408u, "image_sample_c_b_o" }, // image_sample_c_b_o v[4:7], v[8:12], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0F80F08u, 0x00220408u, "image_sample_c_b_cl_o" }, // image_sample_c_b_cl_o v[4:7], v[8:13], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0FC0F08u, 0x00220408u, "image_sample_c_lz_o" }, // image_sample_c_lz_o v[4:7], v[8:11], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1000108u, 0x00220408u, "image_gather4" }, // image_gather4 v[4:7], v[8:9], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1040108u, 0x00220408u, "image_gather4_cl" }, // image_gather4_cl v[4:7], v[8:10], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1100108u, 0x00220408u, "image_gather4_l" }, // image_gather4_l v[4:7], v[8:10], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1140108u, 0x00220408u, "image_gather4_b" }, // image_gather4_b v[4:7], v[8:10], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1180108u, 0x00220408u, "image_gather4_b_cl" }, // image_gather4_b_cl v[4:7], v[8:11], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF11C0108u, 0x00220408u, "image_gather4_lz" }, // image_gather4_lz v[4:7], v[8:9], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1200108u, 0x00220408u, "image_gather4_c" }, // image_gather4_c v[4:7], v[8:10], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1240108u, 0x00220408u, "image_gather4_c_cl" }, // image_gather4_c_cl v[4:7], v[8:11], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1300108u, 0x00220408u, "image_gather4_c_l" }, // image_gather4_c_l v[4:7], v[8:11], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1340108u, 0x00220408u, "image_gather4_c_b" }, // image_gather4_c_b v[4:7], v[8:11], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1380108u, 0x00220408u, "image_gather4_c_b_cl" }, // image_gather4_c_b_cl v[4:7], v[8:12], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF13C0108u, 0x00220408u, "image_gather4_c_lz" }, // image_gather4_c_lz v[4:7], v[8:10], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1400108u, 0x00220408u, "image_gather4_o" }, // image_gather4_o v[4:7], v[8:10], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1440108u, 0x00220408u, "image_gather4_cl_o" }, // image_gather4_cl_o v[4:7], v[8:11], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1500108u, 0x00220408u, "image_gather4_l_o" }, // image_gather4_l_o v[4:7], v[8:11], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1540108u, 0x00220408u, "image_gather4_b_o" }, // image_gather4_b_o v[4:7], v[8:11], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1580108u, 0x00220408u, "image_gather4_b_cl_o" }, // image_gather4_b_cl_o v[4:7], v[8:12], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF15C0108u, 0x00220408u, "image_gather4_lz_o" }, // image_gather4_lz_o v[4:7], v[8:10], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1600108u, 0x00220408u, "image_gather4_c_o" }, // image_gather4_c_o v[4:7], v[8:11], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1640108u, 0x00220408u, "image_gather4_c_cl_o" }, // image_gather4_c_cl_o v[4:7], v[8:12], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1700108u, 0x00220408u, "image_gather4_c_l_o" }, // image_gather4_c_l_o v[4:7], v[8:12], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1740108u, 0x00220408u, "image_gather4_c_b_o" }, // image_gather4_c_b_o v[4:7], v[8:12], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1780108u, 0x00220408u, "image_gather4_c_b_cl_o" }, // image_gather4_c_b_cl_o v[4:7], v[8:13], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF17C0108u, 0x00220408u, "image_gather4_c_lz_o" }, // image_gather4_c_lz_o v[4:7], v[8:11], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1800F08u, 0x00220408u, "image_get_lod" }, // image_get_lod v[4:7], v[8:9], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1840108u, 0x00220408u, "image_gather4h" }, // image_gather4h v[4:7], v[8:9], s[8:15], s[4:7] dmask:0x1 dim:SQ_RSRC_IMG_2D
        { 0xF1A00F08u, 0x00220408u, "image_sample_cd" }, // image_sample_cd v[4:7], v[8:13], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1A40F08u, 0x00220408u, "image_sample_cd_cl" }, // image_sample_cd_cl v[4:7], v[8:14], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1A80F08u, 0x00220408u, "image_sample_c_cd" }, // image_sample_c_cd v[4:7], v[8:14], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1AC0F08u, 0x00220408u, "image_sample_c_cd_cl" }, // image_sample_c_cd_cl v[4:7], v[8:15], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1B00F08u, 0x00220408u, "image_sample_cd_o" }, // image_sample_cd_o v[4:7], v[8:14], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1B40F08u, 0x00220408u, "image_sample_cd_cl_o" }, // image_sample_cd_cl_o v[4:7], v[8:15], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1B80F08u, 0x00220408u, "image_sample_c_cd_o" }, // image_sample_c_cd_o v[4:7], v[8:15], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1BC0F08u, 0x00220408u, "image_sample_c_cd_cl_o" }, // image_sample_c_cd_cl_o v[4:7], v[8:16], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0000131u, 0x00020408u, "image_msaa_load" }, // image_msaa_load v4, v[8:10], s[8:15] dmask:0x1 dim:SQ_RSRC_IMG_2D_MSAA
        { 0xF0880F09u, 0x00220408u, "image_sample_d_g16" }, // image_sample_d_g16 v[4:7], v[8:11], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF08C0F09u, 0x00220408u, "image_sample_d_cl_g16" }, // image_sample_d_cl_g16 v[4:7], v[8:12], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0A80F09u, 0x00220408u, "image_sample_c_d_g16" }, // image_sample_c_d_g16 v[4:7], v[8:12], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0AC0F09u, 0x00220408u, "image_sample_c_d_cl_g16" }, // image_sample_c_d_cl_g16 v[4:7], v[8:13], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0C80F09u, 0x00220408u, "image_sample_d_o_g16" }, // image_sample_d_o_g16 v[4:7], v[8:12], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0CC0F09u, 0x00220408u, "image_sample_d_cl_o_g16" }, // image_sample_d_cl_o_g16 v[4:7], v[8:13], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0E80F09u, 0x00220408u, "image_sample_c_d_o_g16" }, // image_sample_c_d_o_g16 v[4:7], v[8:13], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF0EC0F09u, 0x00220408u, "image_sample_c_d_cl_o_g16" }, // image_sample_c_d_cl_o_g16 v[4:7], v[8:14], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1A00F09u, 0x00220408u, "image_sample_cd_g16" }, // image_sample_cd_g16 v[4:7], v[8:11], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1A40F09u, 0x00220408u, "image_sample_cd_cl_g16" }, // image_sample_cd_cl_g16 v[4:7], v[8:12], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1A80F09u, 0x00220408u, "image_sample_c_cd_g16" }, // image_sample_c_cd_g16 v[4:7], v[8:12], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1AC0F09u, 0x00220408u, "image_sample_c_cd_cl_g16" }, // image_sample_c_cd_cl_g16 v[4:7], v[8:13], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1B00F09u, 0x00220408u, "image_sample_cd_o_g16" }, // image_sample_cd_o_g16 v[4:7], v[8:12], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1B40F09u, 0x00220408u, "image_sample_cd_cl_o_g16" }, // image_sample_cd_cl_o_g16 v[4:7], v[8:13], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1B80F09u, 0x00220408u, "image_sample_c_cd_o_g16" }, // image_sample_c_cd_o_g16 v[4:7], v[8:13], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
        { 0xF1BC0F09u, 0x00220408u, "image_sample_c_cd_cl_o_g16" }, // image_sample_c_cd_cl_o_g16 v[4:7], v[8:14], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D
    };

    [Theory]
    [MemberData(nameof(LlvmCases))]
    public void DecodesTheLlvmEncoding(uint word0, uint word1, string mnemonic)
    {
        var instruction = DecodeFirst(word0, word1);

        Assert.Equal(DecoderName(mnemonic), instruction.Opcode);
        Assert.Equal(Gen5ShaderEncoding.Mimg, instruction.Encoding);
        var image = Assert.IsType<Gen5ImageControl>(instruction.Control);
        Assert.Equal(4u, image.VectorData);
        Assert.Equal(8u, image.VectorAddress);
        Assert.Equal(8u, image.ScalarResource);
        Assert.Equal((word0 >> 8) & 0xF, image.Dmask);
        Assert.Equal((word0 >> 3) & 0x7, image.Dimension);
        Assert.Equal(Gen5ImageOpcodes.UsesSampler(instruction.Opcode) ? 4u : 0u, image.ScalarSampler);
        Assert.NotEqual(Gen5ImageOp.None, Gen5ImageOpcodes.FlagsOf(instruction.Opcode));
    }

    [Fact]
    public void EveryGfx1030OpcodeHasARow()
    {
        // The 110 opcodes llvm-mc disassembles on gfx1030 from this encoding (0x00-0xFF);
        // the BVH forms need other operands and have their own ray-intersect tests.
        Assert.Equal(110, LlvmCases.Count);
        var decoded = LlvmCases.Select(row => DecodeFirst((uint)row[0], (uint)row[1]).Opcode).ToHashSet();
        var tabled = Gen5ImageOpcodes.All
            .Where(entry => (entry.Flags & (Gen5ImageOp.Adjust | Gen5ImageOp.Bvh)) == 0)
            .Select(entry => entry.Name)
            .ToHashSet();
        Assert.Equal(tabled.OrderBy(name => name), decoded.OrderBy(name => name));
    }

    [Theory]
    // The PS5's sampler-adjust samples (unknown to LLVM, which prints v_illegal for them on
    // gfx1010/1013/1030) sit between the _G16 opcodes and never alias one.
    [InlineData(0xA0u, "ImageSampleA")]
    [InlineData(0xA1u, "ImageSampleClA")]
    [InlineData(0xA5u, "ImageSampleBA")]
    [InlineData(0xA8u, "ImageSampleCA")]
    [InlineData(0xB0u, "ImageSampleAO")]
    [InlineData(0xBEu, "ImageSampleCBClAO")]
    public void SampleAdjustFormsKeepTheirPs5Names(uint opcode, string expected)
    {
        var word0 = 0xF000_0000u | ((opcode & 0x7F) << 18) | (opcode >> 7) | (0xFu << 8) | (1u << 3);
        Assert.Equal(expected, DecodeFirst(word0, 0x0022_0408u).Opcode);
        Assert.True(Gen5ImageOpcodes.Has(expected, Gen5ImageOp.Adjust));
    }

    [Fact]
    public void ControlBitsFollowTheGfx10Layout()
    {
        // image_sample v[4:8], v[8:9], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D unorm glc slc dlc tfe lwe
        var image = Control(DecodeFirst(0xF2833F88u, 0x00220408u));
        Assert.True(image.Unorm);
        Assert.True(image.Glc);
        Assert.True(image.Slc);
        Assert.True(image.Dlc);
        Assert.True(image.Tfe);
        Assert.True(image.Lwe);
        Assert.False(image.R128);
        Assert.False(image.A16);
        Assert.False(image.D16);

        // image_sample v[4:7], v[8:9], s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D r128
        image = Control(DecodeFirst(0xF0808F08u, 0x00220408u));
        Assert.True(image.R128);
        Assert.False(image.Dlc || image.Tfe || image.Lwe || image.Unorm || image.Glc || image.Slc);

        // image_sample v[4:5], v8, s[8:15], s[4:7] dmask:0xf dim:SQ_RSRC_IMG_2D a16 d16
        image = Control(DecodeFirst(0xF0800F08u, 0xC0220408u));
        Assert.True(image.A16);
        Assert.True(image.D16);

        // image_load v[4:6], v[8:9], s[8:15] dmask:0x3 dim:SQ_RSRC_IMG_2D unorm tfe
        image = Control(DecodeFirst(0xF0011308u, 0x00020408u));
        Assert.True(image.Tfe);
        Assert.False(image.Lwe);
        Assert.Equal(0x3u, image.Dmask);
    }

    [Theory]
    [InlineData("ImageGather4CBCl", 4u, false, 4u)]
    [InlineData("ImageMsaaLoad", 1u, false, 4u)]
    [InlineData("ImageMsaaLoad", 1u, true, 2u)]
    [InlineData("ImageSampleL", 0xBu, false, 3u)]
    [InlineData("ImageSampleL", 0xBu, true, 2u)]
    [InlineData("ImageLoadPck", 0x3u, false, 2u)]
    public void ResultDwordsCountGatherAndMsaaFragments(string opcode, uint dmask, bool d16, uint expected) =>
        Assert.Equal(expected, Gen5ImageOpcodes.ResultDwords(opcode, dmask, d16));

    private static Gen5ImageControl Control(Gen5ShaderInstruction instruction) =>
        Assert.IsType<Gen5ImageControl>(instruction.Control);

    // llvm-mc's mnemonic in the decoder's naming: image_sample_c_d_o_g16 -> ImageSampleCDOG16.
    private static string DecoderName(string mnemonic) => mnemonic == "image_gather4h"
        ? "ImageGather4H"
        : "Image" + string.Concat(mnemonic.Split('_').Skip(1).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static Gen5ShaderInstruction DecodeFirst(uint word0, uint word1)
    {
        uint[] words = [word0, word1, SEndpgm];
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        }

        var context = new CpuContext(new InstructionMemory(bytes), Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, 0x1000, out var program, out var error), error);
        return program.Instructions[0];
    }

    private sealed class InstructionMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || destination.Length > bytes.Length ||
                address - 0x1000 > (ulong)(bytes.Length - destination.Length))
            {
                return false;
            }

            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
