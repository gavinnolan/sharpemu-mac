// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// A scalar load whose V# the draw cannot evaluate - assembled on the device, or read out
// of a resource table when the shader runs - has no host buffer binding. The load still
// has to happen: the descriptor's own registers hold the base address and the records it
// reaches, which is how the hardware reads it, so the load goes through the
// device-address page table bounded by that descriptor. SILENT HILL The Short Message
// compiles a vertex shader of exactly this shape (s_buffer_load_dwordx16 through a
// device-assembled descriptor) as its first gameplay scene loads, and the Metal backend
// used to refuse the whole program over it.
public sealed class Gen5MslRuntimeScalarBufferTests
{
    private const uint NullOperand = 125;
    private const uint DescriptorRegister = 12;

    [Fact]
    public void ALoadThroughADeviceDescriptorReadsItThroughTheDeviceAddressTable()
    {
        var source = CompileOrThrow(count: 1);

        // The descriptor's own registers are the base address: its low dword, then the
        // low sixteen bits of the second dword.
        Assert.Matches(@"ulong t\d+ = \(\(ulong\)s12 \| \(\(ulong\)\(t\d+ & 0xFFFFu\) << 32\)\);", source);

        // Stride and record count bound it, and the read goes through the page table.
        Assert.Matches(@"uint t\d+ = \(\(t\d+ >> 16u\) & 0x3FFFu\);", source);
        Assert.Contains("sharpemu_load_device_dword(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AWideLoadThroughADeviceDescriptorReadsEveryComponent()
    {
        // The shape that used to fail outright: s_buffer_load_dwordx16 through a
        // descriptor the draw cannot evaluate.
        var source = CompileOrThrow(count: 16);

        // Counted by the call form the translator emits, so the prelude's own uses
        // of the helper are not mistaken for the loads under test.
        Assert.Equal(16, CountOccurrences(source, "sharpemu_load_device_dword(sharpemu_ranges"));
        for (var component = 0u; component < 16; component++)
        {
            // The offsets are the translator's own literal spelling: small values decimal,
            // the rest hexadecimal.
            var offset = component * sizeof(uint);
            var literal = offset <= 9 ? $"{offset}u" : $"0x{offset:X}u";
            Assert.Contains($"= (0u + {literal});", source, StringComparison.Ordinal);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // The descriptor base comes from a lane value, so the host cannot bind the buffer and
    // the device has to read through the registers.
    private static string CompileOrThrow(uint count)
    {
        var program = new Gen5ShaderProgram(0x2000, [
            ReadFirstLane(0, DescriptorRegister, 0),
            MoveScalar(4, DescriptorRegister + 1, 0),
            MoveScalar(8, DescriptorRegister + 2, 16),
            MoveScalar(12, DescriptorRegister + 3, 0),
            ScalarBufferLoad(16, DescriptorRegister, 20, count),
            new Gen5ShaderInstruction(24, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null),
        ]);

        var request = Gen5ComputeFixtures.RequestOrThrow(
            program,
            ShaderStage.Compute,
            localSizeX: 1,
            runtimeInputs: new ResourceRuntimeInputs { UserData = new uint[16] });
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Source;
    }

    private static Gen5ShaderInstruction ReadFirstLane(uint pc, uint scalarDestination, uint vectorSource) =>
        new(
            pc,
            Gen5ShaderEncoding.Vop1,
            "VReadfirstlaneB32",
            [0u],
            [Gen5Operand.Vector(vectorSource)],
            [Gen5Operand.Scalar(scalarDestination)],
            null);

    private static Gen5ShaderInstruction MoveScalar(uint pc, uint destination, uint value) =>
        new(
            pc,
            Gen5ShaderEncoding.Sop1,
            "SMovB32",
            [0u],
            [Gen5Operand.Source(128 + value)],
            [Gen5Operand.Scalar(destination)],
            null);

    // s_buffer_load_dwordxN through the descriptor at resourceRegister.
    private static Gen5ShaderInstruction ScalarBufferLoad(uint pc, uint resourceRegister, uint destination, uint count) =>
        new(
            pc,
            Gen5ShaderEncoding.Smem,
            count == 1 ? "SBufferLoadDword" : $"SBufferLoadDwordx{count}",
            [0u, 0u],
            [Gen5Operand.Scalar(resourceRegister), Gen5Operand.Source(NullOperand)],
            [.. Enumerable.Range((int)destination, (int)count).Select(index => Gen5Operand.Scalar((uint)index))],
            new Gen5ScalarMemoryControl(count, 0, null));
}
