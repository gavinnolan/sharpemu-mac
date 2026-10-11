// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// RDNA2 reads every source operand of an SMEM instruction before it writes the first
// destination dword, so a multi-dword S_BUFFER_LOAD may name a destination range that
// overlaps its own SOFFSET register. Demon's Souls does exactly that in a per-light
// loop: `s_buffer_load_dwordx2 s[106:107], s[8:11], s106`. Re-reading the register file
// for each component would compute the second dword's address from the first dword's
// value, which is how the second component came back as garbage.
public sealed class Gen5MslScalarBufferLoadTests
{
    private const uint NullOperand = 125;
    private const uint DescriptorRegister = 8;

    [Fact]
    public void MultiDwordLoadReadsSoffsetBeforeWritingAnOverlappingDestination()
    {
        // s_buffer_load_dwordx2 s[12:13], s[8:11], s12 — SOFFSET is also destination 0.
        var source = Compile(destination: 12, dynamicOffsetRegister: 12);

        // The offset is snapshotted once, before the first component's store.
        Assert.Matches(@"uint t\d+ = s12;", source);

        // Neither component's address may be built from the register file, which the
        // first store clobbers.
        Assert.DoesNotContain("(s12 + 0u)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("(s12 + 4u)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiDwordLoadWithADisjointSoffsetStillAddressesBothComponents()
    {
        var source = Compile(destination: 12, dynamicOffsetRegister: 20);
        Assert.Matches(@"uint t\d+ = s20;", source);
        Assert.Contains("+ 0u) + bias[0])", source, StringComparison.Ordinal);
        Assert.Contains("+ 4u) + bias[0])", source, StringComparison.Ordinal);
    }

    private static string Compile(uint destination, uint dynamicOffsetRegister)
    {
        var program = new Gen5ShaderProgram(0x2000, [
            ScalarBufferLoad(0, DescriptorRegister, destination, 2, dynamicOffsetRegister),
            new Gen5ShaderInstruction(8, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null),
        ]);

        // A V# the materializer accepts: a base address, a record count and the
        // identity destination select, so the load resolves to a dense buffer binding.
        var userData = new uint[16];
        userData[DescriptorRegister] = 0x200000;
        userData[DescriptorRegister + 2] = 0x1000;
        userData[DescriptorRegister + 3] = DescriptorConstants.IdentityDestinationSelect;

        var request = Gen5ComputeFixtures.RequestOrThrow(
            program,
            ShaderStage.Compute,
            localSizeX: 1,
            runtimeInputs: new ResourceRuntimeInputs { UserData = userData });
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Source;
    }

    // s_buffer_load_dwordxN through the descriptor at resourceRegister. Copied from the
    // shared resource fixtures: this project depends only on the codegen under test.
    private static Gen5ShaderInstruction ScalarBufferLoad(
        uint pc, uint resourceRegister, uint destination, uint count, uint? dynamicOffsetRegister) =>
        new(
            pc,
            Gen5ShaderEncoding.Smem,
            count == 1 ? "SBufferLoadDword" : $"SBufferLoadDwordx{count}",
            [0u, 0u],
            [
                Gen5Operand.Scalar(resourceRegister),
                dynamicOffsetRegister is { } register
                    ? Gen5Operand.Scalar(register)
                    : Gen5Operand.Source(NullOperand),
            ],
            [.. Enumerable.Range((int)destination, (int)count).Select(index => Gen5Operand.Scalar((uint)index))],
            new Gen5ScalarMemoryControl(count, 0, dynamicOffsetRegister));
}
