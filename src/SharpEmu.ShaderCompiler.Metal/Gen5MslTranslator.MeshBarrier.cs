// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace SharpEmu.ShaderCompiler.Metal;

// A mesh program with S_BARRIER normally runs from the dispatcher, where a barrier parks the
// wave and the loop body rendezvouses the whole workgroup (see Gen5MslTranslator.Mesh.cs).
// That costs two threadgroup barriers and three atomics per guest barrier and keeps every
// guest register live across the loop. When every barrier is workgroup-uniform the barrier
// can instead be emitted where it stands, and the body assembled without the rendezvous:
//
//  - one wave per threadgroup: every pc is uniform, so any control flow reaches a barrier
//    with the whole group; or
//  - several waves, but every block holding a barrier post-dominates the entry and sits at
//    the top level of the body (outside any conditional or loop), so each thread runs it
//    exactly once, in program order.
//
// The structured assembler is tried first. When it cannot express the fused ES+GS program's
// control flow, a linear form keeps the dispatcher's blocks in pc order, each guarded by
// "pc == block", with the ranges of back edges wrapped in while loops.
public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        private const string MeshBarrierMarker = "sharpemu_mesh_wait = true;";

        private bool[]? _meshBarrierBlocks;
        private bool _meshBarrierNested;

        private string MeshBarrierStatement =>
            UsesDeviceLds || ProgramWritesDeviceMemory()
                ? "threadgroup_barrier(mem_flags::mem_threadgroup | mem_flags::mem_device);"
                : "threadgroup_barrier(mem_flags::mem_threadgroup);";

        /// <summary>Builds the barrier-in-place body of a mesh program, or returns false
        /// (leaving the blocks as the dispatcher wants them) when some barrier is not
        /// workgroup-uniform. <paramref name="linear"/> reports which form was built; the
        /// structured form needs no pc or active variable, the linear one keeps both.</summary>
        private bool TryBuildMeshBarrierBody(IReadOnlyList<ShaderBlock> blocks, out string body, out bool linear)
        {
            body = string.Empty;
            linear = false;
            var count = blocks.Count;
            var original = _blockText.ToArray();
            var hasBarrier = new bool[count];
            var statement = MeshBarrierStatement;
            for (var index = 0; index < count; index++)
            {
                hasBarrier[index] = original[index].Contains(MeshBarrierMarker, StringComparison.Ordinal);
                _blockText[index] = original[index].Replace(MeshBarrierMarker, statement, StringComparison.Ordinal);
            }

            var oneWave = Mesh.WavesPerThreadgroup == 1;
            _meshBarrierBlocks = hasBarrier;
            _meshBarrierNested = false;
            var structured = TryBuildStructuredBody(blocks, out body);
            _meshBarrierBlocks = null;
            if (structured && (oneWave || !_meshBarrierNested))
            {
                return true;
            }

            if (TryBuildLinearBody(blocks, hasBarrier, oneWave, out body))
            {
                linear = true;
                return true;
            }

            for (var index = 0; index < count; index++)
            {
                _blockText[index] = original[index];
            }

            body = string.Empty;
            return false;
        }

        private bool TryBuildLinearBody(
            IReadOnlyList<ShaderBlock> blocks,
            bool[] hasBarrier,
            bool oneWave,
            out string body)
        {
            body = string.Empty;
            var count = blocks.Count;
            _successors = new List<int>[count];
            for (var index = 0; index < count; index++)
            {
                var terminator = _blockTerminators[index];
                _successors[index] = terminator.Kind == TerminatorKind.CondJump
                    ? [terminator.TrueTarget, terminator.FalseTarget]
                    : [terminator.TrueTarget];
            }

            // Back-edge ranges, one per header: [header, last block that jumps back to it].
            var rangeEnd = new SortedDictionary<int, int>();
            for (var index = 0; index < count; index++)
            {
                foreach (var target in _successors[index])
                {
                    if (target >= 0 && target <= index)
                    {
                        rangeEnd[target] = rangeEnd.TryGetValue(target, out var end) ? Math.Max(end, index) : index;
                    }
                }
            }

            // Ranges have to nest or be disjoint, or a while loop cannot span exactly one.
            var ranges = rangeEnd.Select(static pair => (Lo: pair.Key, Hi: pair.Value)).ToList();
            for (var a = 0; a < ranges.Count; a++)
            {
                for (var b = a + 1; b < ranges.Count; b++)
                {
                    if (ranges[b].Lo <= ranges[a].Hi && ranges[b].Hi > ranges[a].Hi)
                    {
                        return false;
                    }
                }
            }

            if (!oneWave)
            {
                _ipdom = PostDominators(count);
                for (var index = 0; index < count; index++)
                {
                    if (!hasBarrier[index])
                    {
                        continue;
                    }

                    // The barrier has to be reached by every thread: it post-dominates the
                    // entry, and no loop around it lets threads arrive a different number
                    // of times.
                    var reached = false;
                    for (var node = 0; node >= 0; node = _ipdom[node])
                    {
                        if (node == index)
                        {
                            reached = true;
                            break;
                        }
                    }

                    if (!reached || ranges.Any(range => index >= range.Lo && index <= range.Hi))
                    {
                        return false;
                    }
                }
            }

            var text = new StringBuilder();
            var statement = MeshBarrierStatement;
            var open = new Stack<int>();
            for (var index = 0; index < count; index++)
            {
                if (rangeEnd.TryGetValue(index, out var hi))
                {
                    AppendIndented(text, $"while (active && pc >= {index}u && pc <= {hi}u)", 1);
                    AppendIndented(text, "{", 1);
                    if (_maxDispatcherSteps > 0)
                    {
                        AppendIndented(text, $"if (++steps >= {_maxDispatcherSteps}u)", 2);
                        AppendIndented(text, "{", 2);
                        AppendIndented(text, "active = false;", 3);
                        AppendIndented(text, StepCapReportCode, 3);
                        AppendIndented(text, "}", 2);
                    }

                    open.Push(hi);
                }

                var depth = 1 + open.Count;
                AppendIndented(
                    text,
                    Gen5MslDump.Enabled
                        ? $"if (active && pc == {index}u) // pc=0x{blocks[index].StartPc:X4}"
                        : $"if (active && pc == {index}u)",
                    depth);
                AppendIndented(text, "{", depth);

                // With several waves the barrier must not sit under "active && pc == N": a
                // wave that ran out of steps clears active and would skip it while the others
                // wait. These blocks post-dominate the entry and lie outside every loop, so
                // every thread reaches the barrier once, and it can stand just after the block.
                var blockText = _blockText[index];
                var unguardedBarrier = !oneWave && hasBarrier[index];
                if (unguardedBarrier)
                {
                    blockText = blockText.Replace(statement, string.Empty, StringComparison.Ordinal);
                }

                AppendBlockText(text, blockText, depth + 1);
                foreach (var line in _blockTerminators[index].DispatcherLines)
                {
                    AppendIndented(text, line, depth + 1);
                }

                AppendIndented(text, "}", depth);
                if (unguardedBarrier)
                {
                    AppendIndented(text, statement, depth);
                }

                while (open.Count != 0 && open.Peek() == index)
                {
                    open.Pop();
                    AppendIndented(text, "}", 1 + open.Count);
                }
            }

            body = text.ToString();
            return true;
        }
    }
}
