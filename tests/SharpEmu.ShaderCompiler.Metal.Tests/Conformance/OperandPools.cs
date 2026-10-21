// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

/// <summary>Operand values that hit every edge of a type: signed zeros, infinities, quiet and
/// signalling NaNs with payloads, denormals, the smallest and largest normals, values around
/// rounding and integer boundaries, integer extremes, shift amounts at and past the width,
/// and seeded random values.</summary>
internal static class OperandPools
{
    public static IReadOnlyList<ulong> Edges(OpType type) => type switch
    {
        OpType.F32 => F32Edges,
        OpType.F16 => F16Edges,
        OpType.F64 => F64Edges,
        OpType.B16 or OpType.U16 or OpType.I16 => Int16Edges,
        OpType.B64 or OpType.U64 or OpType.I64 => Int64Edges,
        _ => Int32Edges,
    };

    public static readonly ulong[] F32Edges =
    [
        0x00000000, 0x80000000, // +-0
        0x7F800000, 0xFF800000, // +-inf
        0x7FC00000, 0xFFC00000, 0x7FC12345, 0xFFFFFFFF, // quiet NaNs with payloads
        0x7F800001, 0xFF812345, 0x7FBFFFFF, // signalling NaNs
        0x00000001, 0x80000001, 0x007FFFFF, 0x807FFFFF, 0x00400000, // denormals
        0x00800000, 0x80800000, 0x7F7FFFFF, 0xFF7FFFFF, // min / max normals
        0x3F800000, 0xBF800000, 0x3F000000, 0xBF000000, 0x40000000, 0x3FC00000, 0xBFC00000, // 1, -1, 0.5, 2, +-1.5
        0x40200000, 0xC0200000, 0x3F7FFFFF, 0x3F800001, 0x3EAAAAAB, // 2.5, -2.5, 1 -+ ulp, 1/3
        0x4B000000, 0x4B000001, 0x4AFFFFFF, 0x4B800000, // 2^23 boundary (integral)
        0x4F000000, 0xCF000000, 0x4F800000, 0x4EFFFFFF, // 2^31, -2^31, 2^32, below 2^31
        0x477FE000, 0x477FF000, 0x38800000, 0x33800000, 0x33000000, // f16 max, f16 overflow, f16 min normal, f16 min denormal and half of it
        0x42F60000, 0xC2F60000, 0x3DCCCCCD, 0xC1200000, 0x501502F9, // 123, -123, 0.1, -10, 1e10
    ];

    public static readonly ulong[] F16Edges =
    [
        0x0000, 0x8000, 0x7C00, 0xFC00, // +-0, +-inf
        0x7E00, 0xFE00, 0x7E5A, 0xFFFF, // quiet NaNs
        0x7C01, 0xFD5A, 0x7DFF, // signalling NaNs
        0x0001, 0x8001, 0x03FF, 0x83FF, 0x0200, // denormals
        0x0400, 0x8400, 0x7BFF, 0xFBFF, // min / max normals
        0x3C00, 0xBC00, 0x3800, 0xB800, 0x4000, 0x3E00, 0xBE00, 0x4100, 0xC100, // 1, -1, .5, 2, 1.5, 2.5
        0x3BFF, 0x3C01, 0x3555, 0x6400, 0x6401, 0x63FF, // 1 -+ ulp, 1/3, 1024 boundary
        0x57B0, 0xD7B0, 0x2E66, 0xC900, 0x7800, // 123, -123, 0.1, -10, 32768
    ];

    public static readonly ulong[] F64Edges =
    [
        0x0000000000000000, 0x8000000000000000, 0x7FF0000000000000, 0xFFF0000000000000,
        0x7FF8000000000000, 0xFFF8000000000000, 0x7FF8000012345678, // quiet NaNs
        0x7FF0000000000001, 0xFFF4000000000000, // signalling NaNs
        0x0000000000000001, 0x8000000000000001, 0x000FFFFFFFFFFFFF, // denormals
        0x0010000000000000, 0x8010000000000000, 0x7FEFFFFFFFFFFFFF, 0xFFEFFFFFFFFFFFFF, // min / max normals
        0x3FF0000000000000, 0xBFF0000000000000, 0x3FE0000000000000, 0x4000000000000000, 0x3FF8000000000000,
        0xC004000000000000, 0x3FEFFFFFFFFFFFFF, 0x3FF0000000000001, 0x3FD5555555555555,
        0x4330000000000000, 0x4330000000000001, 0x432FFFFFFFFFFFFF, // 2^52 boundary
        0x41E0000000000000, 0xC1E0000000000000, 0x41F0000000000000, 0x41DFFFFFFFC00000, // 2^31, -2^31, 2^32, 2^31-1
        0x380FFFFFE0000000, 0x3810000000000000, 0x47EFFFFFE0000000, 0x47EFFFFFF0000000, // f32 denormal/min normal/max/overflow
        0x405EC00000000000, 0x3FB999999999999A, 0xC024000000000000,
    ];

    public static readonly ulong[] Int32Edges =
    [
        0, 1, 2, 3, 0xFFFFFFFF, 0xFFFFFFFE, 0x7FFFFFFF, 0x80000000, 0x80000001,
        15, 16, 17, 31, 32, 33, 63, 64, 65, 0xFF, 0x100, 0x7F, 0x80, 0xFFFF, 0x10000, 0x7FFF, 0x8000,
        0x00FFFFFF, 0x01000000, 0xFF800000, 0x00800000, 0xFFFF8000,
        0x12345678, 0x87654321, 0xDEADBEEF, 0x55555555, 0xAAAAAAAA, 0x0F0F0F0F, 0xF0000000,
    ];

    public static readonly ulong[] Int16Edges =
    [
        0, 1, 2, 3, 0xFFFF, 0xFFFE, 0x7FFF, 0x8000, 0x8001, 7, 8, 15, 16, 17, 31, 32, 0xFF, 0x100, 0x7F, 0x80,
        0x1234, 0xBEEF, 0x5555, 0xAAAA, 0x00F0,
        // high halves carry data too: an op that reads the whole register would see it.
        0xABCD0001, 0x12348000, 0xFFFF7FFF,
    ];

    public static readonly ulong[] Int64Edges =
    [
        0, 1, 2, 0xFFFFFFFFFFFFFFFF, 0xFFFFFFFFFFFFFFFE, 0x7FFFFFFFFFFFFFFF, 0x8000000000000000, 0x8000000000000001,
        0xFFFFFFFF, 0x100000000, 0x80000000, 0xFFFFFFFF80000000, 0x7FFFFFFF,
        31, 32, 33, 63, 64, 65, 127, 128,
        0x123456789ABCDEF0, 0xFEDCBA9876543210, 0x5555555555555555, 0xAAAAAAAAAAAAAAAA, 0x00000000DEADBEEF,
    ];

    /// <summary>Random values of a type: uniformly random bits, plus (for floats) random values
    /// of moderate magnitude so arithmetic is not dominated by overflow.</summary>
    public static ulong Random(OpType type, Random random)
    {
        var bits = (ulong)random.NextInt64() ^ ((ulong)random.Next() << 33);
        switch (type)
        {
            case OpType.F32 when random.Next(2) == 0:
                return SoftFloat.F32((float)((random.NextDouble() - 0.5) * Math.Pow(2, random.Next(-30, 30))));
            case OpType.F16 when random.Next(2) == 0:
                return SoftFloat.F16((float)((random.NextDouble() - 0.5) * Math.Pow(2, random.Next(-14, 14))));
            case OpType.F64 when random.Next(2) == 0:
                return SoftFloat.F64((random.NextDouble() - 0.5) * Math.Pow(2, random.Next(-60, 60)));
            default:
                return bits & (type.Width() == 64 ? ulong.MaxValue : type.Width() == 16 ? 0xFFFFFFFF : 0xFFFFFFFF);
        }
    }

    /// <summary>Operand tuples for an op: the cross product of the first two sources' edges, the
    /// third source cycling through its edges, then seeded random tuples, capped at
    /// <paramref name="limit"/>.</summary>
    public static List<ulong[]> Tuples(IReadOnlyList<OpType> types, Func<int, IReadOnlyList<ulong>>? pool,
        int limit, int randomCount, int seed)
    {
        var random = new Random(seed);
        var count = types.Count;
        var pools = Enumerable.Range(0, count).Select(index => pool?.Invoke(index) ?? Edges(types[index])).ToArray();
        var tuples = new List<ulong[]>();
        if (count == 0)
        {
            tuples.Add([0, 0, 0]);
            return tuples;
        }

        if (count == 1)
        {
            foreach (var value in pools[0]) tuples.Add([value, 0, 0]);
        }
        else
        {
            var third = 0;
            foreach (var a in pools[0])
            {
                foreach (var b in pools[1])
                {
                    var c = count > 2 ? pools[2][third++ % pools[2].Count] : 0;
                    tuples.Add([a, b, c]);
                }
            }

            if (count > 2)
            {
                // Every third-operand edge against a spread of the first two.
                for (var index = 0; index < pools[2].Count; index++)
                {
                    foreach (var a in pools[0].Where((_, i) => i % 5 == index % 5))
                    {
                        tuples.Add([a, pools[1][(index * 7) % pools[1].Count], pools[2][index]]);
                    }
                }
            }
        }

        if (tuples.Count > limit)
        {
            // Keep a deterministic spread rather than the head of the list.
            var step = (double)tuples.Count / limit;
            tuples = Enumerable.Range(0, limit).Select(index => tuples[(int)(index * step)]).ToList();
        }

        for (var index = 0; index < randomCount; index++)
        {
            var tuple = new ulong[3];
            for (var source = 0; source < count; source++)
            {
                tuple[source] = random.Next(4) == 0
                    ? pools[source][random.Next(pools[source].Count)]
                    : Random(types[source], random);
            }

            tuples.Add(tuple);
        }

        return tuples;
    }
}
