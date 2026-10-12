// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Ir;

/// <summary>
/// A guest wave's LDS operations execute in order, so kernels with one wave per workgroup
/// pass data between lanes through LDS with no S_BARRIER. A host that runs the wave as two
/// subgroups has to order the halves itself: this finds the instructions an LDS access of
/// one half must not overtake an access of the other half.
/// </summary>
public static class Gen5Wave64SharedMemoryOrdering
{
    private readonly record struct State(bool Reached, bool PendingRead, bool PendingWrite)
    {
        public static State Meet(State left, State right) =>
            !left.Reached ? right
            : !right.Reached ? left
            : new State(true, left.PendingRead | right.PendingRead, left.PendingWrite | right.PendingWrite);
    }

    /// <summary>The pcs that need a workgroup barrier before them, or null when the program
    /// has dynamic control flow. Mask ballots do not clear the pending state.</summary>
    public static IReadOnlySet<uint>? BarriersBefore(Gen5ShaderProgram program, IReadOnlySet<uint> sharedFlatPcs)
    {
        var instructions = program.Instructions;
        if (instructions.Count == 0)
        {
            return null;
        }

        var successors = Gen5Wave64HalfMaskAnalysis.BuildSuccessors(instructions);
        if (successors is null)
        {
            return null;
        }

        var states = new State[instructions.Count];
        states[0] = new State(true, false, false);
        var pending = new Queue<int>();
        var queued = new bool[instructions.Count];
        pending.Enqueue(0);
        queued[0] = true;
        while (pending.TryDequeue(out var index))
        {
            queued[index] = false;
            var output = Step(instructions[index], states[index], sharedFlatPcs, out _);
            foreach (var successor in successors[index])
            {
                var merged = State.Meet(states[successor], output);
                if (merged != states[successor])
                {
                    states[successor] = merged;
                    if (!queued[successor])
                    {
                        queued[successor] = true;
                        pending.Enqueue(successor);
                    }
                }
            }
        }

        var barriers = new HashSet<uint>();
        for (var index = 0; index < instructions.Count; index++)
        {
            if (states[index].Reached)
            {
                Step(instructions[index], states[index], sharedFlatPcs, out var barrier);
                if (barrier)
                {
                    barriers.Add(instructions[index].Pc);
                }
            }
        }

        return barriers;
    }

    private static State Step(Gen5ShaderInstruction instruction, State input, IReadOnlySet<uint> sharedFlatPcs, out bool barrier)
    {
        var (reads, writes) = Gen5Wave64HalfMaskAnalysis.SharedMemoryAccess(instruction, sharedFlatPcs);
        var pendingRead = input.PendingRead;
        var pendingWrite = input.PendingWrite;
        barrier = (writes && (pendingRead || pendingWrite)) || (reads && pendingWrite);
        if (barrier)
        {
            pendingRead = false;
            pendingWrite = false;
        }

        pendingRead |= reads;
        pendingWrite |= writes;
        if (instruction.Opcode == "SBarrier")
        {
            pendingRead = false;
            pendingWrite = false;
        }

        return new State(true, pendingRead, pendingWrite);
    }
}
