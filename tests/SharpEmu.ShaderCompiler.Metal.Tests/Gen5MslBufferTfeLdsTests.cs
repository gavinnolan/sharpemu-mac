// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// MUBUF/MTBUF loads with TFE write a status dword after their data (zero when resident,
// nonzero for a dword on an unmapped page of a device-descriptor access), and MUBUF loads
// with LDS=1 write their dword to LDS at M0[15:0] + inst_offset + lane * 4 instead of a
// VGPR (Gen5MslTranslator.TryEmitBufferLoadWithStatus / TryEmitBufferLoadToLds).
public sealed class Gen5MslBufferTfeLdsTests(ITestOutputHelper output)
{
    private const int Lanes = 32;
    private const int OutputOffset = 512;
    private const int BufferBytes = 1024;
    private const ulong GuestBase = 0x4000_0000;
    private const int GuestBytes = 1 << 14;
    // The device V# sits 64 bytes before the end of the one mapped page and reaches 112
    // bytes: dwords 0-15 are mapped, 16-27 in range but unmapped, 28 on out of range.
    private const int DeviceBufferOffset = GuestBytes - 64;
    private const uint DeviceRecords = 112;

    private static bool DeviceRequired => Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1";

    [Fact]
    public void ABoundTfeLoadWritesItsDataAndAZeroStatus()
    {
        var shader = CompileBound(TfeProgram(resource: 8));
        if (!RequireDevice())
        {
            return;
        }

        var input = Input();
        var result = Execute(shader, input, tables: null);
        for (var lane = 0; lane < Lanes; lane++)
        {
            var at = OutputOffset + lane * 16;
            Assert.Equal(Word(input, lane), Word(result, at / 4));
            Assert.Equal(Word(input, lane + 1), Word(result, at / 4 + 1));
            Assert.Equal(0u, Word(result, at / 4 + 2));
        }
    }

    [Fact]
    public void ADeviceDescriptorTfeLoadReportsUnmappedDwordsInRange()
    {
        var shader = CompileBound(TfeProgram(resource: 12));
        Assert.Contains("(void)sharpemu_resolve_device_address(", shader.Source, StringComparison.Ordinal);
        if (!RequireDevice())
        {
            return;
        }

        var input = Input();
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(BufferBytes - 4), (uint)GuestBase);
        var guest = new byte[GuestBytes];
        var descriptor = new uint[] { (uint)(GuestBase + DeviceBufferOffset), 0, DeviceRecords, DescriptorConstants.IdentityDestinationSelect | (20u << 12) };
        for (var word = 0; word < descriptor.Length; word++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(guest.AsSpan(word * 4), descriptor[word]);
        }

        for (var word = 0; word < 16; word++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(guest.AsSpan(DeviceBufferOffset + word * 4), 0xC0DE_0000u + (uint)word);
        }

        var (tables, _) = MetalNative.CreateGuestMapping(GuestBase, guest, 0x1000);
        var result = Execute(shader, input, tables);
        for (var lane = 0; lane < Lanes; lane++)
        {
            uint Expected(int dword) => dword < 16 && dword * 4 + 3 < DeviceRecords ? 0xC0DE_0000u + (uint)dword : 0u;
            bool Faults(int dword) => dword >= 16 && dword * 4 < DeviceRecords;
            var at = (OutputOffset + lane * 16) / 4;
            var expectedStatus = Faults(lane) || Faults(lane + 1) ? 1u : 0u;
            output.WriteLine($"lane {lane}: 0x{Word(result, at):X8} 0x{Word(result, at + 1):X8} status {Word(result, at + 2)}");
            Assert.Equal(Expected(lane), Word(result, at));
            Assert.Equal(Expected(lane + 1), Word(result, at + 1));
            Assert.Equal(expectedStatus, Word(result, at + 2));
        }
    }

    [Theory]
    [InlineData(0xE0041000u, 0x80820401u, 6u)]  // buffer_load_format_xy v[4:6], v1, s[8:11], 0 offen tfe
    [InlineData(0xE0281000u, 0x80830401u, 5u)]  // buffer_load_ushort v[4:5], v1, s[12:15], 0 offen tfe
    public void TfeWritesTheStatusAfterTheDataRegisters(uint word0, uint word1, uint statusRegister)
    {
        var shader = CompileBound(Program(word0, word1));
        // The translator names a register v[n] or, promoted to a local, vn.
        Assert.True(
            shader.Source.Contains($"v[{statusRegister}] = ", StringComparison.Ordinal) ||
            shader.Source.Contains($" v{statusRegister} = ", StringComparison.Ordinal),
            shader.Source);
    }

    // The decoder already refuses both: TFE exists only on loads, and gfx10 returns to LDS
    // only the single-dword loads.
    [Theory]
    [InlineData(0xE0701000u, 0x80820401u, "unsupported-mubuf-tfe")]  // buffer_store_dword v4, v1, s[8:11], 0 offen + TFE
    [InlineData(0xE0351000u, 0x80020001u, "unsupported-mubuf-lds")]  // buffer_load_dwordx2 + LDS
    public void TfeOnAStoreAndLdsOnAWideLoadAreRefused(uint word0, uint word1, string reason)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Gen5ComputeFixtures.DecodeOrThrow(Program(word0, word1)));
        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
    }

    // buffer_load_dword ... offset:4 lds at M0 0x40 and buffer_load_sbyte ... lds at M0
    // 0x100, then ds_read_b32 of each lane's slot. VDATA of both is v0, the lane id the
    // program reads again afterwards, so the load must leave it untouched.
    [Fact]
    public void ABufferLoadToLdsWritesEachLanesDwordAtM0()
    {
        var shader = CompileBound(LdsProgram(resource: 8));
        if (!RequireDevice())
        {
            return;
        }

        var input = Input();
        var result = Execute(shader, input, tables: null);
        for (var lane = 0; lane < Lanes; lane++)
        {
            var at = (OutputOffset + lane * 8) / 4;
            Assert.Equal(Word(input, lane + 1), Word(result, at));
            Assert.Equal((uint)(sbyte)(Word(input, lane) >> 24), Word(result, at + 1));
        }
    }

    [Fact]
    public void ABufferLoadToLdsThroughADeviceDescriptorCompiles()
    {
        var shader = CompileBound(Program(0xE0311000u, 0x80030001u));  // buffer_load_dword v1, s[12:15], 0 offen lds
        Assert.Contains("sharpemu_lds[", shader.Source, StringComparison.Ordinal);
    }

    private static Gen5ComputeFixture TfeProgram(uint resource) =>
        Program(0xE0341000u, 0x80000401u | resource / 4 << 16 | 1u << 23);  // buffer_load_dwordx2 v[4:6], v1, s[resource], 0 offen tfe

    // The operation under test after v1 = lane * 4, v3 = lane * 16, v6 = 0xdeadbeef and
    // s[12:15] = the V# at the guest address the buffer's last dword names; the three
    // dwords from v4 are stored at 512 + lane * 16.
    private static Gen5ComputeFixture Program(uint word0, uint word1) => new("tfe-lds",
    [
        0xE03003FC, 0x80020300,             // buffer_load_dword v3, off, s[8:11], 0 offset:1020
        0xBF8C3F70,                         // s_waitcnt vmcnt(0)
        0x7E080503,                         // v_readfirstlane_b32 s4, v3
        0xBE850380,                         // s_mov_b32 s5, 0
        0xF4080302, 0xFA000000,             // s_load_dwordx4 s[12:15], s[4:5], 0x0
        0xBF8CC07F,                         // s_waitcnt lgkmcnt(0)
        0xBEFC03C0,                         // s_mov_b32 m0, 64
        0x34020082,                         // v_lshlrev_b32 v1, 2, v0
        0x34060084,                         // v_lshlrev_b32 v3, 4, v0
        0x7E0C02FF, 0xDEADBEEF,             // v_mov_b32 v6, 0xdeadbeef
        word0, word1,
        0xBF8C3F70,                         // s_waitcnt vmcnt(0)
        0xE07C1200, 0x80020403,             // buffer_store_dwordx3 v[4:6], v3, s[8:11], 0 offen offset:512
        0xBF810000,                         // s_endpgm
    ], 8, BufferBytes);

    private static Gen5ComputeFixture LdsProgram(uint resource) => new("buffer-lds",
    [
        0xBEFC03C0,                         // s_mov_b32 m0, 64
        0x34020082,                         // v_lshlrev_b32 v1, 2, v0
        0xE0311004, 0x80000001u | resource / 4 << 16,  // buffer_load_dword v1, s[resource], 0 offen offset:4 lds
        0xBF8C3F70,                         // s_waitcnt vmcnt(0)
        0xB07C0100,                         // s_movk_i32 m0, 0x100
        0x38080283,                         // v_or_b32 v4, 3, v1
        0xE0251000, 0x80000004u | resource / 4 << 16,  // buffer_load_sbyte v4, s[resource], 0 offen lds
        0xBF8C3F70,                         // s_waitcnt vmcnt(0)
        0x34040082,                         // v_lshlrev_b32 v2, 2, v0
        0xD8D80044, 0x05000002,             // ds_read_b32 v5, v2 offset:68
        0xD8D80100, 0x06000002,             // ds_read_b32 v6, v2 offset:256
        0xBF8CC07F,                         // s_waitcnt lgkmcnt(0)
        0x34060083,                         // v_lshlrev_b32 v3, 3, v0
        0xE0741200, 0x80020503,             // buffer_store_dwordx2 v[5:6], v3, s[8:11], 0 offen offset:512
        0xBF810000,                         // s_endpgm
    ], 8, BufferBytes);

    // Dwords whose top bytes alternate between positive and negative signed bytes.
    private static byte[] Input()
    {
        var input = new byte[BufferBytes];
        for (var word = 0; word < 64; word++)
        {
            var value = 0x0102_0304u * (uint)(word + 1) ^ (word % 2 == 0 ? 0x8000_0000u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(word * 4), value);
        }

        return input;
    }

    private static uint Word(byte[] bytes, int index) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * 4));

    private bool RequireDevice()
    {
        if (!DeviceRequired)
        {
            output.WriteLine("Execution requires the exclusive Metal device gate.");
            return false;
        }

        Assert.True(MetalNative.IsAvailable, "The required execution gate needs a Metal device.");
        return true;
    }

    private static ShaderCompileRequest Request(Gen5ComputeFixture fixture)
    {
        var userData = new uint[16];
        userData[8] = 0x200000;
        userData[10] = BufferBytes;
        userData[11] = DescriptorConstants.IdentityDestinationSelect;
        return Gen5ComputeFixtures.RequestOrThrow(Gen5ComputeFixtures.DecodeOrThrow(fixture), ShaderStage.Compute,
            localSizeX: Lanes, runtimeInputs: new ResourceRuntimeInputs { UserData = userData });
    }

    private static Gen5MslShader CompileBound(Gen5ComputeFixture fixture)
    {
        Assert.True(Gen5MslTranslator.TryCompileProgram(Request(fixture), out var shader, out var error), error);
        if (MetalNative.IsAvailable)
        {
            Assert.True(MetalNative.TryCompileLibrary(shader!.Source, out var library, out var compileError), compileError + "\n" + shader.Source);
            if (library != 0) MetalNative.ReleaseCollisionLibrary(library);
        }

        return shader!;
    }

    private static byte[] Execute(Gen5MslShader shader, byte[] initial, IReadOnlyList<DeviceTable>? tables)
    {
        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var compileError), compileError);
        try
        {
            Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, initial,
                new byte[PushData.ByteSize], shader.ArgumentLayout!, Lanes, out var result, out _, out var runError,
                tables: tables), runError);
            return result;
        }
        finally
        {
            MetalNative.ReleaseCollisionLibrary(library);
        }
    }
}
