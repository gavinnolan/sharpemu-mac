// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Libs.Ampr;

internal static class AprFailureDiagnostics
{
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_APR_FAILURES"), "1", StringComparison.Ordinal);
    private static int rows;
    internal static void Failed(string stage, ulong commandBuffer, ulong address, int count,
        int result, int executionResult = 0, uint commandOffset = 0)
    {
        if (!Enabled || Volatile.Read(ref rows) >= 64) return;
        if (Interlocked.Increment(ref rows) > 64) return;
        try
        {
            // All fields belong to the failed call, no memory/provider reread.
            Console.Error.WriteLine($"[apr-failure] stage={stage} command_buffer=0x{commandBuffer:X} " +
                $"address=0x{address:X} bytes={count} result=0x{result:X8} " +
                $"execution_result=0x{executionResult:X8} command_offset={commandOffset}");
        }
        catch (Exception) { } // Optional formatting/sink cannot replace the APR result.
    }
}
