// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Metal;

/// <summary>
/// Codegen switches, passed per compile so a harness can translate the same program two
/// ways in one process without racing other compiles. The emulator uses
/// <see cref="FromEnvironment"/>, which is how each half of the change can be taken out
/// during bring-up.
/// </summary>
/// <param name="StructuredControlFlow">Nest the blocks in if/else and while instead of
/// running them from a PC dispatcher. <c>SHARPEMU_MSL_STRUCTURED=0</c> forces the
/// dispatcher.</param>
/// <param name="RegisterAllocation">Declare only the guest registers a program names,
/// instead of both register files at their architectural size.
/// <c>SHARPEMU_MSL_REGALLOC=0</c> forces the full files.</param>
/// <param name="ForceRegisterInitialization">Zero-fill every declared register instead of
/// only the ones some path reads before writing. <c>SHARPEMU_MSL_REGINIT=1</c> turns this
/// on.</param>
/// <param name="ExecGuardElision">Drop the per-lane EXEC guard where the program proves
/// every lane is set. <c>SHARPEMU_MSL_EXECGUARD=0</c> keeps every guard.</param>
/// <param name="BoundedRelativeMoves">Resolve V_MOVREL*'s register — from M0's known
/// values where straight-line code produces them, and otherwise over the registers the
/// program touches — so the register file needs no run-time index and can be scalarized.
/// <c>SHARPEMU_MSL_MOVREL=0</c> indexes the file instead, which keeps it an array.</param>
/// <param name="RawDwordAccess">Read and write a raw buffer dword with one indexed
/// access, which is how the GCN ISA addresses it and what the SPIR-V backend emits,
/// instead of assembling it from bytes. <c>SHARPEMU_MSL_DWORDACCESS=0</c> goes back to
/// the byte path.</param>
/// <param name="UniformTablesInConstantSpace">Point at the read-only uniform tables (the
/// user-data words, the flattened resource table) through the constant address space,
/// which is Apple's uniform datapath, instead of the device one.
/// <c>SHARPEMU_MSL_CONSTANTTABLES=0</c> goes back to device.</param>
/// <param name="DispatchThreadgroupMemory">Declare the threadgroup memory the dispatch
/// allocated rather than the full 32 KB. Threadgroup memory is a hard occupancy limit, so
/// this is the difference between one threadgroup per core and many.
/// <c>SHARPEMU_MSL_LDSSIZE=0</c> declares all of it.</param>
/// <param name="DeadMaskBallotElision">Skip a wave64 VCC or EXEC ballot whose 64-bit pair
/// nothing reads before it is overwritten, which saves the two threadgroup barriers the
/// cross-simdgroup exchange costs. <c>SHARPEMU_MSL_MASKBALLOT=0</c> materializes every
/// ballot.</param>
/// <param name="ElideByteBias">Drop the per-buffer byte bias (the packed memory-offset
/// bytes of the shader data) from every buffer address. The Metal host never writes
/// those bytes, so each bias is zero and the read and add are dead work.
/// <c>SHARPEMU_MSL_BYTEBIAS=1</c> keeps them.</param>
/// <param name="MipStatisticsFunctionConstant">Guard each GET_LOD_STATS counter site with the
/// function constant <see cref="Gen5MslShader.MipStatisticsFunctionConstantName"/>, so the
/// host compiles the counters out of a pixel pipeline until the title asks for LOD
/// statistics. The MSL text does not depend on when that happens, only the function the host
/// makes of it. <c>SHARPEMU_LOD_STATS=always</c> leaves the sites unguarded: a pipeline has
/// the counters from the first draw.</param>
/// <param name="GraphicsLoopStepCap">Charge the step cap on every loop iteration of a structured
/// vertex or pixel program too. The cap is for a mistranslated exit condition that would wedge
/// the queue; a graphics program's runaway loop is ended by the GPU watchdog, and the counter
/// is a register and a compare on every back edge. Compute and mesh programs keep the cap
/// either way. <c>SHARPEMU_SHADER_MAX_STEPS_GRAPHICS=1</c> restores it.</param>
/// <param name="LaneSpillCopies">Serve a wave64 V_READLANE_B32 that reloads an SGPR the
/// guest compiler spilled to a VGPR lane from a per-thread copy taken at the spill, which
/// saves the cross-simdgroup exchange and its barrier
/// (see <see cref="Ir.Gen5LaneSpillAnalysis"/>). <c>SHARPEMU_MSL_SPILLCOPY=0</c> reads every
/// lane through the exchange.</param>
public sealed record Gen5MslCodegenOptions(
    bool StructuredControlFlow = true,
    bool RegisterAllocation = true,
    bool ForceRegisterInitialization = false,
    bool ExecGuardElision = true,
    bool BoundedRelativeMoves = true,
    bool RawDwordAccess = true,
    bool UniformTablesInConstantSpace = true,
    bool DispatchThreadgroupMemory = true,
    bool DeadMaskBallotElision = true,
    bool ElideByteBias = true,
    bool MipStatisticsFunctionConstant = true,
    bool GraphicsLoopStepCap = false,
    bool LaneSpillCopies = true)
{
    public static Gen5MslCodegenOptions FromEnvironment { get; } = new(
        StructuredControlFlow: Environment.GetEnvironmentVariable("SHARPEMU_MSL_STRUCTURED") != "0",
        RegisterAllocation: Environment.GetEnvironmentVariable("SHARPEMU_MSL_REGALLOC") != "0",
        ForceRegisterInitialization: Environment.GetEnvironmentVariable("SHARPEMU_MSL_REGINIT") == "1",
        ExecGuardElision: Environment.GetEnvironmentVariable("SHARPEMU_MSL_EXECGUARD") != "0",
        BoundedRelativeMoves: Environment.GetEnvironmentVariable("SHARPEMU_MSL_MOVREL") != "0",
        RawDwordAccess: Environment.GetEnvironmentVariable("SHARPEMU_MSL_DWORDACCESS") != "0",
        UniformTablesInConstantSpace:
            Environment.GetEnvironmentVariable("SHARPEMU_MSL_CONSTANTTABLES") != "0",
        DispatchThreadgroupMemory: Environment.GetEnvironmentVariable("SHARPEMU_MSL_LDSSIZE") != "0",
        DeadMaskBallotElision:
            Environment.GetEnvironmentVariable("SHARPEMU_MSL_MASKBALLOT") != "0",
        ElideByteBias: Environment.GetEnvironmentVariable("SHARPEMU_MSL_BYTEBIAS") != "1",
        MipStatisticsFunctionConstant: Environment.GetEnvironmentVariable("SHARPEMU_LOD_STATS") != "always",
        GraphicsLoopStepCap: Environment.GetEnvironmentVariable("SHARPEMU_SHADER_MAX_STEPS_GRAPHICS") == "1",
        LaneSpillCopies: Environment.GetEnvironmentVariable("SHARPEMU_MSL_SPILLCOPY") != "0");
}
