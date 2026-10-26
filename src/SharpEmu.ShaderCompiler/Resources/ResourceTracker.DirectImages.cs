// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Resources;

public sealed partial class ResourceTracker
{
    // The records a counted loop's direct image selects among. A loop whose bound is read per draw
    // and runs further reads its later records' images as the first one's.
    internal const int MaxLoopDirectCandidates = 64;

    // The values a lane-read selector may take below the guard that bounds it (Ghost of Tsushima's material
    // records: an 8-bit material id below 255); a record past the end of the table reads as null. (A V#'s table
    // of records is a bindless one: TryMakeDenseIndirectImage.)
    internal const int MaxLaneDirectCandidates = 256;

    private bool TryMakeDirectImage(ScalarValue handle, out IndirectImagePlan plan)
    {
        plan = null!;
        if (handle.Kind != ScalarValueKind.ImageHandle || handle.Operands.Length != 8)
            return false;

        // The descriptor is read through an address (s_load) or a V# (s_buffer_load), all eight dwords alike.
        var reads = handle.Operands;
        var readKind = reads[0].Kind;
        var memoryKind = readKind == ScalarValueKind.ScalarBufferWord ? MemoryResourceKind.ScalarBuffer : MemoryResourceKind.ScalarAddress;
        var memoryIndices = new int[8];
        var canSuppressMemoryReads = true;
        for (var component = 0; component < reads.Length; component++)
        {
            var read = reads[component];
            if (read.Kind is not (ScalarValueKind.ScalarAddressWord or ScalarValueKind.ScalarBufferWord) || read.Kind != readKind ||
                read.MemoryIndex < 0 || read.MemoryIndex >= _plan.Memory.Count || !MemoryIndexBelongsTo(read.MemoryIndex, read) ||
                !UsesOnly(read, [handle])) return false;
            var memory = _plan.Memory[read.MemoryIndex];
            if (memory.Kind != memoryKind || memory.DataBits != 32 || memory.DataDwords != 1)
                return false;
            canSuppressMemoryReads &= HasOnlyImageConsumers(memory, handle);
            memoryIndices[component] = read.MemoryIndex;
        }

        // The key is the load's dynamic offset, whichever of its dwords the descriptor starts at (the second
        // descriptor of an s_buffer_load_dwordx16 starts at the ninth).
        var keyRead = reads[0];
        var keyMemory = _plan.Memory[keyRead.MemoryIndex];
        var keyInstruction = _graph.Program.Instructions.First(instruction => instruction.Pc == keyMemory.Pc);
        if (keyInstruction.Control is not Gen5ScalarMemoryControl { DynamicOffsetRegister: not null })
            return false;
        var block = _graph.ControlFlow.Blocks.First(candidate => keyMemory.Pc >= candidate.StartPc && keyMemory.Pc < candidate.EndPc);
        if (memoryIndices.Any(index => _plan.Memory[index].Pc < block.StartPc || _plan.Memory[index].Pc >= block.EndPc))
            return false;

        // The record offset is built from one selector: a bit scan of a lane mask (s_ff1), the counter of a
        // loop over a table of records that each hold a descriptor (Ghost of Tsushima's terrain height patches),
        // or a value read from the wave's lanes behind a guard that bounds it (its material records).
        var pending = new Stack<ScalarValue>(reads.Select(read => read.Operands[1]));
        var visited = new HashSet<ScalarValue>();
        ScalarValue? selector = null;
        while (pending.TryPop(out var value))
        {
            if (!visited.Add(value)) continue;
            if (value.Kind == ScalarValueKind.Phi && _graph.ResolveInvariantPhi(value) is { } invariant)
            {
                pending.Push(invariant);
                continue;
            }
            if ((value.Kind == ScalarValueKind.Operation && value.Operation == ScalarOperation.FindLowestBit32) ||
                value.Kind == ScalarValueKind.Phi || IsLaneReadSelector(value))
            {
                if (selector is not null && !ReferenceEquals(selector, value)) return false;
                selector = value;
                continue;
            }
            if (!value.IsConstant && value.Kind != ScalarValueKind.Operation) return false;
            foreach (var operand in value.Operands) pending.Push(operand);
        }
        if (selector is null) return false;

        IEnumerable<uint> results;
        ScalarValue? limit = null;
        var inclusive = false;
        var laneRead = IsLaneReadSelector(selector);
        if (laneRead)
        {
            // Every value below the guard; the records past the end of the table read as null.
            if (!TryLaneSelectorBound(selector, _graph.ControlFlow.BlockOf(keyMemory.Pc), out var bound)) return false;
            results = Enumerable.Range(0, (int)bound).Select(index => (uint)index);
        }
        else if (selector.Kind == ScalarValueKind.Phi)
        {
            // Every counter value the loop can reach, up to the cap; a record past the loop's bound is never
            // read (the materializer compares each candidate's counter with the bound it evaluates per draw).
            if (!BufferCandidateTablePlanner.TryProveInductionBound(_plan, selector, out var initial, out var step,
                    out limit, out inclusive, exitGuards: true) || !initial.IsConstant || limit is null)
                return false;
            var first = initial.ConstantU32;
            var counters = Enumerable.Range(0, MaxLoopDirectCandidates + 1).Select(index => first + (uint)index * step);
            if (limit.IsConstant)
            {
                var bound = limit.ConstantU32;
                var inside = inclusive;
                var reached = counters.TakeWhile(counter => inside ? counter <= bound : counter < bound).ToArray();
                if (reached.Length > MaxLoopDirectCandidates) return false;
                results = reached;
                limit = null;
            }
            else
            {
                results = counters.Take(MaxLoopDirectCandidates);
            }
        }
        else
        {
            // Include the all-zero input result unless the scan's incoming edge proves it cannot occur.
            results = Enumerable.Range(0, 32).Select(index => (uint)index);
            if (!_graph.HasNonZeroBitScanInput(selector)) results = results.Append(uint.MaxValue);
        }

        var candidates = new List<DirectImageCandidate>();
        var sources = new List<DescriptorSource>();
        var keys = new HashSet<uint>();
        foreach (var result in results)
        {
            var replacements = new Dictionary<ScalarValue, ScalarValue> { [selector] = _graph.Constant(result) };
            var memo = new Dictionary<ScalarValue, ScalarValue>();
            var key = _graph.Substitute(keyRead.Operands[1], replacements, memo);
            if (!key.IsConstant || key.Type != ScalarValueType.U32 || !keys.Add(key.ConstantU32)) return false;
            var source = new DescriptorSource
            {
                Dwords = reads.Select(read => _graph.Substitute(read, replacements, memo)).ToArray(),
            };
            if (!ValidateSource(source, out _)) return false;
            sources.Add(source);
            candidates.Add(new DirectImageCandidate(key.ConstantU32, 0, selector.Kind == ScalarValueKind.Phi ? result : null));
        }
        if (candidates.Count == 0) return false;

        for (var index = 0; index < candidates.Count; index++)
            candidates[index] = candidates[index] with { Source = InternSource(sources[index]) };
        uint? limitSource = null;
        if (limit is not null)
        {
            var bound = new DescriptorSource { Dwords = [limit] };
            if (!ValidateSource(bound, out _)) return false;
            limitSource = InternSource(bound);
        }
        var imageSource = new DescriptorSource
        {
            Dwords = sources[0].Dwords,
            IndirectImage = new IndirectImageSelector(0, 0, 0, 0, 0)
            {
                DirectCandidates = candidates,
                DirectLimitSource = limitSource,
                DirectLimitInclusive = inclusive,
                DirectRecordsMayEnd = laneRead,
            },
        };
        plan = new IndirectImagePlan
        {
            Handle = handle,
            Source = InternSource(imageSource),
            Key = keyRead,
            KeyIsAddressOffset = true,
            SuppressMemoryReads = canSuppressMemoryReads,
            Memory = memoryIndices,
            Reads = reads,
        };
        return true;
    }

    // A value the shader read from its wave's lanes (v_readlane), or the smallest or largest of such values: the
    // material id a waterfall loop handles this pass, which the host cannot know but which the wave shares.
    private bool IsLaneReadSelector(ScalarValue value)
    {
        bool LaneRead(ScalarValue candidate) =>
            candidate.Kind == ScalarValueKind.Undefined && _graph.TryGetUndefinedOrigin(candidate, out var origin) &&
            origin.Opcode == "VReadlaneB32";
        return LaneRead(value) ||
            value is { Kind: ScalarValueKind.Operation, Operation: ScalarOperation.UMin32 or ScalarOperation.UMax32 } &&
            value.Operands.All(LaneRead);
    }

    // The bound a guard puts on a lane-read selector before the block that reads the record: a branch that
    // leaves when the selector is at or above it (s_cmpk_ge_u32 then s_cbranch_scc1), in the block whose lane
    // reads make the selector, so no path reaches the block without the guard having tested that very value.
    private bool TryLaneSelectorBound(ScalarValue selector, int readBlock, out uint bound)
    {
        bound = 0;
        var flow = _graph.ControlFlow;
        if (readBlock < 0) return false;
        var laneReads = selector.Kind == ScalarValueKind.Undefined ? [selector] : selector.Operands;
        var found = false;
        foreach (var (pc, condition) in _graph.BranchConditions)
        {
            var test = condition;
            var negated = test is { Kind: ScalarValueKind.Operation, Operation: ScalarOperation.LogicalNot };
            if (negated) test = test.Operands[0];
            if (test.Kind != ScalarValueKind.Operation || test.Operands.Length != 2 || !test.Operands[1].IsConstant ||
                !ReferenceEquals(test.Operands[0], selector) && !_graph.Equivalent(test.Operands[0], selector))
                continue;
            var constant = test.Operands[1].ConstantU32;
            // The largest value the selector keeps past the guard, plus one.
            var limit = (test.Operation, negated) switch
            {
                (ScalarOperation.UGreaterThanEqual32, false) or (ScalarOperation.ULessThan32, true) => (ulong)constant,
                (ScalarOperation.UGreaterThan32, false) or (ScalarOperation.ULessThanEqual32, true) => constant + 1ul,
                _ => ulong.MaxValue,
            };
            if (limit == 0 || limit > MaxLaneDirectCandidates) continue;

            var guard = flow.BlockOf(pc);
            if (guard < 0 || guard == readBlock) continue;
            var range = flow.Blocks[guard];
            if (laneReads.Any(read => !_graph.TryGetUndefinedOrigin(read, out var origin) ||
                    origin.Pc < range.StartPc || origin.Pc >= pc)) continue;
            var fallThrough = flow.Successors[guard].Where(successor => flow.Blocks[successor].StartPc == range.EndPc).ToArray();
            var taken = flow.Successors[guard].Where(successor => flow.Blocks[successor].StartPc != range.EndPc).ToArray();
            if (fallThrough.Length != 1 || taken.Length != 1) continue;

            // Neither the program's start nor the branch's target reaches the record without passing the guard again.
            var pending = new Stack<int>();
            var seen = new HashSet<int> { guard };
            if (guard != 0) pending.Push(0);
            pending.Push(taken[0]);
            var reaches = false;
            while (pending.TryPop(out var block))
            {
                if (!seen.Add(block)) continue;
                if (block == readBlock)
                {
                    reaches = true;
                    break;
                }
                foreach (var successor in flow.Successors[block]) pending.Push(successor);
            }
            if (reaches) continue;

            bound = found ? Math.Min(bound, (uint)limit) : (uint)limit;
            found = true;
        }
        return found;
    }

    // A sampler read from the record a counted loop selects (beside the image a direct table selects) has no
    // candidates of its own: it reads as the first record's, which the loop reads first, rather than as null.
    private bool TryFirstIterationSource(DescriptorSource source, out DescriptorSource first)
    {
        first = source;
        var replacements = new Dictionary<ScalarValue, ScalarValue>();
        var pending = new Stack<ScalarValue>(source.Dwords);
        var visited = new HashSet<ScalarValue>();
        while (pending.TryPop(out var value))
        {
            if (!visited.Add(value)) continue;
            if (value.Kind == ScalarValueKind.Phi)
            {
                if (_graph.ResolveInvariantPhi(value) is not null) continue;
                if (!BufferCandidateTablePlanner.TryProveInductionBound(_plan, value, out var initial, out _, out _, out _,
                        exitGuards: true) || !initial.IsConstant) return false;
                replacements[value] = initial;
                continue;
            }
            foreach (var operand in value.Operands) pending.Push(operand);
        }
        if (replacements.Count == 0) return false;

        var memo = new Dictionary<ScalarValue, ScalarValue>();
        first = new DescriptorSource { Dwords = source.Dwords.Select(dword => _graph.Substitute(dword, replacements, memo)).ToArray() };
        return ValidateSource(first, out _);
    }

    // A walk that reaches this many instructions before the register is written again keeps the load.
    private const int MaxConsumerWalkInstructions = 4096;

    private Dictionary<uint, int>? _instructionIndexByPc;

    // The index of the first instruction at or after pc, in the program's ascending pc order.
    private int FirstInstructionAt(uint pc)
    {
        var instructions = _graph.Program.Instructions;
        if (_instructionIndexByPc is null)
        {
            _instructionIndexByPc = new Dictionary<uint, int>(instructions.Count);
            for (var index = 0; index < instructions.Count; index++)
            {
                _instructionIndexByPc.TryAdd(instructions[index].Pc, index);
            }
        }

        if (_instructionIndexByPc.TryGetValue(pc, out var found))
        {
            return found;
        }

        var low = 0;
        var high = instructions.Count;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (instructions[middle].Pc < pc) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    // Resource-graph uses omit ordinary shader arithmetic; check those reads before removing a load.
    private bool HasOnlyImageConsumers(MemoryAccessInfo memory, ScalarValue handle)
    {
        var instructions = _graph.Program.Instructions;
        var loadIndex = FirstInstructionAt(memory.Pc);
        if (loadIndex >= instructions.Count || instructions[loadIndex].Pc != memory.Pc) return false;
        var load = instructions[loadIndex];
        if (memory.ComponentIndex >= load.Destinations.Count) return false;
        var destination = load.Destinations[(int)memory.ComponentIndex];
        if (destination.Kind != Gen5OperandKind.ScalarRegister || destination.Value >= 106) return false;
        var flow = _graph.ControlFlow;
        var initialBlock = flow.BlockOf(load.Pc);
        if (initialBlock < 0) return false;
        var pending = new Queue<(int Block, uint Start)>();
        var visited = new HashSet<(int Block, uint Start)>();
        var walked = 0;
        pending.Enqueue((initialBlock, load.Pc + (uint)load.Words.Count * sizeof(uint)));
        while (pending.TryDequeue(out var position))
        {
            if (!visited.Add(position)) continue;
            var block = flow.Blocks[position.Block];
            var overwritten = false;
            for (var index = FirstInstructionAt(position.Start); index < instructions.Count && instructions[index].Pc < block.EndPc; index++)
            {
                var instruction = instructions[index];
                if (++walked > MaxConsumerWalkInstructions) return false;
                if (instruction.Opcode.Contains("rel", StringComparison.OrdinalIgnoreCase) ||
                    instruction.Opcode.Contains("GprIdx", StringComparison.Ordinal)) return false;
                var allowedImage = instruction.Control is Gen5ImageControl &&
                    _plan.Memory.TryGetIndex(instruction.Pc, 0, out var imageIndex) &&
                    ReferenceEquals(_plan.Accesses[imageIndex]?.Handle, handle);
                if (!allowedImage && (ReadsScalar(instruction, destination.Value) ||
                    (instruction.Encoding == Gen5ShaderEncoding.Sopk && instruction.Opcode is "SAddkI32" or "SMulkI32" &&
                        instruction.Destinations.Contains(destination)))) return false;
                overwritten = instruction.Destinations.Any(target => target.Kind == Gen5OperandKind.ScalarRegister &&
                    (target == destination || (instruction.DestinationWidth > 1 && target.Value + 1 == destination.Value))) ||
                    instruction.Control is Gen5Vop3Control { ScalarDestination: { } scalarDestination } &&
                        destination.Value >= scalarDestination && destination.Value - scalarDestination < 2 ||
                    instruction.Control is Gen5SdwaControl { ScalarDestination: { } compareDestination } &&
                        destination.Value >= compareDestination && destination.Value - compareDestination < 2;
                if (overwritten) break;
            }
            if (!overwritten)
                foreach (var successor in flow.Successors[position.Block])
                    pending.Enqueue((successor, flow.Blocks[successor].StartPc));
        }
        return true;
    }

    private static bool ReadsScalar(Gen5ShaderInstruction instruction, uint register)
    {
        bool InRange(uint first, uint width) => register >= first && register - first < width;
        var width = instruction.Opcode.Contains("64", StringComparison.Ordinal) ? 2u : 1u;
        if (instruction.Sources.Any(source => source.Kind == Gen5OperandKind.ScalarRegister && InRange(source.Value, width)))
            return true;
        return instruction.Control switch
        {
            Gen5ImageControl image => InRange(image.ScalarResource, 8) || InRange(image.ScalarSampler, 4),
            Gen5BufferMemoryControl buffer => InRange(buffer.ScalarResource, 4),
            Gen5GlobalMemoryControl global => InRange(global.ScalarAddress, 2),
            Gen5ScalarMemoryControl scalar =>
                instruction.Sources.Count > 0 && InRange(instruction.Sources[0].Value,
                    instruction.Opcode.StartsWith("SBuffer", StringComparison.Ordinal) ? 4u : 2u) ||
                scalar.DynamicOffsetRegister == register,
            _ => false,
        };
    }
}
