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
public sealed record Gen5MslCodegenOptions(
    bool StructuredControlFlow = true,
    bool RegisterAllocation = true,
    bool ForceRegisterInitialization = false,
    bool ExecGuardElision = true,
    bool BoundedRelativeMoves = true,
    bool RawDwordAccess = true,
    bool UniformTablesInConstantSpace = true,
    bool DispatchThreadgroupMemory = true)
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
        DispatchThreadgroupMemory: Environment.GetEnvironmentVariable("SHARPEMU_MSL_LDSSIZE") != "0");
}
