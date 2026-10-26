// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Metal.Tests;

// The three changes that cut the GPU time of translated shaders: the relaxed math mode, the
// early-fragment-tests attribute, and one page-table lookup per global record.
public sealed class MslShaderTimeChangesTests
{
    private const ulong GuestBase = 0x4000_0000;
    private const int GuestBytes = 4 << 14;
    private const uint Lanes = 32;

    private static bool DeviceRequired => Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE") == "1";

    // ---- early fragment tests ----

    [Fact]
    public void APixelShaderWhoseGuestStateTestsDepthEarlyDeclaresEarlyFragmentTests()
    {
        var shader = Gen5ComputeFixtures.CompilePixelOrThrow(pixelEarlyDepth: true);
        Assert.Contains("[[early_fragment_tests]] fragment Gen5PsOut gen5_ps(", shader.Source, StringComparison.Ordinal);
        if (DeviceRequired)
        {
            Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var error), error);
            MetalNative.ReleaseCollisionLibrary(library);
        }
    }

    [Fact]
    public void APixelShaderWithLateDepthTestKeepsMetalsChoice()
    {
        var shader = Gen5ComputeFixtures.CompilePixelOrThrow(pixelEarlyDepth: false);
        Assert.DoesNotContain("early_fragment_tests", shader.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEarlyFragmentTestsSwitchTurnsTheAttributeOff()
    {
        var shader = Gen5ComputeFixtures.CompilePixelOrThrow(
            pixelEarlyDepth: true, options: new Gen5MslCodegenOptions(EarlyFragmentTests: false));
        Assert.DoesNotContain("early_fragment_tests", shader.Source, StringComparison.Ordinal);
    }

    // ---- math mode ----

    [Fact]
    public void TheRelaxedMathModeIsTheDefaultAndSafeIsTheSwitch()
    {
        // SHARPEMU_MSL_MATHMODE=safe is read once at startup, so the default is what a run
        // without it gets.
        var requested = Environment.GetEnvironmentVariable("SHARPEMU_MSL_MATHMODE");
        Assert.Equal(
            string.Equals(requested, "safe", StringComparison.OrdinalIgnoreCase) ? MslMathMode.Safe : MslMathMode.Relaxed,
            MslMathMode.Default);
        Assert.NotEqual(MslMathMode.Identity(MslMathMode.Safe), MslMathMode.Identity(MslMathMode.Relaxed));
    }

    // omod flushes a zero result to +0. A test of the result against 0.0f cannot say which zero
    // it is under the relaxed mode (signed zeros are not distinguished), so the flush tests the
    // bits.
    [Fact]
    public void AnOutputModifierFlushesZeroByItsBits()
    {
        var program = new Gen5ComputeFixture(
            "omod-negative-zero",
            [
                0xD5030001, 0x08020300, // v_add_f32_e64 v1, v0, v1 mul:2
                0xBF810000,
            ],
            StoreScalarResourceBase: 0,
            StoreBackingBytes: 0);
        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(program);
        Assert.Contains("& 0x7FFFFFFFu) == 0u", shader.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("== 0.0f ? 0.0f", shader.Source, StringComparison.Ordinal);
    }

    // ---- one lookup per global record ----

    // global_load_dwordx4 v[4:7], v[9:10], off; global_store_dwordx4 v[11:12], v[4:7], off.
    private static Gen5ComputeFixture Copy(uint[] addressSetup) => new(
        "global-record-copy",
        [
            .. addressSetup,
            0xDC388000, 0x047D0009, // global_load_dwordx4 v[4:7], v[9:10], off
            0xBF8C3F70,             // s_waitcnt vmcnt(0)
            0xDC788000, 0x007D040B, // global_store_dwordx4 v[11:12], v[4:7], off
            0xBF810000,             // s_endpgm
        ],
        StoreScalarResourceBase: 0,
        StoreBackingBytes: 0);

    [Fact]
    public void AGlobalRecordIsResolvedOnceAndFallsBackPerDword()
    {
        var setup = new uint[] { 0x7E1202FF, 0x40000000, 0x7E140280, 0x7E1602FF, 0x40001000, 0x7E180280 };
        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(Copy(setup));

        // One lookup for the record's load and one for its store, the per-dword forms as fallback.
        Assert.Equal(2, CountOccurrences(shader.Source, "sharpemu_resolve_device_span(sharpemu_ranges"));
        Assert.Equal(4, CountOccurrences(shader.Source, "sharpemu_load_device_dword(sharpemu_ranges"));
        Assert.Equal(4, CountOccurrences(shader.Source, "sharpemu_store_device_dword(sharpemu_ranges"));

        var request = Gen5ComputeFixtures.CreateComputeRequest(Copy(setup));
        Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var off, out var error, new Gen5MslCodegenOptions(DeviceAddressSpans: false)), error);
        Assert.DoesNotContain("sharpemu_resolve_device_span(sharpemu_ranges", off.Source, StringComparison.Ordinal);
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)((index * 7) + ((index >> 8) * 13) + (index >> 14) + 3);
        }

        return bytes;
    }

    private static byte[] RunCopy(Gen5ComputeFixture fixture, byte[] guest, uint guestBytesMapped)
    {
        var shader = Gen5ComputeFixtures.CompileRequestOrThrow(fixture, localSizeX: Lanes);
        Assert.True(MetalNative.TryCompileLibrary(shader.Source, out var library, out var compileError), compileError);
        try
        {
            var (mapping, _) = MetalNative.CreateGuestMapping(GuestBase, guest[..(int)guestBytesMapped], 0x1000);
            // A global store is performed inside the program's written range, slot 0 of the
            // flattened table: base low, base high, size.
            var written = MetalNative.CreateWordTable(
                DescriptorBindingKind.FlattenedResourceTable, [(uint)GuestBase, (uint)(GuestBase >> 32), guestBytesMapped]);
            IReadOnlyList<DeviceTable> tables = [.. mapping, written];
            Assert.True(
                MetalNative.TryExecuteWithArgumentBuffer(
                    library, shader.EntryPoint, new byte[1024], new byte[PushData.ByteSize], shader.ArgumentLayout!,
                    Lanes, out _, out _, out var runError, tables: tables),
                runError);
            var words = MetalNative.ReadWords(mapping[0].Residents[0], (int)guestBytesMapped / 4);
            var bytes = new byte[guestBytesMapped];
            Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            MetalNative.ReleaseCollisionLibrary(library);
        }
    }

    [Fact]
    public void AGlobalRecordCopyMovesEveryByteIncludingRecordsAcrossAPageBoundary()
    {
        if (!DeviceRequired)
        {
            return;
        }

        // Lane n copies the 16 bytes at base + 0x3FF8 + 16n to the same offset + 0x8000. Lane 0's
        // record and its copy straddle a 16 KB page, the rest sit inside one.
        var guest = Pattern(GuestBytes);
        var fixture = Copy(
        [
            0x34120084,             // v_lshlrev_b32 v9, 4, v0
            0x4A1212FF, 0x40003FF8, // v_add_nc_u32 v9, 0x40003FF8, v9
            0x7E140280,             // v_mov_b32 v10, 0
            0x4A1612FF, 0x00008000, // v_add_nc_u32 v11, 0x8000, v9
            0x7E180280,             // v_mov_b32 v12, 0
        ]);
        var result = RunCopy(fixture, guest, GuestBytes);
        for (var lane = 0; lane < Lanes; lane++)
        {
            var source = 0x3FF8 + (lane * 16);
            Assert.True(
                guest.AsSpan(source, 16).SequenceEqual(result.AsSpan(source + 0x8000, 16)),
                $"lane {lane} did not copy its record");
        }
    }

    [Fact]
    public void AGlobalRecordRunningOffTheMappedRangeLoadsZeroAndStoresOnlyWhatIsMapped()
    {
        if (!DeviceRequired)
        {
            return;
        }

        // One mapped page. Every lane reads 16 bytes at page offset 0x3FF8: two dwords are
        // mapped and two are not, and the unmapped ones read as zero. The record is written to
        // page offset 0x1000, inside the mapping.
        var guest = Pattern(1 << 14);
        var fixture = Copy(
        [
            0x7E1202FF, 0x40003FF8, // v_mov_b32 v9, 0x40003FF8
            0x7E140280,             // v_mov_b32 v10, 0
            0x7E1602FF, 0x40001000, // v_mov_b32 v11, 0x40001000
            0x7E180280,             // v_mov_b32 v12, 0
        ]);
        var result = RunCopy(fixture, guest, 1 << 14);
        Assert.Equal(Convert.ToHexString(guest.AsSpan(0x3FF8, 8)), Convert.ToHexString(result.AsSpan(0x1000, 8)));
        Assert.Equal("0000000000000000", Convert.ToHexString(result.AsSpan(0x1008, 8)));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
