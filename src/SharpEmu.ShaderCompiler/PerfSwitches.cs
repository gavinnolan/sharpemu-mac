// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

/// <summary>
/// The environment switches of the performance paths: each is on unless its variable is "0",
/// and each has a verify mode that SHARPEMU_PERF_VERIFY=1 turns on for all of them at once.
/// Callers read a switch once into a static readonly field.
/// </summary>
public static class PerfSwitches
{
    /// <summary>True unless the variable is "0".</summary>
    public static bool Enabled(string name) => Environment.GetEnvironmentVariable(name) != "0";

    /// <summary>True when the verify variable is "1" or the umbrella SHARPEMU_PERF_VERIFY is.</summary>
    public static bool Verify(string name) =>
        Environment.GetEnvironmentVariable(name) == "1" ||
        Environment.GetEnvironmentVariable("SHARPEMU_PERF_VERIFY") == "1";
}
