// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// Synthetic NGG primitive shaders, built as IR so the fixtures stay readable and no guest
/// shader bytes enter the repository. Each one is a merged ES/GS program: it exchanges a
/// value through LDS across the subgroup's two waves, synchronizes with S_BARRIER, asks for
/// export space with S_SENDMSG GS_ALLOC_REQ and then exports POS0 plus the packed primitive.
/// </summary>
internal static class Gen5NggFixtures
{
    // The merged stage's user data starts at s8 (ShaderPipelineCache.VertexUserDataBase), so
    // s0..s7 are the system scalars the geometry engine loads.
    public const uint VertexUserDataBase = 8;

    public const uint VertexCount = 64;
    public const uint PrimitiveCount = 1;

    // Two waves of 32, so the LDS exchange below genuinely crosses a wave boundary.
    public static Gen5NggStageInfo StageInfo { get; } = new(
        VerticesPerSubgroup: VertexCount,
        PrimitivesPerSubgroup: PrimitiveCount,
        MaxOutputPerSubgroup: VertexCount,
        OutputPrimitiveType: Gen5NggExports.OutputPrimitiveTriangleStrip,
        PrimitiveAmplificationFactor: 1,
        LocalDataShareBytes: 1024,
        VertexGroupSize: VertexCount,
        PrimitiveGroupSize: PrimitiveCount);

    /// <summary>The packed connectivity a triangle over subgroup vertices 0/1/2 exports:
    /// one 9-bit index per 10-bit field.</summary>
    public const uint TriangleConnectivity =
        (0u << (0 * Gen5NggExports.PrimitiveIndexStride)) |
        (1u << (1 * Gen5NggExports.PrimitiveIndexStride)) |
        (2u << (2 * Gen5NggExports.PrimitiveIndexStride));

    /// <summary>M0 for GS_ALLOC_REQ: the vertex count in bits 0-9, the primitive count in
    /// bits 12-22.</summary>
    public const uint AllocationRequest =
        VertexCount | (PrimitiveCount << Gen5NggExports.AllocationPrimitiveCountShift);

    private const uint SendMessageWord = 0xBF90_0000u | Gen5NggExports.GsAllocRequestMessage;
    private const uint EndProgramWord = 0xBF81_0000u;

    /// <summary>A culling primitive shader: LDS round trip across both waves, a barrier, the
    /// allocation request, a position export built from the exchanged value and the packed
    /// primitive export. Rasterizes one screen-space triangle through the center.</summary>
    public static Gen5ShaderProgram CullingTriangle(bool nullPrimitive = false, bool bufferFetchPosition = false)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;

        void Add(string opcode, Gen5ShaderEncoding encoding, Gen5Operand[] sources,
            Gen5Operand[] destinations, Gen5InstructionControl? control = null, uint word = 0)
        {
            instructions.Add(new Gen5ShaderInstruction(
                pc, encoding, opcode, [word], sources, destinations, control));
            pc += sizeof(uint);
        }

        // value = vertex index & 31 -> each wave writes its own lane number.
        Add("VAndB32", Gen5ShaderEncoding.Vop2,
            [Literal(31), Vector(Gen5NggExports.EsVertexIndexRegister)], [Vector(10)]);
        Add("VLshlrevB32", Gen5ShaderEncoding.Vop2,
            [Inline(2), Vector(Gen5NggExports.EsVertexIndexRegister)], [Vector(11)]);
        Add("DsWriteB32", Gen5ShaderEncoding.Ds,
            [Vector(11), Vector(10)], [], new Gen5DataShareControl(0, 0, false));
        Add("SBarrier", Gen5ShaderEncoding.Sopp, [], []);

        // Read the slot the *other* wave wrote: (index ^ 32) & 31 == index & 31, so the
        // value only survives if threadgroup LDS and the barrier both work.
        Add("VXorB32", Gen5ShaderEncoding.Vop2,
            [Literal(32), Vector(Gen5NggExports.EsVertexIndexRegister)], [Vector(12)]);
        Add("VLshlrevB32", Gen5ShaderEncoding.Vop2, [Inline(2), Vector(12)], [Vector(13)]);
        Add("DsReadB32", Gen5ShaderEncoding.Ds,
            [Vector(13)], [Vector(14)], new Gen5DataShareControl(0, 0, false));

        Add("VCvtF32U32", Gen5ShaderEncoding.Vop1, [Vector(14)], [Vector(15)]);
        if (bufferFetchPosition)
        {
            // buffer_load_format_xyzw v[24:27], v14, s[8:11], 0 idxen - the ES vertex fetch.
            Add("BufferLoadFormatXyzw", Gen5ShaderEncoding.Mubuf,
                [Vector(14), Scalar(8), Inline(0)],
                [Vector(24), Vector(25), Vector(26), Vector(27)],
                new Gen5BufferMemoryControl(
                    DwordCount: 4,
                    VectorAddress: 14,
                    VectorData: 24,
                    ScalarResource: 8,
                    OffsetBytes: 0,
                    IndexEnabled: true,
                    OffsetEnabled: false,
                    Glc: false,
                    Slc: false));
        }

        // x = 0.8 * value - 0.8 over values 0/1/2.
        Add("VMulF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(0.8f)), Vector(15)], [Vector(16)]);
        Add("VAddF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(-0.8f)), Vector(16)], [Vector(16)]);

        // y = 1.6 * (value & 1) - 0.8, which puts vertex 1 at the top of the triangle.
        Add("VAndB32", Gen5ShaderEncoding.Vop2, [Inline(1), Vector(14)], [Vector(17)]);
        Add("VCvtF32U32", Gen5ShaderEncoding.Vop1, [Vector(17)], [Vector(18)]);
        Add("VMulF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(1.6f)), Vector(18)], [Vector(19)]);
        Add("VAddF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(-0.8f)), Vector(19)], [Vector(19)]);
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Inline(0)], [Vector(20)]);
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Literal(Bits(1.0f))], [Vector(21)]);

        // The allocation request has to precede the exports, as it does in hardware.
        Add("SMovB32", Gen5ShaderEncoding.Sop1, [Literal(AllocationRequest)], [Scalar(124)]);
        Add("SSendmsg", Gen5ShaderEncoding.Sopp, [], [], word: SendMessageWord);
        Add("Exp", Gen5ShaderEncoding.Exp,
            bufferFetchPosition
                ? [Vector(24), Vector(25), Vector(26), Vector(27)]
                : [Vector(16), Vector(19), Vector(20), Vector(21)],
            [],
            new Gen5ExportControl(Gen5NggExports.FirstPositionTarget, 0xF, false, false, false));

        // One parameter export so the mesh vertex payload carries a user location the
        // paired pixel program can read.
        Add("Exp", Gen5ShaderEncoding.Exp,
            [Vector(15), Vector(20), Vector(20), Vector(21)], [],
            new Gen5ExportControl(Gen5NggExports.FirstParameterTarget, 0xF, false, false, false));

        var connectivity = nullPrimitive
            ? TriangleConnectivity | (1u << Gen5NggExports.PrimitiveNullBit)
            : TriangleConnectivity;
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Literal(connectivity)], [Vector(22)]);
        Add("Exp", Gen5ShaderEncoding.Exp,
            [Vector(22), Vector(22), Vector(22), Vector(22)], [],
            new Gen5ExportControl(Gen5NggExports.PrimitiveTarget, 0x1, false, true, false));
        Add("SEndpgm", Gen5ShaderEncoding.Sopp, [], [], word: EndProgramWord);
        return new Gen5ShaderProgram(0, instructions);
    }

    private const uint BranchWord = 0xBF82_0000u;
    private const uint BranchScc0Word = 0xBF84_0000u;
    private const uint BranchScc1Word = 0xBF85_0000u;

    private static uint Branch(uint opcodeWord, uint fromPc, uint toPc) =>
        opcodeWord | (ushort)(short)(((int)toPc - (int)(fromPc + 4)) / 4);

    private static void AppendTail(List<Gen5ShaderInstruction> instructions, ref uint pc)
    {
        var at = pc;
        void Add(string opcode, Gen5ShaderEncoding encoding, Gen5Operand[] sources,
            Gen5Operand[] destinations, Gen5InstructionControl? control = null, uint word = 0)
        {
            instructions.Add(new Gen5ShaderInstruction(
                at, encoding, opcode, [word], sources, destinations, control));
            at += sizeof(uint);
        }

        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Literal(Bits(1.0f))], [Vector(21)]);
        Add("SMovB32", Gen5ShaderEncoding.Sop1, [Literal(AllocationRequest)], [Scalar(124)]);
        Add("SSendmsg", Gen5ShaderEncoding.Sopp, [], [], word: SendMessageWord);
        Add("Exp", Gen5ShaderEncoding.Exp,
            [Vector(21), Vector(21), Vector(21), Vector(21)], [],
            new Gen5ExportControl(Gen5NggExports.FirstPositionTarget, 0xF, false, false, false));
        Add("SEndpgm", Gen5ShaderEncoding.Sopp, [], [], word: EndProgramWord);
        pc = at;
    }

    /// <summary>An S_BARRIER that only the threads which do not take a scalar branch reach.
    /// No barrier built this way is workgroup-uniform.</summary>
    public static Gen5ShaderProgram BarrierInDivergentBranch()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Ds, "DsWriteB32", [0],
            [Vector(11), Vector(10)], [], new Gen5DataShareControl(0, 0, false)));
        pc += 4;
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sopp, "SCbranchScc1",
            [Branch(BranchScc1Word, pc, pc + 8)], [], [], null));
        pc += 4;
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null));
        pc += 4;
        AppendTail(instructions, ref pc);
        return new Gen5ShaderProgram(0, instructions);
    }

    /// <summary>A loop whose body holds the barrier and which leaves through two different
    /// blocks, a shape the structured assembler cannot nest.</summary>
    public static Gen5ShaderProgram BarrierLoopWithTwoExits()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Ds, "DsWriteB32", [0],
            [Vector(11), Vector(10)], [], new Gen5DataShareControl(0, 0, false)));
        pc += 4;

        // 4: header; leaves to the exit at 20 or runs the body.
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sopp, "SCbranchScc1",
            [Branch(BranchScc1Word, pc, 20)], [], [], null));
        pc += 4;

        // 8: the barrier; 12: the back edge to the header.
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null));
        pc += 4;
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sopp, "SCbranchScc0",
            [Branch(BranchScc0Word, pc, 4)], [], [], null));
        pc += 4;

        // 16: the second exit jumps over the first to the tail at 24.
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sopp, "SBranch",
            [Branch(BranchWord, pc, 24)], [], [], null));
        pc += 4;

        // 20: the first exit falls into the tail.
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sop1, "SMovB32", [0],
            [Inline(0)], [Scalar(20)], null));
        pc += 4;
        AppendTail(instructions, ref pc);
        return new Gen5ShaderProgram(0, instructions);
    }

    /// <summary>The same two-exit loop, then a barrier after it: the loop holds no barrier,
    /// so every thread reaches the barrier once whatever the number of waves.</summary>
    public static Gen5ShaderProgram BarrierAfterLoopWithTwoExits()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Ds, "DsWriteB32", [0],
            [Vector(11), Vector(10)], [], new Gen5DataShareControl(0, 0, false)));
        pc += 4;
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sopp, "SCbranchScc1",
            [Branch(BranchScc1Word, pc, 20)], [], [], null));
        pc += 4;
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sop1, "SMovB32", [0],
            [Inline(0)], [Scalar(20)], null));
        pc += 4;
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sopp, "SCbranchScc0",
            [Branch(BranchScc0Word, pc, 4)], [], [], null));
        pc += 4;
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sopp, "SBranch",
            [Branch(BranchWord, pc, 24)], [], [], null));
        pc += 4;
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sop1, "SMovB32", [0],
            [Inline(0)], [Scalar(20)], null));
        pc += 4;
        instructions.Add(new Gen5ShaderInstruction(pc, Gen5ShaderEncoding.Sopp, "SBarrier", [0], [], [], null));
        pc += 4;
        AppendTail(instructions, ref pc);
        return new Gen5ShaderProgram(0, instructions);
    }

    /// <summary>The stage state of a wave64 primitive shader with no GS half.</summary>
    public static Gen5NggStageInfo Wave64StageInfo { get; } = StageInfo with
    {
        PrimitivesPerSubgroup = 64,
        IsWave64 = true,
    };

    /// <summary>A subgroup of 10 triangles over 30 vertices, the narrow-wave64 launch.</summary>
    public const uint Wave64SubgroupAllocation = 30u | (10u << Gen5NggExports.AllocationPrimitiveCountShift);

    /// <summary>The compiler-generated culling template's shape, reduced: no barrier, no LDS,
    /// every primitive owning three vertices. A vertex's position comes from its fetched
    /// index (x = 0.8 * index - 0.8, y = 1.6 * (index &amp; 1) - 0.8), and a primitive decodes
    /// its three local vertices from v0/v1 the way the guest does (bits 2-15 of each 16-bit
    /// field) before exporting them.</summary>
    public static Gen5ShaderProgram Wave64Triangles()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;

        void Add(string opcode, Gen5ShaderEncoding encoding, Gen5Operand[] sources,
            Gen5Operand[] destinations, Gen5InstructionControl? control = null, uint word = 0)
        {
            instructions.Add(new Gen5ShaderInstruction(
                pc, encoding, opcode, [word], sources, destinations, control));
            pc += sizeof(uint);
        }

        Add("DsWriteB32", Gen5ShaderEncoding.Ds,
            [Vector(11), Vector(10)], [], new Gen5DataShareControl(0, 0, false));
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Vector(Gen5NggExports.EsVertexIndexRegister)], [Vector(14)]);
        Add("VCvtF32U32", Gen5ShaderEncoding.Vop1, [Vector(14)], [Vector(15)]);
        Add("VMulF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(0.8f)), Vector(15)], [Vector(16)]);
        Add("VAddF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(-0.8f)), Vector(16)], [Vector(16)]);
        Add("VAndB32", Gen5ShaderEncoding.Vop2, [Inline(1), Vector(14)], [Vector(17)]);
        Add("VCvtF32U32", Gen5ShaderEncoding.Vop1, [Vector(17)], [Vector(18)]);
        Add("VMulF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(1.6f)), Vector(18)], [Vector(19)]);
        Add("VAddF32", Gen5ShaderEncoding.Vop2, [Literal(Bits(-0.8f)), Vector(19)], [Vector(19)]);
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Inline(0)], [Vector(20)]);
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Literal(Bits(1.0f))], [Vector(21)]);
        Add("SMovB32", Gen5ShaderEncoding.Sop1, [Literal(Wave64SubgroupAllocation)], [Scalar(124)]);
        Add("SSendmsg", Gen5ShaderEncoding.Sopp, [], [], word: SendMessageWord);
        Add("Exp", Gen5ShaderEncoding.Exp,
            [Vector(16), Vector(19), Vector(20), Vector(21)], [],
            new Gen5ExportControl(Gen5NggExports.FirstPositionTarget, 0xF, false, false, false));
        Add("Exp", Gen5ShaderEncoding.Exp,
            [Vector(15), Vector(20), Vector(20), Vector(21)], [],
            new Gen5ExportControl(Gen5NggExports.FirstParameterTarget, 0xF, false, false, false));

        // i0 = bfe(v0, 2, 14), i1 = v0 >> 18, i2 = bfe(v1, 2, 14), packed one per 10-bit field.
        Add("VLshrrevB32", Gen5ShaderEncoding.Vop2, [Inline(2), Vector(Gen5NggExports.GsVertexOffset01Register)], [Vector(30)]);
        Add("VAndB32", Gen5ShaderEncoding.Vop2, [Literal(0x3FFF), Vector(30)], [Vector(30)]);
        Add("VLshrrevB32", Gen5ShaderEncoding.Vop2, [Literal(18), Vector(Gen5NggExports.GsVertexOffset01Register)], [Vector(31)]);
        Add("VLshlrevB32", Gen5ShaderEncoding.Vop2, [Literal(Gen5NggExports.PrimitiveIndexStride), Vector(31)], [Vector(31)]);
        Add("VLshrrevB32", Gen5ShaderEncoding.Vop2, [Inline(2), Vector(Gen5NggExports.GsVertexOffset23Register)], [Vector(32)]);
        Add("VAndB32", Gen5ShaderEncoding.Vop2, [Literal(0x3FFF), Vector(32)], [Vector(32)]);
        Add("VLshlrevB32", Gen5ShaderEncoding.Vop2, [Literal(2 * Gen5NggExports.PrimitiveIndexStride), Vector(32)], [Vector(32)]);
        Add("VOrB32", Gen5ShaderEncoding.Vop2, [Vector(31), Vector(30)], [Vector(22)]);
        Add("VOrB32", Gen5ShaderEncoding.Vop2, [Vector(32), Vector(22)], [Vector(22)]);
        Add("Exp", Gen5ShaderEncoding.Exp,
            [Vector(22), Vector(22), Vector(22), Vector(22)], [],
            new Gen5ExportControl(Gen5NggExports.PrimitiveTarget, 0x1, false, true, false));
        Add("SEndpgm", Gen5ShaderEncoding.Sopp, [], [], word: EndProgramWord);
        return new Gen5ShaderProgram(0, instructions);
    }

    /// <summary>A lane mask written to an SGPR pair whose high dword held stale data, then
    /// counted with s_bcnt1_i32_b64: the culling templates' survivor count.</summary>
    public static Gen5ShaderProgram Wave64MaskPairProbe()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;

        void Add(string opcode, Gen5ShaderEncoding encoding, Gen5Operand[] sources,
            Gen5Operand[] destinations, Gen5InstructionControl? control = null, uint word = 0)
        {
            instructions.Add(new Gen5ShaderInstruction(
                pc, encoding, opcode, [word], sources, destinations, control));
            pc += sizeof(uint);
        }

        Add("DsWriteB32", Gen5ShaderEncoding.Ds,
            [Vector(11), Vector(10)], [], new Gen5DataShareControl(0, 0, false));
        Add("SMovB32", Gen5ShaderEncoding.Sop1, [Literal(0xFFFF_FFFFu)], [Scalar(1)]);
        Add("VCmpNeU32", Gen5ShaderEncoding.Vop3, [Inline(0), Vector(8)], [Scalar(0)]);
        Add("SBcnt1I32B64", Gen5ShaderEncoding.Sop1, [Scalar(0)], [Scalar(10)]);
        Add("SMovB32", Gen5ShaderEncoding.Sop1, [Literal(Wave64SubgroupAllocation)], [Scalar(124)]);
        Add("SSendmsg", Gen5ShaderEncoding.Sopp, [], [], word: SendMessageWord);
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Scalar(10)], [Vector(21)]);
        Add("Exp", Gen5ShaderEncoding.Exp,
            [Vector(21), Vector(21), Vector(21), Vector(21)], [],
            new Gen5ExportControl(Gen5NggExports.FirstPositionTarget, 0xF, false, false, false));
        Add("SEndpgm", Gen5ShaderEncoding.Sopp, [], [], word: EndProgramWord);
        return new Gen5ShaderProgram(0, instructions);
    }

    /// <summary>The cross-lane operations whose wave64 behaviour the narrow lowering pins:
    /// v_readlane of lane 63, v_mbcnt_hi, and an all-ones write to EXEC_HI.</summary>
    public static Gen5ShaderProgram Wave64LaneProbe()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;

        void Add(string opcode, Gen5ShaderEncoding encoding, Gen5Operand[] sources,
            Gen5Operand[] destinations, Gen5InstructionControl? control = null, uint word = 0)
        {
            instructions.Add(new Gen5ShaderInstruction(
                pc, encoding, opcode, [word], sources, destinations, control));
            pc += sizeof(uint);
        }

        Add("DsWriteB32", Gen5ShaderEncoding.Ds,
            [Vector(11), Vector(10)], [], new Gen5DataShareControl(0, 0, false));
        Add("VReadlaneB32", Gen5ShaderEncoding.Vop3,
            [Vector(Gen5NggExports.EsVertexIndexRegister), Literal(63)], [Scalar(20)]);
        Add("VMbcntHiU32B32", Gen5ShaderEncoding.Vop3, [Scalar(20), Vector(11)], [Vector(12)]);
        Add("SMovB32", Gen5ShaderEncoding.Sop1, [Literal(0xFFFF_FFFFu)], [Scalar(127)]);
        Add("VMovB32", Gen5ShaderEncoding.Vop1, [Literal(Bits(1.0f))], [Vector(21)]);
        Add("SMovB32", Gen5ShaderEncoding.Sop1, [Literal(Wave64SubgroupAllocation)], [Scalar(124)]);
        Add("SSendmsg", Gen5ShaderEncoding.Sopp, [], [], word: SendMessageWord);
        Add("Exp", Gen5ShaderEncoding.Exp,
            [Vector(12), Vector(21), Vector(21), Vector(21)], [],
            new Gen5ExportControl(Gen5NggExports.FirstPositionTarget, 0xF, false, false, false));
        Add("SEndpgm", Gen5ShaderEncoding.Sopp, [], [], word: EndProgramWord);
        return new Gen5ShaderProgram(0, instructions);
    }

    /// <summary>A primitive shader with no LDS and no barrier: the passthrough shape that
    /// must keep using the per-vertex path.</summary>
    public static Gen5ShaderProgram Passthrough()
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            new(0, Gen5ShaderEncoding.Vop1, "VMovB32", [0],
                [Literal(Bits(1.0f))], [Vector(21)], null),
            new(4, Gen5ShaderEncoding.Exp, "Exp", [0],
                [Vector(21), Vector(21), Vector(21), Vector(21)], [],
                new Gen5ExportControl(Gen5NggExports.FirstPositionTarget, 0xF, false, false, false)),
            new(8, Gen5ShaderEncoding.Sopp, "SSendmsg", [SendMessageWord], [], [], null),
            new(12, Gen5ShaderEncoding.Sopp, "SEndpgm", [EndProgramWord], [], [], null),
        };
        return new Gen5ShaderProgram(0, instructions);
    }

    /// <summary>The same program with its vertex positions fetched the way the ES part of a
    /// real primitive shader does: <c>buffer_load_format_xyzw</c> indexed by the vertex
    /// index, over a V# the geometry stage's user data carries. That fetch is exactly what
    /// the fixed-function vertex-fetch substitution used to swallow, leaving the mesh stage
    /// with no buffer binding at all.</summary>
    public static Gen5ShaderProgram BufferFetchTriangle() => CullingTriangle(bufferFetchPosition: true);

    // Vertex positions live at the start of the fetched buffer, one float4 per vertex.
    public const uint PositionStrideBytes = 16;
    public const uint PositionRecordCount = 4;
    private const uint PositionBaseAddress = 0x20_0000;

    // 32_32_32_32_FLOAT with the identity destination select, as a V# carries it.
    private const uint PositionDescriptorFormat = 77;

    /// <summary>The geometry stage's user data for <see cref="BufferFetchTriangle"/>: the V#
    /// at s[8:11], which is user-data words 0..3 when the stage's user data starts at s8.</summary>
    public static uint[] PositionUserData()
    {
        var userData = new uint[16];
        userData[0] = PositionBaseAddress;
        userData[1] = PositionStrideBytes << 16;
        userData[2] = PositionRecordCount;
        userData[3] = (PositionDescriptorFormat << 12) | DescriptorConstants.IdentityDestinationSelect;
        return userData;
    }

    public static ShaderCompileRequest Request(
        Gen5ShaderProgram program,
        Gen5NggStageInfo? stageInfo = null,
        int requiredVertexOutputCount = 1,
        ResourceRuntimeInputs? runtimeInputs = null,
        uint positionExportControl = 0,
        ShaderClipSpaceTransform clipSpace = default) =>
        Gen5ComputeFixtures.RequestOrThrow(
            program,
            ShaderStage.Vertex,
            requiredVertexOutputCount: requiredVertexOutputCount,
            runtimeInputs: runtimeInputs,
            primitiveShader: stageInfo ?? StageInfo,
            userDataBase: VertexUserDataBase,
            positionExportControl: positionExportControl,
            clipSpace: clipSpace);

    public static Gen5MslShader CompileOrThrow(
        Gen5ShaderProgram program,
        Gen5NggStageInfo? stageInfo = null,
        int requiredVertexOutputCount = 1,
        ResourceRuntimeInputs? runtimeInputs = null,
        uint positionExportControl = 0,
        ShaderClipSpaceTransform clipSpace = default)
    {
        var request = Request(program, stageInfo, requiredVertexOutputCount, runtimeInputs, positionExportControl, clipSpace);
        if (!Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error))
        {
            throw new InvalidOperationException($"NGG mesh emit failed: {error}");
        }

        return shader;
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static Gen5Operand Literal(uint value) => new(Gen5OperandKind.LiteralConstant, value);

    private static Gen5Operand Inline(uint value) =>
        new(Gen5OperandKind.EncodedConstant, 128 + value);

    private static Gen5Operand Vector(uint index) => Gen5Operand.Vector(index);

    private static Gen5Operand Scalar(uint index) => Gen5Operand.Scalar(index);
}
