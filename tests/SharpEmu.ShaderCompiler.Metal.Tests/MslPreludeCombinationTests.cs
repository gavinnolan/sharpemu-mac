// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

/// <summary>
/// A translated module concatenates up to eight helper blocks, chosen per shader. The
/// startup library prewarm recompiles recorded modules verbatim, so every combination a
/// game can record has to compile on its own; fixtures only cover the ones their
/// programs happen to need. This compiles each combination the translator can emit.
/// </summary>
public sealed class MslPreludeCombinationTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Combinations()
    {
        for (var bits = 0; bits <= (int)Gen5MslPreludes.All; bits++)
        {
            var preludes = (Gen5MslPreludes)bits;
            // A pair lock always pulls in the coherent helpers, so the variant with the
            // coherent flag set renders the same text.
            if ((preludes & Gen5MslPreludes.PairLocks) != 0 && (preludes & Gen5MslPreludes.CoherentLoads) != 0)
            {
                continue;
            }

            yield return [bits];
        }
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void EveryPreludeCombinationCompiles(int bits)
    {
        var preludes = (Gen5MslPreludes)bits;
        var source = Gen5MslTranslator.RenderModulePreludes(preludes) + """

            kernel void sharpemu_prelude_probe(device uint* sink [[buffer(0)]], uint lane [[thread_position_in_grid]])
            {
                sink[lane] = lane;
            }

            """;
        Assert.Contains("static inline uint sharpemu_ballot(", source);
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine($"Source-only: {preludes}; no native compilation.");
            return;
        }

        Assert.True(MetalNative.IsAvailable, "Required native compilation needs a Metal device.");
        nint library = 0;
        try
        {
            Assert.True(MetalNative.TryCompileLibrary(source, out library, out var error), $"{preludes}: {error}");
            Assert.NotEqual((nint)0, library);
        }
        finally
        {
            if (library != 0)
            {
                MetalNative.ReleaseCollisionLibrary(library);
            }
        }
    }
}
