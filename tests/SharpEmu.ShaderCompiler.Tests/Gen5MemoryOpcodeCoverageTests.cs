// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// The gfx10.3 MUBUF, FLAT/GLOBAL and DS opcodes and modifier bits, each checked against the
// words llvm-mc assembles for it (llvm-mc 21, -mcpu=gfx1030 -show-encoding).
public sealed class Gen5MemoryOpcodeCoverageTests
{
    private const ulong ShaderAddress = 0x1000;
    private const uint EndProgram = 0xBF810000;
    private const uint M0 = 124;

    [Theory]
    // asm, word 0, word 1, opcode, data registers the atomic reads (v4 on)
    [InlineData("buffer_atomic_swap v4, off, s[8:11], 0", 0xE0C00000u, 0x80020400u, "BufferAtomicSwap", 1u)]
    [InlineData("buffer_atomic_cmpswap v[4:5], off, s[8:11], 0", 0xE0C40000u, 0x80020400u, "BufferAtomicCmpswap", 2u)]
    [InlineData("buffer_atomic_add v4, off, s[8:11], 0", 0xE0C80000u, 0x80020400u, "BufferAtomicAdd", 1u)]
    [InlineData("buffer_atomic_sub v4, off, s[8:11], 0", 0xE0CC0000u, 0x80020400u, "BufferAtomicSub", 1u)]
    [InlineData("buffer_atomic_csub v4, off, s[8:11], 0 glc", 0xE0D04000u, 0x80020400u, "BufferAtomicCsub", 1u)]
    [InlineData("buffer_atomic_smin v4, off, s[8:11], 0", 0xE0D40000u, 0x80020400u, "BufferAtomicSmin", 1u)]
    [InlineData("buffer_atomic_umin v4, off, s[8:11], 0", 0xE0D80000u, 0x80020400u, "BufferAtomicUmin", 1u)]
    [InlineData("buffer_atomic_smax v4, off, s[8:11], 0", 0xE0DC0000u, 0x80020400u, "BufferAtomicSmax", 1u)]
    [InlineData("buffer_atomic_umax v4, off, s[8:11], 0", 0xE0E00000u, 0x80020400u, "BufferAtomicUmax", 1u)]
    [InlineData("buffer_atomic_and v4, off, s[8:11], 0", 0xE0E40000u, 0x80020400u, "BufferAtomicAnd", 1u)]
    [InlineData("buffer_atomic_or v4, off, s[8:11], 0", 0xE0E80000u, 0x80020400u, "BufferAtomicOr", 1u)]
    [InlineData("buffer_atomic_xor v4, off, s[8:11], 0", 0xE0EC0000u, 0x80020400u, "BufferAtomicXor", 1u)]
    [InlineData("buffer_atomic_inc v4, off, s[8:11], 0", 0xE0F00000u, 0x80020400u, "BufferAtomicInc", 1u)]
    [InlineData("buffer_atomic_dec v4, off, s[8:11], 0", 0xE0F40000u, 0x80020400u, "BufferAtomicDec", 1u)]
    [InlineData("buffer_atomic_fcmpswap v[4:5], off, s[8:11], 0", 0xE0F80000u, 0x80020400u, "BufferAtomicFcmpswap", 2u)]
    [InlineData("buffer_atomic_fmin v4, off, s[8:11], 0", 0xE0FC0000u, 0x80020400u, "BufferAtomicFmin", 1u)]
    [InlineData("buffer_atomic_fmax v4, off, s[8:11], 0", 0xE1000000u, 0x80020400u, "BufferAtomicFmax", 1u)]
    [InlineData("buffer_atomic_swap_x2 v[4:5], off, s[8:11], 0", 0xE1400000u, 0x80020400u, "BufferAtomicSwapX2", 2u)]
    [InlineData("buffer_atomic_cmpswap_x2 v[4:7], off, s[8:11], 0", 0xE1440000u, 0x80020400u, "BufferAtomicCmpswapX2", 4u)]
    [InlineData("buffer_atomic_add_x2 v[4:5], off, s[8:11], 0", 0xE1480000u, 0x80020400u, "BufferAtomicAddX2", 2u)]
    [InlineData("buffer_atomic_sub_x2 v[4:5], off, s[8:11], 0", 0xE14C0000u, 0x80020400u, "BufferAtomicSubX2", 2u)]
    [InlineData("buffer_atomic_smin_x2 v[4:5], off, s[8:11], 0", 0xE1540000u, 0x80020400u, "BufferAtomicSminX2", 2u)]
    [InlineData("buffer_atomic_umin_x2 v[4:5], off, s[8:11], 0", 0xE1580000u, 0x80020400u, "BufferAtomicUminX2", 2u)]
    [InlineData("buffer_atomic_smax_x2 v[4:5], off, s[8:11], 0", 0xE15C0000u, 0x80020400u, "BufferAtomicSmaxX2", 2u)]
    [InlineData("buffer_atomic_umax_x2 v[4:5], off, s[8:11], 0", 0xE1600000u, 0x80020400u, "BufferAtomicUmaxX2", 2u)]
    [InlineData("buffer_atomic_and_x2 v[4:5], off, s[8:11], 0", 0xE1640000u, 0x80020400u, "BufferAtomicAndX2", 2u)]
    [InlineData("buffer_atomic_or_x2 v[4:5], off, s[8:11], 0", 0xE1680000u, 0x80020400u, "BufferAtomicOrX2", 2u)]
    [InlineData("buffer_atomic_xor_x2 v[4:5], off, s[8:11], 0", 0xE16C0000u, 0x80020400u, "BufferAtomicXorX2", 2u)]
    [InlineData("buffer_atomic_inc_x2 v[4:5], off, s[8:11], 0", 0xE1700000u, 0x80020400u, "BufferAtomicIncX2", 2u)]
    [InlineData("buffer_atomic_dec_x2 v[4:5], off, s[8:11], 0", 0xE1740000u, 0x80020400u, "BufferAtomicDecX2", 2u)]
    [InlineData("buffer_atomic_fcmpswap_x2 v[4:7], off, s[8:11], 0", 0xE1780000u, 0x80020400u, "BufferAtomicFcmpswapX2", 4u)]
    [InlineData("buffer_atomic_fmin_x2 v[4:5], off, s[8:11], 0", 0xE17C0000u, 0x80020400u, "BufferAtomicFminX2", 2u)]
    [InlineData("buffer_atomic_fmax_x2 v[4:5], off, s[8:11], 0", 0xE1800000u, 0x80020400u, "BufferAtomicFmaxX2", 2u)]
    public void EveryMubufAtomicDecodesWithItsDataRegisters(string asm, uint word0, uint word1, string opcode, uint dataRegisters)
    {
        var instruction = DecodeFirst(asm, word0, word1);
        Assert.Equal(opcode, instruction.Opcode);
        var control = Assert.IsType<Gen5BufferMemoryControl>(instruction.Control);
        Assert.Equal(dataRegisters, control.DwordCount);
        Assert.Equal(4u, control.VectorData);
        Assert.Equal(8u, control.ScalarResource);
        Assert.Equal((word0 & (1u << 14)) != 0, control.Glc);
    }

    [Theory]
    [InlineData("buffer_gl0_inv", 0xE1C40000u, 0x00000000u, "BufferGl0Inv")]
    [InlineData("buffer_gl1_inv", 0xE1C80000u, 0x00000000u, "BufferGl1Inv")]
    public void CacheInvalidationsDecodeWithoutData(string asm, uint word0, uint word1, string opcode)
    {
        var instruction = DecodeFirst(asm, word0, word1);
        Assert.Equal(opcode, instruction.Opcode);
        Assert.Null(instruction.Control);
        Assert.Empty(instruction.Sources);
        Assert.Empty(instruction.Destinations);
    }

    [Theory]
    // asm, word 0, word 1, opcode, memory dwords, data registers read (v4 on), returns
    [InlineData("global_atomic_csub v1, v[2:3], v4, off glc", 0xDCD18000u, 0x017D0402u, "GlobalAtomicCsub", 1u, 1, 1)]
    [InlineData("global_atomic_fcmpswap v1, v[2:3], v[4:5], off glc", 0xDCF98000u, 0x017D0402u, "GlobalAtomicFcmpswap", 1u, 2, 1)]
    [InlineData("global_atomic_fcmpswap_x2 v[1:2], v[2:3], v[4:7], off glc", 0xDD798000u, 0x017D0402u, "GlobalAtomicFcmpswapX2", 2u, 4, 2)]
    [InlineData("flat_atomic_fcmpswap v1, v[2:3], v[4:5] glc", 0xDCF90000u, 0x017D0402u, "FlatAtomicFcmpswap", 1u, 2, 1)]
    [InlineData("flat_atomic_fcmpswap_x2 v[1:2], v[2:3], v[4:7] glc", 0xDD790000u, 0x017D0402u, "FlatAtomicFcmpswapX2", 2u, 4, 2)]
    public void FlatAndGlobalFloatCompareSwapAndClampedSubtractDecode(
        string asm, uint word0, uint word1, string opcode, uint dwords, int dataRegisters, int returned)
    {
        var instruction = DecodeFirst(asm, word0, word1);
        Assert.Equal(opcode, instruction.Opcode);
        var control = Assert.IsType<Gen5GlobalMemoryControl>(instruction.Control);
        Assert.Equal(dwords, control.DwordCount);
        Assert.True(control.Glc);
        Assert.Equal(dataRegisters, instruction.Sources.Count(source =>
            source.Kind == Gen5OperandKind.VectorRegister && source.Value >= 4));
        Assert.Equal(Enumerable.Range(1, returned).Select(register => Gen5Operand.Vector((uint)register)), instruction.Destinations);
    }

    [Fact]
    public void GlobalLoadDwordAddtidReadsOnlyTheScalarBase()
    {
        var instruction = DecodeFirst("global_load_dword_addtid v1, s[2:3] offset:16", 0xDC588010u, 0x01020000u);
        Assert.Equal("GlobalLoadDwordAddtid", instruction.Opcode);
        var control = Assert.IsType<Gen5GlobalMemoryControl>(instruction.Control);
        Assert.True(control.AddThreadId);
        Assert.Equal(2u, control.ScalarAddress);
        Assert.Equal(16, control.OffsetBytes);
        Assert.Equal(1u, control.DwordCount);
        Assert.Equal([Gen5Operand.Scalar(2)], instruction.Sources);
        Assert.Equal([Gen5Operand.Vector(1)], instruction.Destinations);
    }

    [Fact]
    public void GlobalStoreDwordAddtidReadsTheScalarBaseAndItsData()
    {
        var instruction = DecodeFirst("global_store_dword_addtid v1, s[2:3] offset:16", 0xDC5C8010u, 0x00020100u);
        Assert.Equal("GlobalStoreDwordAddtid", instruction.Opcode);
        Assert.True(Assert.IsType<Gen5GlobalMemoryControl>(instruction.Control).AddThreadId);
        Assert.Equal([Gen5Operand.Scalar(2), Gen5Operand.Vector(1)], instruction.Sources);
        Assert.Empty(instruction.Destinations);
    }

    [Theory]
    // ADDTID and CSUB exist only in the GLOBAL segment.
    [InlineData(0xDC580010u, 0x01020000u)] // FLAT op 0x16
    [InlineData(0xDC5C4010u, 0x00020100u)] // SCRATCH op 0x17
    [InlineData(0xDCD10000u, 0x017D0402u)] // FLAT op 0x34
    public void AddtidAndCsubOutsideTheGlobalSegmentAreRefused(uint word0, uint word1)
    {
        Assert.Contains("unknown-flat", DecodeError(word0, word1));
    }

    [Theory]
    // asm, word 0, word 1, opcode, sources, destinations
    [InlineData("ds_gws_init v1 offset:3 gds", 0xD8660003u, 0x00000001u, "DsGwsInit", "v1 s124", "")]
    [InlineData("ds_gws_sema_v offset:3 gds", 0xD86A0003u, 0x00000000u, "DsGwsSemaV", "s124", "")]
    [InlineData("ds_gws_sema_br v1 offset:3 gds", 0xD86E0003u, 0x00000001u, "DsGwsSemaBr", "v1 s124", "")]
    [InlineData("ds_gws_sema_p offset:3 gds", 0xD8720003u, 0x00000000u, "DsGwsSemaP", "s124", "")]
    [InlineData("ds_gws_barrier v1 offset:3 gds", 0xD8760003u, 0x00000001u, "DsGwsBarrier", "v1 s124", "")]
    [InlineData("ds_gws_sema_release_all offset:3 gds", 0xD8620003u, 0x00000000u, "DsGwsSemaReleaseAll", "s124", "")]
    [InlineData("ds_ordered_count v1, v2 offset:772 gds", 0xD8FE0304u, 0x01000002u, "DsOrderedCount", "v2 s124", "v1")]
    [InlineData("ds_condxchg32_rtn_b64 v[1:2], v3, v[4:5] offset:8", 0xD9F80008u, 0x01000403u, "DsCondxchg32RtnB64", "v3 v4 v5", "v1 v2")]
    [InlineData("ds_cmpst_f64 v1, v[2:3], v[4:5]", 0xD9440000u, 0x00040201u, "DsCmpstF64", "v1 v2 v3 v4 v5", "")]
    [InlineData("ds_min_f64 v1, v[2:3]", 0xD9480000u, 0x00000201u, "DsMinF64", "v1 v2 v3 v0 v1", "")]
    [InlineData("ds_max_f64 v1, v[2:3]", 0xD94C0000u, 0x00000201u, "DsMaxF64", "v1 v2 v3 v0 v1", "")]
    [InlineData("ds_cmpst_rtn_f64 v[6:7], v1, v[2:3], v[4:5]", 0xD9C40000u, 0x06040201u, "DsCmpstRtnF64", "v1 v2 v3 v4 v5", "v6 v7")]
    [InlineData("ds_min_rtn_f64 v[6:7], v1, v[2:3]", 0xD9C80000u, 0x06000201u, "DsMinRtnF64", "v1 v2 v3 v0 v1", "v6 v7")]
    [InlineData("ds_max_rtn_f64 v[6:7], v1, v[2:3]", 0xD9CC0000u, 0x06000201u, "DsMaxRtnF64", "v1 v2 v3 v0 v1", "v6 v7")]
    public void DataShareOpcodesDecodeTheirOperands(string asm, uint word0, uint word1, string opcode, string sources, string destinations)
    {
        var instruction = DecodeFirst(asm, word0, word1);
        Assert.Equal(opcode, instruction.Opcode);
        Assert.Equal(sources, Describe(instruction.Sources));
        Assert.Equal(destinations, Describe(instruction.Destinations));
    }

    [Fact]
    public void OrderedCountKeepsBothOffsetFieldsAndTheGdsBit()
    {
        var control = Assert.IsType<Gen5DataShareControl>(
            DecodeFirst("ds_ordered_count v1, v2 offset:772 gds", 0xD8FE0304u, 0x01000002u).Control);
        Assert.Equal(4u, control.Offset0);
        Assert.Equal(3u, control.Offset1);
        Assert.True(control.Gds);
    }

    [Theory]
    // The GWS and ordered-count opcodes exist only with the GDS bit.
    [InlineData(0xD8640003u, 0x00000001u)] // op 0x19 without gds
    [InlineData(0xD8FC0304u, 0x01000002u)] // op 0x3F without gds
    public void GdsOnlyOpcodesWithoutTheGdsBitAreRefused(uint word0, uint word1)
    {
        Assert.Contains("unknown-ds", DecodeError(word0, word1));
    }

    [Theory]
    // asm, word 0, word 1, opcode, data dwords; the status dword follows them
    [InlineData("buffer_load_dword v[1:2], off, s[8:11], 0 tfe", 0xE0300000u, 0x80820100u, "BufferLoadDword", 1u)]
    [InlineData("buffer_load_dwordx2 v[1:3], off, s[8:11], 0 tfe", 0xE0340000u, 0x80820100u, "BufferLoadDwordx2", 2u)]
    [InlineData("buffer_load_format_d16_xyzw v[1:3], off, s[8:11], 0 tfe", 0xE20C0000u, 0x80820100u, "BufferLoadFormatD16Xyzw", 2u)]
    [InlineData("buffer_load_ubyte v[1:2], off, s[8:11], 0 tfe", 0xE0200000u, 0x80820100u, "BufferLoadUbyte", 1u)]
    public void TfeAppendsTheStatusRegisterAfterTheData(string asm, uint word0, uint word1, string opcode, uint dwords)
    {
        var instruction = DecodeFirst(asm, word0, word1);
        Assert.Equal(opcode, instruction.Opcode);
        var control = Assert.IsType<Gen5BufferMemoryControl>(instruction.Control);
        Assert.True(control.Tfe);
        Assert.Equal(dwords, control.DwordCount);
        Assert.Equal(
            Enumerable.Range(1, (int)dwords + 1).Select(register => Gen5Operand.Vector((uint)register)),
            instruction.Destinations);
    }

    [Fact]
    public void TypedLoadTfeAppendsTheStatusRegister()
    {
        // tbuffer_load_format_xy v[1:3], off, s[8:11], 0 format:22 tfe. llvm-mc 21 does not
        // accept tfe on MTBUF, so the word is its tbuffer_load_format_xy with TFE (bit 55) set.
        var instruction = DecodeFirst(0xE8B10000u, 0x80820100u);
        Assert.Equal("TBufferLoadFormatXy", instruction.Opcode);
        Assert.True(Assert.IsType<Gen5BufferMemoryControl>(instruction.Control).Tfe);
        Assert.Equal([Gen5Operand.Vector(1), Gen5Operand.Vector(2), Gen5Operand.Vector(3)], instruction.Destinations);
    }

    [Theory]
    [InlineData(0xE0700000u, 0x80820100u)] // buffer_store_dword ... tfe
    [InlineData(0xE0C84000u, 0x80820100u)] // buffer_atomic_add ... glc tfe
    [InlineData(0xE8B50000u, 0x80820100u)] // tbuffer_store_format_xy ... tfe
    public void TfeOnAStoreOrAnAtomicIsRefused(uint word0, uint word1)
    {
        Assert.Contains("-tfe", DecodeError(word0, word1));
    }

    [Theory]
    // asm, word 0, word 1, opcode
    [InlineData("buffer_load_dword off, s[8:11], 0 offset:16 lds", 0xE0310010u, 0x80020000u, "BufferLoadDword")]
    [InlineData("buffer_load_ubyte v1, s[8:11], 0 offen offset:16 lds", 0xE0211010u, 0x80020001u, "BufferLoadUbyte")]
    [InlineData("buffer_load_format_x v1, s[8:11], 0 idxen lds", 0xE0012000u, 0x80020001u, "BufferLoadFormatX")]
    public void ALoadToLdsWritesNoRegisterAndReadsM0(string asm, uint word0, uint word1, string opcode)
    {
        var instruction = DecodeFirst(asm, word0, word1);
        Assert.Equal(opcode, instruction.Opcode);
        Assert.True(Assert.IsType<Gen5BufferMemoryControl>(instruction.Control).Lds);
        Assert.Empty(instruction.Destinations);
        Assert.Equal(Gen5Operand.Scalar(M0), instruction.Sources[^1]);
    }

    [Theory]
    [InlineData(0xE0350000u, 0x80020100u)] // buffer_load_dwordx2 ... lds: not a load-to-LDS opcode
    [InlineData(0xE0710000u, 0x80020100u)] // buffer_store_dword ... lds
    [InlineData(0xE0310000u, 0x80820100u)] // buffer_load_dword ... lds tfe
    public void LdsOnAnythingButTheSingleDwordLoadsIsRefused(uint word0, uint word1)
    {
        Assert.Contains("unsupported-mubuf-lds", DecodeError(word0, word1));
    }

    [Theory]
    [InlineData("buffer_load_dword v1, off, s[8:11], 0 dlc", 0xE0308000u, 0x80020100u, true, false)]
    [InlineData("buffer_load_dword v1, off, s[8:11], 0 glc slc dlc", 0xE030C000u, 0x80420100u, true, true)]
    [InlineData("tbuffer_load_format_x v1, off, s[8:11], 0 format:22 dlc", 0xE8B08000u, 0x80020100u, true, false)]
    [InlineData("buffer_load_dword v1, off, s[8:11], 0", 0xE0300000u, 0x80020100u, false, false)]
    public void BufferDlcIsRead(string asm, uint word0, uint word1, bool dlc, bool glc)
    {
        var control = Assert.IsType<Gen5BufferMemoryControl>(DecodeFirst(asm, word0, word1).Control);
        Assert.Equal(dlc, control.Dlc);
        Assert.Equal(glc, control.Glc);
    }

    [Theory]
    [InlineData("s_load_dword s1, s[2:3], 0x0 dlc", 0xF4004041u, 0xFA000000u, false)]
    [InlineData("s_buffer_load_dword s1, s[4:7], 0x0 glc dlc", 0xF4214042u, 0xFA000000u, true)]
    public void ScalarMemoryDlcIsRead(string asm, uint word0, uint word1, bool glc)
    {
        var control = Assert.IsType<Gen5ScalarMemoryControl>(DecodeFirst(asm, word0, word1).Control);
        Assert.True(control.Dlc);
        Assert.Equal(glc, control.Glc);
    }

    [Theory]
    [InlineData("global_load_dword v1, v[2:3], off dlc", 0xDC309000u, 0x017D0002u)]
    [InlineData("flat_load_dword v1, v[2:3] dlc", 0xDC301000u, 0x017D0002u)]
    public void FlatDlcIsRead(string asm, uint word0, uint word1)
    {
        var control = Assert.IsType<Gen5GlobalMemoryControl>(DecodeFirst(asm, word0, word1).Control);
        Assert.True(control.Dlc);
        Assert.Equal(0, control.OffsetBytes);
    }

    private static string Describe(IEnumerable<Gen5Operand> operands) =>
        string.Join(' ', operands.Select(operand => operand.Kind switch
        {
            Gen5OperandKind.VectorRegister => $"v{operand.Value}",
            Gen5OperandKind.ScalarRegister => $"s{operand.Value}",
            _ => $"{operand.Kind}:{operand.Value}",
        }));

    private static Gen5ShaderInstruction DecodeFirst(string asm, uint word0, uint word1)
    {
        _ = asm;
        return DecodeFirst(word0, word1);
    }

    private static Gen5ShaderInstruction DecodeFirst(uint word0, uint word1)
    {
        var context = WriteProgram([word0, word1, EndProgram]);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, ShaderAddress, out var program, out var error), error);
        return program.Instructions[0];
    }

    private static string DecodeError(uint word0, uint word1)
    {
        var context = WriteProgram([word0, word1, EndProgram]);
        Assert.False(Gen5ShaderTranslator.TryDecodeProgram(context, ShaderAddress, out _, out var error));
        return error;
    }

    private static CpuContext WriteProgram(uint[] words)
    {
        var memory = new TestCpuMemory(ShaderAddress, 0x100);
        Span<byte> shader = stackalloc byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(shader[(index * sizeof(uint))..], words[index]);
        }

        Assert.True(memory.TryWrite(ShaderAddress, shader));
        return new CpuContext(memory, Generation.Gen5);
    }

    private sealed class TestCpuMemory(ulong baseAddress, int size) : ICpuMemory
    {
        private readonly byte[] _storage = new byte[size];

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        private bool TryResolve(ulong virtualAddress, int length, out int offset)
        {
            offset = 0;
            if (virtualAddress < baseAddress || virtualAddress - baseAddress + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)(virtualAddress - baseAddress);
            return true;
        }
    }
}
