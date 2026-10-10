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
    public static Gen5ShaderProgram CullingTriangle(bool nullPrimitive = false)
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

        // x = 0.8 * value - 0.8 over values 0/1/2.
        Add("VCvtF32U32", Gen5ShaderEncoding.Vop1, [Vector(14)], [Vector(15)]);
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
            [Vector(16), Vector(19), Vector(20), Vector(21)], [],
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

    public static ShaderCompileRequest Request(
        Gen5ShaderProgram program,
        Gen5NggStageInfo? stageInfo = null,
        int requiredVertexOutputCount = 1) =>
        Gen5ComputeFixtures.RequestOrThrow(
            program,
            ShaderStage.Vertex,
            requiredVertexOutputCount: requiredVertexOutputCount,
            primitiveShader: stageInfo ?? StageInfo,
            userDataBase: VertexUserDataBase);

    public static Gen5MslShader CompileOrThrow(
        Gen5ShaderProgram program,
        Gen5NggStageInfo? stageInfo = null,
        int requiredVertexOutputCount = 1)
    {
        var request = Request(program, stageInfo, requiredVertexOutputCount);
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
