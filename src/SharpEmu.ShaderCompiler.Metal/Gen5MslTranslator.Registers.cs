// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace SharpEmu.ShaderCompiler.Metal;

// Guest register allocation.
//
// The emitters write every SGPR and VGPR access as an index into one of two
// arrays, s[] and v[], which used to be declared at their architectural sizes:
// "uint s[128] = {}; uint v[256] = {};" — 384 dwords of per-thread state, all
// zero-initialized, in every single shader. A shader that touches twelve
// registers still paid for all of them: 1.5 KB of per-thread storage the Apple
// GPU compiler has to promote or spill, and the zero-fill at entry.
//
// This pass reads back the MSL the emitters just produced and finds which
// registers it actually names. That catches every implicit one for free — the
// VCC and EXEC halves, M0, the extra dwords a 64-bit or multi-dword op writes,
// the architectural registers the launch state seeds — because the emitters have
// already written them out by index. Each one named becomes its own local
// ("uint s14, v3;"), which the compiler keeps in a register with no aliasing
// question to answer.
//
// Dynamic indexing (V_MOVREL* and friends address the register file through M0)
// is the exception: that file stays an array, since any element may be the one
// addressed. The other file is still scalarized.
//
// Initialization follows liveness: a register is zero-filled only when some path
// from entry reads it before writing it. Registers the program defines before
// use — the overwhelming majority — are declared without an initializer, and the
// launch state's seeded inputs count as definitions, so they are never clobbered.
public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        private const int ScalarSlotBase = 0;
        private const int VectorSlotBase = (int)ScalarRegisterFileCount;
        private const int RegisterSlotCount =
            (int)ScalarRegisterFileCount + (int)VectorRegisterFileCount;

        /// <summary>Which guest registers this shader declares, how, and which of them
        /// need an initial value.</summary>
        private sealed class RegisterPlan
        {
            public bool ScalarArray { get; init; }

            public bool VectorArray { get; init; }

            public SortedSet<uint> Scalars { get; } = [];

            public SortedSet<uint> Vectors { get; } = [];

            public HashSet<uint> ScalarNeedsInitialValue { get; } = [];

            public HashSet<uint> VectorNeedsInitialValue { get; } = [];

            /// <summary>Rewrites s[14] into s14 for whichever file was scalarized. The
            /// index form stays for a file kept as an array.</summary>
            public string Rewrite(string text)
            {
                if (ScalarArray && VectorArray)
                {
                    return text;
                }

                var result = new StringBuilder(text.Length);
                var cursor = 0;
                foreach (var reference in ScanRegisterReferences(text))
                {
                    if (reference.Index < 0 ||
                        (reference.File == 's' ? ScalarArray : VectorArray))
                    {
                        continue;
                    }

                    result.Append(text, cursor, reference.Start - cursor);
                    result.Append(reference.File);
                    result.Append(reference.Index);
                    cursor = reference.Start + reference.Length;
                }

                result.Append(text, cursor, text.Length - cursor);
                return result.ToString();
            }
        }

        /// <summary>One s[...] or v[...] occurrence. <see cref="Index"/> is -1 when the
        /// subscript is not a decimal literal, which is how dynamic indexing shows up.</summary>
        private readonly record struct RegisterReference(
            char File,
            int Index,
            int Start,
            int Length);

        /// <summary>Finds every s[...]/v[...] in generated MSL. The name must stand alone:
        /// "sharpemu_lds[" and "buffers[" end in s and v but are preceded by an
        /// identifier character, so they are skipped.</summary>
        private static IEnumerable<RegisterReference> ScanRegisterReferences(string text)
        {
            for (var index = 0; index + 1 < text.Length; index++)
            {
                var file = text[index];
                if ((file != 's' && file != 'v') || text[index + 1] != '[')
                {
                    continue;
                }

                if (index > 0 && IsIdentifierCharacter(text[index - 1]))
                {
                    continue;
                }

                var cursor = index + 2;
                var value = 0L;
                var digits = 0;
                while (cursor < text.Length && text[cursor] >= '0' && text[cursor] <= '9')
                {
                    value = (value * 10) + (text[cursor] - '0');
                    cursor++;
                    digits++;
                }

                if (digits != 0 && cursor < text.Length && text[cursor] == ']' &&
                    value < (file == 's' ? ScalarRegisterFileCount : VectorRegisterFileCount))
                {
                    yield return new RegisterReference(file, (int)value, index, cursor + 1 - index);
                    index = cursor;
                    continue;
                }

                // Not a literal subscript: the file is addressed dynamically.
                yield return new RegisterReference(file, -1, index, 2);
            }
        }

        private static bool IsIdentifierCharacter(char value) =>
            value == '_' || char.IsLetterOrDigit(value);

        private RegisterPlan BuildRegisterPlan(
            IReadOnlyList<ShaderBlock> blocks,
            string initialState)
        {
            if (!_options.RegisterAllocation)
            {
                return new RegisterPlan { ScalarArray = true, VectorArray = true };
            }

            // Everything the finished function can name, whichever form the body took.
            var texts = new List<string>(blocks.Count + 1) { initialState };
            for (var index = 0; index < blocks.Count; index++)
            {
                var terminator = _blockTerminators[index];
                texts.Add(_blockText[index]);
                texts.Add(terminator.Condition);
                foreach (var line in terminator.DispatcherLines)
                {
                    texts.Add(line);
                }
            }

            var dynamicScalar = false;
            var dynamicVector = false;
            var scalars = new SortedSet<uint>();
            var vectors = new SortedSet<uint>();
            foreach (var text in texts)
            {
                foreach (var reference in ScanRegisterReferences(text))
                {
                    if (reference.Index < 0)
                    {
                        dynamicScalar |= reference.File == 's';
                        dynamicVector |= reference.File == 'v';
                    }
                    else
                    {
                        (reference.File == 's' ? scalars : vectors).Add((uint)reference.Index);
                    }
                }
            }

            var plan = new RegisterPlan { ScalarArray = dynamicScalar, VectorArray = dynamicVector };
            plan.Scalars.UnionWith(scalars);
            plan.Vectors.UnionWith(vectors);

            var live = LiveAtEntry(blocks, initialState);
            foreach (var register in plan.Scalars)
            {
                if (live[ScalarSlotBase + (int)register])
                {
                    plan.ScalarNeedsInitialValue.Add(register);
                }
            }

            foreach (var register in plan.Vectors)
            {
                if (live[VectorSlotBase + (int)register])
                {
                    plan.VectorNeedsInitialValue.Add(register);
                }
            }

            return plan;
        }

        /// <summary>Backward liveness over the block CFG, with the launch state as a
        /// pseudo-block ahead of the entry: the result is the set of registers some path
        /// reads before writing, which is exactly the set that needs an initial value.
        /// The CFG used is the dispatcher's (every block, successors from the
        /// terminators), so it over-approximates the structured form, never the reverse.</summary>
        private bool[] LiveAtEntry(IReadOnlyList<ShaderBlock> blocks, string initialState)
        {
            var count = blocks.Count;
            if (_options.ForceRegisterInitialization)
            {
                var all = new bool[RegisterSlotCount];
                Array.Fill(all, true);
                return all;
            }

            var exposed = new bool[count + 1][];
            var killed = new bool[count + 1][];
            for (var index = 0; index < count; index++)
            {
                var terminator = _blockTerminators[index];
                var lines = new List<string>(SplitLines(_blockText[index]));
                if (terminator.Condition.Length != 0)
                {
                    // The branch condition reads at the end of the block.
                    lines.Add(terminator.Condition);
                }

                Summarize(lines, out exposed[index], out killed[index]);
            }

            Summarize([.. SplitLines(initialState)], out exposed[count], out killed[count]);

            var liveIn = new bool[count + 1][];
            for (var index = 0; index <= count; index++)
            {
                liveIn[index] = new bool[RegisterSlotCount];
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                for (var index = count - 1; index >= 0; index--)
                {
                    var terminator = _blockTerminators[index];
                    var outLive = new bool[RegisterSlotCount];
                    Union(outLive, Successor(terminator.TrueTarget, count, liveIn));
                    if (terminator.Kind == TerminatorKind.CondJump)
                    {
                        Union(outLive, Successor(terminator.FalseTarget, count, liveIn));
                    }

                    for (var slot = 0; slot < RegisterSlotCount; slot++)
                    {
                        var value = exposed[index][slot] || (outLive[slot] && !killed[index][slot]);
                        if (value && !liveIn[index][slot])
                        {
                            liveIn[index][slot] = true;
                            changed = true;
                        }
                    }
                }
            }

            var result = new bool[RegisterSlotCount];
            for (var slot = 0; slot < RegisterSlotCount; slot++)
            {
                result[slot] = exposed[count][slot] ||
                    (count != 0 && liveIn[0][slot] && !killed[count][slot]);
            }

            return result;
        }

        private static bool[]? Successor(int target, int count, bool[][] liveIn) =>
            target == ExitTarget || target >= count ? null : liveIn[target];

        private static void Union(bool[] target, bool[]? source)
        {
            if (source is null)
            {
                return;
            }

            for (var slot = 0; slot < target.Length; slot++)
            {
                target[slot] |= source[slot];
            }
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            var start = 0;
            while (start < text.Length)
            {
                var end = text.IndexOf('\n', start);
                if (end < 0)
                {
                    end = text.Length;
                }

                yield return text[start..end].TrimEnd('\r');
                start = end + 1;
            }
        }

        /// <summary>Per-block gen/kill: which registers are read before being written in
        /// this block, and which are definitely written by it. "Definitely" means an
        /// unconditional top-level assignment — a statement at brace depth zero of the
        /// form "s[14] = ...;". An EXEC-guarded store ("if (exec) { v[3] = ...; }") is
        /// not a definition, so a register only ever written under EXEC still gets an
        /// initial value.</summary>
        private static void Summarize(List<string> lines, out bool[] exposed, out bool[] killed)
        {
            exposed = new bool[RegisterSlotCount];
            killed = new bool[RegisterSlotCount];
            var depth = 0;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                var definition = depth == 0 ? DefinitionTarget(trimmed) : -1;
                foreach (var reference in ScanRegisterReferences(trimmed))
                {
                    if (reference.Index < 0)
                    {
                        // A dynamically indexed file keeps its array, which is always
                        // zero-filled, so liveness over it does not matter.
                        continue;
                    }

                    if (definition >= 0 && reference.Start == 0)
                    {
                        continue;
                    }

                    var slot = Slot(reference.File, reference.Index);
                    if (!killed[slot])
                    {
                        exposed[slot] = true;
                    }
                }

                if (definition >= 0)
                {
                    killed[definition] = true;
                }

                foreach (var character in line)
                {
                    depth += character == '{' ? 1 : character == '}' ? -1 : 0;
                }

                depth = Math.Max(depth, 0);
            }
        }

        private static int Slot(char file, int index) =>
            (file == 's' ? ScalarSlotBase : VectorSlotBase) + index;

        /// <summary>The register slot a line unconditionally assigns, or -1.</summary>
        private static int DefinitionTarget(string trimmed)
        {
            if (trimmed.Length == 0 || !trimmed.EndsWith(';') ||
                (trimmed[0] != 's' && trimmed[0] != 'v') ||
                trimmed.Length < 2 || trimmed[1] != '[')
            {
                return -1;
            }

            var close = trimmed.IndexOf(']', 2);
            if (close < 0 || !trimmed.AsSpan(close + 1).StartsWith(" = "))
            {
                return -1;
            }

            if (!uint.TryParse(trimmed.AsSpan(2, close - 2), out var register))
            {
                return -1;
            }

            var limit = trimmed[0] == 's' ? ScalarRegisterFileCount : VectorRegisterFileCount;
            return register < limit ? Slot(trimmed[0], (int)register) : -1;
        }

        private void EmitRegisterFile(StringBuilder source, RegisterPlan plan, bool structured)
        {
            if (plan.ScalarArray)
            {
                source.AppendLine($"    uint s[{ScalarRegisterFileCount}] = {{}};");
            }
            else
            {
                AppendRegisterDeclarations(source, 's', plan.Scalars, plan.ScalarNeedsInitialValue);
            }

            if (plan.VectorArray)
            {
                source.AppendLine($"    uint v[{VectorRegisterFileCount}] = {{}};");
            }
            else
            {
                AppendRegisterDeclarations(source, 'v', plan.Vectors, plan.VectorNeedsInitialValue);
            }

            source.AppendLine("    bool exec = true;");
            if (_usesPixelValidMask)
            {
                source.AppendLine("    bool pixel_valid_mask_active = true;");
            }

            source.AppendLine("    bool vcc = false;");
            source.AppendLine("    bool scc = false;");
            if (!structured)
            {
                source.AppendLine("    uint pc = 0u;");
                source.AppendLine("    bool active = true;");
                source.AppendLine("    uint steps = 0u;");
                return;
            }

            if (_stage == Gen5MslStage.Compute)
            {
                // Still carries the partial-group guard the launch state computes.
                source.AppendLine("    bool active = true;");
            }

            if (_structuredUsesSteps)
            {
                source.AppendLine("    uint steps = 0u;");
            }
        }

        private static void AppendRegisterDeclarations(
            StringBuilder source,
            char file,
            SortedSet<uint> registers,
            HashSet<uint> needsInitialValue)
        {
            AppendRegisterDeclaration(source, file, registers.Where(needsInitialValue.Contains), true);
            AppendRegisterDeclaration(source, file, registers.Where(register => !needsInitialValue.Contains(register)), false);
        }

        private static void AppendRegisterDeclaration(
            StringBuilder source,
            char file,
            IEnumerable<uint> registers,
            bool initialize)
        {
            var line = new StringBuilder();
            foreach (var register in registers)
            {
                var name = initialize ? $"{file}{register} = 0u" : $"{file}{register}";
                if (line.Length == 0)
                {
                    line.Append("    uint ").Append(name);
                    continue;
                }

                if (line.Length + name.Length + 2 > 96)
                {
                    source.Append(line).AppendLine(";");
                    line.Clear();
                    line.Append("    uint ").Append(name);
                    continue;
                }

                line.Append(", ").Append(name);
            }

            if (line.Length != 0)
            {
                source.Append(line).AppendLine(";");
            }
        }
    }
}
