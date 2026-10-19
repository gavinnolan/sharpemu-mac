// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu;

/// <summary>
/// The verify switch of the per-draw CPU work (workstream A). A shortcut that skips work is
/// checked against the full path when its own <c>*_VERIFY=1</c> switch is on, or when
/// SHARPEMU_PERF_VERIFY=1 turns every one of them on at once. Verifying costs time and
/// logs a mismatch instead of acting on it; it is for measurement runs, not for play.
/// </summary>
public static class PerfVerify
{
    /// <summary>SHARPEMU_PERF_VERIFY=1: every shortcut also runs and checks the full path.</summary>
    public static readonly bool All =
        Environment.GetEnvironmentVariable("SHARPEMU_PERF_VERIFY") == "1";

    /// <summary>Whether the shortcut with this verify variable (<c>SHARPEMU_..._VERIFY</c>) is checked.</summary>
    public static bool IsOn(string variable) =>
        All || Environment.GetEnvironmentVariable(variable) == "1";
}
