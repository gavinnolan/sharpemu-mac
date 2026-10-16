// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal.Tests.Scenes;

/// <summary>
/// The compute scenes' RDNA2 programs, assembled with
/// <c>llvm-mc -triple=amdgcn -mcpu=gfx1030 -mattr=+wavefrontsize64 -show-encoding</c> (compute waves are wave64:
/// the dispatch initiator leaves CS_W32_EN clear). Each word carries its source line.
/// </summary>
internal static class ComputeScenePrograms
{
    /// <summary>every thread adds 1</summary>
    public static readonly uint[] AtomicAdd =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0x7E040281,                // v_mov_b32_e32 v2, 1
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xE0C82000, 0x80000204,    // buffer_atomic_add v2, v4, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>every thread adds 1 and stores the old value</summary>
    public static readonly uint[] AtomicAddReturn =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0x7E040281,                // v_mov_b32_e32 v2, 1
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xE0C86000, 0x80000204,    // buffer_atomic_add v2, v4, s[0:3], 0 idxen glc
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0xE0702000, 0x80010201,    // buffer_store_dword v2, v1, s[4:7], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>umax of the global id</summary>
    public static readonly uint[] AtomicUmax =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0x7E040301,                // v_mov_b32_e32 v2, v1
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xE0E02000, 0x80000204,    // buffer_atomic_umax v2, v4, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>umin of the global id + 5</summary>
    public static readonly uint[] AtomicUmin =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0x4A040285,                // v_add_nc_u32_e32 v2, 5, v1
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xE0D82000, 0x80000204,    // buffer_atomic_umin v2, v4, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>smax of the global id - 100</summary>
    public static readonly uint[] AtomicSmax =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0x4A0402FF, 0xFFFFFF9C,    // v_add_nc_u32_e32 v2, 0xffffff9c, v1
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xE0DC2000, 0x80000204,    // buffer_atomic_smax v2, v4, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>smin of the global id - 100</summary>
    public static readonly uint[] AtomicSmin =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0x4A0402FF, 0xFFFFFF9C,    // v_add_nc_u32_e32 v2, 0xffffff9c, v1
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xE0D42000, 0x80000204,    // buffer_atomic_smin v2, v4, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>and with ~(1 << id)</summary>
    public static readonly uint[] AtomicAnd =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0xD51A0002, 0x00010301,    // v_lshlrev_b32_e64 v2, v1, 1
        0x7E046F02,                // v_not_b32_e32 v2, v2
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xE0E42000, 0x80000204,    // buffer_atomic_and v2, v4, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>or with 1 << id</summary>
    public static readonly uint[] AtomicOr =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0xD51A0002, 0x00010301,    // v_lshlrev_b32_e64 v2, v1, 1
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xE0E82000, 0x80000204,    // buffer_atomic_or v2, v4, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>xor with 1 << id for ids below 32</summary>
    public static readonly uint[] AtomicXor =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0xD51A0002, 0x00010301,    // v_lshlrev_b32_e64 v2, v1, 1
        0x7D8802A0,                // v_cmp_gt_u32_e32 vcc, 32, v1
        0x02040480,                // v_cndmask_b32_e32 v2, 0, v2, vcc
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xE0EC2000, 0x80000204,    // buffer_atomic_xor v2, v4, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>wrapping increment, limit 9</summary>
    public static readonly uint[] AtomicInc =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0x7E040289,                // v_mov_b32_e32 v2, 9
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xE0F02000, 0x80000204,    // buffer_atomic_inc v2, v4, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>wrapping decrement, limit 9</summary>
    public static readonly uint[] AtomicDec =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0x7E040289,                // v_mov_b32_e32 v2, 9
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xE0F42000, 0x80000204,    // buffer_atomic_dec v2, v4, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>swap in id + 1, store the old value</summary>
    public static readonly uint[] AtomicSwapReturn =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0x4A040281,                // v_add_nc_u32_e32 v2, 1, v1
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xE0C06000, 0x80000204,    // buffer_atomic_swap v2, v4, s[0:3], 0 idxen glc
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0xE0702000, 0x80010201,    // buffer_store_dword v2, v1, s[4:7], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>cmpswap(compare 0, new id + 1), store the old value</summary>
    public static readonly uint[] AtomicCmpSwapReturn =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0x4A040281,                // v_add_nc_u32_e32 v2, 1, v1
        0x7E060280,                // v_mov_b32_e32 v3, 0
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xE0C46000, 0x80000204,    // buffer_atomic_cmpswap v[2:3], v4, s[0:3], 0 idxen glc
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0xE0702000, 0x80010201,    // buffer_store_dword v2, v1, s[4:7], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>cmpswap(compare 0, new id + 1) without return</summary>
    public static readonly uint[] AtomicCmpSwapNoReturn =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0x4A040281,                // v_add_nc_u32_e32 v2, 1, v1
        0x7E060280,                // v_mov_b32_e32 v3, 0
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xE0C42000, 0x80000204,    // buffer_atomic_cmpswap v[2:3], v4, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>umax returning the old value</summary>
    public static readonly uint[] AtomicAddReturnUmax =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0x7E040301,                // v_mov_b32_e32 v2, v1
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xE0E06000, 0x80000204,    // buffer_atomic_umax v2, v4, s[0:3], 0 idxen glc
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0xE0702000, 0x80010201,    // buffer_store_dword v2, v1, s[4:7], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>FLAT/global atomic add: the counter is addressed through the pointer in s[0:1]</summary>
    public static readonly uint[] GlobalAtomicAdd =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0x7E040281,                // v_mov_b32_e32 v2, 1
        0x7E080280,                // v_mov_b32_e32 v4, 0
        0xDCC98000, 0x02000204,    // global_atomic_add v2, v4, v2, s[0:1] glc
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0xE0702000, 0x80010201,    // buffer_store_dword v2, v1, s[4:7], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>256-thread LDS tree reduction (sum), result of each group to out[group]</summary>
    public static readonly uint[] LdsReduce =
    [
        0x8F098808,                // s_lshl_b32 s9, s8, 8
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0xE0302000, 0x80000201,    // buffer_load_dword v2, v1, s[0:3], 0 idxen
        0x34060082,                // v_lshlrev_b32_e32 v3, 2, v0
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0xD8340000, 0x00000203,    // ds_write_b32 v3, v2
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBF8A0000,                // s_barrier
        0xBE8E047E,                // s_mov_b64 s[14:15], exec
        0x7D8800FF, 0x00000080,    // v_cmp_gt_u32_e32 vcc, 0x80, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D80200, 0x05000003,    // ds_read_b32 v5, v3 offset:512
        0xD8D80000, 0x06000003,    // ds_read_b32 v6, v3
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD8340000, 0x00000603,    // ds_write_b32 v3, v6
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D8800C0,                // v_cmp_gt_u32_e32 vcc, 64, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D80100, 0x05000003,    // ds_read_b32 v5, v3 offset:256
        0xD8D80000, 0x06000003,    // ds_read_b32 v6, v3
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD8340000, 0x00000603,    // ds_write_b32 v3, v6
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D8800A0,                // v_cmp_gt_u32_e32 vcc, 32, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D80080, 0x05000003,    // ds_read_b32 v5, v3 offset:128
        0xD8D80000, 0x06000003,    // ds_read_b32 v6, v3
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD8340000, 0x00000603,    // ds_write_b32 v3, v6
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D880090,                // v_cmp_gt_u32_e32 vcc, 16, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D80040, 0x05000003,    // ds_read_b32 v5, v3 offset:64
        0xD8D80000, 0x06000003,    // ds_read_b32 v6, v3
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD8340000, 0x00000603,    // ds_write_b32 v3, v6
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D880088,                // v_cmp_gt_u32_e32 vcc, 8, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D80020, 0x05000003,    // ds_read_b32 v5, v3 offset:32
        0xD8D80000, 0x06000003,    // ds_read_b32 v6, v3
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD8340000, 0x00000603,    // ds_write_b32 v3, v6
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D880084,                // v_cmp_gt_u32_e32 vcc, 4, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D80010, 0x05000003,    // ds_read_b32 v5, v3 offset:16
        0xD8D80000, 0x06000003,    // ds_read_b32 v6, v3
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD8340000, 0x00000603,    // ds_write_b32 v3, v6
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D880082,                // v_cmp_gt_u32_e32 vcc, 2, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D80008, 0x05000003,    // ds_read_b32 v5, v3 offset:8
        0xD8D80000, 0x06000003,    // ds_read_b32 v6, v3
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD8340000, 0x00000603,    // ds_write_b32 v3, v6
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D880081,                // v_cmp_gt_u32_e32 vcc, 1, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D80004, 0x05000003,    // ds_read_b32 v5, v3 offset:4
        0xD8D80000, 0x06000003,    // ds_read_b32 v6, v3
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD8340000, 0x00000603,    // ds_write_b32 v3, v6
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D840080,                // v_cmp_eq_u32_e32 vcc, 0, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D80000, 0x05000003,    // ds_read_b32 v5, v3
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x7E0E0208,                // v_mov_b32_e32 v7, s8
        0xE0702000, 0x80010507,    // buffer_store_dword v5, v7, s[4:7], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>the same reduction placed 0xF000 bytes into the LDS</summary>
    public static readonly uint[] LdsReduceHigh =
    [
        0x8F098808,                // s_lshl_b32 s9, s8, 8
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0xE0302000, 0x80000201,    // buffer_load_dword v2, v1, s[0:3], 0 idxen
        0x34060082,                // v_lshlrev_b32_e32 v3, 2, v0
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0xD834F000, 0x00000203,    // ds_write_b32 v3, v2 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBF8A0000,                // s_barrier
        0xBE8E047E,                // s_mov_b64 s[14:15], exec
        0x7D8800FF, 0x00000080,    // v_cmp_gt_u32_e32 vcc, 0x80, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D8F200, 0x05000003,    // ds_read_b32 v5, v3 offset:61952
        0xD8D8F000, 0x06000003,    // ds_read_b32 v6, v3 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD834F000, 0x00000603,    // ds_write_b32 v3, v6 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D8800C0,                // v_cmp_gt_u32_e32 vcc, 64, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D8F100, 0x05000003,    // ds_read_b32 v5, v3 offset:61696
        0xD8D8F000, 0x06000003,    // ds_read_b32 v6, v3 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD834F000, 0x00000603,    // ds_write_b32 v3, v6 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D8800A0,                // v_cmp_gt_u32_e32 vcc, 32, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D8F080, 0x05000003,    // ds_read_b32 v5, v3 offset:61568
        0xD8D8F000, 0x06000003,    // ds_read_b32 v6, v3 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD834F000, 0x00000603,    // ds_write_b32 v3, v6 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D880090,                // v_cmp_gt_u32_e32 vcc, 16, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D8F040, 0x05000003,    // ds_read_b32 v5, v3 offset:61504
        0xD8D8F000, 0x06000003,    // ds_read_b32 v6, v3 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD834F000, 0x00000603,    // ds_write_b32 v3, v6 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D880088,                // v_cmp_gt_u32_e32 vcc, 8, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D8F020, 0x05000003,    // ds_read_b32 v5, v3 offset:61472
        0xD8D8F000, 0x06000003,    // ds_read_b32 v6, v3 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD834F000, 0x00000603,    // ds_write_b32 v3, v6 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D880084,                // v_cmp_gt_u32_e32 vcc, 4, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D8F010, 0x05000003,    // ds_read_b32 v5, v3 offset:61456
        0xD8D8F000, 0x06000003,    // ds_read_b32 v6, v3 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD834F000, 0x00000603,    // ds_write_b32 v3, v6 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D880082,                // v_cmp_gt_u32_e32 vcc, 2, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D8F008, 0x05000003,    // ds_read_b32 v5, v3 offset:61448
        0xD8D8F000, 0x06000003,    // ds_read_b32 v6, v3 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD834F000, 0x00000603,    // ds_write_b32 v3, v6 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D880081,                // v_cmp_gt_u32_e32 vcc, 1, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D8F004, 0x05000003,    // ds_read_b32 v5, v3 offset:61444
        0xD8D8F000, 0x06000003,    // ds_read_b32 v6, v3 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x4A0C0B06,                // v_add_nc_u32_e32 v6, v6, v5
        0xD834F000, 0x00000603,    // ds_write_b32 v3, v6 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7D840080,                // v_cmp_eq_u32_e32 vcc, 0, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D8F000, 0x05000003,    // ds_read_b32 v5, v3 offset:61440
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x7E0E0208,                // v_mov_b32_e32 v7, s8
        0xE0702000, 0x80010507,    // buffer_store_dword v5, v7, s[4:7], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>ds_add/max/min/and/or/inc atomics on LDS words, ds_add_rtn returns per thread</summary>
    public static readonly uint[] LdsAtomics =
    [
        0x8F098808,                // s_lshl_b32 s9, s8, 8
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0x7E140280,                // v_mov_b32_e32 v10, 0
        0xBE8E047E,                // s_mov_b64 s[14:15], exec
        0x7D840080,                // v_cmp_eq_u32_e32 vcc, 0, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0x7E1602C1,                // v_mov_b32_e32 v11, -1
        0xD8340000, 0x00000A0A,    // ds_write_b32 v10, v10
        0xD8340004, 0x00000A0A,    // ds_write_b32 v10, v10 offset:4
        0xD8340008, 0x00000B0A,    // ds_write_b32 v10, v11 offset:8
        0xD834000C, 0x00000A0A,    // ds_write_b32 v10, v10 offset:12
        0xD8340010, 0x00000B0A,    // ds_write_b32 v10, v11 offset:16
        0xD8340014, 0x00000A0A,    // ds_write_b32 v10, v10 offset:20
        0xD8340018, 0x00000A0A,    // ds_write_b32 v10, v10 offset:24
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xBEFE040E,                // s_mov_b64 exec, s[14:15]
        0xBF8A0000,                // s_barrier
        0x7E040281,                // v_mov_b32_e32 v2, 1
        0xD8000000, 0x0000020A,    // ds_add_u32 v10, v2
        0xD8200004, 0x0000000A,    // ds_max_u32 v10, v0 offset:4
        0x4A060087,                // v_add_nc_u32_e32 v3, 7, v0
        0xD81C0008, 0x0000030A,    // ds_min_u32 v10, v3 offset:8
        0xD880000C, 0x0400020A,    // ds_add_rtn_u32 v4, v10, v2 offset:12
        0xD51A0005, 0x00010300,    // v_lshlrev_b32_e64 v5, v0, 1
        0x7E0C6F05,                // v_not_b32_e32 v6, v5
        0xD8240010, 0x0000060A,    // ds_and_b32 v10, v6 offset:16
        0xD8280014, 0x0000050A,    // ds_or_b32 v10, v5 offset:20
        0x7E0E0289,                // v_mov_b32_e32 v7, 9
        0xD80C0018, 0x0000070A,    // ds_inc_u32 v10, v7 offset:24
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0xE0702000, 0x80010401,    // buffer_store_dword v4, v1, s[4:7], 0 idxen
        0xBF8A0000,                // s_barrier
        0x7D840080,                // v_cmp_eq_u32_e32 vcc, 0, v0
        0x87FE6A0E,                // s_and_b64 exec, s[14:15], vcc
        0xD8D80000, 0x0C00000A,    // ds_read_b32 v12, v10
        0xD8D80004, 0x0D00000A,    // ds_read_b32 v13, v10 offset:4
        0xD8D80008, 0x0E00000A,    // ds_read_b32 v14, v10 offset:8
        0xD8D8000C, 0x0F00000A,    // ds_read_b32 v15, v10 offset:12
        0xD8D80010, 0x1000000A,    // ds_read_b32 v16, v10 offset:16
        0xD8D80014, 0x1100000A,    // ds_read_b32 v17, v10 offset:20
        0xD8D80018, 0x1200000A,    // ds_read_b32 v18, v10 offset:24
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x8F0A8308,                // s_lshl_b32 s10, s8, 3
        0x7E28020A,                // v_mov_b32_e32 v20, s10
        0xE0702000, 0x80000C14,    // buffer_store_dword v12, v20, s[0:3], 0 idxen
        0x4A282881,                // v_add_nc_u32_e32 v20, 1, v20
        0xE0702000, 0x80000D14,    // buffer_store_dword v13, v20, s[0:3], 0 idxen
        0x4A282881,                // v_add_nc_u32_e32 v20, 1, v20
        0xE0702000, 0x80000E14,    // buffer_store_dword v14, v20, s[0:3], 0 idxen
        0x4A282881,                // v_add_nc_u32_e32 v20, 1, v20
        0xE0702000, 0x80000F14,    // buffer_store_dword v15, v20, s[0:3], 0 idxen
        0x4A282881,                // v_add_nc_u32_e32 v20, 1, v20
        0xE0702000, 0x80001014,    // buffer_store_dword v16, v20, s[0:3], 0 idxen
        0x4A282881,                // v_add_nc_u32_e32 v20, 1, v20
        0xE0702000, 0x80001114,    // buffer_store_dword v17, v20, s[0:3], 0 idxen
        0x4A282881,                // v_add_nc_u32_e32 v20, 1, v20
        0xE0702000, 0x80001214,    // buffer_store_dword v18, v20, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>writes global/local/group ids; TIDIG_COMP_CNT 0, group id X</summary>
    public static readonly uint[] MultiDimX =
    [
        0x7E020280,                // v_mov_b32_e32 v1, 0
        0x7E040280,                // v_mov_b32_e32 v2, 0
        0x93140609,                // s_mul_i32 s20, s9, s6
        0x4A060014,                // v_add_nc_u32_e32 v3, s20, v0
        0x7E080301,                // v_mov_b32_e32 v4, v1
        0x7E0A0302,                // v_mov_b32_e32 v5, v2
        0xD5690006, 0x00000B05,    // v_mul_lo_u32 v6, v5, s5
        0x4A0C0906,                // v_add_nc_u32_e32 v6, v6, v4
        0xD5690006, 0x00000906,    // v_mul_lo_u32 v6, v6, s4
        0x4A0C0706,                // v_add_nc_u32_e32 v6, v6, v3
        0x340E088A,                // v_lshlrev_b32_e32 v7, 10, v4
        0x380E0707,                // v_or_b32_e32 v7, v7, v3
        0x34140A94,                // v_lshlrev_b32_e32 v10, 20, v5
        0x380E1507,                // v_or_b32_e32 v7, v7, v10
        0x34100286,                // v_lshlrev_b32_e32 v8, 6, v1
        0x38100108,                // v_or_b32_e32 v8, v8, v0
        0x3414048C,                // v_lshlrev_b32_e32 v10, 12, v2
        0x38101508,                // v_or_b32_e32 v8, v8, v10
        0x7E160209,                // v_mov_b32_e32 v11, s9
        0x34161690,                // v_lshlrev_b32_e32 v11, 16, v11
        0x38101708,                // v_or_b32_e32 v8, v8, v11
        0xE0742000, 0x80000706,    // buffer_store_dwordx2 v[7:8], v6, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>TIDIG_COMP_CNT 1, group ids X and Y</summary>
    public static readonly uint[] MultiDimXY =
    [
        0x7E040280,                // v_mov_b32_e32 v2, 0
        0x93140609,                // s_mul_i32 s20, s9, s6
        0x4A060014,                // v_add_nc_u32_e32 v3, s20, v0
        0x9314070A,                // s_mul_i32 s20, s10, s7
        0x4A080214,                // v_add_nc_u32_e32 v4, s20, v1
        0x7E0A0302,                // v_mov_b32_e32 v5, v2
        0xD5690006, 0x00000B05,    // v_mul_lo_u32 v6, v5, s5
        0x4A0C0906,                // v_add_nc_u32_e32 v6, v6, v4
        0xD5690006, 0x00000906,    // v_mul_lo_u32 v6, v6, s4
        0x4A0C0706,                // v_add_nc_u32_e32 v6, v6, v3
        0x340E088A,                // v_lshlrev_b32_e32 v7, 10, v4
        0x380E0707,                // v_or_b32_e32 v7, v7, v3
        0x34140A94,                // v_lshlrev_b32_e32 v10, 20, v5
        0x380E1507,                // v_or_b32_e32 v7, v7, v10
        0x34100286,                // v_lshlrev_b32_e32 v8, 6, v1
        0x38100108,                // v_or_b32_e32 v8, v8, v0
        0x3414048C,                // v_lshlrev_b32_e32 v10, 12, v2
        0x38101508,                // v_or_b32_e32 v8, v8, v10
        0x7E160209,                // v_mov_b32_e32 v11, s9
        0x34161690,                // v_lshlrev_b32_e32 v11, 16, v11
        0x38101708,                // v_or_b32_e32 v8, v8, v11
        0x7E16020A,                // v_mov_b32_e32 v11, s10
        0x34161696,                // v_lshlrev_b32_e32 v11, 22, v11
        0x38101708,                // v_or_b32_e32 v8, v8, v11
        0xE0742000, 0x80000706,    // buffer_store_dwordx2 v[7:8], v6, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>TIDIG_COMP_CNT 2, group ids X, Y and Z</summary>
    public static readonly uint[] MultiDimXYZ =
    [
        0x93140609,                // s_mul_i32 s20, s9, s6
        0x4A060014,                // v_add_nc_u32_e32 v3, s20, v0
        0x9314070A,                // s_mul_i32 s20, s10, s7
        0x4A080214,                // v_add_nc_u32_e32 v4, s20, v1
        0x9314080B,                // s_mul_i32 s20, s11, s8
        0x4A0A0414,                // v_add_nc_u32_e32 v5, s20, v2
        0xD5690006, 0x00000B05,    // v_mul_lo_u32 v6, v5, s5
        0x4A0C0906,                // v_add_nc_u32_e32 v6, v6, v4
        0xD5690006, 0x00000906,    // v_mul_lo_u32 v6, v6, s4
        0x4A0C0706,                // v_add_nc_u32_e32 v6, v6, v3
        0x340E088A,                // v_lshlrev_b32_e32 v7, 10, v4
        0x380E0707,                // v_or_b32_e32 v7, v7, v3
        0x34140A94,                // v_lshlrev_b32_e32 v10, 20, v5
        0x380E1507,                // v_or_b32_e32 v7, v7, v10
        0x34100286,                // v_lshlrev_b32_e32 v8, 6, v1
        0x38100108,                // v_or_b32_e32 v8, v8, v0
        0x3414048C,                // v_lshlrev_b32_e32 v10, 12, v2
        0x38101508,                // v_or_b32_e32 v8, v8, v10
        0x7E160209,                // v_mov_b32_e32 v11, s9
        0x34161690,                // v_lshlrev_b32_e32 v11, 16, v11
        0x38101708,                // v_or_b32_e32 v8, v8, v11
        0x7E16020A,                // v_mov_b32_e32 v11, s10
        0x34161696,                // v_lshlrev_b32_e32 v11, 22, v11
        0x38101708,                // v_or_b32_e32 v8, v8, v11
        0x7E16020B,                // v_mov_b32_e32 v11, s11
        0x3416169A,                // v_lshlrev_b32_e32 v11, 26, v11
        0x38101708,                // v_or_b32_e32 v8, v8, v11
        0xE0742000, 0x80000706,    // buffer_store_dwordx2 v[7:8], v6, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>TIDIG_COMP_CNT 2, group ids X and Z only</summary>
    public static readonly uint[] MultiDimXZ =
    [
        0x93140609,                // s_mul_i32 s20, s9, s6
        0x4A060014,                // v_add_nc_u32_e32 v3, s20, v0
        0x7E080301,                // v_mov_b32_e32 v4, v1
        0x9314080A,                // s_mul_i32 s20, s10, s8
        0x4A0A0414,                // v_add_nc_u32_e32 v5, s20, v2
        0xD5690006, 0x00000B05,    // v_mul_lo_u32 v6, v5, s5
        0x4A0C0906,                // v_add_nc_u32_e32 v6, v6, v4
        0xD5690006, 0x00000906,    // v_mul_lo_u32 v6, v6, s4
        0x4A0C0706,                // v_add_nc_u32_e32 v6, v6, v3
        0x340E088A,                // v_lshlrev_b32_e32 v7, 10, v4
        0x380E0707,                // v_or_b32_e32 v7, v7, v3
        0x34140A94,                // v_lshlrev_b32_e32 v10, 20, v5
        0x380E1507,                // v_or_b32_e32 v7, v7, v10
        0x34100286,                // v_lshlrev_b32_e32 v8, 6, v1
        0x38100108,                // v_or_b32_e32 v8, v8, v0
        0x3414048C,                // v_lshlrev_b32_e32 v10, 12, v2
        0x38101508,                // v_or_b32_e32 v8, v8, v10
        0x7E160209,                // v_mov_b32_e32 v11, s9
        0x34161690,                // v_lshlrev_b32_e32 v11, 16, v11
        0x38101708,                // v_or_b32_e32 v8, v8, v11
        0x7E16020A,                // v_mov_b32_e32 v11, s10
        0x3416169A,                // v_lshlrev_b32_e32 v11, 26, v11
        0x38101708,                // v_or_b32_e32 v8, v8, v11
        0xE0742000, 0x80000706,    // buffer_store_dwordx2 v[7:8], v6, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>lane t < 3 writes the t-th group count of the indirect dispatch arguments</summary>
    public static readonly uint[] WriteDispatchArgs =
    [
        0x7E040204,                // v_mov_b32_e32 v2, s4
        0x7D840081,                // v_cmp_eq_u32_e32 vcc, 1, v0
        0xD5010002, 0x01A80B02,    // v_cndmask_b32_e64 v2, v2, s5, vcc
        0x7D840082,                // v_cmp_eq_u32_e32 vcc, 2, v0
        0xD5010002, 0x01A80D02,    // v_cndmask_b32_e64 v2, v2, s6, vcc
        0x7D880083,                // v_cmp_gt_u32_e32 vcc, 3, v0
        0xBE8A246A,                // s_and_saveexec_b64 s[10:11], vcc
        0xE0702000, 0x80000200,    // buffer_store_dword v2, v0, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>args[k] = {count, visible[k] ? instances : 0, 0, 0, 0}</summary>
    public static readonly uint[] CullToDrawArgs =
    [
        0x7D88000A,                // v_cmp_gt_u32_e32 vcc, s10, v0
        0xBE8E246A,                // s_and_saveexec_b64 s[14:15], vcc
        0xE0302000, 0x80010100,    // buffer_load_dword v1, v0, s[4:7], 0 idxen
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x7D8A0280,                // v_cmp_ne_u32_e32 vcc, 0, v1
        0x7E040209,                // v_mov_b32_e32 v2, s9
        0x02040480,                // v_cndmask_b32_e32 v2, 0, v2, vcc
        0xD5690003, 0x00001700,    // v_mul_lo_u32 v3, v0, s11
        0x7E080208,                // v_mov_b32_e32 v4, s8
        0x7E0A0280,                // v_mov_b32_e32 v5, 0
        0xE0702000, 0x80000403,    // buffer_store_dword v4, v3, s[0:3], 0 idxen
        0x4A060681,                // v_add_nc_u32_e32 v3, 1, v3
        0xE0702000, 0x80000203,    // buffer_store_dword v2, v3, s[0:3], 0 idxen
        0x4A060681,                // v_add_nc_u32_e32 v3, 1, v3
        0xE0702000, 0x80000503,    // buffer_store_dword v5, v3, s[0:3], 0 idxen
        0x4A060681,                // v_add_nc_u32_e32 v3, 1, v3
        0xE0702000, 0x80000503,    // buffer_store_dword v5, v3, s[0:3], 0 idxen
        0xBF06840B,                // s_cmp_eq_u32 s11, 4
        0xBF850003,                // s_cbranch_scc1 3
        0x4A060681,                // v_add_nc_u32_e32 v3, 1, v3
        0xE0702000, 0x80000503,    // buffer_store_dword v5, v3, s[0:3], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>FetchVertex with the position shifted right by 0.5 * instance id</summary>
    public static readonly uint[] FetchVertexInstanced =
    [
        0xE0382000, 0x80020005,    // buffer_load_dwordx4 v[0:3], v5, s[8:11], 0 idxen
        0xE0382000, 0x80030A05,    // buffer_load_dwordx4 v[10:13], v5, s[12:15], 0 idxen
        0x7E1C0D08,                // v_cvt_f32_u32_e32 v14, v8
        0xBF8C3F71,                // s_waitcnt vmcnt(1)
        0x56001CF0,                // v_fmac_f32_e32 v0, 0.5, v14
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0xF80008CF, 0x03020100,    // exp pos0 v0, v1, v2, v3 done
        0xF800020F, 0x0D0C0B0A,    // exp param0 v10, v11, v12, v13
        0xBF810000,                // s_endpgm
    ];

    /// <summary>depth texture -> R32F mip 0 (image_load, image_store)</summary>
    public static readonly uint[] HzbCopy =
    [
        0xF40C0304, 0xFA000000,    // s_load_dwordx8 s[12:19], s[8:9], 0x0
        0xF4080504, 0xFA000020,    // s_load_dwordx4 s[20:23], s[8:9], 0x20
        0x8F18830A,                // s_lshl_b32 s24, s10, 3
        0x8F19830B,                // s_lshl_b32 s25, s11, 3
        0x4A040018,                // v_add_nc_u32_e32 v2, s24, v0
        0x4A060219,                // v_add_nc_u32_e32 v3, s25, v1
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x7D880416,                // v_cmp_gt_u32_e32 vcc, s22, v2
        0xD4C4001A, 0x00020617,    // v_cmp_gt_u32_e64 s[26:27], s23, v3
        0x87EA1A6A,                // s_and_b64 vcc, vcc, s[26:27]
        0xBE9C246A,                // s_and_saveexec_b64 s[28:29], vcc
        0xF0001108, 0x00000402,    // image_load v4, v[2:3], s[0:7] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0xF0201108, 0x00030402,    // image_store v4, v[2:3], s[12:19] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xBF810000,                // s_endpgm
    ];

    /// <summary>2x2 max reduction of the previous level (edge texels clamp)</summary>
    public static readonly uint[] HzbReduceMax =
    [
        0xF40C0304, 0xFA000000,    // s_load_dwordx8 s[12:19], s[8:9], 0x0
        0xF4080504, 0xFA000020,    // s_load_dwordx4 s[20:23], s[8:9], 0x20
        0x8F18830A,                // s_lshl_b32 s24, s10, 3
        0x8F19830B,                // s_lshl_b32 s25, s11, 3
        0x4A040018,                // v_add_nc_u32_e32 v2, s24, v0
        0x4A060219,                // v_add_nc_u32_e32 v3, s25, v1
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x7D880416,                // v_cmp_gt_u32_e32 vcc, s22, v2
        0xD4C4001A, 0x00020617,    // v_cmp_gt_u32_e64 s[26:27], s23, v3
        0x87EA1A6A,                // s_and_b64 vcc, vcc, s[26:27]
        0xBE9C246A,                // s_and_saveexec_b64 s[28:29], vcc
        0x80988114,                // s_sub_u32 s24, s20, 1
        0x80998115,                // s_sub_u32 s25, s21, 1
        0x34140481,                // v_lshlrev_b32_e32 v10, 1, v2
        0x34160681,                // v_lshlrev_b32_e32 v11, 1, v3
        0x4A181481,                // v_add_nc_u32_e32 v12, 1, v10
        0x26181818,                // v_min_u32_e32 v12, s24, v12
        0x7E1A030B,                // v_mov_b32_e32 v13, v11
        0x7E1C030A,                // v_mov_b32_e32 v14, v10
        0x4A1E1681,                // v_add_nc_u32_e32 v15, 1, v11
        0x261E1E19,                // v_min_u32_e32 v15, s25, v15
        0x7E20030C,                // v_mov_b32_e32 v16, v12
        0x7E22030F,                // v_mov_b32_e32 v17, v15
        0xF0001108, 0x0000120A,    // image_load v18, v[10:11], s[0:7] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0000130C,    // image_load v19, v[12:13], s[0:7] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0000140E,    // image_load v20, v[14:15], s[0:7] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x00001510,    // image_load v21, v[16:17], s[0:7] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x20242712,                // v_max_f32_e32 v18, v18, v19
        0x20282B14,                // v_max_f32_e32 v20, v20, v21
        0x20242912,                // v_max_f32_e32 v18, v18, v20
        0xF0201108, 0x00031202,    // image_store v18, v[2:3], s[12:19] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xBF810000,                // s_endpgm
    ];

    /// <summary>2x2 min reduction of the previous level (edge texels clamp)</summary>
    public static readonly uint[] HzbReduceMin =
    [
        0xF40C0304, 0xFA000000,    // s_load_dwordx8 s[12:19], s[8:9], 0x0
        0xF4080504, 0xFA000020,    // s_load_dwordx4 s[20:23], s[8:9], 0x20
        0x8F18830A,                // s_lshl_b32 s24, s10, 3
        0x8F19830B,                // s_lshl_b32 s25, s11, 3
        0x4A040018,                // v_add_nc_u32_e32 v2, s24, v0
        0x4A060219,                // v_add_nc_u32_e32 v3, s25, v1
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x7D880416,                // v_cmp_gt_u32_e32 vcc, s22, v2
        0xD4C4001A, 0x00020617,    // v_cmp_gt_u32_e64 s[26:27], s23, v3
        0x87EA1A6A,                // s_and_b64 vcc, vcc, s[26:27]
        0xBE9C246A,                // s_and_saveexec_b64 s[28:29], vcc
        0x80988114,                // s_sub_u32 s24, s20, 1
        0x80998115,                // s_sub_u32 s25, s21, 1
        0x34140481,                // v_lshlrev_b32_e32 v10, 1, v2
        0x34160681,                // v_lshlrev_b32_e32 v11, 1, v3
        0x4A181481,                // v_add_nc_u32_e32 v12, 1, v10
        0x26181818,                // v_min_u32_e32 v12, s24, v12
        0x7E1A030B,                // v_mov_b32_e32 v13, v11
        0x7E1C030A,                // v_mov_b32_e32 v14, v10
        0x4A1E1681,                // v_add_nc_u32_e32 v15, 1, v11
        0x261E1E19,                // v_min_u32_e32 v15, s25, v15
        0x7E20030C,                // v_mov_b32_e32 v16, v12
        0x7E22030F,                // v_mov_b32_e32 v17, v15
        0xF0001108, 0x0000120A,    // image_load v18, v[10:11], s[0:7] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0000130C,    // image_load v19, v[12:13], s[0:7] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0000140E,    // image_load v20, v[14:15], s[0:7] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x00001510,    // image_load v21, v[16:17], s[0:7] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x1E242712,                // v_min_f32_e32 v18, v18, v19
        0x1E282B14,                // v_min_f32_e32 v20, v20, v21
        0x1E242912,                // v_min_f32_e32 v18, v18, v20
        0xF0201108, 0x00031202,    // image_store v18, v[2:3], s[12:19] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xBF810000,                // s_endpgm
    ];

    /// <summary>global_load_dword / global_store_dword through the pointer in s[0:1]</summary>
    public static readonly uint[] GlobalLoadStore =
    [
        0x8F098608,                // s_lshl_b32 s9, s8, 6
        0x4A020009,                // v_add_nc_u32_e32 v1, s9, v0
        0x34060282,                // v_lshlrev_b32_e32 v3, 2, v1
        0xDC308000, 0x02000003,    // global_load_dword v2, v3, s[0:1]
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x4A0404FF, 0x000003E8,    // v_add_nc_u32_e32 v2, 0x3e8, v2
        0xDC708400, 0x00000203,    // global_store_dword v3, v2, s[0:1] offset:1024
        0xE0702000, 0x80010201,    // buffer_store_dword v2, v1, s[4:7], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>one group per object (lane 0): the level covering the bounds is picked by a scalar compare chain, each level reads its own T# at a constant table offset</summary>
    public static readonly uint[] HzbOcclusionStaticLevels =
    [
        0x7D840080,                // v_cmp_eq_u32_e32 vcc, 0, v0
        0xBEA8246A,                // s_and_saveexec_b64 s[40:41], vcc
        0x7E02020D,                // v_mov_b32_e32 v1, s13
        0xE0382000, 0x80010201,    // buffer_load_dwordx4 v[2:5], v1, s[4:7], 0 idxen
        0xE0302010, 0x80010601,    // buffer_load_dword v6, v1, s[4:7], 0 idxen offset:16
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x7E1C0502,                // v_readfirstlane_b32 s14, v2
        0x7E1E0503,                // v_readfirstlane_b32 s15, v3
        0x7E200504,                // v_readfirstlane_b32 s16, v4
        0x7E220505,                // v_readfirstlane_b32 s17, v5
        0x7E240506,                // v_readfirstlane_b32 s18, v6
        0x80940E10,                // s_sub_u32 s20, s16, s14
        0x80950F11,                // s_sub_u32 s21, s17, s15
        0x84961514,                // s_max_u32 s22, s20, s21
        0xBE971516,                // s_flbit_i32_b32 s23, s22
        0x809817A0,                // s_sub_u32 s24, 32, s23
        0xBF068016,                // s_cmp_eq_u32 s22, 0
        0x85181880,                // s_cselect_b32 s24, 0, s24
        0x83980C18,                // s_min_u32 s24, s24, s12
        0xBF068018,                // s_cmp_eq_u32 s24, 0
        0xBF84002B,                // s_cbranch_scc0 SKIP0
        0xF40C0700, 0xFA000000,    // s_load_dwordx8 s[28:35], s[0:1], null
        0xF4040900, 0xFA000020,    // s_load_dwordx2 s[36:37], s[0:1], 0x20
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x80A48124,                // s_sub_u32 s36, s36, 1
        0x80A58125,                // s_sub_u32 s37, s37, 1
        0x900E180E,                // s_lshr_b32 s14, s14, s24
        0x90101810,                // s_lshr_b32 s16, s16, s24
        0x900F180F,                // s_lshr_b32 s15, s15, s24
        0x90111811,                // s_lshr_b32 s17, s17, s24
        0x838E240E,                // s_min_u32 s14, s14, s36
        0x83902410,                // s_min_u32 s16, s16, s36
        0x838F250F,                // s_min_u32 s15, s15, s37
        0x83912511,                // s_min_u32 s17, s17, s37
        0x7E14020E,                // v_mov_b32_e32 v10, s14
        0x7E16020F,                // v_mov_b32_e32 v11, s15
        0x7E180210,                // v_mov_b32_e32 v12, s16
        0x7E1A020F,                // v_mov_b32_e32 v13, s15
        0x7E1C020E,                // v_mov_b32_e32 v14, s14
        0x7E1E0211,                // v_mov_b32_e32 v15, s17
        0x7E200210,                // v_mov_b32_e32 v16, s16
        0x7E220211,                // v_mov_b32_e32 v17, s17
        0xF0001108, 0x0007120A,    // image_load v18, v[10:11], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007130C,    // image_load v19, v[12:13], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007140E,    // image_load v20, v[14:15], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x00071510,    // image_load v21, v[16:17], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x20242712,                // v_max_f32_e32 v18, v18, v19
        0x20282B14,                // v_max_f32_e32 v20, v20, v21
        0x20242912,                // v_max_f32_e32 v18, v18, v20
        0x7E2C0212,                // v_mov_b32_e32 v22, s18
        0x7C062516,                // v_cmp_le_f32_e32 vcc, v22, v18
        0xD5010017, 0x01A90280,    // v_cndmask_b32_e64 v23, 0, 1, vcc
        0x7E30020D,                // v_mov_b32_e32 v24, s13
        0xE0702000, 0x80021718,    // buffer_store_dword v23, v24, s[8:11], 0 idxen
        0xBF810000,                // s_endpgm
        0xBF068118,                // s_cmp_eq_u32 s24, 1
        0xBF84002B,                // s_cbranch_scc0 SKIP1
        0xF40C0700, 0xFA000040,    // s_load_dwordx8 s[28:35], s[0:1], 0x40
        0xF4040900, 0xFA000060,    // s_load_dwordx2 s[36:37], s[0:1], 0x60
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x80A48124,                // s_sub_u32 s36, s36, 1
        0x80A58125,                // s_sub_u32 s37, s37, 1
        0x900E180E,                // s_lshr_b32 s14, s14, s24
        0x90101810,                // s_lshr_b32 s16, s16, s24
        0x900F180F,                // s_lshr_b32 s15, s15, s24
        0x90111811,                // s_lshr_b32 s17, s17, s24
        0x838E240E,                // s_min_u32 s14, s14, s36
        0x83902410,                // s_min_u32 s16, s16, s36
        0x838F250F,                // s_min_u32 s15, s15, s37
        0x83912511,                // s_min_u32 s17, s17, s37
        0x7E14020E,                // v_mov_b32_e32 v10, s14
        0x7E16020F,                // v_mov_b32_e32 v11, s15
        0x7E180210,                // v_mov_b32_e32 v12, s16
        0x7E1A020F,                // v_mov_b32_e32 v13, s15
        0x7E1C020E,                // v_mov_b32_e32 v14, s14
        0x7E1E0211,                // v_mov_b32_e32 v15, s17
        0x7E200210,                // v_mov_b32_e32 v16, s16
        0x7E220211,                // v_mov_b32_e32 v17, s17
        0xF0001108, 0x0007120A,    // image_load v18, v[10:11], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007130C,    // image_load v19, v[12:13], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007140E,    // image_load v20, v[14:15], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x00071510,    // image_load v21, v[16:17], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x20242712,                // v_max_f32_e32 v18, v18, v19
        0x20282B14,                // v_max_f32_e32 v20, v20, v21
        0x20242912,                // v_max_f32_e32 v18, v18, v20
        0x7E2C0212,                // v_mov_b32_e32 v22, s18
        0x7C062516,                // v_cmp_le_f32_e32 vcc, v22, v18
        0xD5010017, 0x01A90280,    // v_cndmask_b32_e64 v23, 0, 1, vcc
        0x7E30020D,                // v_mov_b32_e32 v24, s13
        0xE0702000, 0x80021718,    // buffer_store_dword v23, v24, s[8:11], 0 idxen
        0xBF810000,                // s_endpgm
        0xBF068218,                // s_cmp_eq_u32 s24, 2
        0xBF84002B,                // s_cbranch_scc0 SKIP2
        0xF40C0700, 0xFA000080,    // s_load_dwordx8 s[28:35], s[0:1], 0x80
        0xF4040900, 0xFA0000A0,    // s_load_dwordx2 s[36:37], s[0:1], 0xa0
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x80A48124,                // s_sub_u32 s36, s36, 1
        0x80A58125,                // s_sub_u32 s37, s37, 1
        0x900E180E,                // s_lshr_b32 s14, s14, s24
        0x90101810,                // s_lshr_b32 s16, s16, s24
        0x900F180F,                // s_lshr_b32 s15, s15, s24
        0x90111811,                // s_lshr_b32 s17, s17, s24
        0x838E240E,                // s_min_u32 s14, s14, s36
        0x83902410,                // s_min_u32 s16, s16, s36
        0x838F250F,                // s_min_u32 s15, s15, s37
        0x83912511,                // s_min_u32 s17, s17, s37
        0x7E14020E,                // v_mov_b32_e32 v10, s14
        0x7E16020F,                // v_mov_b32_e32 v11, s15
        0x7E180210,                // v_mov_b32_e32 v12, s16
        0x7E1A020F,                // v_mov_b32_e32 v13, s15
        0x7E1C020E,                // v_mov_b32_e32 v14, s14
        0x7E1E0211,                // v_mov_b32_e32 v15, s17
        0x7E200210,                // v_mov_b32_e32 v16, s16
        0x7E220211,                // v_mov_b32_e32 v17, s17
        0xF0001108, 0x0007120A,    // image_load v18, v[10:11], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007130C,    // image_load v19, v[12:13], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007140E,    // image_load v20, v[14:15], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x00071510,    // image_load v21, v[16:17], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x20242712,                // v_max_f32_e32 v18, v18, v19
        0x20282B14,                // v_max_f32_e32 v20, v20, v21
        0x20242912,                // v_max_f32_e32 v18, v18, v20
        0x7E2C0212,                // v_mov_b32_e32 v22, s18
        0x7C062516,                // v_cmp_le_f32_e32 vcc, v22, v18
        0xD5010017, 0x01A90280,    // v_cndmask_b32_e64 v23, 0, 1, vcc
        0x7E30020D,                // v_mov_b32_e32 v24, s13
        0xE0702000, 0x80021718,    // buffer_store_dword v23, v24, s[8:11], 0 idxen
        0xBF810000,                // s_endpgm
        0xBF068318,                // s_cmp_eq_u32 s24, 3
        0xBF84002B,                // s_cbranch_scc0 SKIP3
        0xF40C0700, 0xFA0000C0,    // s_load_dwordx8 s[28:35], s[0:1], 0xc0
        0xF4040900, 0xFA0000E0,    // s_load_dwordx2 s[36:37], s[0:1], 0xe0
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x80A48124,                // s_sub_u32 s36, s36, 1
        0x80A58125,                // s_sub_u32 s37, s37, 1
        0x900E180E,                // s_lshr_b32 s14, s14, s24
        0x90101810,                // s_lshr_b32 s16, s16, s24
        0x900F180F,                // s_lshr_b32 s15, s15, s24
        0x90111811,                // s_lshr_b32 s17, s17, s24
        0x838E240E,                // s_min_u32 s14, s14, s36
        0x83902410,                // s_min_u32 s16, s16, s36
        0x838F250F,                // s_min_u32 s15, s15, s37
        0x83912511,                // s_min_u32 s17, s17, s37
        0x7E14020E,                // v_mov_b32_e32 v10, s14
        0x7E16020F,                // v_mov_b32_e32 v11, s15
        0x7E180210,                // v_mov_b32_e32 v12, s16
        0x7E1A020F,                // v_mov_b32_e32 v13, s15
        0x7E1C020E,                // v_mov_b32_e32 v14, s14
        0x7E1E0211,                // v_mov_b32_e32 v15, s17
        0x7E200210,                // v_mov_b32_e32 v16, s16
        0x7E220211,                // v_mov_b32_e32 v17, s17
        0xF0001108, 0x0007120A,    // image_load v18, v[10:11], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007130C,    // image_load v19, v[12:13], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007140E,    // image_load v20, v[14:15], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x00071510,    // image_load v21, v[16:17], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x20242712,                // v_max_f32_e32 v18, v18, v19
        0x20282B14,                // v_max_f32_e32 v20, v20, v21
        0x20242912,                // v_max_f32_e32 v18, v18, v20
        0x7E2C0212,                // v_mov_b32_e32 v22, s18
        0x7C062516,                // v_cmp_le_f32_e32 vcc, v22, v18
        0xD5010017, 0x01A90280,    // v_cndmask_b32_e64 v23, 0, 1, vcc
        0x7E30020D,                // v_mov_b32_e32 v24, s13
        0xE0702000, 0x80021718,    // buffer_store_dword v23, v24, s[8:11], 0 idxen
        0xBF810000,                // s_endpgm
        0xBF068418,                // s_cmp_eq_u32 s24, 4
        0xBF84002B,                // s_cbranch_scc0 SKIP4
        0xF40C0700, 0xFA000100,    // s_load_dwordx8 s[28:35], s[0:1], 0x100
        0xF4040900, 0xFA000120,    // s_load_dwordx2 s[36:37], s[0:1], 0x120
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x80A48124,                // s_sub_u32 s36, s36, 1
        0x80A58125,                // s_sub_u32 s37, s37, 1
        0x900E180E,                // s_lshr_b32 s14, s14, s24
        0x90101810,                // s_lshr_b32 s16, s16, s24
        0x900F180F,                // s_lshr_b32 s15, s15, s24
        0x90111811,                // s_lshr_b32 s17, s17, s24
        0x838E240E,                // s_min_u32 s14, s14, s36
        0x83902410,                // s_min_u32 s16, s16, s36
        0x838F250F,                // s_min_u32 s15, s15, s37
        0x83912511,                // s_min_u32 s17, s17, s37
        0x7E14020E,                // v_mov_b32_e32 v10, s14
        0x7E16020F,                // v_mov_b32_e32 v11, s15
        0x7E180210,                // v_mov_b32_e32 v12, s16
        0x7E1A020F,                // v_mov_b32_e32 v13, s15
        0x7E1C020E,                // v_mov_b32_e32 v14, s14
        0x7E1E0211,                // v_mov_b32_e32 v15, s17
        0x7E200210,                // v_mov_b32_e32 v16, s16
        0x7E220211,                // v_mov_b32_e32 v17, s17
        0xF0001108, 0x0007120A,    // image_load v18, v[10:11], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007130C,    // image_load v19, v[12:13], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007140E,    // image_load v20, v[14:15], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x00071510,    // image_load v21, v[16:17], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x20242712,                // v_max_f32_e32 v18, v18, v19
        0x20282B14,                // v_max_f32_e32 v20, v20, v21
        0x20242912,                // v_max_f32_e32 v18, v18, v20
        0x7E2C0212,                // v_mov_b32_e32 v22, s18
        0x7C062516,                // v_cmp_le_f32_e32 vcc, v22, v18
        0xD5010017, 0x01A90280,    // v_cndmask_b32_e64 v23, 0, 1, vcc
        0x7E30020D,                // v_mov_b32_e32 v24, s13
        0xE0702000, 0x80021718,    // buffer_store_dword v23, v24, s[8:11], 0 idxen
        0xBF810000,                // s_endpgm
        0xBF068518,                // s_cmp_eq_u32 s24, 5
        0xBF84002B,                // s_cbranch_scc0 SKIP5
        0xF40C0700, 0xFA000140,    // s_load_dwordx8 s[28:35], s[0:1], 0x140
        0xF4040900, 0xFA000160,    // s_load_dwordx2 s[36:37], s[0:1], 0x160
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x80A48124,                // s_sub_u32 s36, s36, 1
        0x80A58125,                // s_sub_u32 s37, s37, 1
        0x900E180E,                // s_lshr_b32 s14, s14, s24
        0x90101810,                // s_lshr_b32 s16, s16, s24
        0x900F180F,                // s_lshr_b32 s15, s15, s24
        0x90111811,                // s_lshr_b32 s17, s17, s24
        0x838E240E,                // s_min_u32 s14, s14, s36
        0x83902410,                // s_min_u32 s16, s16, s36
        0x838F250F,                // s_min_u32 s15, s15, s37
        0x83912511,                // s_min_u32 s17, s17, s37
        0x7E14020E,                // v_mov_b32_e32 v10, s14
        0x7E16020F,                // v_mov_b32_e32 v11, s15
        0x7E180210,                // v_mov_b32_e32 v12, s16
        0x7E1A020F,                // v_mov_b32_e32 v13, s15
        0x7E1C020E,                // v_mov_b32_e32 v14, s14
        0x7E1E0211,                // v_mov_b32_e32 v15, s17
        0x7E200210,                // v_mov_b32_e32 v16, s16
        0x7E220211,                // v_mov_b32_e32 v17, s17
        0xF0001108, 0x0007120A,    // image_load v18, v[10:11], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007130C,    // image_load v19, v[12:13], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007140E,    // image_load v20, v[14:15], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x00071510,    // image_load v21, v[16:17], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x20242712,                // v_max_f32_e32 v18, v18, v19
        0x20282B14,                // v_max_f32_e32 v20, v20, v21
        0x20242912,                // v_max_f32_e32 v18, v18, v20
        0x7E2C0212,                // v_mov_b32_e32 v22, s18
        0x7C062516,                // v_cmp_le_f32_e32 vcc, v22, v18
        0xD5010017, 0x01A90280,    // v_cndmask_b32_e64 v23, 0, 1, vcc
        0x7E30020D,                // v_mov_b32_e32 v24, s13
        0xE0702000, 0x80021718,    // buffer_store_dword v23, v24, s[8:11], 0 idxen
        0xBF810000,                // s_endpgm
        0xBF068618,                // s_cmp_eq_u32 s24, 6
        0xBF84002B,                // s_cbranch_scc0 SKIP6
        0xF40C0700, 0xFA000180,    // s_load_dwordx8 s[28:35], s[0:1], 0x180
        0xF4040900, 0xFA0001A0,    // s_load_dwordx2 s[36:37], s[0:1], 0x1a0
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x80A48124,                // s_sub_u32 s36, s36, 1
        0x80A58125,                // s_sub_u32 s37, s37, 1
        0x900E180E,                // s_lshr_b32 s14, s14, s24
        0x90101810,                // s_lshr_b32 s16, s16, s24
        0x900F180F,                // s_lshr_b32 s15, s15, s24
        0x90111811,                // s_lshr_b32 s17, s17, s24
        0x838E240E,                // s_min_u32 s14, s14, s36
        0x83902410,                // s_min_u32 s16, s16, s36
        0x838F250F,                // s_min_u32 s15, s15, s37
        0x83912511,                // s_min_u32 s17, s17, s37
        0x7E14020E,                // v_mov_b32_e32 v10, s14
        0x7E16020F,                // v_mov_b32_e32 v11, s15
        0x7E180210,                // v_mov_b32_e32 v12, s16
        0x7E1A020F,                // v_mov_b32_e32 v13, s15
        0x7E1C020E,                // v_mov_b32_e32 v14, s14
        0x7E1E0211,                // v_mov_b32_e32 v15, s17
        0x7E200210,                // v_mov_b32_e32 v16, s16
        0x7E220211,                // v_mov_b32_e32 v17, s17
        0xF0001108, 0x0007120A,    // image_load v18, v[10:11], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007130C,    // image_load v19, v[12:13], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007140E,    // image_load v20, v[14:15], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x00071510,    // image_load v21, v[16:17], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x20242712,                // v_max_f32_e32 v18, v18, v19
        0x20282B14,                // v_max_f32_e32 v20, v20, v21
        0x20242912,                // v_max_f32_e32 v18, v18, v20
        0x7E2C0212,                // v_mov_b32_e32 v22, s18
        0x7C062516,                // v_cmp_le_f32_e32 vcc, v22, v18
        0xD5010017, 0x01A90280,    // v_cndmask_b32_e64 v23, 0, 1, vcc
        0x7E30020D,                // v_mov_b32_e32 v24, s13
        0xE0702000, 0x80021718,    // buffer_store_dword v23, v24, s[8:11], 0 idxen
        0xBF810000,                // s_endpgm
        0xBF068718,                // s_cmp_eq_u32 s24, 7
        0xBF84002B,                // s_cbranch_scc0 SKIP7
        0xF40C0700, 0xFA0001C0,    // s_load_dwordx8 s[28:35], s[0:1], 0x1c0
        0xF4040900, 0xFA0001E0,    // s_load_dwordx2 s[36:37], s[0:1], 0x1e0
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x80A48124,                // s_sub_u32 s36, s36, 1
        0x80A58125,                // s_sub_u32 s37, s37, 1
        0x900E180E,                // s_lshr_b32 s14, s14, s24
        0x90101810,                // s_lshr_b32 s16, s16, s24
        0x900F180F,                // s_lshr_b32 s15, s15, s24
        0x90111811,                // s_lshr_b32 s17, s17, s24
        0x838E240E,                // s_min_u32 s14, s14, s36
        0x83902410,                // s_min_u32 s16, s16, s36
        0x838F250F,                // s_min_u32 s15, s15, s37
        0x83912511,                // s_min_u32 s17, s17, s37
        0x7E14020E,                // v_mov_b32_e32 v10, s14
        0x7E16020F,                // v_mov_b32_e32 v11, s15
        0x7E180210,                // v_mov_b32_e32 v12, s16
        0x7E1A020F,                // v_mov_b32_e32 v13, s15
        0x7E1C020E,                // v_mov_b32_e32 v14, s14
        0x7E1E0211,                // v_mov_b32_e32 v15, s17
        0x7E200210,                // v_mov_b32_e32 v16, s16
        0x7E220211,                // v_mov_b32_e32 v17, s17
        0xF0001108, 0x0007120A,    // image_load v18, v[10:11], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007130C,    // image_load v19, v[12:13], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007140E,    // image_load v20, v[14:15], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x00071510,    // image_load v21, v[16:17], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x20242712,                // v_max_f32_e32 v18, v18, v19
        0x20282B14,                // v_max_f32_e32 v20, v20, v21
        0x20242912,                // v_max_f32_e32 v18, v18, v20
        0x7E2C0212,                // v_mov_b32_e32 v22, s18
        0x7C062516,                // v_cmp_le_f32_e32 vcc, v22, v18
        0xD5010017, 0x01A90280,    // v_cndmask_b32_e64 v23, 0, 1, vcc
        0x7E30020D,                // v_mov_b32_e32 v24, s13
        0xE0702000, 0x80021718,    // buffer_store_dword v23, v24, s[8:11], 0 idxen
        0xBF810000,                // s_endpgm
        0xBF810000,                // s_endpgm
    ];

    /// <summary>the same test with the level descriptor addressed by a computed pointer (table + level * 64)</summary>
    public static readonly uint[] HzbOcclusionDynamicTable =
    [
        0x7D840080,                // v_cmp_eq_u32_e32 vcc, 0, v0
        0xBEA8246A,                // s_and_saveexec_b64 s[40:41], vcc
        0x7E02020D,                // v_mov_b32_e32 v1, s13
        0xE0382000, 0x80010201,    // buffer_load_dwordx4 v[2:5], v1, s[4:7], 0 idxen
        0xE0302010, 0x80010601,    // buffer_load_dword v6, v1, s[4:7], 0 idxen offset:16
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x7E1C0502,                // v_readfirstlane_b32 s14, v2
        0x7E1E0503,                // v_readfirstlane_b32 s15, v3
        0x7E200504,                // v_readfirstlane_b32 s16, v4
        0x7E220505,                // v_readfirstlane_b32 s17, v5
        0x7E240506,                // v_readfirstlane_b32 s18, v6
        0x80940E10,                // s_sub_u32 s20, s16, s14
        0x80950F11,                // s_sub_u32 s21, s17, s15
        0x84961514,                // s_max_u32 s22, s20, s21
        0xBE971516,                // s_flbit_i32_b32 s23, s22
        0x809817A0,                // s_sub_u32 s24, 32, s23
        0xBF068016,                // s_cmp_eq_u32 s22, 0
        0x85181880,                // s_cselect_b32 s24, 0, s24
        0x83980C18,                // s_min_u32 s24, s24, s12
        0x8F198618,                // s_lshl_b32 s25, s24, 6
        0x801A1900,                // s_add_u32 s26, s0, s25
        0x821B8001,                // s_addc_u32 s27, s1, 0
        0xF40C070D, 0xFA000000,    // s_load_dwordx8 s[28:35], s[26:27], null
        0xF404090D, 0xFA000020,    // s_load_dwordx2 s[36:37], s[26:27], 0x20
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x80A48124,                // s_sub_u32 s36, s36, 1
        0x80A58125,                // s_sub_u32 s37, s37, 1
        0x900E180E,                // s_lshr_b32 s14, s14, s24
        0x90101810,                // s_lshr_b32 s16, s16, s24
        0x900F180F,                // s_lshr_b32 s15, s15, s24
        0x90111811,                // s_lshr_b32 s17, s17, s24
        0x838E240E,                // s_min_u32 s14, s14, s36
        0x83902410,                // s_min_u32 s16, s16, s36
        0x838F250F,                // s_min_u32 s15, s15, s37
        0x83912511,                // s_min_u32 s17, s17, s37
        0x7E14020E,                // v_mov_b32_e32 v10, s14
        0x7E16020F,                // v_mov_b32_e32 v11, s15
        0x7E180210,                // v_mov_b32_e32 v12, s16
        0x7E1A020F,                // v_mov_b32_e32 v13, s15
        0x7E1C020E,                // v_mov_b32_e32 v14, s14
        0x7E1E0211,                // v_mov_b32_e32 v15, s17
        0x7E200210,                // v_mov_b32_e32 v16, s16
        0x7E220211,                // v_mov_b32_e32 v17, s17
        0xF0001108, 0x0007120A,    // image_load v18, v[10:11], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007130C,    // image_load v19, v[12:13], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x0007140E,    // image_load v20, v[14:15], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xF0001108, 0x00071510,    // image_load v21, v[16:17], s[28:35] dmask:0x1 dim:SQ_RSRC_IMG_2D unorm
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0x20242712,                // v_max_f32_e32 v18, v18, v19
        0x20282B14,                // v_max_f32_e32 v20, v20, v21
        0x20242912,                // v_max_f32_e32 v18, v18, v20
        0x7E2C0212,                // v_mov_b32_e32 v22, s18
        0x7C062516,                // v_cmp_le_f32_e32 vcc, v22, v18
        0xD5010017, 0x01A90280,    // v_cndmask_b32_e64 v23, 0, 1, vcc
        0x7E30020D,                // v_mov_b32_e32 v24, s13
        0xE0702000, 0x80021718,    // buffer_store_dword v23, v24, s[8:11], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>s_load_dwordx4 with an SGPR soffset: group k reads record k of the table at s[0:1]</summary>
    public static readonly uint[] ScalarLoadSgprOffset =
    [
        0x8F0A8408,                // s_lshl_b32 s10, s8, 4
        0xF4080300, 0x14000000,    // s_load_dwordx4 s[12:15], s[0:1], s10
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x7D840080,                // v_cmp_eq_u32_e32 vcc, 0, v0
        0xBEA8246A,                // s_and_saveexec_b64 s[40:41], vcc
        0x8F0B8208,                // s_lshl_b32 s11, s8, 2
        0x7E14020B,                // v_mov_b32_e32 v10, s11
        0x7E04020C,                // v_mov_b32_e32 v2, s12
        0x7E06020D,                // v_mov_b32_e32 v3, s13
        0x7E08020E,                // v_mov_b32_e32 v4, s14
        0x7E0A020F,                // v_mov_b32_e32 v5, s15
        0xE0702000, 0x8001020A,    // buffer_store_dword v2, v10, s[4:7], 0 idxen
        0x4A141481,                // v_add_nc_u32_e32 v10, 1, v10
        0xE0702000, 0x8001030A,    // buffer_store_dword v3, v10, s[4:7], 0 idxen
        0x4A141481,                // v_add_nc_u32_e32 v10, 1, v10
        0xE0702000, 0x8001040A,    // buffer_store_dword v4, v10, s[4:7], 0 idxen
        0x4A141481,                // v_add_nc_u32_e32 v10, 1, v10
        0xE0702000, 0x8001050A,    // buffer_store_dword v5, v10, s[4:7], 0 idxen
        0xBF810000,                // s_endpgm
    ];

    /// <summary>s_load_dwordx4 with an immediate offset of 16: every group reads record 1</summary>
    public static readonly uint[] ScalarLoadImmediateOffset =
    [
        0xF4080300, 0xFA000010,    // s_load_dwordx4 s[12:15], s[0:1], 0x10
        0xBF8CC07F,                // s_waitcnt lgkmcnt(0)
        0x7D840080,                // v_cmp_eq_u32_e32 vcc, 0, v0
        0xBEA8246A,                // s_and_saveexec_b64 s[40:41], vcc
        0x8F0B8208,                // s_lshl_b32 s11, s8, 2
        0x7E14020B,                // v_mov_b32_e32 v10, s11
        0x7E04020C,                // v_mov_b32_e32 v2, s12
        0x7E06020D,                // v_mov_b32_e32 v3, s13
        0x7E08020E,                // v_mov_b32_e32 v4, s14
        0x7E0A020F,                // v_mov_b32_e32 v5, s15
        0xE0702000, 0x8001020A,    // buffer_store_dword v2, v10, s[4:7], 0 idxen
        0x4A141481,                // v_add_nc_u32_e32 v10, 1, v10
        0xE0702000, 0x8001030A,    // buffer_store_dword v3, v10, s[4:7], 0 idxen
        0x4A141481,                // v_add_nc_u32_e32 v10, 1, v10
        0xE0702000, 0x8001040A,    // buffer_store_dword v4, v10, s[4:7], 0 idxen
        0x4A141481,                // v_add_nc_u32_e32 v10, 1, v10
        0xE0702000, 0x8001050A,    // buffer_store_dword v5, v10, s[4:7], 0 idxen
        0xBF810000,                // s_endpgm
    ];
}
