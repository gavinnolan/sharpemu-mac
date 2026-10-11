// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// The dispatcher's step cap keeps a mistranslated loop-exit condition from wedging the
// single Metal queue, and the step-report word is how the host learns it happened: the
// last field of every stage argument buffer points at one word per shader hash, and the
// dispatcher increments it on the way out. Without it a capped shader is indistinguishable
// from a slow one.
public sealed class Gen5MslStepCapReportTests(ITestOutputHelper output)
{
    // s_branch -1 jumps to itself: the dispatcher can only leave through the cap.
    private static readonly Gen5ComputeFixture SelfBranch = new("step-cap", [0xBF82FFFFu, 0xBF810000u], 0, 0);

    // One pass through straight-line ALU; the cap is never reached.
    private static readonly Gen5ComputeFixture Terminating = Gen5ComputeFixtures.Fmac;

    [Fact]
    public void TheDispatcherReportsEveryInvocationThatRanOutOfSteps()
    {
        if (SkipWithoutMetalDevice()) return;

        const uint threads = 8;
        var hits = Execute(SelfBranch, threads);
        Assert.Equal(threads, hits);
    }

    [Fact]
    public void AProgramThatTerminatesReportsNothing()
    {
        if (SkipWithoutMetalDevice()) return;

        Assert.Equal(0u, Execute(Terminating, threads: 4));
    }

    private uint Execute(Gen5ComputeFixture fixture, uint threads)
    {
        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture, localSizeX: threads);
        Assert.True(
            MetalNative.TryCompileLibrary(shader.Source, out var library, out var compileError),
            $"[{fixture.Name}] {compileError}");
        Assert.True(
            MetalNative.TryExecuteWithArgumentBuffer(
                library,
                shader.EntryPoint,
                new byte[32],
                new byte[Resources.PushData.ByteSize],
                shader.ArgumentLayout!,
                threads,
                out _,
                out var hits,
                out var runError),
            $"[{fixture.Name}] {runError}");
        return hits;
    }

    private bool SkipWithoutMetalDevice()
    {
        if (MetalNative.IsAvailable) return false;
        Assert.False(
            OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1",
            "The required gate cannot run the step-cap report test without a Metal device.");
        output.WriteLine("No Metal device on this host; the step-cap report test skipped.");
        return true;
    }
}
