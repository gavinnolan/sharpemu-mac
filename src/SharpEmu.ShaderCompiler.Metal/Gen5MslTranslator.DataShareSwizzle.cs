// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Metal;

public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        private bool TryEmitDataShareSwizzle(Gen5ShaderInstruction instruction,
            Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            var selection = control.SingleOffsetBytes;
            if (selection >= 0xE000 || instruction.Sources.Count != 1 || instruction.Destinations.Count != 1)
            {
                error = selection >= 0xE000 ? "unsupported FFT data-share swizzle" : "invalid data-share swizzle operands";
                return false;
            }
            if (IsSingleLaneStage)
            {
                error = "data-share swizzle requires subgroup lane access";
                return false;
            }

            var lane = Temp("uint", "sharpemu_lane & 31u");
            string sourceLane;
            if (selection >= 0xC000)
            {
                var rotation = (selection >> 5) & 31;
                var displacement = (selection & 0x400) != 0 ? unchecked(0u - rotation) : rotation;
                var mask = selection & 31;
                sourceLane = Temp("uint", $"({lane} & {mask}u) | (({lane} + {displacement}u) & {~mask & 31}u)");
            }
            else if ((selection & 0x8000) != 0)
                sourceLane = Temp("uint", $"({lane} & ~3u) | (({selection}u >> (2u * ({lane} & 3u))) & 3u)");
            else
                sourceLane = Temp("uint", $"(({lane} & {selection & 31}u) | {(selection >> 5) & 31}u) ^ {(selection >> 10) & 31}u");

            var activeLanes = Temp("uint", "sharpemu_ballot(exec)");
            var sourceActive = Temp("bool", $"(({activeLanes} >> {sourceLane}) & 1u) != 0u");
            var safeSourceLane = Temp("uint", $"{sourceActive} ? {sourceLane} : {lane}");
            // All host lanes must participate before the destination EXEC check.
            var value = Temp("uint", $"simd_shuffle({RawSource(instruction, 0)}, (ushort){safeSourceLane})");
            StoreVector(instruction.Destinations[0].Value, $"{sourceActive} ? {value} : 0u");
            return true;
        }

        private bool TryEmitDataShareBpermute(Gen5ShaderInstruction instruction,
            Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count != 2 || instruction.Destinations.Count != 1)
            {
                error = "invalid data-share bpermute operands";
                return false;
            }
            if (IsSingleLaneStage)
            {
                error = "data-share bpermute requires subgroup lane access";
                return false;
            }

            var lane = Temp("uint", "sharpemu_lane");
            var address = Temp("uint", $"{RawSource(instruction, 0)} + {control.SingleOffsetBytes}u");
            var index = Temp("uint", $"({address} >> 2u) & 31u");
            var sourceLane = Temp("uint", $"({lane} & ~31u) | {index}");
            var activeLanes = Temp("uint", "sharpemu_ballot(exec)");
            var sourceActive = Temp("bool", $"(({activeLanes} >> ({sourceLane} & 31u)) & 1u) != 0u");
            var safeSourceLane = Temp("uint", $"{sourceActive} ? {sourceLane} : {lane}");
            // A wave64 runs as two 32-wide simdgroups and bpermute stays inside each half, so the
            // shuffle takes the lane within the simdgroup; sourceLane carries the half's base (32..63).
            var value = Temp("uint", $"simd_shuffle({RawSource(instruction, 1)}, (ushort)({safeSourceLane} & 31u))");
            StoreVector(instruction.Destinations[0].Value, $"{sourceActive} ? {value} : 0u");
            return true;
        }

        // DS_PERMUTE_B32, the forward permute: every active lane sends DATA0 to the lane its
        // address names ((ADDR + offset) / 4), and each active lane returns what it was sent,
        // zero when no lane sent it anything; when several lanes send to one lane the highest
        // wins. No LDS is touched. Like the backward form it stays inside each 32-lane half
        // of a wave64. Each lane gathers by walking the 32 senders of its simdgroup.
        private bool TryEmitDataSharePermute(Gen5ShaderInstruction instruction,
            Gen5DataShareControl control, out string error)
        {
            error = string.Empty;
            if (instruction.Sources.Count != 2 || instruction.Destinations.Count != 1)
            {
                error = "invalid data-share permute operands";
                return false;
            }
            if (IsSingleLaneStage)
            {
                error = "data-share permute requires subgroup lane access";
                return false;
            }

            var lane = Temp("uint", "sharpemu_lane & 31u");
            var target = Temp("uint", $"(({RawSource(instruction, 0)} + {control.SingleOffsetBytes}u) >> 2u) & 31u");
            var data = Temp("uint", RawSource(instruction, 1));
            var activeLanes = Temp("uint", "sharpemu_ballot(exec)");
            var received = Temp("uint", "0u");
            Line("for (ushort sharpemu_sender = 0; sharpemu_sender < 32; sharpemu_sender++)");
            Line("{");
            _indent++;
            var senderTarget = Temp("uint", $"simd_shuffle({target}, sharpemu_sender)");
            var senderData = Temp("uint", $"simd_shuffle({data}, sharpemu_sender)");
            Line($"if ((({activeLanes} >> sharpemu_sender) & 1u) != 0u && {senderTarget} == {lane})");
            Line("{");
            Line($"    {received} = {senderData};");
            Line("}");
            _indent--;
            Line("}");
            StoreVector(instruction.Destinations[0].Value, received);
            return true;
        }
    }
}
