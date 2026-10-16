// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

/// <summary>An IEEE-754 binary format: f16, f32 or f64.</summary>
internal readonly record struct FloatFormat(int ExponentBits, int MantissaBits)
{
    public static readonly FloatFormat F16 = new(5, 10);
    public static readonly FloatFormat F32 = new(8, 23);
    public static readonly FloatFormat F64 = new(11, 52);

    public int Width => 1 + ExponentBits + MantissaBits;
    public int Bias => (1 << (ExponentBits - 1)) - 1;
    public int MinExponent => 1 - Bias;
    public int MaxExponent => Bias;
    public ulong SignMask => 1ul << (Width - 1);
    public ulong MantissaMask => (1ul << MantissaBits) - 1;
    public ulong ExponentMask => ((1ul << ExponentBits) - 1) << MantissaBits;
    public ulong QuietBit => 1ul << (MantissaBits - 1);
    public ulong ValueMask => Width == 64 ? ulong.MaxValue : (1ul << Width) - 1;
    public ulong Infinity => ExponentMask;
    public ulong MaxFinite => ExponentMask - (1ul << MantissaBits) | MantissaMask;
    public ulong One => (ulong)Bias << MantissaBits;
    /// <summary>The quiet NaN a GCN/RDNA ALU generates for an invalid operation.</summary>
    public ulong DefaultNaN => ExponentMask | QuietBit;
}

/// <summary>Exact software IEEE arithmetic used as the conformance oracle. Every operation is
/// evaluated on exact integers and rounded once, so the model never depends on the host FPU
/// (its denormal mode, its NaN choice or a double rounding through a wider type).</summary>
internal static class SoftFloat
{
    public static bool IsNaN(ulong bits, FloatFormat f) =>
        (bits & f.ExponentMask) == f.ExponentMask && (bits & f.MantissaMask) != 0;

    public static bool IsSignalingNaN(ulong bits, FloatFormat f) => IsNaN(bits, f) && (bits & f.QuietBit) == 0;

    public static bool IsQuietNaN(ulong bits, FloatFormat f) => IsNaN(bits, f) && (bits & f.QuietBit) != 0;

    public static bool IsInfinity(ulong bits, FloatFormat f) => (bits & ~f.SignMask & f.ValueMask) == f.ExponentMask;

    public static bool IsZero(ulong bits, FloatFormat f) => (bits & ~f.SignMask & f.ValueMask) == 0;

    public static bool IsDenormal(ulong bits, FloatFormat f) =>
        (bits & f.ExponentMask) == 0 && (bits & f.MantissaMask) != 0;

    public static bool IsNegative(ulong bits, FloatFormat f) => (bits & f.SignMask) != 0;

    public static ulong Quiet(ulong bits, FloatFormat f) => bits | f.QuietBit;

    public static ulong Negate(ulong bits, FloatFormat f) => bits ^ f.SignMask;

    public static ulong Abs(ulong bits, FloatFormat f) => bits & ~f.SignMask;

    public static ulong Zero(bool negative, FloatFormat f) => negative ? f.SignMask : 0;

    public static ulong Inf(bool negative, FloatFormat f) => f.Infinity | (negative ? f.SignMask : 0);

    /// <summary>A denormal becomes a zero of its sign.</summary>
    public static ulong FlushDenormal(ulong bits, FloatFormat f) =>
        IsDenormal(bits, f) ? bits & f.SignMask : bits;

    /// <summary>Finite value = (-1)^sign * mantissa * 2^exponent.</summary>
    public static (bool Negative, BigInteger Mantissa, int Exponent) Decompose(ulong bits, FloatFormat f)
    {
        var negative = IsNegative(bits, f);
        var biased = (int)((bits & f.ExponentMask) >> f.MantissaBits);
        var fraction = bits & f.MantissaMask;
        return biased == 0
            ? (negative, fraction, f.MinExponent - f.MantissaBits)
            : (negative, fraction | (1ul << f.MantissaBits), biased - f.Bias - f.MantissaBits);
    }

    /// <summary>Rounds the exact value (-1)^negative * mantissa * 2^exponent (mantissa > 0) to
    /// the format. Mode 0 is nearest-even, 1 toward +inf, 2 toward -inf, 3 toward zero.
    /// A result that is denormal after rounding becomes a zero of its sign unless
    /// <paramref name="keepDenormal"/>.</summary>
    public static ulong Round(bool negative, BigInteger mantissa, int exponent, FloatFormat f,
        bool keepDenormal = true, int mode = 0)
    {
        if (mantissa.IsZero) return Zero(negative, f);
        var precision = f.MantissaBits + 1;
        var length = (int)mantissa.GetBitLength();
        var top = exponent + length - 1;
        // The weight of the result's last mantissa bit.
        var lsb = Math.Max(top - precision + 1, f.MinExponent - f.MantissaBits);
        BigInteger rounded;
        if (lsb > exponent)
        {
            var shift = lsb - exponent;
            var quotient = mantissa >> shift;
            var remainder = mantissa - (quotient << shift);
            var half = BigInteger.One << (shift - 1);
            var up = mode switch
            {
                0 => remainder > half || (remainder == half && !quotient.IsEven),
                1 => !negative && !remainder.IsZero,
                2 => negative && !remainder.IsZero,
                _ => false,
            };
            rounded = up ? quotient + 1 : quotient;
        }
        else
        {
            rounded = mantissa << (exponent - lsb);
        }

        // Renormalise a carry out of the top bit.
        if (rounded.GetBitLength() > precision)
        {
            rounded >>= 1;
            lsb++;
        }

        if (rounded.IsZero) return Zero(negative, f);
        var resultTop = lsb + (int)rounded.GetBitLength() - 1;
        if (resultTop > f.MaxExponent)
        {
            var toInfinity = mode switch { 0 => true, 1 => !negative, 2 => negative, _ => false };
            return toInfinity ? Inf(negative, f) : f.MaxFinite | (negative ? f.SignMask : 0);
        }

        var sign = negative ? f.SignMask : 0;
        if (rounded.GetBitLength() < precision)
        {
            // Denormal: lsb is the denormal step.
            return keepDenormal ? sign | (ulong)rounded : sign;
        }

        var biased = (ulong)(resultTop + f.Bias);
        return sign | (biased << f.MantissaBits) | ((ulong)rounded & f.MantissaMask);
    }

    /// <summary>Exact a * b + c, rounded once (a fused multiply-add). Operands must not be
    /// NaN or infinity; zero signs follow IEEE (an exact zero sum is +0 in round-to-nearest
    /// unless both addends are -0).</summary>
    private static ulong FiniteFma(ulong a, ulong b, ulong c, FloatFormat f, bool keepDenormal, int mode)
    {
        var (sa, ma, ea) = Decompose(a, f);
        var (sb, mb, eb) = Decompose(b, f);
        var (sc, mc, ec) = Decompose(c, f);
        var productNegative = sa ^ sb;
        var product = ma * mb;
        var productExponent = ea + eb;
        var productZero = product.IsZero;
        if (productZero && mc.IsZero)
        {
            var negative = mode == 2 ? productNegative || sc : productNegative && sc;
            return Zero(negative, f);
        }

        var exponent = Math.Min(productExponent, ec);
        var p = (productNegative ? -product : product) << (productExponent - exponent);
        var q = (sc ? -mc : mc) << (ec - exponent);
        var sum = p + q;
        if (sum.IsZero) return Zero(mode == 2, f);
        return Round(sum.Sign < 0, BigInteger.Abs(sum), exponent, f, keepDenormal, mode);
    }

    /// <summary>IEEE fused multiply-add with NaN/infinity rules. NaN results are the format's
    /// default NaN; callers treat a NaN result's payload as unspecified.</summary>
    public static ulong Fma(ulong a, ulong b, ulong c, FloatFormat f, bool keepDenormal = true, int mode = 0)
    {
        if (IsNaN(a, f) || IsNaN(b, f) || IsNaN(c, f)) return f.DefaultNaN;
        var productNegative = IsNegative(a, f) ^ IsNegative(b, f);
        var aInf = IsInfinity(a, f);
        var bInf = IsInfinity(b, f);
        if ((aInf && IsZero(b, f)) || (bInf && IsZero(a, f))) return f.DefaultNaN;
        if (aInf || bInf)
        {
            if (IsInfinity(c, f) && IsNegative(c, f) != productNegative) return f.DefaultNaN;
            return Inf(productNegative, f);
        }

        if (IsInfinity(c, f)) return c & f.ValueMask;
        return FiniteFma(a, b, c, f, keepDenormal, mode);
    }

    public static ulong Add(ulong a, ulong b, FloatFormat f, bool keepDenormal = true, int mode = 0) =>
        Fma(a, f.One, b, f, keepDenormal, mode);

    public static ulong Sub(ulong a, ulong b, FloatFormat f, bool keepDenormal = true, int mode = 0) =>
        Add(a, Negate(b, f), f, keepDenormal, mode);

    public static ulong Mul(ulong a, ulong b, FloatFormat f, bool keepDenormal = true, int mode = 0)
    {
        if (IsNaN(a, f) || IsNaN(b, f)) return f.DefaultNaN;
        var negative = IsNegative(a, f) ^ IsNegative(b, f);
        if (IsInfinity(a, f) || IsInfinity(b, f))
        {
            return IsZero(a, f) || IsZero(b, f) ? f.DefaultNaN : Inf(negative, f);
        }

        var (_, ma, ea) = Decompose(a, f);
        var (_, mb, eb) = Decompose(b, f);
        var product = ma * mb;
        return product.IsZero ? Zero(negative, f) : Round(negative, product, ea + eb, f, keepDenormal, mode);
    }

    /// <summary>Converts between formats (exact widening, rounded narrowing). A NaN keeps its
    /// sign and the top of its payload and is quieted.</summary>
    public static ulong Convert(ulong bits, FloatFormat from, FloatFormat to, bool keepDenormal = true, int mode = 0)
    {
        var negative = IsNegative(bits, from);
        if (IsNaN(bits, from))
        {
            var payload = bits & from.MantissaMask;
            var shifted = to.MantissaBits >= from.MantissaBits
                ? payload << (to.MantissaBits - from.MantissaBits)
                : payload >> (from.MantissaBits - to.MantissaBits);
            return (negative ? to.SignMask : 0) | to.ExponentMask | to.QuietBit | shifted;
        }

        if (IsInfinity(bits, from)) return Inf(negative, to);
        if (IsZero(bits, from)) return Zero(negative, to);
        var (n, m, e) = Decompose(bits, from);
        return Round(n, m, e, to, keepDenormal, mode);
    }

    /// <summary>Exact value of a finite float as a rational m * 2^e (for comparisons and
    /// integer conversions).</summary>
    public static int Compare(ulong a, ulong b, FloatFormat f)
    {
        // Callers exclude NaN. -0 == +0.
        if (IsZero(a, f) && IsZero(b, f)) return 0;
        var ka = OrderKey(a, f);
        var kb = OrderKey(b, f);
        return ka.CompareTo(kb);
    }

    private static long OrderKey(ulong bits, FloatFormat f)
    {
        var magnitude = (long)(bits & ~f.SignMask & f.ValueMask);
        return IsNegative(bits, f) ? -magnitude : magnitude;
    }

    /// <summary>Rounds a finite float to an integer-valued float with the given mode
    /// (0 nearest-even, 1 ceil, 2 floor, 3 trunc), keeping the sign of zero.</summary>
    public static ulong RoundToIntegral(ulong bits, FloatFormat f, int mode)
    {
        if (IsNaN(bits, f)) return Quiet(bits, f);
        if (IsInfinity(bits, f) || IsZero(bits, f)) return bits;
        var (negative, m, e) = Decompose(bits, f);
        if (e >= 0) return bits;
        var integral = m >> -e;
        var remainder = m - (integral << -e);
        if (!remainder.IsZero)
        {
            var half = BigInteger.One << (-e - 1);
            var up = mode switch
            {
                0 => remainder > half || (remainder == half && !integral.IsEven),
                1 => !negative,
                2 => negative,
                _ => false,
            };
            if (up) integral += 1;
        }

        return integral.IsZero ? Zero(negative, f) : Round(negative, integral, 0, f);
    }

    /// <summary>Converts an exact integer to the format with the given rounding.</summary>
    public static ulong FromInteger(BigInteger value, FloatFormat f, int mode = 0) =>
        value.IsZero ? 0 : Round(value.Sign < 0, BigInteger.Abs(value), 0, f, true, mode);

    /// <summary>The exact value of a finite float truncated toward zero, as an integer.</summary>
    public static BigInteger TruncateToInteger(ulong bits, FloatFormat f)
    {
        var (negative, m, e) = Decompose(bits, f);
        var magnitude = e >= 0 ? m << e : m >> -e;
        return negative ? -magnitude : magnitude;
    }

    public static ulong F32(float value) => BitConverter.SingleToUInt32Bits(value);
    public static ulong F64(double value) => BitConverter.DoubleToUInt64Bits(value);
    public static ulong F16(float value) => BitConverter.HalfToUInt16Bits((Half)value);
}
