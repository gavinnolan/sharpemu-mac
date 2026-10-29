// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using static SharpEmu.Libs.Gpu.GpuCommands.Registers.ShaderRegisterOffset;

namespace SharpEmu.Libs.Gpu.GpuCommands.Registers;

public static partial class RegisterWriteTable
{
    // Experimental until matched gameplay measurements establish the cost of the comparison.
    private static readonly bool ScalarReuseEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_REGISTER_IDENTICAL_SCALARS") == "1";
    private static readonly bool ScalarReuseProfileEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_IDENTICAL_SCALARS") == "1";

    [ThreadStatic] private static long _scalarPacketsChecked;
    [ThreadStatic] private static long _scalarPacketsReused;

    private static bool TryReuseScalarPacket(RegisterBanks banks, uint offset, ReadOnlySpan<uint> values)
    {
        // Only the four direct scalar writers apply one marker to an entire packet. Legacy
        // vertex/export packets use per-entry writers, which clear it after the first word.
        UserScalarRegisters target;
        uint slot;
        uint capacity = 32;
        if (offset - SpiShaderUserDataPs0 < 32)
        {
            slot = offset - SpiShaderUserDataPs0;
            target = banks.Shader.Pixel.UserScalars;
        }
        else if (offset - SpiShaderUserDataGs0 < 32)
        {
            slot = offset - SpiShaderUserDataGs0;
            target = banks.Shader.Vertex.GeometryUserScalars;
        }
        else if (offset - SpiShaderUserDataHs0 < 32)
        {
            slot = offset - SpiShaderUserDataHs0;
            target = banks.Shader.Vertex.HullUserScalars;
        }
        else if (offset - ComputeUserData0 < 16)
        {
            slot = offset - ComputeUserData0;
            target = banks.Shader.Compute.UserScalars;
            capacity = 16;
        }
        else
        {
            return false;
        }

        // Leave invalid packets to the original writer so its failure and marker semantics stay
        // intact. Zero words can still initialize previously unwritten slots: Count must match too.
        if (values.IsEmpty || (uint)values.Length > capacity - slot)
            return false;

        var unchanged = slot + (uint)values.Length <= target.Count &&
            ((ReadOnlySpan<uint>)target.Values).Slice((int)slot, values.Length).SequenceEqual(values);
        if (unchanged)
        {
            var marker = banks.UserDataMarker;
            for (var index = 0; index < values.Length; index++)
            {
                if (target.Kinds[(int)slot + index] != marker)
                {
                    unchanged = false;
                    break;
                }
            }
        }

        if (ScalarReuseProfileEnabled)
        {
            _scalarPacketsChecked++;
            if (unchanged) _scalarPacketsReused++;
            if ((_scalarPacketsChecked & 0x3FFFF) == 0)
                Console.Error.WriteLine($"[REGISTER-SCALAR-REUSE] thread={Environment.CurrentManagedThreadId} checked={_scalarPacketsChecked} reused={_scalarPacketsReused}");
        }

        return unchanged;
    }
}
