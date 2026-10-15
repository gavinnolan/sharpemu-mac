// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Metal;

// Valid-mask exports that publish the entry EXEC.
//
// A pixel program saves EXEC before it widens it for whole-quad mode and puts the saved
// copy back right before its EXP.VM: S_MOV_B64 s[n:n+1], exec at the top, S_WQM_B64 exec,
// ..., S_MOV_B64 exec, s[n:n+1], EXP ... VM. EXEC is then exactly the mask the wave
// started with, every lane of which is a live fragment, so the export marks every
// fragment valid and the discard at the end of the program can never fire. Translated
// literally the valid mask is the lane's bit of a ballot the compiler cannot see through,
// and the discard stays in the program. A fragment program that may discard forces the
// host to keep its render pass's colour load, since a discarded pixel keeps what was
// there; one that provably cannot lets a full-screen draw skip the load.
//
// The proof is kept narrow on purpose. The copy has to be the first thing the program
// does with EXEC, on the straight-line entry no branch reaches back into; nothing else
// may ever write the copy's two registers; and the export has to follow the restore with
// no write to EXEC and no branch target in between. Anything else keeps the ballot.
public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        /// <summary>The program counters of the EXP.VM instructions whose EXEC is the
        /// entry EXEC restored from a saved copy; empty when there are none.</summary>
        internal static HashSet<uint> FindEntryMaskExports(IReadOnlyList<Gen5ShaderInstruction> instructions)
        {
            var exports = new HashSet<uint>();
            var targets = new HashSet<uint>();
            foreach (var instruction in instructions)
            {
                if (IsIndirectControlFlow(instruction.Opcode))
                {
                    return exports;
                }

                if (IsBranch(instruction.Opcode) && TryGetBranchTargetPc(instruction, out var target))
                {
                    targets.Add(target);
                }
                else if (IsBranch(instruction.Opcode))
                {
                    return exports;
                }
            }

            var copyIndex = -1;
            uint copy = 0;
            for (var index = 0; index < instructions.Count; index++)
            {
                var instruction = instructions[index];
                if (targets.Contains(instruction.Pc) || IsBranch(instruction.Opcode) || instruction.Opcode == "SEndpgm")
                {
                    return exports;
                }

                if (instruction.Opcode == "SMovB64" &&
                    instruction.Sources is [{ Kind: Gen5OperandKind.ScalarRegister, Value: ExecLoRegister }, ..] &&
                    instruction.Destinations is [{ Kind: Gen5OperandKind.ScalarRegister, Value: var saved }, ..] &&
                    saved + 1 < VccLoRegister)
                {
                    copyIndex = index;
                    copy = saved;
                    break;
                }

                if (MayWriteExec(instruction))
                {
                    return exports;
                }
            }

            if (copyIndex < 0)
            {
                return exports;
            }

            for (var index = 0; index < instructions.Count; index++)
            {
                if (index != copyIndex &&
                    (MayWriteScalar(instructions[index], copy) || MayWriteScalar(instructions[index], copy + 1)))
                {
                    return exports;
                }
            }

            for (var index = copyIndex + 1; index < instructions.Count; index++)
            {
                if (instructions[index].Control is not Gen5ExportControl { ValidMask: true })
                {
                    continue;
                }

                for (var previous = index - 1; previous > copyIndex; previous--)
                {
                    var instruction = instructions[previous];
                    if (targets.Contains(instructions[previous + 1].Pc) ||
                        IsBranch(instruction.Opcode) || instruction.Opcode == "SEndpgm")
                    {
                        break;
                    }

                    if (!MayWriteExec(instruction))
                    {
                        continue;
                    }

                    if (instruction.Opcode == "SMovB64" &&
                        instruction.Destinations is [{ Kind: Gen5OperandKind.ScalarRegister, Value: ExecLoRegister }, ..] &&
                        instruction.Sources is [{ Kind: Gen5OperandKind.ScalarRegister } restored, ..] &&
                        restored.Value == copy)
                    {
                        exports.Add(instructions[index].Pc);
                    }

                    break;
                }
            }

            return exports;
        }

        // Control flow the block builder does not follow: any of it and nothing about
        // the program's order can be trusted.
        private static bool IsIndirectControlFlow(string opcode) =>
            opcode.StartsWith("SSetpc", StringComparison.Ordinal) ||
            opcode.StartsWith("SSwappc", StringComparison.Ordinal) ||
            opcode.StartsWith("SCall", StringComparison.Ordinal) ||
            opcode.StartsWith("SSubvector", StringComparison.Ordinal) ||
            opcode.StartsWith("SRfe", StringComparison.Ordinal) ||
            opcode.StartsWith("SCbranchJoin", StringComparison.Ordinal) ||
            opcode.StartsWith("SCbranchG", StringComparison.Ordinal);

        /// <summary>Whether the instruction can change EXEC: through an operand that names
        /// it, or implicitly as the saveexec, wrexec and V_CMPX families do.</summary>
        private static bool MayWriteExec(Gen5ShaderInstruction instruction) =>
            instruction.Opcode.Contains("exec", StringComparison.OrdinalIgnoreCase) ||
            instruction.Opcode.Contains("Cmpx", StringComparison.Ordinal) ||
            instruction.Opcode.Contains("Cmpsx", StringComparison.Ordinal) ||
            MayWriteScalar(instruction, ExecLoRegister) ||
            MayWriteScalar(instruction, ExecHiRegister);

        /// <summary>An over-approximation of whether the instruction writes one SGPR: its
        /// listed destinations at their full width, a VOP3 or SDWA scalar destination,
        /// VCC for any vector instruction, and anything relative-addressed.</summary>
        private static bool MayWriteScalar(Gen5ShaderInstruction instruction, uint register)
        {
            if (instruction.Opcode.Contains("Movrel", StringComparison.Ordinal))
            {
                return true;
            }

            // A vector instruction's scalar destination is a lane mask, which can be a pair.
            var width = instruction.Opcode.StartsWith('V') ? 2u : instruction.DestinationWidth;
            foreach (var destination in instruction.Destinations)
            {
                if (destination.Kind == Gen5OperandKind.ScalarRegister &&
                    register >= destination.Value && register < destination.Value + width)
                {
                    return true;
                }
            }

            if (instruction.Control is Gen5ScalarMemoryControl memory &&
                instruction.Destinations is [{ Kind: Gen5OperandKind.ScalarRegister, Value: var first }, ..] &&
                register >= first && register < first + memory.DestinationCount)
            {
                return true;
            }

            var scalarDestination = instruction.Control switch
            {
                Gen5Vop3Control control => control.ScalarDestination,
                Gen5SdwaControl control => control.ScalarDestination,
                _ => null,
            };
            if (scalarDestination is { } pair && (register == pair || register == pair + 1))
            {
                return true;
            }

            return instruction.Opcode.StartsWith('V') && register is VccLoRegister or VccHiRegister;
        }
    }
}
