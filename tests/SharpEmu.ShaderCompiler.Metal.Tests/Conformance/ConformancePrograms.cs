// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Metal.Tests.Conformance;

/// <summary>
/// The harness programs, as gfx1030 assembly. Each lane owns a 128-byte record of the data
/// buffer (s[8:11]): dwords 0..9 are inputs, 16..23 outputs.
/// <para>Vector: v1:v2, v3:v4, v5:v6 = sources, v7:v8 = old destination, v9 = the lane's VCC
/// bit, v10 = its EXEC bit. After the instruction (EXEC restored) the program stores v7, v8,
/// VCC_LO, VCC_HI, the EXEC the instruction left, and s24, s25.</para>
/// <para>Scalar: one case per lane, run in a loop over the lanes: S0 = s[0:1] (v1:v2),
/// S1 = s[4:5] (v3:v4), D = s[14:15] (old value from v7:v8), SCC from v9, EXEC for the
/// EXEC-writing ops from v10:v5. Results go back with V_WRITELANE: D, SCC, EXEC.</para>
/// </summary>
internal static class ConformancePrograms
{
    public const int LaneBytes = 128;
    public const int OutputDword = 16;

    private static IEnumerable<string> Prologue(int wave)
    {
        yield return "v_mbcnt_lo_u32_b32 v0, -1, 0";
        if (wave == 64) yield return "v_mbcnt_hi_u32_b32 v0, -1, v0";
        yield return "v_lshlrev_b32 v20, 7, v0";
        for (var register = 1; register <= 10; register++)
        {
            yield return register == 1
                ? "buffer_load_dword v1, v20, s[8:11], 0 offen"
                : $"buffer_load_dword v{register}, v20, s[8:11], 0 offen offset:{(register - 1) * 4}";
        }

        yield return "s_waitcnt vmcnt(0)";
    }

    private static string Store(int register, int dword) =>
        $"buffer_store_dword v{register}, v20, s[8:11], 0 offen offset:{dword * 4}";

    public static List<string> Vector(string instruction, int wave)
    {
        var lines = new List<string>(Prologue(wave));
        if (wave == 32)
        {
            lines.Add("v_cmp_ne_u32_e32 vcc_lo, 0, v9");
            lines.Add($"s_mov_b32 vcc_hi, 0x{VectorWave.Wave32VccHigh:x}");
            lines.Add("v_cmp_ne_u32_e64 s28, 0, v10");
        }
        else
        {
            lines.Add("v_cmp_ne_u32_e32 vcc, 0, v9");
            lines.Add("v_cmp_ne_u32_e64 s[28:29], 0, v10");
        }

        lines.Add($"s_mov_b32 s24, 0x{VectorWave.ScalarDestinationLow:x}");
        lines.Add($"s_mov_b32 s25, 0x{VectorWave.ScalarDestinationHigh:x}");
        lines.Add(wave == 32 ? "s_mov_b32 exec_lo, s28" : "s_mov_b64 exec, s[28:29]");
        lines.Add(instruction);
        if (wave == 32)
        {
            lines.Add("s_mov_b32 s28, exec_lo");
            lines.Add("s_mov_b32 exec_lo, -1");
        }
        else
        {
            lines.Add("s_mov_b64 s[28:29], exec");
            lines.Add("s_mov_b64 exec, -1");
        }

        lines.Add("v_mov_b32 v11, vcc_lo");
        lines.Add("v_mov_b32 v12, vcc_hi");
        lines.Add("v_mov_b32 v13, s28");
        lines.Add("v_mov_b32 v14, s29");
        lines.Add("v_mov_b32 v15, s24");
        lines.Add("v_mov_b32 v16, s25");
        lines.Add(Store(7, OutputDword));
        lines.Add(Store(8, OutputDword + 1));
        for (var register = 11; register <= 16; register++)
        {
            lines.Add(Store(register, OutputDword + register - 9));
        }

        lines.Add("s_endpgm");
        return lines;
    }

    /// <summary>The scalar loop. "@loop" marks the branch target and "s_cbranch_scc1 @loop" is
    /// patched by <see cref="Assemble"/>.</summary>
    public static List<string> Scalar(string instruction, int wave, bool exec)
    {
        var lines = new List<string>(Prologue(wave))
        {
            "s_mov_b32 s3, 0",
            "@loop",
            "v_readlane_b32 s0, v1, s3",
            "v_readlane_b32 s1, v2, s3",
            "v_readlane_b32 s4, v3, s3",
            "v_readlane_b32 s5, v4, s3",
            "v_readlane_b32 s14, v7, s3",
            "v_readlane_b32 s15, v8, s3",
            "v_readlane_b32 s6, v9, s3",
        };
        if (exec)
        {
            lines.Add("v_readlane_b32 s16, v10, s3");
            lines.Add("v_readlane_b32 s17, v5, s3");
        }

        lines.Add("s_cmp_lg_u32 s6, 0");
        if (exec) lines.Add(wave == 32 ? "s_mov_b32 exec_lo, s16" : "s_mov_b64 exec, s[16:17]");
        lines.Add(instruction);
        lines.Add("s_cselect_b32 s7, 1, 0");
        if (exec)
        {
            if (wave == 32)
            {
                lines.Add("s_mov_b32 s16, exec_lo");
                lines.Add("s_mov_b32 exec_lo, -1");
            }
            else
            {
                lines.Add("s_mov_b64 s[16:17], exec");
                lines.Add("s_mov_b64 exec, -1");
            }
        }

        lines.Add("v_writelane_b32 v7, s14, s3");
        lines.Add("v_writelane_b32 v8, s15, s3");
        lines.Add("v_writelane_b32 v11, s7, s3");
        if (exec)
        {
            lines.Add("v_writelane_b32 v12, s16, s3");
            lines.Add("v_writelane_b32 v13, s17, s3");
        }

        lines.Add("s_add_u32 s3, s3, 1");
        lines.Add($"s_cmp_lt_u32 s3, {wave}");
        lines.Add("s_cbranch_scc1 @loop");
        lines.Add(Store(7, OutputDword));
        lines.Add(Store(8, OutputDword + 1));
        lines.Add(Store(11, OutputDword + 2));
        if (exec)
        {
            lines.Add(Store(12, OutputDword + 3));
            lines.Add(Store(13, OutputDword + 4));
        }

        lines.Add("s_endpgm");
        return lines;
    }

    /// <summary>Resolves every line through the llvm-mc table. Returns null and the missing
    /// lines when the table lacks any.</summary>
    public static uint[]? Assemble(IReadOnlyList<string> lines, int wave, List<string> missing)
    {
        var words = new List<uint>();
        var label = -1;
        var branches = new List<int>();
        foreach (var line in lines)
        {
            if (line == "@loop")
            {
                label = words.Count;
                continue;
            }

            if (line == "s_cbranch_scc1 @loop")
            {
                branches.Add(words.Count);
                words.Add(0);
                continue;
            }

            if (LlvmMcEncodings.TryEncode(wave, line, out var encoded))
            {
                words.AddRange(encoded);
            }
            else
            {
                missing.Add(line);
            }
        }

        if (missing.Count > 0) return null;
        foreach (var branch in branches)
        {
            // SOPP S_CBRANCH_SCC1 (opcode 5): PC = PC + 4 + simm16 * 4 (ISA 12.5).
            var offset = label - (branch + 1);
            words[branch] = 0xBF850000u | ((uint)offset & 0xFFFF);
        }

        return [.. words];
    }

    /// <summary>Every assembly line a program needs, for the encoding table.</summary>
    public static IEnumerable<string> Lines(IEnumerable<string> program) =>
        program.Where(static line => line != "@loop" && line != "s_cbranch_scc1 @loop");
}
