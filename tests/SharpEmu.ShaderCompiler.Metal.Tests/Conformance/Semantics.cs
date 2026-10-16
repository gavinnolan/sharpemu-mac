// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

/// <summary>Shared building blocks for writing opcode semantics: MODE-aware float arithmetic
/// whose NaN results are payload-unspecified, and integer helpers.</summary>
internal static class Semantics
{
    /// <summary>A float result: a NaN is marked loose (any NaN is accepted) since the ISA does
    /// not define arithmetic NaN payloads.</summary>
    public static LaneResult FloatResult(ulong bits, FloatFormat format) =>
        SoftFloat.IsNaN(bits, format) ? new LaneResult(format.DefaultNaN, false, format.ValueMask) : new LaneResult(bits);

    /// <summary>An IEEE arithmetic op under MODE: input denormals flushed unless FP_DENORM keeps
    /// them, the result rounded with output denormals flushed unless kept.</summary>
    public static LaneFunction Binary(FloatFormat format, Func<ulong, ulong, FloatFormat, bool, ulong> operation) =>
        (in LaneInputs lane) =>
        {
            var a = lane.Mode.FlushIn(lane.S0 & format.ValueMask, format);
            var b = lane.Mode.FlushIn(lane.S1 & format.ValueMask, format);
            return FloatResult(operation(a, b, format, lane.Mode.KeepsOutputDenormals(format)), format);
        };

    public static LaneFunction Ternary(FloatFormat format, Func<ulong, ulong, ulong, FloatFormat, bool, ulong> operation) =>
        (in LaneInputs lane) =>
        {
            var a = lane.Mode.FlushIn(lane.S0 & format.ValueMask, format);
            var b = lane.Mode.FlushIn(lane.S1 & format.ValueMask, format);
            var c = lane.Mode.FlushIn(lane.S2 & format.ValueMask, format);
            return FloatResult(operation(a, b, c, format, lane.Mode.KeepsOutputDenormals(format)), format);
        };

    public static LaneFunction Int(Func<ulong, ulong, ulong, ulong> operation) =>
        (in LaneInputs lane) => new LaneResult(operation(lane.S0, lane.S1, lane.S2));

    public static long Signed(ulong value, int width) => width switch
    {
        16 => (short)value,
        32 => (int)value,
        _ => (long)value,
    };

    /// <summary>Saturates a wide signed result to the signed range of the width.</summary>
    public static ulong SaturateSigned(Int128 value, int width)
    {
        var max = (Int128.One << (width - 1)) - 1;
        var min = -(Int128.One << (width - 1));
        var clamped = value > max ? max : value < min ? min : value;
        return (ulong)(clamped & (width == 64 ? ulong.MaxValue : (Int128.One << width) - 1));
    }

    /// <summary>Saturates a wide unsigned result to [0, 2^width - 1].</summary>
    public static ulong SaturateUnsigned(Int128 value, int width)
    {
        var max = (Int128.One << width) - 1;
        return (ulong)(value > max ? max : value < 0 ? 0 : value);
    }
}
