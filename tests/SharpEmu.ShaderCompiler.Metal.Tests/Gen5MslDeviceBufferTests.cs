// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// A vector buffer access whose V# has no host binding - read from memory by s_load, or
// past the binding budget - or that sits in the dead tail of a fused shader. Poppy
// Playtime's vertex shader aborted the whole program with "missing buffer-memory
// binding" on the first of these.
public sealed class Gen5MslDeviceBufferTests
{
    private const uint NullOperand = 125;

    [Fact]
    public void ABufferLoadThroughADescriptorReadByAScalarLoadReadsItOnTheDevice()
    {
        var source = Compile(
            ReadFirstLane(0, 4, 0),
            MoveScalar(4, 5, 0),
            ScalarLoad(8, baseRegister: 4, destination: 8, count: 4),
            BufferLoad(16, resourceRegister: 8, dwords: 4),
            End(24));
        var withoutBufferLoad = Compile(
            ReadFirstLane(0, 4, 0),
            MoveScalar(4, 5, 0),
            ScalarLoad(8, baseRegister: 4, destination: 8, count: 4),
            End(16));

        // The four dwords of the buffer load come on top of the scalar load's own four.
        const string call = "sharpemu_load_device_dword(sharpemu_ranges";
        Assert.Equal(CountOccurrences(withoutBufferLoad, call) + 4, CountOccurrences(source, call));
    }

    [Fact]
    public void ABufferStoreThroughADescriptorReadByAScalarLoadIsBoundedAndMasked()
    {
        var source = Compile(
            ReadFirstLane(0, 4, 0),
            MoveScalar(4, 5, 0),
            ScalarLoad(8, baseRegister: 4, destination: 8, count: 4),
            BufferAccess(16, "BufferStoreDwordx2", resourceRegister: 8, dwords: 2),
            End(24));

        Assert.Equal(2, CountOccurrences(source, "sharpemu_store_device_dword(sharpemu_ranges"));
    }

    [Fact]
    public void ABufferAtomicThroughADeviceDescriptorIsRejectedExplicitly()
    {
        var program = new Gen5ShaderProgram(0x2000, [
            ReadFirstLane(0, 4, 0),
            MoveScalar(4, 5, 0),
            ScalarLoad(8, baseRegister: 4, destination: 8, count: 4),
            BufferAccess(16, "BufferAtomicAdd", resourceRegister: 8, dwords: 1),
            End(24),
        ]);
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Compute, localSizeX: 1);

        Assert.False(Gen5MslTranslator.TryCompileProgram(request, out _, out var error));
        Assert.Contains("BufferAtomicAdd", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ABufferLoadPastTheBindingBudgetCompiles()
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        for (uint index = 0; index <= ShaderResourceInfo.MaxBuffers; index++)
        {
            for (uint dword = 0; dword < 4; dword++)
            {
                instructions.Add(MoveScalar(pc, 8 + dword, index + dword + 0x100));
                pc += 8;
            }

            instructions.Add(BufferLoad(pc, resourceRegister: 8, dwords: 1));
            pc += 8;
        }

        instructions.Add(End(pc));
        var source = Compile([.. instructions]);

        Assert.DoesNotContain("missing buffer-memory binding", source, StringComparison.Ordinal);
    }

    [Fact]
    public void APlanningOnlyBufferLoadAfterAnIndirectJumpEmitsNothing()
    {
        var program = new Gen5ShaderProgram(0x2000, [
            MoveScalar(0, 12, 0),
            MoveScalar(8, 13, 0),
            new Gen5ShaderInstruction(16, Gen5ShaderEncoding.Sop1, "SSetpcB64", [0u], [Gen5Operand.Scalar(12)], [Gen5Operand.Scalar(12)], null),
            BufferLoad(20, resourceRegister: 100, dwords: 4),
            End(28),
        ]);
        var request = Gen5ComputeFixtures.RequestOrThrow(program, ShaderStage.Compute, localSizeX: 1);
        Assert.True(request.Memory.Find(20) is { PlanningOnly: true },
            string.Join(";", request.Memory.Entries.Select(m => $"pc={m.Pc} po={m.PlanningOnly} res={m.Resource}")));

        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Assert.DoesNotContain("sharpemu_load_device_dword(sharpemu_ranges", shader.Source, StringComparison.Ordinal);
    }

    private static string Compile(params Gen5ShaderInstruction[] instructions)
    {
        var request = Gen5ComputeFixtures.RequestOrThrow(
            new Gen5ShaderProgram(0x2000, instructions), ShaderStage.Compute, localSizeX: 1);
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);

        // Where a device exists, the emitted source must also be valid Metal.
        if (MetalNative.IsAvailable)
        {
            Assert.True(MetalNative.TryCompileLibrary(shader.Source, out _, out error), error);
        }

        return shader.Source;
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

    private static Gen5ShaderInstruction End(uint pc) =>
        new(pc, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null);

    private static Gen5Operand Operand(uint value) =>
        value <= 64 ? Gen5Operand.Source(128 + value) : new Gen5Operand(Gen5OperandKind.LiteralConstant, value);

    private static Gen5ShaderInstruction MoveScalar(uint pc, uint destination, uint value) =>
        new(pc, Gen5ShaderEncoding.Sop1, "SMovB32", [0u], [Operand(value)], [Gen5Operand.Scalar(destination)], null);

    private static Gen5ShaderInstruction ReadFirstLane(uint pc, uint scalarDestination, uint vectorSource) =>
        new(pc, Gen5ShaderEncoding.Vop1, "VReadfirstlaneB32", [0u], [Gen5Operand.Vector(vectorSource)], [Gen5Operand.Scalar(scalarDestination)], null);

    private static Gen5ShaderInstruction ScalarLoad(uint pc, uint baseRegister, uint destination, uint count) =>
        new(
            pc,
            Gen5ShaderEncoding.Smem,
            count == 1 ? "SLoadDword" : $"SLoadDwordx{count}",
            [0u, 0u],
            [Gen5Operand.Scalar(baseRegister), Gen5Operand.Source(NullOperand)],
            [.. Enumerable.Range((int)destination, (int)count).Select(index => Gen5Operand.Scalar((uint)index))],
            new Gen5ScalarMemoryControl(count, 0, null));

    private static Gen5ShaderInstruction BufferLoad(uint pc, uint resourceRegister, uint dwords) =>
        BufferAccess(pc, dwords == 1 ? "BufferLoadDword" : $"BufferLoadDwordx{dwords}", resourceRegister, dwords);

    private static Gen5ShaderInstruction BufferAccess(uint pc, string opcode, uint resourceRegister, uint dwords)
    {
        const uint vectorData = 4;
        var isStore = opcode.Contains("Store", StringComparison.Ordinal) || opcode.Contains("Atomic", StringComparison.Ordinal);
        var data = Enumerable.Range((int)vectorData, (int)dwords).Select(index => Gen5Operand.Vector((uint)index)).ToArray();
        return new Gen5ShaderInstruction(
            pc,
            Gen5ShaderEncoding.Mubuf,
            opcode,
            [0u, 0u],
            isStore
                ? [Gen5Operand.Vector(0), Gen5Operand.Scalar(resourceRegister), Gen5Operand.Source(NullOperand), .. data]
                : [Gen5Operand.Vector(0), Gen5Operand.Scalar(resourceRegister), Gen5Operand.Source(NullOperand)],
            isStore ? [] : data,
            new Gen5BufferMemoryControl(dwords, 0, vectorData, resourceRegister, 0, false, false, Glc: false, Slc: false));
    }
}
