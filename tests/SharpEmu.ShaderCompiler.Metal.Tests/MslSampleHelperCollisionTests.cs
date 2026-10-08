// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

public sealed class MslSampleHelperCollisionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("ImageSampleB", 9u, "bias")]
    [InlineData("ImageSampleL", 9u, "level")]
    [InlineData("ImageSampleD", 9u, "gradient2d")]
    [InlineData("ImageSampleD", 10u, "gradient3d")]
    public void ProductionSampleModifiersUseMetalNamespaceDespiteActualBufferBiasLocal(string opcode, uint type, string helper)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow(opcode, type, stage: ShaderStage.Pixel);
        Assert.Contains("uint bias[", shader.Source); // The actual emitted collision.
        Assert.Contains("metal::" + helper + "(", shader.Source);
        Assert.DoesNotContain(", " + helper + "(", shader.Source);
    }

    [Theory]
    [InlineData("ImageSampleB", 9u)]
    [InlineData("ImageSampleL", 9u)]
    [InlineData("ImageSampleD", 9u)]
    [InlineData("ImageSampleD", 10u)]
    public void ActualTranslatedFragmentSamplingCompilesWithCollidingLocalHelperNames(string opcode, uint type)
    {
        var shader = Gen5ImageFixtures.CompileOrThrow(opcode, type, stage: ShaderStage.Pixel);
        Assert.Contains("uint bias[", shader.Source);
        // Bias is a real production local. Add the other same-scope names to
        // qualify namespace lookup independently of future emitter naming.
        var source = shader.Source.Replace("    uint bias[",
            "    uint level[1] = {}; uint gradient2d[1] = {}; uint gradient3d[1] = {};\n    uint bias[", StringComparison.Ordinal);
        Assert.NotEqual(shader.Source, source);
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") != "1")
        {
            output.WriteLine("Source-only fixture; no native compiler/device execution.");
            return;
        }
        Assert.True(MetalNative.IsAvailable, "Required native compilation needs a Metal device.");
        nint library = 0;
        try
        {
            Assert.True(MetalNative.TryCompileLibrary(source, out library, out var error), error + "\n" + source);
            Assert.NotEqual((nint)0, library);
            output.WriteLine($"Actual runtime Metal fragment compiler accepted translated {opcode} type={type}, production bias array and adversarial local level/gradient names; qualified sample modifiers.");
        }
        finally { if (library != 0) MetalNative.ReleaseCollisionLibrary(library); }
    }
}

internal static partial class MetalNative
{
    internal static void ReleaseCollisionLibrary(nint library) => SendVoid(library, Selector("release"));
}
