// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

// The Gen5 (RDNA2/GFX10.3) NGG hardware encodings a primitive shader speaks, in one place
// so every backend decodes them the same way. Each layout is stated as the field offsets
// the emitters shift by.
public static class Gen5NggExports
{
    // EXP targets, from the RDNA2 ISA's EXP encoding table: 12..16 are POS0..POS4, 20 is
    // the primitive-connectivity export and 32..63 are PARAM0..PARAM31.
    public const uint FirstPositionTarget = 12;
    public const uint PositionTargetCount = 5;
    public const uint PrimitiveTarget = 20;
    public const uint FirstParameterTarget = 32;
    public const uint ParameterTargetCount = 32;

    // S_SENDMSG message ids live in SIMM16[3:0]; 9 is GS_ALLOC_REQ, which reserves the
    // subgroup's export space in the parameter cache.
    public const uint SendMessageIdMask = 0xF;
    public const uint GsAllocRequestMessage = 9;

    // M0 at GS_ALLOC_REQ, per the ISA's send-message table: the vertex count the subgroup
    // exports in M0[9:0] and the primitive count in M0[22:12]. Mesa builds exactly
    // (num_prim << 12) | num_vtx.
    public const int AllocationVertexCountShift = 0;
    public const uint AllocationVertexCountMask = 0x3FF;
    public const int AllocationPrimitiveCountShift = 12;
    public const uint AllocationPrimitiveCountMask = 0x7FF;

    // The merged ES/GS wave-info scalar. Its fields, as Mesa's arg lowering unpacks them:
    //   [7:0]   ES (input) vertex threads valid in this wave
    //   [15:8]  GS (input) primitive threads valid in this wave
    //   [23:16] GS wave id, which cross-wave LDS addressing uses
    //   [27:24] this wave's subgroup id inside the workgroup
    //   [31:28] number of waves in the workgroup
    // The vertex translator already rebuilds EXEC from the first pair.
    public const int MergedWaveVertexCountShift = 0;
    public const int MergedWavePrimitiveCountShift = 8;
    public const uint MergedWaveCountMask = 0xFF;
    public const int MergedWaveGsWaveIdShift = 16;
    public const int MergedWaveSubgroupIdShift = 24;
    public const int MergedWaveSubgroupCountShift = 28;
    public const uint MergedWaveInfoRegister = 3;

    // The merged group-info scalar: the ordered-append id in [11:0], the workgroup's ES
    // vertex count in [20:12] and its input primitive count in [30:22].
    public const uint MergedGroupInfoRegister = 2;
    public const int GroupVertexCountShift = 12;
    public const int GroupPrimitiveCountShift = 22;
    public const uint GroupCountMask = 0x1FF;

    // The merged stage's vector inputs, in the order the hardware loads them. The GS block
    // packs two 16-bit ES vertex offsets per register; the ES/VS block follows it, which is
    // why the vertex index lands in v5 and the instance index in v8 — the registers the
    // per-vertex path already seeds.
    public const uint GsVertexOffset01Register = 0;
    public const uint GsVertexOffset23Register = 1;
    public const uint GsPrimitiveIdRegister = 2;
    public const uint GsInvocationIdRegister = 3;
    public const uint GsVertexOffset45Register = 4;
    public const uint EsVertexIndexRegister = 5;
    public const uint EsInstanceIndexRegister = 8;

    // The primitive export dword packs one 9-bit vertex index per 10-bit field, leaving
    // each field's top bit as that vertex's edge flag, and marks a culled primitive with
    // bit 31. Index i therefore lives at bits (10 * i) .. (10 * i + 8). This is what
    // Mesa's NGG primitive packing builds for pre-GFX12 parts, GFX10.3 included.
    public const int PrimitiveIndexStride = 10;
    public const uint PrimitiveIndexMask = 0x1FF;
    public const int PrimitiveEdgeFlagOffset = 9;
    public const int PrimitiveNullBit = 31;

    // VGT_GS_OUT_PRIM_TYPE values the NGG path emits.
    public const uint OutputPrimitivePointList = 0;
    public const uint OutputPrimitiveLineStrip = 1;
    public const uint OutputPrimitiveTriangleStrip = 2;

    /// <summary>Vertices one exported primitive carries for an output primitive type.</summary>
    public static uint VerticesPerPrimitive(uint outputPrimitiveType) => outputPrimitiveType switch
    {
        OutputPrimitivePointList => 1,
        OutputPrimitiveLineStrip => 2,
        _ => 3,
    };

    /// <summary>True when this S_SENDMSG is GS_ALLOC_REQ. SOPP keeps its SIMM16 in the
    /// first instruction word; the message id is its low nibble.</summary>
    public static bool IsAllocationRequest(Gen5ShaderInstruction instruction) =>
        instruction.Opcode == "SSendmsg" &&
        instruction.Words.Count != 0 &&
        (instruction.Words[0] & SendMessageIdMask) == GsAllocRequestMessage;
}
