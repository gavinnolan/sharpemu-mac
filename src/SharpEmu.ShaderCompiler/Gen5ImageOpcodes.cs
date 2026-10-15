// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

/// <summary>
/// What an MIMG opcode does and which optional address operands it carries. The address
/// operands follow the RDNA2 ISA order (section 8.2.5): {offset}{bias}{z-compare}
/// {derivatives}{body}, where the body ends with the LOD of an _L form or the clamp of a
/// _CL form.
/// </summary>
[Flags]
public enum Gen5ImageOp : uint
{
    None = 0,

    // ---- the access ----
    Load = 1u << 0,
    Store = 1u << 1,
    Atomic = 1u << 2,
    Sample = 1u << 3,
    Gather = 1u << 4,
    GetResinfo = 1u << 5,
    GetLod = 1u << 6,
    MsaaLoad = 1u << 7,
    Bvh = 1u << 8,

    // ---- loads and stores ----
    /// <summary>A mip level follows the coordinates (LOAD_MIP / STORE_MIP).</summary>
    Mip = 1u << 9,
    /// <summary>No format conversion: the texel's raw bits (the _PCK forms).</summary>
    Packed = 1u << 10,
    /// <summary>A _PCK_SGN load sign-extends 8- and 16-bit texels.</summary>
    PackedSigned = 1u << 11,

    // ---- samples and gathers ----
    Offset = 1u << 12,
    Bias = 1u << 13,
    Compare = 1u << 14,
    /// <summary>User derivatives (_D).</summary>
    Derivatives = 1u << 15,
    /// <summary>User derivatives taken once per quad (_CD).</summary>
    CoarseDerivatives = 1u << 16,
    /// <summary>A user LOD (_L) after the coordinates.</summary>
    Lod = 1u << 17,
    /// <summary>Level zero (_LZ).</summary>
    LodZero = 1u << 18,
    /// <summary>A minimum-LOD clamp (_CL) after the coordinates.</summary>
    Clamp = 1u << 19,
    /// <summary>16-bit derivatives (_G16).</summary>
    G16 = 1u << 20,
    /// <summary>The PS5's sampler-adjust forms (_A).</summary>
    Adjust = 1u << 21,
    /// <summary>GATHER4H: four horizontally adjacent texels.</summary>
    Horizontal = 1u << 22,
}

/// <summary>
/// The gfx10.3 MIMG opcode table, checked against llvm-mc (-mcpu=gfx1030; gfx1010 lacks
/// IMAGE_MSAA_LOAD), plus the PS5's sampler-adjust forms at 0xA0-0xBE, which LLVM does not
/// know (it disassembles them as v_illegal on gfx1010/gfx1013/gfx1030) but which share no
/// opcode with a gfx10 instruction: the _G16 forms fill exactly the D/D_CL slots between them.
/// </summary>
public static class Gen5ImageOpcodes
{
    private const Gen5ImageOp S = Gen5ImageOp.Sample;
    private const Gen5ImageOp G = Gen5ImageOp.Gather;
    private const Gen5ImageOp O = Gen5ImageOp.Offset;
    private const Gen5ImageOp B = Gen5ImageOp.Bias;
    private const Gen5ImageOp C = Gen5ImageOp.Compare;
    private const Gen5ImageOp D = Gen5ImageOp.Derivatives;
    private const Gen5ImageOp Cd = Gen5ImageOp.Derivatives | Gen5ImageOp.CoarseDerivatives;
    private const Gen5ImageOp L = Gen5ImageOp.Lod;
    private const Gen5ImageOp Lz = Gen5ImageOp.LodZero;
    private const Gen5ImageOp Cl = Gen5ImageOp.Clamp;
    private const Gen5ImageOp G16 = Gen5ImageOp.G16;
    private const Gen5ImageOp A = Gen5ImageOp.Adjust;
    private const Gen5ImageOp Ld = Gen5ImageOp.Load;
    private const Gen5ImageOp St = Gen5ImageOp.Store;
    private const Gen5ImageOp At = Gen5ImageOp.Atomic;
    private const Gen5ImageOp Mip = Gen5ImageOp.Mip;
    private const Gen5ImageOp Pck = Gen5ImageOp.Packed;
    private const Gen5ImageOp Sgn = Gen5ImageOp.PackedSigned;

    private static readonly (uint Opcode, string Name, Gen5ImageOp Flags)[] Table =
    [
        (0x00, "ImageLoad", Ld),
        (0x01, "ImageLoadMip", Ld | Mip),
        (0x02, "ImageLoadPck", Ld | Pck),
        (0x03, "ImageLoadPckSgn", Ld | Pck | Sgn),
        (0x04, "ImageLoadMipPck", Ld | Mip | Pck),
        (0x05, "ImageLoadMipPckSgn", Ld | Mip | Pck | Sgn),
        (0x08, "ImageStore", St),
        (0x09, "ImageStoreMip", St | Mip),
        (0x0A, "ImageStorePck", St | Pck),
        (0x0B, "ImageStoreMipPck", St | Mip | Pck),
        (0x0E, "ImageGetResinfo", Gen5ImageOp.GetResinfo),
        (0x0F, "ImageAtomicSwap", At),
        (0x10, "ImageAtomicCmpswap", At),
        (0x11, "ImageAtomicAdd", At),
        (0x12, "ImageAtomicSub", At),
        (0x14, "ImageAtomicSmin", At),
        (0x15, "ImageAtomicUmin", At),
        (0x16, "ImageAtomicSmax", At),
        (0x17, "ImageAtomicUmax", At),
        (0x18, "ImageAtomicAnd", At),
        (0x19, "ImageAtomicOr", At),
        (0x1A, "ImageAtomicXor", At),
        (0x1B, "ImageAtomicInc", At),
        (0x1C, "ImageAtomicDec", At),
        (0x1D, "ImageAtomicFcmpswap", At),
        (0x1E, "ImageAtomicFmin", At),
        (0x1F, "ImageAtomicFmax", At),
        (0x20, "ImageSample", S),
        (0x21, "ImageSampleCl", S | Cl),
        (0x22, "ImageSampleD", S | D),
        (0x23, "ImageSampleDCl", S | D | Cl),
        (0x24, "ImageSampleL", S | L),
        (0x25, "ImageSampleB", S | B),
        (0x26, "ImageSampleBCl", S | B | Cl),
        (0x27, "ImageSampleLz", S | Lz),
        (0x28, "ImageSampleC", S | C),
        (0x29, "ImageSampleCCl", S | C | Cl),
        (0x2A, "ImageSampleCD", S | C | D),
        (0x2B, "ImageSampleCDCl", S | C | D | Cl),
        (0x2C, "ImageSampleCL", S | C | L),
        (0x2D, "ImageSampleCB", S | C | B),
        (0x2E, "ImageSampleCBCl", S | C | B | Cl),
        (0x2F, "ImageSampleCLz", S | C | Lz),
        (0x30, "ImageSampleO", S | O),
        (0x31, "ImageSampleClO", S | Cl | O),
        (0x32, "ImageSampleDO", S | D | O),
        (0x33, "ImageSampleDClO", S | D | Cl | O),
        (0x34, "ImageSampleLO", S | L | O),
        (0x35, "ImageSampleBO", S | B | O),
        (0x36, "ImageSampleBClO", S | B | Cl | O),
        (0x37, "ImageSampleLzO", S | Lz | O),
        (0x38, "ImageSampleCO", S | C | O),
        (0x39, "ImageSampleCClO", S | C | Cl | O),
        (0x3A, "ImageSampleCDO", S | C | D | O),
        (0x3B, "ImageSampleCDClO", S | C | D | Cl | O),
        (0x3C, "ImageSampleCLO", S | C | L | O),
        (0x3D, "ImageSampleCBO", S | C | B | O),
        (0x3E, "ImageSampleCBClO", S | C | B | Cl | O),
        (0x3F, "ImageSampleCLzO", S | C | Lz | O),
        (0x40, "ImageGather4", G),
        (0x41, "ImageGather4Cl", G | Cl),
        (0x44, "ImageGather4L", G | L),
        (0x45, "ImageGather4B", G | B),
        (0x46, "ImageGather4BCl", G | B | Cl),
        (0x47, "ImageGather4Lz", G | Lz),
        (0x48, "ImageGather4C", G | C),
        (0x49, "ImageGather4CCl", G | C | Cl),
        (0x4C, "ImageGather4CL", G | C | L),
        (0x4D, "ImageGather4CB", G | C | B),
        (0x4E, "ImageGather4CBCl", G | C | B | Cl),
        (0x4F, "ImageGather4CLz", G | C | Lz),
        (0x50, "ImageGather4O", G | O),
        (0x51, "ImageGather4ClO", G | Cl | O),
        (0x54, "ImageGather4LO", G | L | O),
        (0x55, "ImageGather4BO", G | B | O),
        (0x56, "ImageGather4BClO", G | B | Cl | O),
        (0x57, "ImageGather4LzO", G | Lz | O),
        (0x58, "ImageGather4CO", G | C | O),
        (0x59, "ImageGather4CClO", G | C | Cl | O),
        (0x5C, "ImageGather4CLO", G | C | L | O),
        (0x5D, "ImageGather4CBO", G | C | B | O),
        (0x5E, "ImageGather4CBClO", G | C | B | Cl | O),
        (0x5F, "ImageGather4CLzO", G | C | Lz | O),
        (0x60, "ImageGetLod", Gen5ImageOp.GetLod),
        (0x61, "ImageGather4H", G | Gen5ImageOp.Horizontal),
        (0x68, "ImageSampleCd", S | Cd),
        (0x69, "ImageSampleCdCl", S | Cd | Cl),
        (0x6A, "ImageSampleCCd", S | C | Cd),
        (0x6B, "ImageSampleCCdCl", S | C | Cd | Cl),
        (0x6C, "ImageSampleCdO", S | Cd | O),
        (0x6D, "ImageSampleCdClO", S | Cd | Cl | O),
        (0x6E, "ImageSampleCCdO", S | C | Cd | O),
        (0x6F, "ImageSampleCCdClO", S | C | Cd | Cl | O),
        (0x80, "ImageMsaaLoad", Gen5ImageOp.MsaaLoad),
        (0xA0, "ImageSampleA", S | A),
        (0xA1, "ImageSampleClA", S | Cl | A),
        (0xA2, "ImageSampleDG16", S | D | G16),
        (0xA3, "ImageSampleDClG16", S | D | Cl | G16),
        (0xA5, "ImageSampleBA", S | B | A),
        (0xA6, "ImageSampleBClA", S | B | Cl | A),
        (0xA8, "ImageSampleCA", S | C | A),
        (0xA9, "ImageSampleCClA", S | C | Cl | A),
        (0xAA, "ImageSampleCDG16", S | C | D | G16),
        (0xAB, "ImageSampleCDClG16", S | C | D | Cl | G16),
        (0xAD, "ImageSampleCBA", S | C | B | A),
        (0xAE, "ImageSampleCBClA", S | C | B | Cl | A),
        (0xB0, "ImageSampleAO", S | O | A),
        (0xB1, "ImageSampleClAO", S | Cl | O | A),
        (0xB2, "ImageSampleDOG16", S | D | O | G16),
        (0xB3, "ImageSampleDClOG16", S | D | Cl | O | G16),
        (0xB5, "ImageSampleBAO", S | B | O | A),
        (0xB6, "ImageSampleBClAO", S | B | Cl | O | A),
        (0xB8, "ImageSampleCAO", S | C | O | A),
        (0xB9, "ImageSampleCClAO", S | C | Cl | O | A),
        (0xBA, "ImageSampleCDOG16", S | C | D | O | G16),
        (0xBB, "ImageSampleCDClOG16", S | C | D | Cl | O | G16),
        (0xBD, "ImageSampleCBAO", S | C | B | O | A),
        (0xBE, "ImageSampleCBClAO", S | C | B | Cl | O | A),
        (0xE6, "ImageBvhIntersectRay", Gen5ImageOp.Bvh),
        (0xE7, "ImageBvh64IntersectRay", Gen5ImageOp.Bvh),
        (0xE8, "ImageSampleCdG16", S | Cd | G16),
        (0xE9, "ImageSampleCdClG16", S | Cd | Cl | G16),
        (0xEA, "ImageSampleCCdG16", S | C | Cd | G16),
        (0xEB, "ImageSampleCCdClG16", S | C | Cd | Cl | G16),
        (0xEC, "ImageSampleCdOG16", S | Cd | O | G16),
        (0xED, "ImageSampleCdClOG16", S | Cd | Cl | O | G16),
        (0xEE, "ImageSampleCCdOG16", S | C | Cd | O | G16),
        (0xEF, "ImageSampleCCdClOG16", S | C | Cd | Cl | O | G16),
    ];

    private static readonly string?[] NamesByOpcode = BuildNames();
    private static readonly Dictionary<string, Gen5ImageOp> FlagsByName =
        Table.ToDictionary(static entry => entry.Name, static entry => entry.Flags, StringComparer.Ordinal);

    /// <summary>Every decoded opcode, for table-driven tests.</summary>
    public static IEnumerable<(uint Opcode, string Name, Gen5ImageOp Flags)> All => Table;

    /// <summary>The decoder's name for an 8-bit MIMG opcode, or null when it has none.</summary>
    public static string? NameOf(uint opcode) =>
        opcode < (uint)NamesByOpcode.Length ? NamesByOpcode[opcode] : null;

    /// <summary>What a decoded MIMG opcode does; <see cref="Gen5ImageOp.None"/> for any other name.</summary>
    public static Gen5ImageOp FlagsOf(string opcode) =>
        FlagsByName.TryGetValue(opcode, out var flags) ? flags : Gen5ImageOp.None;

    public static bool Has(string opcode, Gen5ImageOp flag) => (FlagsOf(opcode) & flag) != 0;

    /// <summary>Opcodes that take a sampler (S#).</summary>
    public static bool UsesSampler(string opcode) =>
        (FlagsOf(opcode) & (Gen5ImageOp.Sample | Gen5ImageOp.Gather | Gen5ImageOp.GetLod)) != 0;

    /// <summary>
    /// The data dwords a read returns before any TFE/LWE status dword: four components for a
    /// gather (whatever DMASK selects) and for MSAA_LOAD (four fragments of one component),
    /// the DMASK population otherwise, halved (rounded up) for D16.
    /// </summary>
    public static uint ResultDwords(string opcode, uint dmask, bool d16)
    {
        var flags = FlagsOf(opcode);
        var components = (flags & (Gen5ImageOp.Gather | Gen5ImageOp.MsaaLoad)) != 0
            ? 4u
            : (uint)System.Numerics.BitOperations.PopCount(dmask & 0xF);
        return d16 ? (components + 1) / 2 : components;
    }

    private static string?[] BuildNames()
    {
        var names = new string?[256];
        foreach (var (opcode, name, _) in Table)
        {
            names[opcode] = name;
        }

        return names;
    }
}
