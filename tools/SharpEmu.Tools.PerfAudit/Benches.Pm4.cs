// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.Tests.Scenes;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.PerfAudit.Planning;

namespace SharpEmu.PerfAudit;

internal static class Pm4Benches
{
    public const int FrameDraws = 6331;
    public const int Draws = 6000;
    public const ulong StreamAddress = NativeGuestMemory.Base + 0x1000;

    /// <summary>
    /// A DCB of <paramref name="draws"/> draws. Each draw re-sets <paramref name="contextRegisters"/> context registers
    /// (one SET_CONTEXT_REG packet each, values vary per draw), the VS and PS user-data blocks (one SET_SH_REG packet
    /// each), NUM_INSTANCES, and a DRAW_INDEX_AUTO or DRAW_INDEX_2.
    /// </summary>
    public static uint[] BuildStream(int draws, int contextRegisters, bool indexed, out int packets)
    {
        uint[] contextOffsets =
        [
            Cx.PaClVportXScale, Cx.PaClVportXOffset, Cx.PaClVportYScale, Cx.PaClVportYOffset, Cx.PaClVportZScale, Cx.PaClVportZOffset,
            Cx.CbTargetMask, Cx.CbShaderMask, Cx.DbDepthControl, Cx.PaSuScModeCntl, Cx.CbBlend0Control, Cx.CbColorControl,
            Cx.DbShaderControl, Cx.DbRenderControl, Cx.SpiPsInputEna, Cx.SpiPsInputAddr, Cx.PaScScreenScissorTl,
            Cx.PaScScreenScissorBr, Cx.PaScWindowScissorTl, Cx.CbColor0Base, Cx.PaClClipCntl, Cx.PaClVteCntl, Cx.PaScAaConfig,
            Cx.SpiShaderColFormat, Cx.DbEqaa,
        ];
        var pm4 = new ScenePm4();
        var count = 0;
        for (var draw = 0; draw < draws; draw++)
        {
            for (var register = 0; register < contextRegisters; register++)
            {
                pm4.Context(contextOffsets[register % contextOffsets.Length], SceneState.F(draw * 0.25f + register));
                count++;
            }

            // VS user data (GS user SGPRs) and PS user data: 8 words each, different buffers per draw.
            var vs = new uint[8];
            var ps = new uint[8];
            for (var word = 0; word < 8; word++)
            {
                vs[word] = (uint)(0x1000 + draw * 64 + word);
                ps[word] = (uint)(0x9000 + draw * 64 + word);
            }

            pm4.Shader(Sh.UserDataGs0, vs);
            pm4.Shader(Sh.UserDataPs0, ps);
            pm4.NumInstances(1);
            count += 3;
            if (indexed)
            {
                pm4.IndexType(0);
                pm4.DrawIndexed(NativeGuestMemory.Base + 0x40_0000, 1024, 6);
                count += 2;
            }
            else
            {
                pm4.DrawAuto(6);
                count++;
            }
        }

        packets = count;
        return pm4.ToArray();
    }

    public static void Run()
    {
        Console.WriteLine("--- 1. PM4 interpreter (GpuCommandInterpreter.Process) ---");
        using var memory = new NativeGuestMemory(64 << 20);
        foreach (var (label, contextRegisters, indexed, blocks) in new (string, int, bool, bool)[]
        {
            ("20 ctx regs + 2 SH user-data + NUM_INST + DRAW_INDEX_AUTO, block-read host", 20, false, true),
            ("same stream, per-packet-read host (no command blocks)", 20, false, false),
            ("20 ctx regs + 2 SH + NUM_INST + INDEX_TYPE + DRAW_INDEX_2, block-read host", 20, true, true),
            ("5 ctx regs only (delta-light draw), block-read host", 5, false, true),
            ("25 ctx regs, block-read host", 25, false, true),
        })
        {
            var words = BuildStream(Draws, contextRegisters, indexed, out var packets);
            memory.WriteWords(StreamAddress, words);
            var host = new CountingCommandHost(memory, blocks);
            var interpreter = new GpuCommandInterpreter(host, 0, 0);
            var id = label.StartsWith("20 ctx regs + 2 SH user-data") ? "1a" : label.StartsWith("same") ? "1b" : label.StartsWith("20 ctx regs + 2 SH + NUM_INST + INDEX") ? "1c" : label.StartsWith("5 ") ? "1d" : "1e";
            var packetsPerDraw = (double)packets / Draws;
            var result = Harness.Measure(id, $"PM4 {label}", Draws, FrameDraws, _ =>
            {
                var progress = interpreter.Process(new PacketCursorStack(), StreamAddress, (uint)words.Length);
                if (progress != SubmissionProgress.Complete) throw new InvalidOperationException(progress.ToString());
            }, note: $"{packetsPerDraw:F1} packets/draw, {words.Length / (double)Draws:F1} dwords/draw, stream {words.Length * 4 / 1024} KiB");
            Console.WriteLine($"       -> {result.MedianNs / packetsPerDraw:F1} ns/packet ({packetsPerDraw:F1} packets/draw), host draws seen {host.Draws}");
            Harness.Results.Add(result with { Id = id + "p", Name = $"  per packet: {label}", MedianNs = result.MedianNs / packetsPerDraw, P95Ns = result.P95Ns / packetsPerDraw, MinNs = result.MinNs / packetsPerDraw, BytesPerOp = result.BytesPerOp / packetsPerDraw, OpsPerFrame = FrameDraws * packetsPerDraw, Unit = "packet" });
        }
    }
}
