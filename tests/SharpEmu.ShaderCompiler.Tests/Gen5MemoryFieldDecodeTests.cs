// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// gfx10 FLAT/GLOBAL/SCRATCH and MUBUF field layouts (LLVM FLATInstructions.td / BUFInstructions.td).
public sealed class Gen5MemoryFieldDecodeTests
{
    private const ulong ShaderAddress = 0x1000;
    private const uint EndProgram = 0xBF810000;

    [Fact]
    public void GlobalLoadDword_WithDlcAndNoOffset_KeepsOpcodeAndZeroOffset()
    {
        var instruction = DecodeFirst(Flat(segment: 2, opcode: 0x0C, offset: 0, dlc: true), FlatWord1(vdst: 1));

        Assert.Equal("GlobalLoadDword", instruction.Opcode);
        Assert.Equal(0, Assert.IsType<Gen5GlobalMemoryControl>(instruction.Control).OffsetBytes);
    }

    [Fact]
    public void GlobalLoad_NegativeOffset_IsTwelveBitSigned()
    {
        var instruction = DecodeFirst(Flat(segment: 2, opcode: 0x0C, offset: -4), FlatWord1(vdst: 1));

        Assert.Equal("GlobalLoadDword", instruction.Opcode);
        Assert.Equal(-4, Assert.IsType<Gen5GlobalMemoryControl>(instruction.Control).OffsetBytes);
    }

    [Fact]
    public void GlobalLoad_NegativeOffsetWithDlc_DoesNotLetDlcAlterTheOffset()
    {
        var instruction = DecodeFirst(Flat(segment: 2, opcode: 0x0C, offset: -2048, dlc: true), FlatWord1(vdst: 1));

        Assert.Equal(-2048, Assert.IsType<Gen5GlobalMemoryControl>(instruction.Control).OffsetBytes);
    }

    [Fact]
    public void GlobalLoadUbyte_OddOffset_DecodesAsTheSameOpcode()
    {
        var instruction = DecodeFirst(Flat(segment: 2, opcode: 0x08, offset: 1), FlatWord1(vdst: 1));

        Assert.Equal("GlobalLoadUbyte", instruction.Opcode);
        Assert.Equal(1, Assert.IsType<Gen5GlobalMemoryControl>(instruction.Control).OffsetBytes);
    }

    [Fact]
    public void ScratchLoad_NegativeOffset_IsTwelveBitSigned()
    {
        var instruction = DecodeFirst(Flat(segment: 1, opcode: 0x0C, offset: -8), FlatWord1(vdst: 1));

        Assert.Equal("ScratchLoadDword", instruction.Opcode);
        Assert.Equal(-8, Assert.IsType<Gen5GlobalMemoryControl>(instruction.Control).OffsetBytes);
    }

    [Fact]
    public void FlatLoad_OffsetIsPositive()
    {
        // FLAT segment offsets "must be positive": bit 11 is not a sign bit.
        var instruction = DecodeFirst(Flat(segment: 0, opcode: 0x0C, offset: 0x7FF, dlc: true), FlatWord1(vdst: 1));

        Assert.Equal("FlatLoadDword", instruction.Opcode);
        Assert.Equal(0x7FF, Assert.IsType<Gen5GlobalMemoryControl>(instruction.Control).OffsetBytes);
    }

    [Fact]
    public void MubufLoadUbyte_OddInstOffset_DecodesAsTheSameOpcode()
    {
        var instruction = DecodeFirst(Mubuf(opcode: 0x08, offset: 1), MubufWord1());

        Assert.Equal("BufferLoadUbyte", instruction.Opcode);
        Assert.Equal(1, Assert.IsType<Gen5BufferMemoryControl>(instruction.Control).OffsetBytes);
    }

    [Theory]
    [InlineData(0x80u, "BufferLoadFormatD16X")]
    [InlineData(0x81u, "BufferLoadFormatD16Xy")]
    [InlineData(0x82u, "BufferLoadFormatD16Xyz")]
    [InlineData(0x83u, "BufferLoadFormatD16Xyzw")]
    [InlineData(0x84u, "BufferStoreFormatD16X")]
    [InlineData(0x87u, "BufferStoreFormatD16Xyzw")]
    public void MubufOpcodeBit7_ComesFromWordBit25(uint opcode, string expected)
    {
        // The offset is odd on purpose: bit 0 must not be mistaken for opcode bit 7.
        var instruction = DecodeFirst(Mubuf(opcode, offset: 3), MubufWord1());

        Assert.Equal(expected, instruction.Opcode);
        Assert.Equal(3, Assert.IsType<Gen5BufferMemoryControl>(instruction.Control).OffsetBytes);
    }

    [Fact]
    public void Mubuf_OpcodeBelow0x80_WithBit25Clear_IsUnchanged()
    {
        Assert.Equal("BufferLoadFormatXy", DecodeFirst(Mubuf(opcode: 0x01, offset: 1), MubufWord1()).Opcode);
        Assert.Equal("BufferStoreDword", DecodeFirst(Mubuf(opcode: 0x1C, offset: 0), MubufWord1()).Opcode);
    }

    [Fact]
    public void Mubuf_Op3E_IsRefusedAsFloatCompareSwap()
    {
        Assert.Contains("fcmpswap", DecodeError(Mubuf(opcode: 0x3E, offset: 0), MubufWord1()));
    }

    [Fact]
    public void Mubuf_Tfe_IsRefused()
    {
        Assert.Contains("unsupported-mubuf-tfe", DecodeError(Mubuf(opcode: 0x0C, offset: 0), MubufWord1(tfe: true)));
    }

    [Fact]
    public void Mubuf_Lds_IsRefused()
    {
        Assert.Contains("unsupported-mubuf-lds", DecodeError(Mubuf(opcode: 0x0C, offset: 0, lds: true), MubufWord1()));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    public void Mimg_TfeAndLwe_Decode(int bit)
    {
        // image_sample_lz v[4:8], v0, s[8:15], s[0:3] dmask:0xF dim:2D, plus tfe or lwe: the
        // status dword is only written on a PRT NACK or an LOD warning, so both decode.
        var word0 = 0xF000_0000u | (0x27u << 18) | (1u << bit) | (0xFu << 8) | (1u << 3);
        var image = Assert.IsType<Gen5ImageControl>(DecodeFirst(word0, 0x0002_0400u).Control);
        Assert.Equal(bit == 16, image.Tfe);
        Assert.Equal(bit == 17, image.Lwe);
    }

    [Fact]
    public void Mimg_WithoutTfeOrLwe_StillDecodes()
    {
        var word0 = 0xF000_0000u | (0x27u << 18) | (0xFu << 8) | (1u << 3);
        Assert.Equal("ImageSampleLz", DecodeFirst(word0, 0x0002_0400u).Opcode);
    }

    [Theory]
    // D16 X/XY pack into one register, XYZ/XYZW into two; the Hi form uses one register's high half.
    [InlineData(0x80u, "BufferLoadFormatD16X", 1u)]
    [InlineData(0x81u, "BufferLoadFormatD16Xy", 1u)]
    [InlineData(0x82u, "BufferLoadFormatD16Xyz", 2u)]
    [InlineData(0x83u, "BufferLoadFormatD16Xyzw", 2u)]
    [InlineData(0x26u, "BufferLoadFormatD16HiX", 1u)]
    [InlineData(0x27u, "BufferStoreFormatD16HiX", 1u)]
    [InlineData(0x86u, "BufferStoreFormatD16Xyz", 2u)]
    public void MubufFormatD16_TransfersPackedRegisters(uint opcode, string expected, uint registers)
    {
        var instruction = DecodeFirst(Mubuf(opcode, offset: 0), MubufWord1());

        Assert.Equal(expected, instruction.Opcode);
        Assert.Equal(registers, Assert.IsType<Gen5BufferMemoryControl>(instruction.Control).DwordCount);
    }

    [Theory]
    [InlineData(0x24u, "SMemtime")]
    [InlineData(0x25u, "SMemrealtime")]
    public void SmemCounter_WritesTheSdataPair(uint opcode, string expected)
    {
        // s_memtime s[4:5]
        var instruction = DecodeFirst(Smem(opcode, sdata: 4, sbase: 0), 0u);

        Assert.Equal(expected, instruction.Opcode);
        Assert.Empty(instruction.Sources);
        Assert.Equal([Gen5Operand.Scalar(4), Gen5Operand.Scalar(5)], instruction.Destinations);
    }

    [Theory]
    [InlineData(0x1Fu, "SGl1Inv")]
    [InlineData(0x20u, "SDcacheInv")]
    [InlineData(0x21u, "SDcacheWb")]
    public void SmemCacheMaintenance_DecodesWithoutRegisters(uint opcode, string expected)
    {
        var instruction = DecodeFirst(Smem(opcode, sdata: 0, sbase: 0), 0u);

        Assert.Equal(expected, instruction.Opcode);
        Assert.Empty(instruction.Sources);
        Assert.Empty(instruction.Destinations);
    }

    [Fact]
    public void Ds_Op1E_IsWriteB8()
    {
        // The word GTA V's compute shader 0x5B10BEC40C2E4C27 stopped on: ds_write_b8 v1, v2.
        var instruction = DecodeFirst(0xD8780200u, 0x0000_0201u);

        Assert.Equal("DsWriteB8", instruction.Opcode);
        Assert.Equal([Gen5Operand.Vector(1), Gen5Operand.Vector(2)], instruction.Sources);
        Assert.Empty(instruction.Destinations);
        Assert.Equal(2u, Assert.IsType<Gen5DataShareControl>(instruction.Control).Offset1);
    }

    [Theory]
    // opcode, name, sources (address + data), destinations
    [InlineData(0x02u, "DsRsubU32", 2, 0)]
    [InlineData(0x0Cu, "DsMskorB32", 3, 0)]
    [InlineData(0x11u, "DsCmpstF32", 3, 0)]
    [InlineData(0x12u, "DsMinF32", 3, 0)]
    [InlineData(0x15u, "DsAddF32", 2, 0)]
    [InlineData(0x1Fu, "DsWriteB16", 2, 0)]
    [InlineData(0x2Eu, "DsWrxchg2RtnB32", 3, 2)]
    [InlineData(0x34u, "DsWrapRtnB32", 3, 1)]
    [InlineData(0x3Au, "DsReadU8", 1, 1)]
    [InlineData(0x3Bu, "DsReadI16", 1, 1)]
    [InlineData(0x3Cu, "DsReadU16", 1, 1)]
    [InlineData(0x40u, "DsAddU64", 3, 0)]
    [InlineData(0x50u, "DsCmpstB64", 5, 0)]
    [InlineData(0x55u, "DsAddRtnF32", 2, 1)]
    [InlineData(0x60u, "DsAddRtnU64", 3, 2)]
    [InlineData(0x6Eu, "DsWrxchg2RtnB64", 5, 4)]
    [InlineData(0x78u, "DsRead2St64B64", 1, 4)]
    [InlineData(0xA0u, "DsWriteB8D16Hi", 2, 0)]
    [InlineData(0xA1u, "DsWriteB16D16Hi", 2, 0)]
    [InlineData(0xA3u, "DsReadU8D16Hi", 2, 1)]
    [InlineData(0xA6u, "DsReadU16D16", 2, 1)]
    [InlineData(0xB2u, "DsPermuteB32", 2, 1)]
    public void Ds_Gfx10Opcode_DecodesItsOperands(uint opcode, string expected, int sources, int destinations)
    {
        // ds_* v8, v1, v2, v4 (address, data0, data1, vdst)
        var instruction = DecodeFirst(0xD800_0000u | opcode << 18, 0x0804_0201u);

        Assert.Equal(expected, instruction.Opcode);
        Assert.Equal(sources, instruction.Sources.Count);
        Assert.Equal(destinations, instruction.Destinations.Count);
        Assert.Equal(Gen5Operand.Vector(1), instruction.Sources[0]);
    }

    [Theory]
    // opcode, name, memory dwords, data registers read
    [InlineData(0x30u, "GlobalAtomicSwap", 1u, 1)]
    [InlineData(0x31u, "GlobalAtomicCmpswap", 1u, 2)]
    [InlineData(0x3Bu, "GlobalAtomicXor", 1u, 1)]
    [InlineData(0x40u, "GlobalAtomicFmax", 1u, 1)]
    [InlineData(0x52u, "GlobalAtomicAddX2", 2u, 2)]
    [InlineData(0x51u, "GlobalAtomicCmpswapX2", 2u, 4)]
    [InlineData(0x58u, "GlobalAtomicUmaxX2", 2u, 2)]
    public void GlobalAtomic_DecodesWidthAndData(uint opcode, string expected, uint dwords, int dataRegisters)
    {
        // global_atomic_* v[2:3], v[4:...], off
        var instruction = DecodeFirst(Flat(segment: 2, opcode, offset: 0), (0x7Fu << 16) | (4u << 8) | 2u);

        Assert.Equal(expected, instruction.Opcode);
        Assert.Equal(dwords, Assert.IsType<Gen5GlobalMemoryControl>(instruction.Control).DwordCount);
        Assert.Equal(dataRegisters, instruction.Sources.Count(source =>
            source.Kind == Gen5OperandKind.VectorRegister && source.Value >= 4));
    }

    // SMEM word 0: sbase[5:0] (pairs), sdata[12:6], glc[16], op[25:18], encoding 0x3D.
    private static uint Smem(uint opcode, uint sdata, uint sbase) =>
        0xF400_0000u | (opcode << 18) | (sdata << 6) | sbase;

    // FLAT word 0: offset[11:0], dlc[12], seg[15:14], glc[16], slc[17], op[24:18], encoding 0x37.
    private static uint Flat(uint segment, uint opcode, int offset, bool dlc = false) =>
        0xDC00_0000u | (opcode << 18) | (segment << 14) | (dlc ? 1u << 12 : 0) | ((uint)offset & 0xFFF);

    // FLAT word 1: addr[7:0], data[15:8], saddr[22:16] (0x7F = off), vdst[31:24].
    private static uint FlatWord1(uint vdst) => (vdst << 24) | (0x7Fu << 16) | 2u;

    // MUBUF word 0: offset[11:0], offen[12], idxen[13], glc[14], dlc[15], lds[16], op[24:18], op[7] at 25.
    private static uint Mubuf(uint opcode, uint offset, bool lds = false) =>
        0xE000_0000u | ((opcode & 0x7F) << 18) | (((opcode >> 7) & 1) << 25) | (lds ? 1u << 16 : 0) | offset;

    // MUBUF word 1: vaddr[7:0], vdata[15:8], srsrc[20:16], slc[22], tfe[23], soffset[31:24] (0x80 = 0).
    private static uint MubufWord1(bool tfe = false) =>
        (0x80u << 24) | (tfe ? 1u << 23 : 0) | (2u << 16) | (4u << 8);

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
