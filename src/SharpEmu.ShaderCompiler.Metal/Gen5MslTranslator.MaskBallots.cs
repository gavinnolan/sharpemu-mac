// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace SharpEmu.ShaderCompiler.Metal;

// Dead mask-ballot elision.
//
// A guest wave64 runs as two 32-wide Apple simdgroups, so the 64-bit mask a V_CMP or
// V_CMPX produces spans both of them. simd_ballot only sees its own simdgroup, so the
// two halves are exchanged through threadgroup memory, and that exchange needs a barrier
// each way: write your half, barrier, read both, barrier so the next ballot cannot
// overwrite a slot someone is still reading.
//
// That is two threadgroup barriers per comparison, and a threadgroup barrier makes every
// simdgroup wait. Profiling Octopath Traveler II found 27 of them in the dispatch that
// spends 19% of the frame's GPU time, 17 in the next one and 8 in the one after.
//
// Most of them are for nothing. The per-lane result — the vcc/exec bool the EXEC guards
// and V_CNDMASK read — is just this lane's own condition and needs no exchange at all;
// the emitter has already stored it before the ballot runs. The 64-bit pair is only
// needed when the program reads it as data: an S_CBRANCH_VCCZ, a 64-bit operand, VCC
// moved into another register. None of those three dispatches branches on VCCZ or reads
// VCC as a mask; they use s106/s107 as ordinary scratch registers, so each V_CMP's
// ballot is overwritten before anything looks at it.
//
// So the ballot is emitted as a marker and resolved once every block's text exists, by
// asking whether the pair is live afterwards. Taking the answer from the emitted MSL
// rather than from the IR is what makes it safe: every way the translator can read one
// of these registers spells it s[106], s[107], s[126] or s[127], so a reader cannot hide
// from a scan of the text, and anything unrecognised reads as a use and keeps the ballot.
public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        /// <summary>Marks a wave64 mask ballot: the low register of the pair and the
        /// condition. Resolved by <see cref="ResolveMaskBallots"/>.</summary>
        private const string MaskBallotMarker = "SHARPEMU_BALLOT";

        /// <summary>The four registers a mask ballot can write: VCC and EXEC, low and
        /// high. Tracked as slots 0..3 in that order.</summary>
        private static readonly uint[] MaskRegisters =
            [VccLoRegister, VccHiRegister, ExecLoRegister, ExecHiRegister];

        private static int MaskSlotOf(uint register)
        {
            for (var slot = 0; slot < MaskRegisters.Length; slot++)
            {
                if (MaskRegisters[slot] == register)
                {
                    return slot;
                }
            }

            return -1;
        }

        /// <summary>Replaces every mask-ballot marker with the cross-simdgroup exchange,
        /// or with nothing where the pair it writes is dead.</summary>
        private void ResolveMaskBallots(IReadOnlyList<ShaderBlock> blocks)
        {
            var anyMarker = false;
            foreach (var text in _blockText)
            {
                anyMarker |= text.Contains(MaskBallotMarker, StringComparison.Ordinal);
            }

            if (!anyMarker)
            {
                return;
            }

            var count = blocks.Count;
            var lines = new List<string>[count];
            var exposed = new bool[count][];
            var defined = new bool[count][];
            for (var index = 0; index < count; index++)
            {
                lines[index] = [.. SplitLines(_blockText[index])];
                var condition = _blockTerminators[index].Condition;
                if (condition.Length != 0)
                {
                    // The branch condition reads at the end of the block, and the VCCZ and
                    // EXECZ forms read the pair.
                    lines[index].Add(condition);
                }

                SummarizeMasks(lines[index], out exposed[index], out defined[index]);
            }

            var liveIn = new bool[count][];
            for (var index = 0; index < count; index++)
            {
                liveIn[index] = new bool[MaskRegisters.Length];
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                for (var index = count - 1; index >= 0; index--)
                {
                    var live = LiveOutOf(index, count, liveIn);
                    for (var slot = 0; slot < MaskRegisters.Length; slot++)
                    {
                        var value = exposed[index][slot] ||
                            (live[slot] && !defined[index][slot]);
                        if (value && !liveIn[index][slot])
                        {
                            liveIn[index][slot] = true;
                            changed = true;
                        }
                    }
                }
            }

            for (var index = 0; index < count; index++)
            {
                _blockText[index] = ResolveBlockBallots(
                    lines[index],
                    _blockTerminators[index].Condition.Length != 0,
                    LiveOutOf(index, count, liveIn));
            }
        }

        /// <summary>Whether the translated body ended up referring to the wave64 bridge,
        /// which is only known once the mask ballots are resolved.</summary>
        private bool _waveScratchReferenced;

        private void NoteWaveScratchUse()
        {
            foreach (var text in _blockText)
            {
                _waveScratchReferenced |=
                    text.Contains(WaveScratchName, StringComparison.Ordinal);
            }
        }

        private const string WaveScratchName = "sharpemu_wave_scratch";

        /// <summary>Whether the bridge has to be declared. The pre-pass over the program
        /// says whether the entry seed wants it; the emitted body says whether anything
        /// else does. Both have to be asked: a plain V_CMP in a wave64 program emits a
        /// ballot exchange without being a cross-lane op, so a program with one and no
        /// V_CMPX used to reference an array nobody had declared and fail to compile.</summary>
        private bool DeclaresWaveScratch => _usesWaveScratch || _waveScratchReferenced;

        private bool[] LiveOutOf(int block, int count, bool[][] liveIn)
        {
            var live = new bool[MaskRegisters.Length];
            var terminator = _blockTerminators[block];
            Union(live, Successor(terminator.TrueTarget, count, liveIn));
            if (terminator.Kind == TerminatorKind.CondJump)
            {
                Union(live, Successor(terminator.FalseTarget, count, liveIn));
            }

            return live;
        }

        /// <summary>Which of the four mask registers this block reads before writing, and
        /// which it writes. A ballot marker writes both halves of its pair; anything else
        /// follows the same rules the register allocator uses, so a conditional write is
        /// not a definition and keeps the old value live.</summary>
        private static void SummarizeMasks(
            List<string> lines,
            out bool[] exposed,
            out bool[] defined)
        {
            exposed = new bool[MaskRegisters.Length];
            defined = new bool[MaskRegisters.Length];
            var depth = 0;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (TryReadBallotMarker(trimmed, out var loRegister, out _))
                {
                    MarkDefined(exposed, defined, loRegister);
                    MarkDefined(exposed, defined, loRegister + 1);
                    continue;
                }

                var definition = depth == 0 ? DefinitionTarget(trimmed) : -1;
                foreach (var reference in ScanRegisterReferences(trimmed))
                {
                    if (reference.File != 's' || reference.Index < 0 ||
                        (definition >= 0 && reference.Start == 0))
                    {
                        continue;
                    }

                    var slot = MaskSlotOf((uint)reference.Index);
                    if (slot >= 0 && !defined[slot])
                    {
                        exposed[slot] = true;
                    }
                }

                if (definition >= ScalarSlotBase && definition < VectorSlotBase)
                {
                    var slot = MaskSlotOf((uint)(definition - ScalarSlotBase));
                    if (slot >= 0)
                    {
                        defined[slot] = true;
                    }
                }

                foreach (var character in line)
                {
                    depth += character == '{' ? 1 : character == '}' ? -1 : 0;
                }

                depth = Math.Max(depth, 0);
            }

            static void MarkDefined(bool[] exposed, bool[] defined, uint register)
            {
                var slot = MaskSlotOf(register);
                if (slot >= 0 && !exposed[slot])
                {
                    defined[slot] = true;
                }
            }
        }

        /// <summary>Walks a block backwards and expands each marker, now that the live set
        /// at the end of the block is known.</summary>
        private string ResolveBlockBallots(
            List<string> lines,
            bool hasTerminatorCondition,
            bool[] liveOut)
        {
            // The terminator condition was appended for the liveness pass; it is not part
            // of the block's text.
            var bodyCount = hasTerminatorCondition ? lines.Count - 1 : lines.Count;
            var live = (bool[])liveOut.Clone();
            if (hasTerminatorCondition)
            {
                AddMaskReads(live, lines[^1]);
            }

            var resolved = new string?[bodyCount];
            for (var index = bodyCount - 1; index >= 0; index--)
            {
                var line = lines[index];
                var trimmed = line.Trim();
                if (TryReadBallotMarker(trimmed, out var loRegister, out var condition))
                {
                    var loSlot = MaskSlotOf(loRegister);
                    var hiSlot = MaskSlotOf(loRegister + 1);
                    var needed = (loSlot >= 0 && live[loSlot]) || (hiSlot >= 0 && live[hiSlot]);
                    resolved[index] = needed
                        ? ExpandMaskBallot(line, loRegister, condition)
                        : null;
                    if (loSlot >= 0)
                    {
                        live[loSlot] = false;
                    }

                    if (hiSlot >= 0)
                    {
                        live[hiSlot] = false;
                    }

                    continue;
                }

                resolved[index] = line;
                var definition = DefinitionTarget(trimmed);
                if (definition >= ScalarSlotBase && definition < VectorSlotBase)
                {
                    var slot = MaskSlotOf((uint)(definition - ScalarSlotBase));
                    if (slot >= 0)
                    {
                        live[slot] = false;
                    }
                }

                AddMaskReads(live, line);
            }

            var text = new StringBuilder();
            foreach (var line in resolved)
            {
                if (line is not null)
                {
                    text.AppendLine(line);
                }
            }

            return text.ToString();
        }

        private static void AddMaskReads(bool[] live, string line)
        {
            var trimmed = line.Trim();
            var definition = DefinitionTarget(trimmed);
            foreach (var reference in ScanRegisterReferences(trimmed))
            {
                if (reference.File != 's' || reference.Index < 0 ||
                    (definition >= 0 && reference.Start == 0))
                {
                    continue;
                }

                var slot = MaskSlotOf((uint)reference.Index);
                if (slot >= 0)
                {
                    live[slot] = true;
                }
            }
        }

        /// <summary>The cross-simdgroup exchange, at the marker's own indentation.</summary>
        private string ExpandMaskBallot(string markerLine, uint loRegister, string condition)
        {
            var indent = markerLine.Length - markerLine.TrimStart().Length;
            var pad = markerLine[..indent];
            var text = new StringBuilder();

            // simd_ballot is uniform across a simdgroup, so every lane of a half writes
            // the same 32-bit value to that half's slot — no first-lane guard needed.
            // Barrier, read both halves, barrier before the slot can be reused.
            text.AppendLine(
                $"{pad}sharpemu_wave_scratch[(sharpemu_lane >> 5) & 1u] = sharpemu_ballot({condition});");
            text.AppendLine($"{pad}threadgroup_barrier(mem_flags::mem_threadgroup);");
            text.AppendLine($"{pad}s[{loRegister}] = sharpemu_wave_scratch[0];");
            if (loRegister + 1 < ScalarRegisterFileCount)
            {
                text.AppendLine($"{pad}s[{loRegister + 1}] = sharpemu_wave_scratch[1];");
            }

            text.Append($"{pad}threadgroup_barrier(mem_flags::mem_threadgroup);");
            return text.ToString();
        }

        private static bool TryReadBallotMarker(string trimmed, out uint loRegister, out string condition)
        {
            loRegister = 0;
            condition = string.Empty;
            if (!trimmed.StartsWith(MaskBallotMarker, StringComparison.Ordinal))
            {
                return false;
            }

            var open = MaskBallotMarker.Length;
            var close = trimmed.LastIndexOf(')');
            if (open >= trimmed.Length || trimmed[open] != '(' || close <= open)
            {
                return false;
            }

            var arguments = trimmed[(open + 1)..close].Split(',');
            if (arguments.Length != 2 || !uint.TryParse(arguments[0].Trim(), out loRegister))
            {
                return false;
            }

            condition = arguments[1].Trim();
            return true;
        }
    }
}
