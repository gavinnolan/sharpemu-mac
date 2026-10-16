// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text.RegularExpressions;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// Every BUFFER/GLOBAL_ATOMIC_*_X2 is one 64-bit read-modify-write under the pair lock
// (Gen5MslTranslator.EmitQwordAtomic), and CSUB/FCMPSWAP are 32-bit atomics. Each is run
// on the device against the RDNA2 ISA 12.14 pseudo-code (Reference below): one lane per
// qword with edge-case operands, then many workgroups on one qword to check that the
// lock makes the read-modify-write atomic.
public sealed class Gen5MslQwordAtomicTests(ITestOutputHelper output)
{
    private const int Lanes = 32;
    private const int TargetOffset = 0;
    private const int OperandOffset = 256;
    private const int ReturnOffset = 768;
    private const int BufferBytes = 1024;
    private const ulong GuestBase = 0x4000_0000;
    private const int GuestBytes = 1 << 14;
    private const int GuestTargets = 0x1000;

    private static bool DeviceRequired => Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1";

    public static TheoryData<uint, bool> QwordOpcodes()
    {
        var data = new TheoryData<uint, bool>();
        foreach (var opcode in new uint[] { 0x50, 0x51, 0x52, 0x53, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F, 0x60 })
        {
            data.Add(opcode, true);
            data.Add(opcode, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(QwordOpcodes))]
    public void ABoundBufferX2AtomicMatchesTheIsa(uint opcode, bool glc)
    {
        var shader = CompileBound(PerLaneProgram(opcode, glc, resource: 8), BufferBytes);
        Assert.Contains("sharpemu_pair_try_lock(", shader.Source, StringComparison.Ordinal);
        if (!RequireDevice())
        {
            return;
        }

        var (olds, sources, comparators) = Operands(opcode);
        var initial = PerLaneBuffer(olds, sources, comparators, x2: true);
        var result = Execute(shader, initial, 1, out var stepCapHits);
        Assert.Equal(0u, stepCapHits);
        CheckPerLane(opcode, glc, x2: true, olds, sources, comparators, result, TargetOffset, result, ReturnOffset);
    }

    [Theory]
    [InlineData(0x34u, true)]  // CSUB (gfx10.3 requires GLC)
    [InlineData(0x3Eu, true)]  // FCMPSWAP
    [InlineData(0x3Eu, false)]
    public void ABoundBufferDwordAtomicMatchesTheIsa(uint opcode, bool glc)
    {
        var shader = CompileBound(PerLaneProgram(opcode, glc, resource: 8), BufferBytes);
        Assert.Contains(opcode == 0x34 ? "sharpemu_atomic_clamped_sub(" : "sharpemu_atomic_float_compare_swap(", shader.Source, StringComparison.Ordinal);
        if (!RequireDevice())
        {
            return;
        }

        var (olds, sources, comparators) = Operands(opcode);
        var initial = PerLaneBuffer(olds, sources, comparators, x2: false);
        var result = Execute(shader, initial, 1, out _);
        CheckPerLane(opcode, glc, x2: false, olds, sources, comparators, result, TargetOffset, result, ReturnOffset);
    }

    // The same programs through a V# the shader loads from guest memory at run time: the
    // device-descriptor path, over the device-address page table.
    [Theory]
    [MemberData(nameof(QwordOpcodes))]
    public void ADeviceDescriptorX2AtomicMatchesTheIsa(uint opcode, bool glc)
    {
        var shader = CompileBound(PerLaneProgram(opcode, glc, resource: 12), BufferBytes);
        Assert.Contains("sharpemu_resolve_device_address(sharpemu_ranges", shader.Source, StringComparison.Ordinal);
        Assert.Contains("sharpemu_pair_try_lock(", shader.Source, StringComparison.Ordinal);
        if (!RequireDevice())
        {
            return;
        }

        var (olds, sources, comparators) = Operands(opcode);
        var initial = PerLaneBuffer(olds, sources, comparators, x2: true);
        BinaryPrimitives.WriteUInt32LittleEndian(initial.AsSpan(BufferBytes - 4), (uint)GuestBase);
        var guest = new byte[GuestBytes];
        // V# at the guest base: base GuestBase + GuestTargets, stride 0, num_records 256,
        // FORMAT 32_UINT with the identity destination select, OOB_SELECT 3 (raw).
        var descriptor = new uint[] { (uint)(GuestBase + GuestTargets), 0, Lanes * 8, DescriptorConstants.IdentityDestinationSelect | (20u << 12) | (3u << 28) };
        for (var word = 0; word < descriptor.Length; word++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(guest.AsSpan(word * 4), descriptor[word]);
        }

        for (var lane = 0; lane < Lanes; lane++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(guest.AsSpan(GuestTargets + lane * 8), olds[lane]);
        }

        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var error), error);
        try
        {
            var (tables, _) = MetalNative.CreateGuestMapping(GuestBase, guest, 0x1000);
            Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, initial, new byte[PushData.ByteSize],
                shader.ArgumentLayout!, Lanes, out var result, out var stepCapHits, out error, tables: tables), error);
            Assert.Equal(0u, stepCapHits);
            var memory = MetalNative.ReadWords(tables[0].Residents[0], GuestBytes / 4);
            var guestAfter = new byte[GuestBytes];
            Buffer.BlockCopy(memory, 0, guestAfter, 0, GuestBytes);
            CheckPerLane(opcode, glc, x2: true, olds, sources, comparators, guestAfter, GuestTargets, result, ReturnOffset);
        }
        finally
        {
            MetalNative.ReleaseCollisionLibrary(library);
        }
    }

    // 64 workgroups of 32 lanes on one qword. Each lane takes a ticket and stores what its
    // GLC X2 atomic returned at the ticket's slot. ADD_X2 (two fetch-adds, the carry
    // between them) and INC_X2 / SMAX_X2 (a load, compute and store) are atomic exactly
    // when every lane saw a distinct pre-operation value and memory ends at the sum.
    [Theory]
    [InlineData(0x52u, 0x0000_0001_8000_0001ul, 0x0000_0000_FFFF_FF00ul)]  // ADD_X2: a carry every other add
    [InlineData(0x5Cu, ulong.MaxValue, 0x0000_0000_FFFF_FF80ul)]           // INC_X2 across the dword boundary
    [InlineData(0x51u, 0ul, 0ul)]                                         // CMPSWAP_X2 counter, see below
    public void ContendedX2AtomicsAcrossWorkgroupsAreAtomic(uint opcode, ulong operand, ulong initialValue)
    {
        const uint groups = 64;
        const int returns = 64;
        const int bytes = returns + (int)groups * Lanes * 8;
        var shader = CompileBound(ContendedProgram(opcode, operand), (uint)bytes);
        if (!RequireDevice())
        {
            return;
        }

        var initial = new byte[bytes];
        BinaryPrimitives.WriteUInt64LittleEndian(initial, initialValue);
        var result = Execute(shader, initial, groups, out var stepCapHits);
        Assert.Equal(0u, stepCapHits);
        const int total = (int)groups * Lanes;
        Assert.Equal((uint)total, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(8)));
        var seen = new ulong[total];
        for (var ticket = 0; ticket < total; ticket++)
        {
            seen[ticket] = BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(returns + ticket * 8));
        }

        Array.Sort(seen);
        var final = BinaryPrimitives.ReadUInt64LittleEndian(result);
        if (opcode == 0x51)
        {
            // Every lane loops CMPSWAP_X2 (old -> old + 2^32 - 1) until it wins once, so the
            // successful pre-operation values are exactly initial + k * step.
            const ulong step = 0xFFFF_FFFFul;
            Assert.Equal(initialValue + (ulong)total * step, final);
            for (var index = 0; index < total; index++)
            {
                Assert.Equal(initialValue + (ulong)index * step, seen[index]);
            }

            return;
        }

        var increment = opcode == 0x52 ? operand : 1ul;
        Assert.Equal(initialValue + (ulong)total * increment, final);
        for (var index = 0; index < total; index++)
        {
            Assert.Equal(initialValue + (ulong)index * increment, seen[index]);
        }
    }

    // Global X2 atomics share EmitQwordAtomic: every form compiles, GLC returns the pair.
    [Theory]
    [MemberData(nameof(QwordOpcodes))]
    public void EveryGlobalX2AtomicCompilesAndReturnsThePairWithGlc(uint opcode, bool glc)
    {
        var word = 0xDC00_8000u | (opcode << 18) | (glc ? 1u << 16 : 0u);
        var fixture = new Gen5ComputeFixture("global-atomic64", [word, 0x0810_0309, 0xBF81_0000], 16, 64);
        var source = Gen5ComputeFixtures.CompileRequestOrThrow(fixture).Source;
        if (MetalNative.IsAvailable)
        {
            Assert.True(MetalNative.TryCompileLibrary(source, out _, out var error), error);
        }

        Assert.Contains("sharpemu_pair_try_lock(", source, StringComparison.Ordinal);
        Assert.Equal(glc, Regex.IsMatch(source, @"(?m)^\s*(if \(exec\) \{ )?v8 = "));
        Assert.Equal(glc, Regex.IsMatch(source, @"(?m)^\s*(if \(exec\) \{ )?v9 = "));
        Assert.Equal(opcode is 0x5E or 0x5F or 0x60, source.Contains("sharpemu_f64_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x34u, "sharpemu_atomic_clamped_sub(")]
    [InlineData(0x3Eu, "sharpemu_atomic_float_compare_swap(")]
    public void GlobalCsubAndFcmpswapAreDeviceAtomics(uint opcode, string call)
    {
        var word = 0xDC00_8000u | (opcode << 18) | (1u << 16);
        var fixture = new Gen5ComputeFixture("global-atomic32", [word, 0x0810_0309, 0xBF81_0000], 16, 64);
        var source = Gen5ComputeFixtures.CompileRequestOrThrow(fixture).Source;
        Assert.Contains(call, source, StringComparison.Ordinal);
        // FCMPSWAP reads DATA from v3 and the comparator from v4.
        if (opcode == 0x3E)
        {
            Assert.Contains(", v4, v3)", source, StringComparison.Ordinal);
        }
    }

    [Theory]
    // global_load/store_dword_addtid v1, s[2:3] offset:16 (llvm-mc -mcpu=gfx1030)
    [InlineData(0xDC588010u, 0x01020000u, "sharpemu_load_device_dword(")]
    [InlineData(0xDC5C8010u, 0x00020100u, "sharpemu_store_device_dword(")]
    public void AddtidAccessesAddTheLaneTimesFourToSaddr(uint word0, uint word1, string call)
    {
        var fixture = new Gen5ComputeFixture("global-addtid", [word0, word1, 0xBF81_0000], 16, 64);
        var source = Gen5ComputeFixtures.CompileRequestOrThrow(fixture).Source;
        Assert.Contains("sharpemu_lane * 4u", source, StringComparison.Ordinal);
        Assert.Contains(call, source, StringComparison.Ordinal);
    }

    [Fact]
    public void CacheInvalidationsEmitNothing()
    {
        // buffer_gl0_inv; buffer_gl1_inv; s_endpgm
        var fixture = new Gen5ComputeFixture("gl-inv", [0xE1C40000, 0, 0xE1C80000, 0, 0xBF81_0000], 16, 64);
        Gen5ComputeFixtures.CompileRequestOrThrow(fixture);
    }

    // Lane i: its qword (or dword) at 8i, its operands (DATA, comparator) at 256 + 16i;
    // the atomic, then VDATA[0:1] stored at 768 + 8i. With GLC that is the pre-operation
    // value, without it the unchanged DATA. resource 12 is a V# loaded from the guest
    // address in the buffer's last dword.
    private static Gen5ComputeFixture PerLaneProgram(uint opcode, bool glc, uint resource)
    {
        var words = new List<uint>
        {
            0x34020083,                         // v_lshlrev_b32 v1, 3, v0
            0x34040084,                         // v_lshlrev_b32 v2, 4, v0
            0xE0381100, 0x80020402,             // buffer_load_dwordx4 v[4:7], v2, s[8:11], 0 offen offset:256
        };
        if (resource == 12)
        {
            words.AddRange(
            [
                0xE03003FC, 0x80020300,         // buffer_load_dword v3, off, s[8:11], 0 offset:1020
                0xBF8C3F70,                     // s_waitcnt vmcnt(0)
                0x7E080503,                     // v_readfirstlane_b32 s4, v3
                0xBE850380,                     // s_mov_b32 s5, 0
                0xF4080302, 0xFA000000,         // s_load_dwordx4 s[12:15], s[4:5], 0x0
                0xBF8CC07F,                     // s_waitcnt lgkmcnt(0)
            ]);
        }

        words.AddRange(
        [
            0xBF8C3F70,                                                     // s_waitcnt vmcnt(0)
            0xE0001000u | (opcode << 18) | (glc ? 1u << 14 : 0u),           // buffer_atomic_* v[4:], v1, s[resource], 0 offen [glc]
            0x80000401u | (resource / 4) << 16,
            0xBF8C3F70,                                                     // s_waitcnt vmcnt(0)
            0xE0741300, 0x80020401,                                         // buffer_store_dwordx2 v[4:5], v1, s[8:11], 0 offen offset:768
            0xBF810000,                                                     // s_endpgm
        ]);
        return new Gen5ComputeFixture("qword-atomic", [.. words], 8, BufferBytes);
    }

    // Every lane takes a ticket (BUFFER_ATOMIC_ADD on the dword at 8) and runs one GLC X2
    // atomic on the qword at 0, storing what it returned at 64 + 8 * ticket.
    private static Gen5ComputeFixture ContendedProgram(uint opcode, ulong operand)
    {
        if (opcode == 0x51)
        {
            return new Gen5ComputeFixture("contended-cmpswap-x2", CasWords, 8, 64 + 64 * Lanes * 8);
        }

        return new Gen5ComputeFixture("contended-x2",
        [
            0x7E0C0281,                         // v_mov_b32 v6, 1
            0xE0C84008, 0x80020600,             // buffer_atomic_add v6, off, s[8:11], 0 offset:8 glc
            0xBF8C3F70,                         // s_waitcnt vmcnt(0)
            0x340E0C83,                         // v_lshlrev_b32 v7, 3, v6
            0x7E0802FF, (uint)operand,          // v_mov_b32 v4, operand low
            0x7E0A02FF, (uint)(operand >> 32),  // v_mov_b32 v5, operand high
            0xE0004000u | (opcode << 18), 0x80020400, // buffer_atomic_*_x2 v[4:5], off, s[8:11], 0 glc
            0xBF8C3F70,                         // s_waitcnt vmcnt(0)
            0xE0741040, 0x80020407,             // buffer_store_dwordx2 v[4:5], v7, s[8:11], 0 offen offset:64
            0xBF810000,                         // s_endpgm
        ], 8, 64 + 64 * Lanes * 8);
    }

    // A lock-free 64-bit add built from CMPSWAP_X2, as guest code builds one: each lane
    // reads the qword, tries old -> old + 0xFFFFFFFF (a carry into the high dword every
    // time), and retries with the value it got back until its swap wins; a winner stores
    // the value it replaced at its ticket's slot. Assembled with llvm-mc -mcpu=gfx1030.
    private static readonly uint[] CasWords =
    [
        0x7E0C0281,                             // v_mov_b32 v6, 1
        0xE0C84008, 0x80020600,                 // buffer_atomic_add v6, off, s[8:11], 0 offset:8 glc
        0xBF8C3F70,                             // s_waitcnt vmcnt(0)
        0x34160C83,                             // v_lshlrev_b32 v11, 3, v6
        0xE0344000, 0x80020800,                 // buffer_load_dwordx2 v[8:9], off, s[8:11], 0 glc
        0xBF8C3F70,                             // s_waitcnt vmcnt(0)
        // retry:
        0xD70F6A04, 0x00018308,                 // v_add_co_u32 v4, vcc_lo, v8, -1
        0x500A1280,                             // v_add_co_ci_u32 v5, vcc_lo, 0, v9, vcc_lo
        0x7E0C0308,                             // v_mov_b32 v6, v8
        0x7E0E0309,                             // v_mov_b32 v7, v9
        0xE1444000, 0x80020400,                 // buffer_atomic_cmpswap_x2 v[4:7], off, s[8:11], 0 glc
        0xBF8C3F70,                             // s_waitcnt vmcnt(0)
        0x7DC40D04,                             // v_cmp_eq_u64 vcc_lo, v[4:5], v[6:7]
        0xBE953C6A,                             // s_and_saveexec_b32 s21, vcc_lo
        0xE0741040, 0x8002040B,                 // buffer_store_dwordx2 v[4:5], v11, s[8:11], 0 offen offset:64
        0x8A7E6A15,                             // s_andn2_b32 exec_lo, s21, vcc_lo
        0x7E100304,                             // v_mov_b32 v8, v4
        0x7E120305,                             // v_mov_b32 v9, v5
        0xBF89FFF0,                             // s_cbranch_execnz retry
        0xBF810000,                             // s_endpgm
    ];

    private static (ulong[] Olds, ulong[] Sources, ulong[] Comparators) Operands(uint opcode)
    {
        var floating = opcode is 0x5E or 0x5F or 0x60;
        ulong[] values = floating
            ?
            [
                Bits(0.0), Bits(-0.0), Bits(1.5), Bits(-2.25), Bits(double.PositiveInfinity), Bits(double.NegativeInfinity),
                0x7FF8_0000_0000_0000, 0x7FF0_0000_0000_0001, 0xFFF8_0000_0000_0001, 1, 0x8000_0000_0000_0001,
                Bits(double.MaxValue), Bits(-double.MaxValue), 0x0010_0000_0000_0000, Bits(1e300), Bits(-1e-300),
                Bits(1.5000000000000002), Bits(3.0),
            ]
            : opcode is 0x34 or 0x3E
                ?
                [
                    0, 1, 2, 0x7FFF_FFFF, 0x8000_0000, 0xFFFF_FFFF, 1000, 999, 0x7F80_0000, 0xFF80_0000, 0x7FC0_0000,
                    0x3F80_0000, 0xBF80_0000, 0x8000_0000, 0x0000_0001, 0x4000_0000,
                ]
                :
                [
                    0, 1, 2, 0xFFFF_FFFF, 0x1_0000_0000, 0x1_0000_0001, 0x7FFF_FFFF_FFFF_FFFF, 0x8000_0000_0000_0000,
                    0xFFFF_FFFF_FFFF_FFFF, 0xFFFF_FFFF_0000_0000, 0x1234_5678_9ABC_DEF0, 0x8000_0000_FFFF_FFFF,
                    0x0000_0001_FFFF_FFFF, 0xFFFF_FFFE_FFFF_FFFF, 1000, 999,
                ];
        var olds = new ulong[Lanes];
        var sources = new ulong[Lanes];
        var comparators = new ulong[Lanes];
        for (var lane = 0; lane < Lanes; lane++)
        {
            olds[lane] = values[lane % values.Length];
            sources[lane] = values[(lane * 7 + 3) % values.Length];
            // Half the comparators match memory; +0/-0 and NaN pairs exercise the float forms.
            comparators[lane] = (lane & 1) == 0 ? olds[lane] : values[(lane * 5 + 1) % values.Length];
        }

        if (floating)
        {
            comparators[1] = Bits(-0.0);                // memory +0.0: equal as doubles
            olds[3] = 0x7FF8_0000_0000_0000;            // a NaN never equals itself
            comparators[3] = olds[3];
        }

        return (olds, sources, comparators);
    }

    private static byte[] PerLaneBuffer(ulong[] olds, ulong[] sources, ulong[] comparators, bool x2)
    {
        var buffer = new byte[BufferBytes];
        for (var lane = 0; lane < Lanes; lane++)
        {
            if (x2)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(TargetOffset + lane * 8), olds[lane]);
                BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(OperandOffset + lane * 16), sources[lane]);
                BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(OperandOffset + lane * 16 + 8), comparators[lane]);
            }
            else
            {
                // A dword atomic: DATA in VDATA[0], the comparator in VDATA[1].
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(TargetOffset + lane * 8), (uint)olds[lane]);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(TargetOffset + lane * 8 + 4), 0xA5A5_A5A5);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(OperandOffset + lane * 16), (uint)sources[lane]);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(OperandOffset + lane * 16 + 4), (uint)comparators[lane]);
            }
        }

        return buffer;
    }

    private void CheckPerLane(
        uint opcode,
        bool glc,
        bool x2,
        ulong[] olds,
        ulong[] sources,
        ulong[] comparators,
        byte[] memory,
        int memoryOffset,
        byte[] returns,
        int returnOffset)
    {
        for (var lane = 0; lane < Lanes; lane++)
        {
            var expected = Reference(opcode, olds[lane], sources[lane], comparators[lane]);
            ulong actual;
            ulong returned;
            ulong expectedReturn;
            if (x2)
            {
                actual = BinaryPrimitives.ReadUInt64LittleEndian(memory.AsSpan(memoryOffset + lane * 8));
                returned = BinaryPrimitives.ReadUInt64LittleEndian(returns.AsSpan(returnOffset + lane * 8));
                expectedReturn = glc ? olds[lane] : sources[lane];
            }
            else
            {
                actual = BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan(memoryOffset + lane * 8));
                Assert.Equal(0xA5A5_A5A5u, BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan(memoryOffset + lane * 8 + 4)));
                returned = BinaryPrimitives.ReadUInt32LittleEndian(returns.AsSpan(returnOffset + lane * 8));
                expectedReturn = glc ? (uint)olds[lane] : (uint)sources[lane];
            }

            if (actual != expected || returned != expectedReturn)
            {
                output.WriteLine($"lane {lane}: old 0x{olds[lane]:X} src 0x{sources[lane]:X} cmp 0x{comparators[lane]:X} -> 0x{actual:X} (expected 0x{expected:X}), returned 0x{returned:X} (expected 0x{expectedReturn:X})");
            }

            Assert.Equal(expected, actual);
            Assert.Equal(expectedReturn, returned);
        }
    }

    // RDNA2 ISA 12.14 (MUBUF) / 12.18 (FLAT): the memory a lane's atomic leaves.
    private static ulong Reference(uint opcode, ulong old, ulong source, ulong comparator) => opcode switch
    {
        0x34 => (uint)old < (uint)source ? 0u : (uint)old - (uint)source,
        0x3E => F((uint)old) == F((uint)comparator) ? (uint)source : (uint)old,
        0x50 => source,
        0x51 => old == comparator ? source : old,
        0x52 => old + source,
        0x53 => old - source,
        0x55 => (ulong)Math.Min((long)old, (long)source),
        0x56 => Math.Min(old, source),
        0x57 => (ulong)Math.Max((long)old, (long)source),
        0x58 => Math.Max(old, source),
        0x59 => old & source,
        0x5A => old | source,
        0x5B => old ^ source,
        0x5C => old >= source ? 0 : old + 1,
        0x5D => old == 0 || old > source ? source : old - 1,
        0x5E => D(old) == D(comparator) ? source : old,
        0x5F => D(source) < D(old) ? source : old,
        0x60 => D(source) > D(old) ? source : old,
        _ => throw new ArgumentOutOfRangeException(nameof(opcode)),
    };

    private static double D(ulong bits) => BitConverter.UInt64BitsToDouble(bits);

    private static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    private static ulong Bits(double value) => BitConverter.DoubleToUInt64Bits(value);

    private bool RequireDevice()
    {
        if (!DeviceRequired)
        {
            output.WriteLine("Atomic execution requires the exclusive Metal device gate.");
            return false;
        }

        Assert.True(MetalNative.IsAvailable, "The required execution gate needs a Metal device.");
        return true;
    }

    private static Gen5MslShader CompileBound(Gen5ComputeFixture fixture, uint bytes)
    {
        var userData = new uint[16];
        userData[8] = 0x200000;
        userData[10] = bytes;
        userData[11] = DescriptorConstants.IdentityDestinationSelect;
        var request = Gen5ComputeFixtures.RequestOrThrow(Gen5ComputeFixtures.DecodeOrThrow(fixture), ShaderStage.Compute,
            localSizeX: Lanes, runtimeInputs: new ResourceRuntimeInputs { UserData = userData });
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
        if (MetalNative.IsAvailable)
        {
            Assert.True(MetalNative.TryCompileLibrary(shader!.Source, out var library, out var compileError), compileError + "\n" + shader.Source);
            if (library != 0) MetalNative.ReleaseCollisionLibrary(library);
        }

        return shader!;
    }

    private static byte[] Execute(Gen5MslShader shader, byte[] initial, uint groups, out uint stepCapHits)
    {
        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var compileError), compileError);
        try
        {
            Assert.True(MetalNative.TryExecuteWithArgumentBuffer(library, shader.EntryPoint, initial,
                new byte[PushData.ByteSize], shader.ArgumentLayout!, Lanes, out var result, out stepCapHits, out var runError,
                threadgroupCount: groups), runError);
            return result;
        }
        finally
        {
            MetalNative.ReleaseCollisionLibrary(library);
        }
    }
}
