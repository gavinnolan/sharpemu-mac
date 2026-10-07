// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.HLE;

public static unsafe partial class GuestImageWriteTracker
{
    // Diagnostic owner identities/epochs never authorize recovery.
    private static long _nativeDiagnosticOwnerIdentity;
    private static long _nativeDiagnosticInvocationCounter;
    private static NativeFaultTraceBuffer? _nativeDiagnosticBuffer;
    private static bool _nativeDiagnosticsInitialized;
    private static readonly object _nativeDiagnosticInitializationGate = new();
    private static long _nativeDiagnosticOperation;

    /// <summary>
    /// Safe-startup contract for the POSIX host package. Default off; owns the
    /// diagnostic mapping until process exit, including fatal signal exit.
    /// Must run before installing handlers; never call from a signal.
    /// </summary>
    public static void InitializeNativeFaultDiagnostics()
    {
        lock (_nativeDiagnosticInitializationGate) // ordinary startup only
        {
            if (_nativeDiagnosticsInitialized) return;
            _nativeDiagnosticsInitialized = true;
            var path = Environment.GetEnvironmentVariable("SHARPEMU_NATIVE_FAULT_TRACE_PATH");
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var buffer = new NativeFaultTraceBuffer(path);
                try
                {
                    // Initialize/warm core operations privately, then publish the complete
                    // buffer and execute public wrappers before handlers are installed.
                    _ = buffer.Write(DiagnosticRecord(1, 0, 0));
                    _ = GetMonotonicNanoseconds();
                    Volatile.Write(ref _nativeDiagnosticBuffer, buffer);
                    var invocation = RecordNativeImageFaultEntry(0, 0, 0, 0, -1, 0, 0);
                    RecordNativeImageFaultResult(false, invocation);
                    RecordNativeProtection(0, 0, 0, 0, 0, false, 0, 0, null);
                    RecordNativeProtection(0, 0, 0, 0, 0, true, 0, 0, null);
                }
                catch
                {
                    Volatile.Write(ref _nativeDiagnosticBuffer, null);
                    buffer.Dispose(); // safe startup: no handler has been installed
                    throw;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Setup is optional diagnostics; never publish a partially initialized view.
                Console.Error.WriteLine($"[WT][NATIVE] disabled: setup failed ({error.GetType().Name}).");
            }
        }
    }

    /// <summary>Signal-facing scalar observations; these are not mapping claims.</summary>
    public static long RecordNativeImageFaultEntry(
        int signal, ulong rawAddress, int signalCode, ulong address,
        int access, ulong errorCode, ulong rip)
    {
        var buffer = Volatile.Read(ref _nativeDiagnosticBuffer);
        if (buffer is null) return 0;
        var invocation = Interlocked.Increment(ref _nativeDiagnosticInvocationCounter);
        var record = DiagnosticRecord(1, address, invocation);
        record.Signal = signal;
        record.RawAddress = rawAddress;
        record.SignalCode = signalCode;
        record.Access = access;
        record.ErrorCode = errorCode;
        record.Rip = rip;
        _ = buffer.Write(record);
        return invocation;
    }

    /// <summary>End this one early-tracker invocation; does not acknowledge a retry.</summary>
    public static void RecordNativeImageFaultResult(bool handled, long invocation)
    {
        var buffer = Volatile.Read(ref _nativeDiagnosticBuffer);
        if (buffer is null) return;
        var record = DiagnosticRecord(2, 0, invocation);
        record.Result = handled ? 1 : 0;
        _ = buffer.Write(record);
    }

    private static NativeFaultTraceRecord DiagnosticRecord(int kind, ulong address, long invocation)
    {
        var record = new NativeFaultTraceRecord
        {
            Kind = kind,
            Nanoseconds = GetMonotonicNanoseconds(),
            Invocation = invocation,
            Address = address,
            OwnerState = -1,
        };
        var ranges = Volatile.Read(ref _rangeSnapshot).Ranges;
        for (var index = 0; index < ranges.Length; index++)
        {
            var range = ranges[index];
            if (address < range.Start || address >= range.End) continue;
            DescribeDiagnosticOwner(ref record, range);
            record.OwnerObservation = 1; // selected snapshot owner, not an initiating identity
            break; // selected observation only; overlapping owners may exist
        }
        return record;
    }

    private static void DescribeDiagnosticOwner(ref NativeFaultTraceRecord record, TrackedRange range)
    {
        record.OwnerAddress = range.Address;
        record.OwnerEnd = range.End;
        record.OwnerIdentity = range.DiagnosticIdentity;
        record.ArmEpoch = Volatile.Read(ref range.DiagnosticArmEpoch);
        record.OwnerState = Volatile.Read(ref range.Armed);
    }

    private static void RecordNativeRangeState(TrackedRange range, bool retired = false, long invocation = 0)
    {
        var buffer = Volatile.Read(ref _nativeDiagnosticBuffer);
        if (buffer is null) return;
        var record = DiagnosticRecord(retired ? 6 : 5, range.Address, invocation);
        DescribeDiagnosticOwner(ref record, range);
        record.OwnerObservation = 2; // initiating range, still diagnostic only
        _ = buffer.Write(record);
    }

    private static void RecordNativeProtection(
        ulong address, ulong size, int protection, int result, int error, bool finished,
        long operation, long invocation, TrackedRange? initiatingRange)
    {
        var buffer = Volatile.Read(ref _nativeDiagnosticBuffer);
        if (buffer is null) return;
        var record = DiagnosticRecord(finished ? 4 : 3, address, invocation);
        record.Size = size;
        record.Protection = protection;
        record.Result = result;
        record.Errno = error;
        record.Operation = operation;
        if (initiatingRange is not null)
        {
            DescribeDiagnosticOwner(ref record, initiatingRange);
            record.OwnerObservation = 2;
        }
        _ = buffer.Write(record);
    }

    internal static NativeFaultTraceRecord[] SnapshotNativeFaultDiagnosticsForTests() =>
        Volatile.Read(ref _nativeDiagnosticBuffer)?.Snapshot() ?? [];

    // Tests install only while this serialized tracker collection has no live handlers.
    internal static NativeFaultTraceBuffer? ExchangeNativeDiagnosticBufferForTests(NativeFaultTraceBuffer? buffer) =>
        Interlocked.Exchange(ref _nativeDiagnosticBuffer, buffer);
}
