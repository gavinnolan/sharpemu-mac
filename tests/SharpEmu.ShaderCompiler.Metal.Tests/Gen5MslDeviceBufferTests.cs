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
    public void AWideScalarLoadFromADeviceAddressWalksThePageTableOnce()
    {
        // Demon's Souls' bokeh pass reads a 16-byte tap record per loop trip this way;
        // one page-table walk per dword was four 13-step searches per tap.
        var source = Compile(
            ReadFirstLane(0, 4, 0),
            MoveScalar(4, 5, 0),
            ScalarLoad(8, baseRegister: 4, destination: 8, count: 4),
            End(16));

        Assert.Equal(1, CountOccurrences(source, "sharpemu_resolve_device_span(sharpemu_ranges"));

        // Every device access shares the invocation's one-entry range cache, so the next
        // access to the same range skips the search entirely.
        Assert.Contains("Gen5RangeCache sharpemu_range_cache = { 0ul, 0ul, nullptr };", source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_fault_words, sharpemu_range_cache, ", source, StringComparison.Ordinal);
        for (var component = 0; component < 4; component++)
        {
            Assert.Matches($@"uint t\d+ = t\d+ \? t\d+\[{component}\] : sharpemu_load_device_dword\(sharpemu_ranges", source);
        }
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

    // A V# held in scalar registers decides swizzling when the access runs: SWIZZLE_ENABLE
    // (word 1 bit 31), INDEX_STRIDE (word 3 bits 21-22) and ADD_TID_ENABLE (word 3 bit 23)
    // are decoded from its words, every dword is placed through the helper the bound path
    // uses, and a swizzled V#'s reach is its swizzled footprint.
    [Fact]
    public void ABufferAccessThroughADeviceDescriptorPlacesEachDwordFromTheRuntimeSwizzleFields()
    {
        var source = Compile(
            ReadFirstLane(0, 4, 0),
            MoveScalar(4, 5, 0),
            ScalarLoad(8, baseRegister: 4, destination: 8, count: 4),
            BufferAccess(16, "BufferStoreDwordx4", resourceRegister: 8, dwords: 4),
            End(24));

        Assert.Equal(4, CountOccurrences(source, " + sharpemu_buffer_offset("));
        Assert.Contains("sharpemu_swizzled_footprint(", source, StringComparison.Ordinal);
        Assert.Matches(@"bool t\d+ = \(t\d+ >> 31u\) != 0u && t\d+ != 0u;", source);
        Assert.Matches(@"uint t\d+ = t\d+ \? \(8u << \(\(t\d+ >> 21u\) & 3u\)\) : 0u;", source);
        Assert.Matches(@"\(\(\(t\d+ >> 23u\) & 1u\) != 0u\) \? sharpemu_lane : 0u", source);
    }

    [Theory]
    [InlineData("BufferAtomicAdd", 1u, false, "atomic_fetch_add_explicit(")]
    [InlineData("BufferAtomicAdd", 1u, true, "atomic_fetch_add_explicit(")]
    [InlineData("BufferAtomicSub", 1u, true, "atomic_fetch_sub_explicit(")]
    [InlineData("BufferAtomicSmin", 1u, false, "atomic_fetch_min_explicit((device atomic_int*)")]
    [InlineData("BufferAtomicUmax", 1u, true, "atomic_fetch_max_explicit((device atomic_uint*)")]
    [InlineData("BufferAtomicAnd", 1u, false, "atomic_fetch_and_explicit(")]
    [InlineData("BufferAtomicXor", 1u, false, "atomic_fetch_xor_explicit(")]
    [InlineData("BufferAtomicSwap", 1u, true, "atomic_exchange_explicit(")]
    [InlineData("BufferAtomicCmpswap", 2u, true, "sharpemu_atomic_compare_swap(")]
    [InlineData("BufferAtomicInc", 1u, true, "sharpemu_atomic_step(")]
    [InlineData("BufferAtomicDec", 1u, false, "sharpemu_atomic_step(")]
    [InlineData("BufferAtomicFmin", 1u, true, "sharpemu_atomic_float_minmax(")]
    [InlineData("BufferAtomicFmax", 1u, false, "sharpemu_atomic_float_minmax(")]
    [InlineData("BufferAtomicSwapX2", 2u, true, "atomic_exchange_explicit(")]
    [InlineData("BufferAtomicOrX2", 2u, false, "atomic_fetch_or_explicit(")]
    public void ABufferAtomicThroughADeviceDescriptorRunsOnTheMappedWord(string opcode, uint dwords, bool glc, string operation)
    {
        Gen5ShaderInstruction[] Program(params Gen5ShaderInstruction[] access) => [
            ReadFirstLane(0, 12, 0),
            MoveScalar(4, 13, 0),
            MoveScalar(8, 14, 16),
            MoveScalar(12, 15, 0),
            ScalarBufferLoad(16, resourceRegister: 12, destination: 8, count: 4),
            .. access,
            End(32),
        ];
        var source = Compile(Program(BufferAccess(24, opcode, resourceRegister: 8, dwords: dwords, glc: glc)));
        var baseline = Compile(Program());

        // One page-table resolve per dword the operation touches: the X2 forms touch two.
        const string resolve = "sharpemu_resolve_device_address(sharpemu_ranges";
        var words = opcode.EndsWith("X2", StringComparison.Ordinal) ? 2 : 1;
        Assert.Equal(CountOccurrences(baseline, resolve) + words, CountOccurrences(source, resolve));
        Assert.Contains(operation, source, StringComparison.Ordinal);
        Assert.Contains("& 3ul) == 0ul", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ABufferAtomicThroughARuntimeDescriptorWithoutACandidateTableRunsOnTheDevice()
    {
        // A V# read by s_load whose candidates cannot be enumerated used to abort with
        // "runtime buffer descriptor has no candidate table"; its SGPRs hold the V#.
        var source = Compile(
            ReadFirstLane(0, 4, 0),
            MoveScalar(4, 5, 0),
            ScalarLoad(8, baseRegister: 4, destination: 8, count: 4),
            BufferAccess(16, "BufferAtomicAdd", resourceRegister: 8, dwords: 1, glc: true),
            End(24));

        Assert.Contains("atomic_fetch_add_explicit(", source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_resolve_device_address(sharpemu_ranges", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BufferLoadUbyte", false, "")]
    [InlineData("BufferLoadSbyte", true, "")]
    [InlineData("BufferLoadUshort", false, "")]
    [InlineData("BufferLoadSshort", true, "")]
    [InlineData("BufferLoadUbyteD16", false, "& 0xFFFF0000u) |")]
    [InlineData("BufferLoadUbyteD16Hi", false, "& 0x0000FFFFu) |")]
    [InlineData("BufferLoadSbyteD16", true, "& 0xFFFF0000u) |")]
    [InlineData("BufferLoadSbyteD16Hi", true, "& 0x0000FFFFu) |")]
    [InlineData("BufferLoadShortD16", false, "& 0xFFFF0000u) |")]
    [InlineData("BufferLoadShortD16Hi", false, "& 0x0000FFFFu) |")]
    public void ASubdwordBufferLoadThroughADeviceDescriptorExtendsAndMergesLikeTheBoundPath(string opcode, bool signed, string merge)
    {
        // GTA V's compute shader 0x4481C2A89BEA7646 aborted with "unsupported device
        // buffer descriptor opcode BufferLoadUshort".
        var source = Compile(
            ReadFirstLane(0, 4, 0),
            MoveScalar(4, 5, 0),
            ScalarLoad(8, baseRegister: 4, destination: 8, count: 4),
            BufferAccess(16, opcode, resourceRegister: 8, dwords: 1),
            End(24));

        Assert.Contains("sharpemu_load_device_unaligned(sharpemu_ranges", source, StringComparison.Ordinal);
        Assert.Equal(signed, source.Contains("extract_bits(as_type<int>(", StringComparison.Ordinal));
        if (merge.Length != 0)
        {
            Assert.Contains(merge, source, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("BufferLoadDwordx3", 3u, "sharpemu_load_device_dword(sharpemu_ranges")]
    [InlineData("BufferStoreDwordx3", 3u, "sharpemu_store_device_dword(sharpemu_ranges")]
    [InlineData("BufferStoreDwordx4", 4u, "sharpemu_store_device_dword(sharpemu_ranges")]
    [InlineData("BufferStoreByte", 1u, "sharpemu_store_device_bytes(sharpemu_ranges")]
    [InlineData("BufferStoreShortD16Hi", 1u, "sharpemu_store_device_bytes(sharpemu_ranges")]
    public void ARawBufferAccessThroughADeviceDescriptorTouchesEachDword(string opcode, uint dwords, string call)
    {
        Gen5ShaderInstruction[] Program(params Gen5ShaderInstruction[] access) => [
            ReadFirstLane(0, 4, 0),
            MoveScalar(4, 5, 0),
            ScalarLoad(8, baseRegister: 4, destination: 8, count: 4),
            .. access,
            End(24),
        ];
        var source = Compile(Program(BufferAccess(16, opcode, resourceRegister: 8, dwords: dwords)));
        var baseline = Compile(Program());

        var expected = call.Contains("bytes", StringComparison.Ordinal) ? 1 : (int)dwords;
        Assert.Equal(CountOccurrences(baseline, call) + expected, CountOccurrences(source, call));
    }

    [Fact]
    public void AFormattedBufferLoadThroughADeviceDescriptorDecodesItsWord3OnTheDevice()
    {
        // GTA V's compute shader 0xF1D19431555095C0 aborted with "device buffer descriptor
        // does not support the formatted access BufferLoadFormatXyz".
        var source = Compile(
            ReadFirstLane(0, 12, 0),
            MoveScalar(4, 13, 0),
            MoveScalar(8, 14, 16),
            MoveScalar(12, 15, 0),
            ScalarBufferLoad(16, resourceRegister: 12, destination: 8, count: 4),
            BufferAccess(24, "BufferLoadFormatXyz", resourceRegister: 8, dwords: 3),
            End(32));

        // Format and dst_sel come from the V#'s own word 3 in s11, decoded by the prelude
        // table, and each component's bytes are read through the page table.
        Assert.Matches(@"uint t\d+ = s11;", source);
        Assert.Contains("sharpemu_gfx10_formats[(", source, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(source, "sharpemu_load_device_unaligned(sharpemu_ranges"));
    }

    [Theory]
    [InlineData("TBufferLoadFormatX", 1u, 22u)]
    [InlineData("TBufferLoadFormatXyzw", 4u, 56u)]
    [InlineData("TBufferLoadFormatXyzw", 4u, 77u)]
    public void ATypedBufferLoadThroughADeviceDescriptorConvertsWithItsOwnFormat(string opcode, uint dwords, uint format)
    {
        var source = Compile(
            ReadFirstLane(0, 12, 0),
            MoveScalar(4, 13, 0),
            MoveScalar(8, 14, 16),
            MoveScalar(12, 15, 0),
            ScalarBufferLoad(16, resourceRegister: 12, destination: 8, count: 4),
            BufferAccess(24, opcode, resourceRegister: 8, dwords: dwords, typedFormat: format),
            End(32));

        Assert.Contains("sharpemu_format_convert(", source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_load_device_unaligned(sharpemu_ranges", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BufferStoreFormatX", 1u)]
    [InlineData("BufferStoreFormatXy", 2u)]
    [InlineData("BufferStoreFormatXyzw", 4u)]
    public void AFormattedBufferStoreThroughADeviceDescriptorEncodesWithItsWord3Format(string opcode, uint dwords)
    {
        var source = Compile(
            ReadFirstLane(0, 12, 0),
            MoveScalar(4, 13, 0),
            MoveScalar(8, 14, 16),
            MoveScalar(12, 15, 0),
            ScalarBufferLoad(16, resourceRegister: 12, destination: 8, count: 4),
            BufferAccess(24, opcode, resourceRegister: 8, dwords: dwords),
            End(32));

        // The format comes from the V#'s word 3 in s11; each transferred component is
        // encoded and merged into device memory through the page table.
        Assert.Matches(@"uint t\d+ = s11;", source);
        Assert.Contains("sharpemu_gfx10_formats[((", source, StringComparison.Ordinal);
        Assert.Equal((int)dwords, CountOccurrences(source, "sharpemu_format_encode(v"));
        // Each component merges into the one or two dwords its bits touch.
        Assert.Equal(2 * (int)dwords, CountOccurrences(source, "sharpemu_store_device_masked_word(sharpemu_ranges"));
    }

    [Theory]
    [InlineData("TBufferStoreFormatX", 1u, 22u)]
    [InlineData("TBufferStoreFormatXy", 2u, 56u)]
    [InlineData("TBufferStoreFormatXyzw", 4u, 56u)]
    [InlineData("TBufferStoreFormatXyzw", 4u, 77u)]
    public void ATypedBufferStoreThroughADeviceDescriptorEncodesWithItsOwnFormat(string opcode, uint dwords, uint format)
    {
        var source = Compile(
            ReadFirstLane(0, 12, 0),
            MoveScalar(4, 13, 0),
            MoveScalar(8, 14, 16),
            MoveScalar(12, 15, 0),
            ScalarBufferLoad(16, resourceRegister: 12, destination: 8, count: 4),
            BufferAccess(24, opcode, resourceRegister: 8, dwords: dwords, typedFormat: format),
            End(32));

        Assert.Contains($"sharpemu_gfx10_formats[{format}u]", source, StringComparison.Ordinal);
        Assert.Equal(2 * (int)dwords, CountOccurrences(source, "sharpemu_store_device_masked_word(sharpemu_ranges"));
    }

    [Theory]
    // opcode, registers, components, typed format (null = the V#'s word 3)
    [InlineData("BufferLoadFormatD16X", 1u, 1, null)]
    [InlineData("BufferLoadFormatD16Xy", 1u, 2, null)]
    [InlineData("BufferLoadFormatD16Xyz", 2u, 3, null)]
    [InlineData("BufferLoadFormatD16Xyzw", 2u, 4, null)]
    [InlineData("BufferLoadFormatD16HiX", 1u, 1, null)]
    [InlineData("TBufferLoadFormatD16Xyzw", 2u, 4, 56u)]
    public void AD16FormattedLoadThroughADeviceDescriptorPacksHalves(string opcode, uint registers, int components, uint? format)
    {
        var source = Compile(
            ReadFirstLane(0, 12, 0),
            MoveScalar(4, 13, 0),
            MoveScalar(8, 14, 16),
            MoveScalar(12, 15, 0),
            ScalarBufferLoad(16, resourceRegister: 12, destination: 8, count: 4),
            BufferAccess(24, opcode, resourceRegister: 8, dwords: registers, typedFormat: format),
            End(32));

        // The V#'s dst_sel picks among the four decoded channels; each transferred component
        // narrows to 16 bits and lands in its register half.
        Assert.Equal(4, CountOccurrences(source, "sharpemu_load_device_unaligned(sharpemu_ranges"));
        Assert.Equal(components, CountOccurrences(source, "= sharpemu_format_d16("));
    }

    [Theory]
    [InlineData("BufferStoreFormatD16X", 1u, 1, null)]
    [InlineData("BufferStoreFormatD16Xyzw", 2u, 4, null)]
    [InlineData("BufferStoreFormatD16HiX", 1u, 1, null)]
    [InlineData("TBufferStoreFormatD16Xy", 1u, 2, 56u)]
    public void AD16FormattedStoreThroughADeviceDescriptorWidensEachHalf(string opcode, uint registers, int components, uint? format)
    {
        var source = Compile(
            ReadFirstLane(0, 12, 0),
            MoveScalar(4, 13, 0),
            MoveScalar(8, 14, 16),
            MoveScalar(12, 15, 0),
            ScalarBufferLoad(16, resourceRegister: 12, destination: 8, count: 4),
            BufferAccess(24, opcode, resourceRegister: 8, dwords: registers, typedFormat: format),
            End(32));

        Assert.Equal(components, CountOccurrences(source, "sharpemu_format_d16_expand((v"));
        Assert.Equal(2 * components, CountOccurrences(source, "sharpemu_store_device_masked_word(sharpemu_ranges"));
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

    // s_buffer_load_dwordxN through the descriptor at resourceRegister. A V# it reads
    // through a device-only descriptor has no host binding either.
    private static Gen5ShaderInstruction ScalarBufferLoad(uint pc, uint resourceRegister, uint destination, uint count) =>
        new(
            pc,
            Gen5ShaderEncoding.Smem,
            $"SBufferLoadDwordx{count}",
            [0u, 0u],
            [Gen5Operand.Scalar(resourceRegister), Gen5Operand.Source(NullOperand)],
            [.. Enumerable.Range((int)destination, (int)count).Select(index => Gen5Operand.Scalar((uint)index))],
            new Gen5ScalarMemoryControl(count, 0, null));

    private static Gen5ShaderInstruction BufferLoad(uint pc, uint resourceRegister, uint dwords) =>
        BufferAccess(pc, dwords == 1 ? "BufferLoadDword" : $"BufferLoadDwordx{dwords}", resourceRegister, dwords);

    private static Gen5ShaderInstruction BufferAccess(uint pc, string opcode, uint resourceRegister, uint dwords, uint? typedFormat = null, bool glc = false)
    {
        const uint vectorData = 4;
        var isStore = opcode.Contains("Store", StringComparison.Ordinal) || opcode.Contains("Atomic", StringComparison.Ordinal);
        var data = Enumerable.Range((int)vectorData, (int)dwords).Select(index => Gen5Operand.Vector((uint)index)).ToArray();
        // A returning (GLC) atomic writes its pre-operation value back over VDATA.
        var returned = glc && opcode.Contains("Atomic", StringComparison.Ordinal);
        return new Gen5ShaderInstruction(
            pc,
            typedFormat is null ? Gen5ShaderEncoding.Mubuf : Gen5ShaderEncoding.Mtbuf,
            opcode,
            [0u, 0u],
            isStore
                ? [Gen5Operand.Vector(0), Gen5Operand.Scalar(resourceRegister), Gen5Operand.Source(NullOperand), .. data]
                : [Gen5Operand.Vector(0), Gen5Operand.Scalar(resourceRegister), Gen5Operand.Source(NullOperand)],
            isStore ? (returned ? data : []) : data,
            new Gen5BufferMemoryControl(
                dwords, 0, vectorData, resourceRegister, 0, false, false, Glc: glc, Slc: false,
                Typed: typedFormat is not null, TypedFormat: typedFormat ?? 0));
    }
}
