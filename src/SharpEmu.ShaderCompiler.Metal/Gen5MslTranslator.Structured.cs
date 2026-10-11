// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

namespace SharpEmu.ShaderCompiler.Metal;

// The structured control-flow assembler: emits each block once, nested in real MSL
// control flow, instead of running them from a PC dispatcher.
//
// Why it matters: the dispatcher's while (active) { switch (pc) } makes every guest
// register live across the loop back edge, because any block may run after any
// other. The Apple GPU compiler then has to keep — or spill — the whole guest
// register file between every pair of blocks, which is what makes translated
// shaders slow enough to be the frame budget. Nesting the blocks in if/else and
// while gives the compiler the program's real CFG, so a value's live range is the
// span it is actually used over.
//
// The recovery is the textbook one: dominators find the back edges, so a header
// with a single entry and a single way out becomes while (true) with continue and
// break; post-dominators find where a scalar branch's two arms reconverge, so a
// conditional becomes if or if/else with that join as the region's end; S_ENDPGM
// and a branch past the program end become an early return with the stage
// epilogue inlined. Metal has no goto, so an edge none of those shapes can
// express — irreducible flow, a loop with several exits, a jump into another
// region — fails the whole shader back to the dispatcher, which is always correct.
//
// Lane semantics are untouched: the conditions are the same scalar-branch
// predicates the dispatcher evaluated (SCC, the VCC/EXEC register pair), each
// Metal thread still follows its own path, and per-lane masking stays in the
// "if (exec)" guards the instruction emitters write.
public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        /// <summary>A region end no block can match, for a loop with no way out but a
        /// return and for the function's own outermost region.</summary>
        private const int NoFollow = int.MinValue;

        /// <summary>True when the assembled structured body needs the step counter
        /// (it has at least one back edge and the cap is enabled).</summary>
        private bool _structuredUsesSteps;

        private sealed record LoopInfo(int Header, bool[] Body, int Follow);

        /// <summary>The loops enclosing the region being emitted, innermost last.</summary>
        private readonly List<LoopInfo> _openLoops = [];

        private int[] _idom = [];
        private int[] _ipdom = [];
        private List<int>[] _successors = [];
        private Dictionary<int, LoopInfo> _loopsByHeader = [];
        private bool[] _structureEmitted = [];
        private bool _structureFailed;

        private bool TryBuildStructuredBody(IReadOnlyList<ShaderBlock> blocks, out string body)
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

            _idom = Dominators(count);
            _ipdom = PostDominators(count);
            if (!TryFindLoops(count, out _loopsByHeader))
            {
                return false;
            }

            _structureEmitted = new bool[count];
            _regionDepth = 0;
            _structureFailed = false;
            _structuredUsesSteps = false;
            _openLoops.Clear();

            var text = new StringBuilder();
            if (_stage == Gen5MslStage.Compute)
            {
                // The partial-group guard the launch state computed: lanes outside the
                // guest dispatch run nothing, which is what "active = false" meant.
                AppendIndented(text, "if (!active)", 1);
                AppendIndented(text, "{", 1);
                AppendExit(text, 2);
                AppendIndented(text, "}", 1);
                text.AppendLine();
            }

            EmitRegion(text, 0, NoFollow, 1);
            if (_structureFailed)
            {
                return false;
            }

            body = text.ToString();
            return true;
        }

        // ---- region emission ----

        /// <summary>Emits the region that starts at <paramref name="node"/> and ends when
        /// control reaches <paramref name="follow"/>. Returns true when the emitted text
        /// can fall out of its end into <paramref name="follow"/>.</summary>
        private bool EmitRegion(
            StringBuilder text,
            int node,
            int follow,
            int indent,
            bool allowLoopHeader = false)
        {
            // Regions nest only where the guest program's conditionals nest, which is
            // shallow in practice; the guard is there so a pathological program takes the
            // dispatcher rather than the stack.
            if (++_regionDepth > MaxRegionDepth)
            {
                _structureFailed = true;
                _regionDepth--;
                return false;
            }

            try
            {
                return EmitRegionCore(text, node, follow, indent, allowLoopHeader);
            }
            finally
            {
                _regionDepth--;
            }
        }

        private const int MaxRegionDepth = 128;

        private int _regionDepth;

        private bool EmitRegionCore(
            StringBuilder text,
            int node,
            int follow,
            int indent,
            bool allowLoopHeader)
        {
            var first = true;
            while (true)
            {
                if (_structureFailed)
                {
                    return false;
                }

                if (node == follow)
                {
                    return true;
                }

                if (node == ExitTarget)
                {
                    AppendExit(text, indent);
                    return false;
                }

                var atHeader = first && allowLoopHeader;
                first = false;
                if (!atHeader && !TryEnterBlock(text, node, indent))
                {
                    return false;
                }

                if (!atHeader && _loopsByHeader.TryGetValue(node, out var loop))
                {
                    if (!EmitLoop(text, loop, indent))
                    {
                        return false;
                    }

                    if (loop.Follow == ExitTarget || loop.Follow == NoFollow)
                    {
                        return false;
                    }

                    node = loop.Follow;
                    continue;
                }

                _structureEmitted[node] = true;
                AppendIndented(
                    text,
                    Gen5MslDump.Enabled ? $"{{ // block {node}" : "{",
                    indent);
                AppendBlockText(text, _blockText[node], indent + 1);
                AppendIndented(text, "}", indent);

                var terminator = _blockTerminators[node];
                if (terminator.Kind == TerminatorKind.Jump ||
                    terminator.TrueTarget == terminator.FalseTarget)
                {
                    // The head of the loop turns the edge into sequencing, a fall out of
                    // the region, a continue, a break, a return, or a failure.
                    node = terminator.TrueTarget;
                    continue;
                }

                // A scalar branch: its arms reconverge at the immediate post-dominator
                // when that block belongs to this region, and otherwise both arms leave
                // the region on their own.
                var join = _ipdom[node];
                if (join < 0 || join == follow || _structureEmitted[join] ||
                    EnclosesLoopBoundary(join))
                {
                    join = follow;
                }

                var taken = new StringBuilder();
                var takenFell = EmitRegion(taken, terminator.TrueTarget, join, indent + 1);
                var fallen = new StringBuilder();
                var fallenFell = EmitRegion(fallen, terminator.FalseTarget, join, indent + 1);
                if (_structureFailed)
                {
                    return false;
                }

                AppendConditional(text, terminator.Condition, taken, fallen, indent);
                if (join == follow)
                {
                    return takenFell || fallenFell;
                }

                if (!takenFell && !fallenFell)
                {
                    return false;
                }

                node = join;
            }
        }

        private bool EmitLoop(StringBuilder text, LoopInfo loop, int indent)
        {
            AppendIndented(text, "while (true)", indent);
            AppendIndented(text, "{", indent);
            _openLoops.Add(loop);
            var fell = EmitRegion(text, loop.Header, loop.Follow, indent + 1, allowLoopHeader: true);
            _openLoops.RemoveAt(_openLoops.Count - 1);
            if (_structureFailed)
            {
                return false;
            }

            if (fell)
            {
                // Falling out of a while (true) body would loop again; the edge that
                // leaves the loop is a break.
                AppendIndented(text, "break;", indent + 1);
            }

            AppendIndented(text, "}", indent);
            return true;
        }

        private static void AppendConditional(
            StringBuilder text,
            string condition,
            StringBuilder taken,
            StringBuilder fallen,
            int indent)
        {
            if (taken.Length == 0 && fallen.Length == 0)
            {
                return;
            }

            if (taken.Length == 0)
            {
                AppendIndented(text, $"if (!({condition}))", indent);
                AppendIndented(text, "{", indent);
                text.Append(fallen);
                AppendIndented(text, "}", indent);
                return;
            }

            AppendIndented(text, $"if ({condition})", indent);
            AppendIndented(text, "{", indent);
            text.Append(taken);
            AppendIndented(text, "}", indent);
            if (fallen.Length == 0)
            {
                return;
            }

            AppendIndented(text, "else", indent);
            AppendIndented(text, "{", indent);
            text.Append(fallen);
            AppendIndented(text, "}", indent);
        }

        /// <summary>Resolves arriving at <paramref name="node"/> from the statement
        /// just emitted. A back edge to the enclosing loop's header is a continue and
        /// the edge that leaves it is a break; an edge to an outer loop's header or exit,
        /// or back into a region already emitted, is one MSL cannot write without a
        /// labelled break or a goto, so the shader falls back to the dispatcher.
        /// Returns false when the arrival was a statement or a failure, true when the
        /// block itself should now be emitted inline.</summary>
        private bool TryEnterBlock(StringBuilder text, int node, int indent)
        {
            var innermost = _openLoops.Count != 0 ? _openLoops[^1] : null;
            if (innermost is not null && node == innermost.Header)
            {
                AppendBackEdge(text, indent);
                AppendIndented(text, "continue;", indent);
                return false;
            }

            if (innermost is not null && node == innermost.Follow)
            {
                AppendIndented(text, "break;", indent);
                return false;
            }

            foreach (var loop in _openLoops)
            {
                if (node == loop.Header || node == loop.Follow)
                {
                    _structureFailed = true;
                    return false;
                }
            }

            if (_structureEmitted[node])
            {
                _structureFailed = true;
                return false;
            }

            return true;
        }

        /// <summary>The dispatcher charged every block against the step cap so a
        /// mistranslated loop-exit condition could not wedge the GPU queue. The
        /// structured form charges every loop iteration, which keeps the same valve.</summary>
        private void AppendBackEdge(StringBuilder text, int indent)
        {
            if (_maxDispatcherSteps <= 0)
            {
                return;
            }

            _structuredUsesSteps = true;
            AppendIndented(text, $"if (++steps >= {_maxDispatcherSteps}u)", indent);
            AppendIndented(text, "{", indent);
            AppendIndented(text, StepCapReportCall, indent + 1);
            AppendExit(text, indent + 1);
            AppendIndented(text, "}", indent);
        }

        /// <summary>Leaving the program. With no goto the stage epilogue is inlined at
        /// every exit instead of being jumped to.</summary>
        private void AppendExit(StringBuilder text, int indent)
        {
            switch (_stage)
            {
                case Gen5MslStage.Pixel:
                    // EXP.VM publishes EXEC as the pixel-valid mask; a program without
                    // VM falls back to the final EXEC value.
                    AppendIndented(
                        text,
                        _usesPixelValidMask
                            ? "if (!pixel_valid_mask_active)"
                            : "if (!exec)",
                        indent);
                    AppendIndented(text, "{", indent);
                    AppendIndented(text, "discard_fragment();", indent + 1);
                    AppendIndented(text, "}", indent);
                    AppendIndented(text, "return sharpemu_out;", indent);
                    return;
                case Gen5MslStage.Vertex:
                    AppendIndented(text, "return sharpemu_out;", indent);
                    return;
                default:
                    AppendIndented(text, "return;", indent);
                    return;
            }
        }

        private bool EnclosesLoopBoundary(int node)
        {
            foreach (var loop in _openLoops)
            {
                if (node == loop.Header || node == loop.Follow)
                {
                    return true;
                }
            }

            return false;
        }

        // ---- CFG analysis ----

        /// <summary>Natural loops, keyed by header. Fails on irreducible flow (a back
        /// edge whose target does not dominate its source) and on a loop with more than
        /// one way out, neither of which while/break/continue can express.</summary>
        private bool TryFindLoops(int count, out Dictionary<int, LoopInfo> loops)
        {
            loops = [];
            var predecessors = new List<int>[count];
            for (var index = 0; index < count; index++)
            {
                predecessors[index] = [];
            }

            for (var index = 0; index < count; index++)
            {
                foreach (var successor in _successors[index])
                {
                    if (successor != ExitTarget)
                    {
                        predecessors[successor].Add(index);
                    }
                }
            }

            var bodies = new Dictionary<int, bool[]>();
            for (var index = 0; index < count; index++)
            {
                foreach (var successor in _successors[index])
                {
                    if (successor == ExitTarget || _idom[index] == -1)
                    {
                        continue;
                    }

                    if (!Dominates(successor, index))
                    {
                        // Only an edge whose target dominates its source closes a
                        // natural loop. Anything else retreating is irreducible flow,
                        // which the emitter rejects when it meets the block twice.
                        continue;
                    }

                    if (!bodies.TryGetValue(successor, out var body))
                    {
                        body = new bool[count];
                        body[successor] = true;
                        bodies[successor] = body;
                    }

                    CollectLoopBody(body, successor, index, predecessors);
                }
            }

            foreach (var (header, body) in bodies)
            {
                var follow = NoFollow;
                for (var index = 0; index < count; index++)
                {
                    if (!body[index])
                    {
                        continue;
                    }

                    foreach (var successor in _successors[index])
                    {
                        if (successor == ExitTarget || body[successor])
                        {
                            continue;
                        }

                        if (follow != NoFollow && follow != successor)
                        {
                            // Several distinct exits; only one can be the break target.
                            return false;
                        }

                        follow = successor;
                    }
                }

                loops[header] = new LoopInfo(header, body, follow);
            }

            // Loops must nest: one body contains the other or they are disjoint.
            foreach (var outer in loops.Values)
            {
                foreach (var inner in loops.Values)
                {
                    if (ReferenceEquals(outer, inner))
                    {
                        continue;
                    }

                    var overlap = false;
                    var innerInOuter = true;
                    var outerInInner = true;
                    for (var index = 0; index < count; index++)
                    {
                        overlap |= outer.Body[index] && inner.Body[index];
                        innerInOuter &= !inner.Body[index] || outer.Body[index];
                        outerInInner &= !outer.Body[index] || inner.Body[index];
                    }

                    if (overlap && !innerInOuter && !outerInInner)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void CollectLoopBody(
            bool[] body,
            int header,
            int latch,
            List<int>[] predecessors)
        {
            var stack = new Stack<int>();
            if (!body[latch])
            {
                body[latch] = true;
                stack.Push(latch);
            }

            while (stack.Count != 0)
            {
                var node = stack.Pop();
                foreach (var predecessor in predecessors[node])
                {
                    if (predecessor == header || body[predecessor])
                    {
                        continue;
                    }

                    body[predecessor] = true;
                    stack.Push(predecessor);
                }
            }
        }

        private bool Dominates(int dominator, int node)
        {
            for (var cursor = node; cursor != -1; cursor = _idom[cursor])
            {
                if (cursor == dominator)
                {
                    return true;
                }

                if (_idom[cursor] == cursor)
                {
                    break;
                }
            }

            return false;
        }

        /// <summary>Immediate dominators by the Cooper-Harvey-Kennedy iteration;
        /// unreachable blocks get -1.</summary>
        private int[] Dominators(int count)
        {
            var order = ReversePostorder(count, forward: true, out var index);
            var idom = new int[count];
            Array.Fill(idom, -1);
            idom[0] = 0;
            var predecessors = EdgeLists(count, forward: false);
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var node in order)
                {
                    if (node == 0)
                    {
                        continue;
                    }

                    var candidate = -1;
                    foreach (var predecessor in predecessors[node])
                    {
                        if (idom[predecessor] == -1)
                        {
                            continue;
                        }

                        candidate = candidate == -1
                            ? predecessor
                            : Intersect(candidate, predecessor, idom, index);
                    }

                    if (candidate != -1 && idom[node] != candidate)
                    {
                        idom[node] = candidate;
                        changed = true;
                    }
                }
            }

            return idom;
        }

        /// <summary>Immediate post-dominators, computed as dominators of the reverse CFG
        /// rooted at the single exit. A block that cannot reach the exit gets -1, and a
        /// block whose post-dominator is the exit itself gets <see cref="ExitTarget"/>.</summary>
        private int[] PostDominators(int count)
        {
            // The exit is node "count" in the reversed graph.
            var successors = new List<int>[count + 1];
            var predecessors = new List<int>[count + 1];
            for (var node = 0; node <= count; node++)
            {
                successors[node] = [];
                predecessors[node] = [];
            }

            for (var node = 0; node < count; node++)
            {
                foreach (var target in _successors[node])
                {
                    var resolved = target == ExitTarget ? count : target;
                    // Reversed: the post-dominator problem is dominance from the exit.
                    successors[resolved].Add(node);
                    predecessors[node].Add(resolved);
                }
            }

            var order = new List<int>(count + 1);
            var index = new int[count + 1];
            Array.Fill(index, -1);
            var visited = new bool[count + 1];
            var postorder = new List<int>(count + 1);
            var stack = new Stack<(int Node, int Next)>();
            visited[count] = true;
            stack.Push((count, 0));
            while (stack.Count != 0)
            {
                var (node, next) = stack.Pop();
                if (next < successors[node].Count)
                {
                    stack.Push((node, next + 1));
                    var successor = successors[node][next];
                    if (!visited[successor])
                    {
                        visited[successor] = true;
                        stack.Push((successor, 0));
                    }

                    continue;
                }

                postorder.Add(node);
            }

            postorder.Reverse();
            foreach (var node in postorder)
            {
                index[node] = order.Count;
                order.Add(node);
            }

            var idom = new int[count + 1];
            Array.Fill(idom, -1);
            idom[count] = count;
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var node in order)
                {
                    if (node == count)
                    {
                        continue;
                    }

                    var candidate = -1;
                    foreach (var predecessor in predecessors[node])
                    {
                        if (idom[predecessor] == -1)
                        {
                            continue;
                        }

                        candidate = candidate == -1
                            ? predecessor
                            : Intersect(candidate, predecessor, idom, index);
                    }

                    if (candidate != -1 && idom[node] != candidate)
                    {
                        idom[node] = candidate;
                        changed = true;
                    }
                }
            }

            var result = new int[count];
            for (var node = 0; node < count; node++)
            {
                result[node] = idom[node] == -1 ? -1 : idom[node] == count ? ExitTarget : idom[node];
            }

            return result;
        }

        private static int Intersect(int left, int right, int[] idom, int[] index)
        {
            while (left != right)
            {
                while (index[left] > index[right])
                {
                    left = idom[left];
                }

                while (index[right] > index[left])
                {
                    right = idom[right];
                }
            }

            return left;
        }

        private List<int>[] EdgeLists(int count, bool forward)
        {
            var lists = new List<int>[count];
            for (var node = 0; node < count; node++)
            {
                lists[node] = [];
            }

            for (var node = 0; node < count; node++)
            {
                foreach (var target in _successors[node])
                {
                    if (target == ExitTarget)
                    {
                        continue;
                    }

                    if (forward)
                    {
                        lists[node].Add(target);
                    }
                    else
                    {
                        lists[target].Add(node);
                    }
                }
            }

            return lists;
        }

        private List<int> ReversePostorder(int count, bool forward, out int[] index)
        {
            var successors = EdgeLists(count, forward);
            var visited = new bool[count];
            var postorder = new List<int>(count);
            var stack = new Stack<(int Node, int Next)>();
            visited[0] = true;
            stack.Push((0, 0));
            while (stack.Count != 0)
            {
                var (node, next) = stack.Pop();
                if (next < successors[node].Count)
                {
                    stack.Push((node, next + 1));
                    var successor = successors[node][next];
                    if (!visited[successor])
                    {
                        visited[successor] = true;
                        stack.Push((successor, 0));
                    }

                    continue;
                }

                postorder.Add(node);
            }

            postorder.Reverse();
            index = new int[count];
            Array.Fill(index, -1);
            for (var position = 0; position < postorder.Count; position++)
            {
                index[postorder[position]] = position;
            }

            return postorder;
        }
    }
}
