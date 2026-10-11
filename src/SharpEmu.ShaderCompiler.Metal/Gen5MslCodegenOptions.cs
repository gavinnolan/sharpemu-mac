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
public sealed record Gen5MslCodegenOptions(
    bool StructuredControlFlow = true,
    bool RegisterAllocation = true,
    bool ForceRegisterInitialization = false)
{
    public static Gen5MslCodegenOptions FromEnvironment { get; } = new(
        StructuredControlFlow: Environment.GetEnvironmentVariable("SHARPEMU_MSL_STRUCTURED") != "0",
        RegisterAllocation: Environment.GetEnvironmentVariable("SHARPEMU_MSL_REGALLOC") != "0",
        ForceRegisterInitialization: Environment.GetEnvironmentVariable("SHARPEMU_MSL_REGINIT") == "1");
}
