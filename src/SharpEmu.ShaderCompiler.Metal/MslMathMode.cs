// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Metal;

/// <summary>
/// The floating-point mode every translated-guest MSL library is compiled with, shared by
/// the emulator's compile sites and the codegen test harness so both build the same code.
/// </summary>
/// <remarks>
/// <see cref="Safe"/> is what the backend shipped with: no reassociation, no contraction.
/// <see cref="Relaxed"/> (MTLMathModeRelaxed, MSL 3.2) lets the compiler contract a multiply
/// and an add into one fused operation and reassociate, but keeps NaN and infinity
/// semantics, which the translated GCN float code relies on. The transcendental library
/// functions stay precise (<see cref="FunctionsPrecise"/>): the fast variants return wrong
/// results for infinite and huge inputs. <c>SHARPEMU_MSL_MATHMODE=safe</c> goes back to
/// <see cref="Safe"/>.
/// </remarks>
public static class MslMathMode
{
    /// <summary>MTLMathModeSafe.</summary>
    public const nuint Safe = 0;

    /// <summary>MTLMathModeRelaxed.</summary>
    public const nuint Relaxed = 1;

    /// <summary>MTLMathFloatingPointFunctionsPrecise.</summary>
    public const nuint FunctionsPrecise = 1;

    /// <summary>The mode the emulator compiles with: <see cref="Relaxed"/> unless
    /// <c>SHARPEMU_MSL_MATHMODE=safe</c>.</summary>
    public static nuint Default { get; } =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_MSL_MATHMODE"), "safe", StringComparison.OrdinalIgnoreCase)
            ? Safe
            : Relaxed;

    /// <summary>The mode as a short token for cache keys and stamps: a binary built in one
    /// mode is not reusable in another.</summary>
    public static string Identity(nuint mode) => mode == Safe ? "math-safe" : "math-relaxed";
}
