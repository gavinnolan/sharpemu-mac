// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Logging;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
    private static readonly bool _faultThreadImportDiagnostics = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_FAULT_THREAD_IMPORT_DIAGNOSTICS"), "1", StringComparison.Ordinal);
    [ThreadStatic] private static FaultThreadImportTrace? _faultThreadImports;

    private unsafe ulong DispatchImport(int importIndex, nint argPackPtr)
    {
        if (!_faultThreadImportDiagnostics) return DispatchImportCore(importIndex, argPackPtr);
        return DispatchImportWithFaultThreadDiagnostics(importIndex, argPackPtr);
    }

    private unsafe ulong DispatchImportWithFaultThreadDiagnostics(int importIndex, nint argPackPtr)
    {
        FaultThreadImportTrace? trace = null; long originalSequence = 0;
        try
        {
            // Same basic dispatcher eligibility; malformed calls still take the
            // existing error path, without diagnostic reads of their arguments.
            if (ActiveCpuContext is not null && (uint)importIndex < (uint)_importEntries.Length)
            {
                trace = _faultThreadImports ??= new();
                // No trusted readable frame extent is available here. In particular,
                // hot memory dispatch needs only three qwords. Diagnostics must not
                // dereference any argument/return slot ahead of the original core.
                originalSequence = trace.Begin(this, importIndex, _importEntries[importIndex].Nid,
                    _activeGuestThreadState?.ThreadHandle ?? GuestThreadExecution.CurrentGuestThreadHandle);
            }
        }
        catch { trace = null; } // optional diagnostics cannot skip/replace dispatch
        try
        {
            var result = DispatchImportCore(importIndex, argPackPtr); // all existing fast paths, ONCE
            try { trace?.Complete(originalSequence, result); } catch { }
            return result;
        }
        catch
        {
            try { trace?.Threw(originalSequence); } catch { }
            throw;
        }
    }

    private void DumpFaultThreadImportTrace()
    {
        if (!_faultThreadImportDiagnostics) return;
        try
        {
            var managed = Environment.CurrentManagedThreadId;
            var guest = _activeGuestThreadState?.ThreadHandle ?? GuestThreadExecution.CurrentGuestThreadHandle;
            var backend = _activeExecutionBackend ?? this;
            var records = _faultThreadImports?.Snapshot(backend, managed, guest) ?? [];
            Log.Info($"   Actual fault/current-thread import diagnostics managed={managed} guest=0x{guest:X16} count={records.Length} bound={FaultThreadImportTrace.Capacity}; exact_backend_filter=true; local_seq_is_not_dispatch_index=true; return_is_not_semantic_success=true; frame_fields=not-observed:");
            foreach (var entry in records)
                Log.Info($"     local_seq={entry.Sequence} import_index={entry.ImportIndex} managed={entry.ManagedThreadId} guest=0x{entry.GuestThreadHandle:X16} nid={entry.Nid} ret=not-observed arguments=not-observed status={entry.Status} returned_observed={entry.Result.HasValue} result={(entry.Result is { } result ? $"0x{result:X16}" : "not-observed")}");
        }
        catch { } // optional snapshot/sink failure cannot alter fault policy
    }
}
