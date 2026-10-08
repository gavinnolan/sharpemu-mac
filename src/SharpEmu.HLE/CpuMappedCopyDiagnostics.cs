// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE;

// Failure-only scalar diagnostics. These observations establish no mapping,
// content, permission, writer exclusion or copy authority.
internal static class CpuMappedCopyDiagnostics
{
    private static bool enabled = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_MAPPED_COPY") == "1";
    private static int rows;
    private static Action<string>? sinkForControl = null;
    internal static bool Enabled => enabled;
    internal readonly record struct Failure(string? Stage, string? Reason, CpuMappedCopyResult Result,
        ulong Footprint, ulong FootprintBytes, ulong Source, int CopyBytes, bool Watched,
        long BackingIdentity = 0, ulong Cursor = 0, int Segments = 0,
        ulong ViewAddress = 0, ulong ViewBytes = 0, long ViewBacking = 0);

    internal static void Emit(in Failure failure)
    {
        if (!enabled || failure.Reason is null) return;
        try
        {
            if (Interlocked.Increment(ref rows) > 64) return;
            var line = $"[MAPPED-COPY] stage={failure.Stage} reason={failure.Reason} result={failure.Result} " +
                $"footprint=0x{failure.Footprint:X} footprint_bytes={failure.FootprintBytes} " +
                $"source=0x{failure.Source:X} copy_bytes={failure.CopyBytes} watched={failure.Watched} " +
                $"backing={failure.BackingIdentity} cursor=0x{failure.Cursor:X} segments={failure.Segments} " +
                $"view_addr=0x{failure.ViewAddress:X} view_bytes={failure.ViewBytes} view_backing={failure.ViewBacking}";
            if (sinkForControl is { } sink) sink(line);
            else Console.Error.WriteLine(line);
        }
        catch (Exception) { } // optional diagnostics cannot replace the original result or exception
    }
}
