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
    [InlineData(16, "unsupported-mimg-tfe")]
    [InlineData(17, "unsupported-mimg-lwe")]
    public void Mimg_TfeAndLwe_AreRefused(int bit, string expected)
    {
        // image_sample_lz v[4:7], v0, s[8:15], s[0:3] dmask:0xF dim:2D, plus tfe or lwe.
        var word0 = 0xF000_0000u | (0x27u << 18) | (1u << bit) | (0xFu << 8) | (1u << 3);
        Assert.Contains(expected, DecodeError(word0, 0x0002_0400u));
    }

    [Fact]
    public void Mimg_WithoutTfeOrLwe_StillDecodes()
    {
        var word0 = 0xF000_0000u | (0x27u << 18) | (0xFu << 8) | (1u << 3);
        Assert.Equal("ImageSampleLz", DecodeFirst(word0, 0x0002_0400u).Opcode);
    }

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
