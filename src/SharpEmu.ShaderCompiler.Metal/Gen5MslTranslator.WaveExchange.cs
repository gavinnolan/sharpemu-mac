// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Metal;

// The wave64 exchange, at one threadgroup barrier.
//
// A guest wave64 runs as two 32-wide Apple simdgroups in one 64-thread threadgroup, so
// anything that needs the whole wave — a 64-bit V_CMP mask, V_READLANE of a lane in the
// other half, V_READFIRSTLANE, DS_APPEND's count — goes through threadgroup memory: each
// half writes, barrier, everyone reads. With one set of slots a second barrier had to
// follow the read, so the next exchange could not overwrite a slot a slower thread was
// still reading. Demon's Souls' compute shaders paid 20-odd barriers per thread for it,
// and a trivial copy kernel was bound by them.
//
// The slots are double-buffered instead. sharpemu_wave_set is a per-thread offset that
// every exchange flips between the two sets before it writes, so exchange k+1 writes the
// set exchange k did not, and only exchange k+2 reuses exchange k's slots. That reuse is
// safe without a barrier of its own: a thread writes in exchange k+2 only after passing
// exchange k+1's barrier, every thread reaches that barrier only after finishing exchange
// k's reads (they come before it in program order, and the barrier fences threadgroup
// memory), so every read of the old contents happens before the new write.
//
// The flip is a runtime counter rather than something the emitter tracks, because the
// exchanges sit in blocks that loops and branches reach along paths with different
// counts. It is the same value in every thread because every thread runs the same
// sequence of exchanges, which the barriers already required: a threadgroup barrier has
// to be reached by all threads, so an exchange one half skipped would have hung the
// group. Exchanges are emitted at statement level, never behind a per-lane EXEC guard,
// and the blocks around them branch only on scalar state (SCC, and VCC/EXEC pairs that
// are themselves exchanged), which is identical in every thread of a bridged wave.
//
// A set is four dwords: the two halves' 32-bit ballots, then a value per half (or one
// broadcast value in the first of them).
public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        private const uint WaveSlotSetDwords = 4;

        /// <summary>Both slot sets of the wave64 bridge, in dwords.</summary>
        private const uint WaveScratchDwords = 2 * WaveSlotSetDwords;

        /// <summary>Opens an exchange on the set the previous one did not use.</summary>
        private static readonly string WaveSetFlip = $"sharpemu_wave_set ^= {WaveSlotSetDwords}u;";

        private string WaveExchangeBarrier => Wave64Barrier;

        /// <summary>Slot <paramref name="slot"/> of the current set.</summary>
        private static string WaveSlot(uint slot) => slot == 0
            ? "sharpemu_wave_scratch[sharpemu_wave_set]"
            : $"sharpemu_wave_scratch[sharpemu_wave_set + {slot}u]";

        /// <summary>This half's ballot slot: 0 for lanes 0..31, 1 for lanes 32..63.</summary>
        private const string WaveHalfBallotSlot = "sharpemu_wave_scratch[(sharpemu_lane >> 5) + sharpemu_wave_set]";

        /// <summary>This half's value slot: 2 for lanes 0..31, 3 for lanes 32..63.</summary>
        private const string WaveHalfValueSlot = "sharpemu_wave_scratch[sharpemu_wave_set + 2u + (sharpemu_lane >> 5)]";

        /// <summary>The statements that publish this half's ballot of
        /// <paramref name="condition"/>: afterwards <c>WaveSlot(0)</c> and
        /// <c>WaveSlot(1)</c> hold the low and high halves of the 64-lane mask. simd_ballot
        /// is uniform across a simdgroup, so every lane of a half writes the same value to
        /// that half's slot and no first-lane guard is needed.</summary>
        private string[] WaveBallotExchange(string condition) =>
        [
            WaveSetFlip,
            $"{WaveHalfBallotSlot} = sharpemu_ballot({condition});",
            WaveExchangeBarrier,
        ];

        /// <summary>Broadcasts <paramref name="value"/> from guest lane
        /// <paramref name="lane"/> (0..63) to every lane. Only that lane evaluates the
        /// value, so it may have a side effect (DS_APPEND's atomic).</summary>
        private string EmitWave64BroadcastFromLane(string value, string lane, string? guard = null)
        {
            Line(WaveSetFlip);
            var condition = guard is null ? $"sharpemu_lane == {lane}" : $"{guard} && sharpemu_lane == {lane}";
            Line($"if ({condition}) {{ {WaveSlot(2)} = {value}; }}");
            Line(WaveExchangeBarrier);
            return Temp("uint", WaveSlot(2));
        }

        /// <summary>DS_APPEND / DS_CONSUME across a wave64: count the guest-active lanes of
        /// both halves, let the first of them do the one atomic
        /// <paramref name="atomicFor"/>(count), and broadcast what it returned. No
        /// active lane means no atomic, and 0.</summary>
        private string EmitWave64AppendAtomic(Func<string, string> atomicFor, string inBounds)
        {
            foreach (var statement in WaveBallotExchange("exec"))
            {
                Line(statement);
            }

            var low = Temp("uint", WaveSlot(0));
            var high = Temp("uint", WaveSlot(1));
            var count = Temp("uint", $"popcount({low}) + popcount({high})");
            var first = Temp(
                "uint",
                $"({low} != 0u) ? (uint)ctz({low}) : (({high} != 0u) ? (32u + (uint)ctz({high})) : 0u)");
            var anyActive = Temp("bool", $"{count} != 0u");
            var broadcast = EmitWave64BroadcastFromLane(
                $"{inBounds} ? {atomicFor(count)} : 0u", first, anyActive);
            return Temp("uint", $"{anyActive} ? {broadcast} : 0u");
        }
    }
}
