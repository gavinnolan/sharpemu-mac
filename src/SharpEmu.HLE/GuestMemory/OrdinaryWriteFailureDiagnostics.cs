// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.GuestMemory;

// Detached first-return values, never write/admission/permission authority.
public readonly record struct SharedWriteFailure(string? Reason, long RejectedAdmission,
    bool OriginalCaptured, bool AliasPinAcquired, bool SnapshotMatched, string? OriginalCaptureReason);
public readonly record struct OrdinaryWriteFailure(string? Reason, ulong Address, ulong Bytes,
    bool RegionFound, ulong RegionAddress, ulong RegionBytes, uint RegionProtection,
    bool RegionBacked, bool RegionReserved, SharedWriteFailure Shared,
    OrdinaryBackingWriteAdmission? WaitOutcome);

public static class OrdinaryWriteFailureDiagnostics
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHARPEMU_LOG_ORDINARY_WRITE_FAILURES") == "1";
    private static int rows;
    public static void Emit(OrdinaryWriteFailure failure)
    {
        if (!Enabled || failure.Reason is null) return;
        for (var count = Volatile.Read(ref rows);; count = Volatile.Read(ref rows))
        {
            if (count >= 64) return;
            if (Interlocked.CompareExchange(ref rows, count + 1, count) == count) break;
        }
        try
        {
            Console.Error.WriteLine($"[ordinary-write-first-refusal] reason={failure.Reason} addr=0x{failure.Address:X} bytes={failure.Bytes} " +
                $"original_region_found={failure.RegionFound} original_region_addr=0x{failure.RegionAddress:X} original_region_bytes={failure.RegionBytes} " +
                $"original_region_protection=0x{failure.RegionProtection:X} original_region_backed={failure.RegionBacked} original_region_reserved={failure.RegionReserved} " +
                $"shared_reason={failure.Shared.Reason ?? "not-classified"} rejected_admission={failure.Shared.RejectedAdmission} " +
                $"original_capture_reason={failure.Shared.OriginalCaptureReason ?? "not-classified"} original_captured={failure.Shared.OriginalCaptured} alias_pin_acquired={failure.Shared.AliasPinAcquired} snapshot_matched={failure.Shared.SnapshotMatched} " +
                $"wait_outcome={failure.WaitOutcome?.ToString() ?? "not-attempted"}");
        }
        catch { } // Optional output cannot replace a failed write or cleanup cause.
    }
}
