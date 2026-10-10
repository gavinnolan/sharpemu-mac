// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;

namespace SharpEmu.HLE.GuestMemory;

public readonly record struct ViewRecord(ulong Address, ulong Size, ulong Offset, HostPageProtection Protection)
{
    public bool WasRestored { get; init; }
}

public sealed unsafe class SharedBackingViews : IDisposable, ICpuMappedCopySource, ICpuMappedReadWatchSource, ICpuMappedPublicationSource
{
    internal static Action<string> OnFatal = message => Environment.FailFast(message);

    private readonly IHostViewMemory _host;
    private readonly HostBackingObject? _backing;
    private readonly object _lock = new();
    private readonly SortedList<ulong, ViewRecord> _views = new();
    private bool _disposed;
    private readonly object _disposeGate = new(); // never needed by access or mapping paths
    private bool _disposeRequested;
    private int _operationThread;
    private int _operationDepth;
    private Exception? _cleanupFailure;
    private bool _cleanupComplete;

    // Guest command writes and their reads by the render thread reach TryWriteBacking and
    // TryReadBacking millions of times per second, a few bytes at a time, while views
    // change rarely. Single-view accesses search this immutable copy of _views without
    // the lock; every change to _views republishes it under the lock.
    private ViewRecord[] _snapshot = [];

    // Single-view accesses in flight; Dispose waits for them before releasing the alias.
    private int _activeAccesses;
    // Default-null controlled ordinary-write boundary. The real operation,
    // chunk admission and alias lifetime pin are already held. Tests must use
    // the single-view path to pause without the shared-view gate held.
    private Action<ulong, int>? _beforeCanonicalAliasCopyForControl = null;

    public SharedBackingViews(IHostViewMemory host, ulong size)
    {
        _host = host;
        _ = host.TryCreateBacking(size, out _backing, out _);
        // Outside the provider gate and before this wrapper is published: all
        // of its participating writes have history from the first operation.
        try { if (host is IHostMappedPublicationMemory) _backing?.EnrollManagedWrites(); }
        catch (Exception primary)
        {
            // This newly created backing was not published to a caller yet.
            // Enrollment allocation failure must not abandon its native owner.
            try { _backing?.Dispose(); }
            catch (Exception cleanup)
            {
                try { primary.Data["ManagedWriterEnrollmentCleanup"] = cleanup; } catch { }
            }
            throw;
        }
    }

    public bool IsAvailable => _backing != null && !Volatile.Read(ref _disposeRequested) && !Volatile.Read(ref _disposed) && !_backing.IsClosing && !_backing.IsDisposed;

    public bool CleanupComplete => Volatile.Read(ref _cleanupComplete);

    public ulong AliasBase => _backing?.AliasBase ?? 0;

    public ulong Size => _backing?.Size ?? 0;

    public CpuMappedPublicationPreparationResult TryPrepareMappedReadValidation(CpuMappedCopyObservation original,
        out CpuMappedReadValidation? validation)
    {
        validation = null;
        if (_host is not IHostMappedReadWatchMemory provider || _backing is null)
            return CpuMappedPublicationPreparationResult.Unavailable;
        if (!TryEnterAliasAccess()) return CpuMappedPublicationPreparationResult.Rejected;
        try
        {
            var result = provider.TryPrepareMappedReadValidation(_backing, original, out validation);
            if (IsAvailable) return result;
            validation?.Dispose(); validation = null;
            return CpuMappedPublicationPreparationResult.Rejected;
        }
        finally { ExitAliasAccess(); }
    }

    public bool CanWaitForMappedPublicationValidation => CanEmitOrdinaryWriteDiagnostic;

    public CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(CpuMappedCopyObservation original,
        out CpuMappedPublicationValidation? validation)
    {
        validation = null;
        if (_host is not IHostMappedPublicationMemory provider || _backing is null)
            return CpuMappedPublicationPreparationResult.Unavailable;
        if (!TryEnterAliasAccess()) return CpuMappedPublicationPreparationResult.Rejected;
        try
        {
            var result = provider.TryPrepareMappedPublicationValidation(_backing, original, out validation);
            if (IsAvailable) return result;
            validation?.Dispose(); validation = null;
            return CpuMappedPublicationPreparationResult.Rejected;
        }
        finally { ExitAliasAccess(); }
    }

    public CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(CpuMappedCopyObservation original,
        out CpuMappedPublicationValidation? validation,
        out CpuMappedPublicationValidationRefusal refusal)
    {
        validation = null; refusal = default;
        if (_host is not IHostMappedPublicationMemory provider || _backing is null)
            return CpuMappedPublicationPreparationResult.Unavailable;
        if (!TryEnterAliasAccess()) return CpuMappedPublicationPreparationResult.Rejected;
        try
        {
            var result = provider.TryPrepareMappedPublicationValidation(_backing, original, out validation, out refusal);
            if (IsAvailable) return result;
            validation?.Dispose(); validation = null; refusal = default;
            return CpuMappedPublicationPreparationResult.Rejected;
        }
        finally { ExitAliasAccess(); }
    }

    public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(CpuMappedCopyObservation original,
        IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication)
        => TryPrepareMappedPublication(original, runs, out publication, out _);

    public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(CpuMappedCopyObservation original,
        IReadOnlyList<CpuMappedWriteRun> runs, out CpuMappedPublication? publication,
        out CpuMappedPublicationPreparationRefusal refusal)
    {
        publication = null; refusal = default;
        if (_host is not IHostMappedPublicationMemory provider || _backing is null)
            return CpuMappedPublicationPreparationResult.Unavailable;
        if (!TryEnterAliasAccess()) return CpuMappedPublicationPreparationResult.Rejected;
        try
        {
            var result = provider.TryPrepareMappedPublication(_backing, original, runs, out publication, out refusal);
            if (IsAvailable) return result;
            publication?.Dispose(); publication = null; refusal = default;
            return CpuMappedPublicationPreparationResult.Rejected;
        }
        finally { ExitAliasAccess(); }
    }

    public CpuMappedCopyResult TryReadMappedCopyWithWriteWatch(ulong footprintAddress, ulong footprintByteCount,
        ulong sourceAddress, Span<byte> destination, out CpuMappedCopyObservation? observation)
    {
        observation = null;
        if (_host is not IHostMappedReadWatchMemory provider || _backing is null)
            return CpuMappedCopyResult.Unavailable;
        if (!TryEnterAliasAccess()) return CpuMappedCopyResult.Rejected;
        try
        {
            var result = provider.TryReadMappedCopyWithWriteWatch(_backing, footprintAddress, footprintByteCount,
                sourceAddress, destination, out observation);
            if (IsAvailable) return result;
            if (observation is not null) provider.ReleaseMappedCopyWriteWatch(observation);
            observation = null; return CpuMappedCopyResult.Rejected;
        }
        finally { ExitAliasAccess(); }
    }
    public bool CanWaitForMappedReadWatchStatus => IsAvailable && CanEmitOrdinaryWriteDiagnostic &&
        _host is IHostMappedReadWatchMemory provider && provider.CanWaitForMappedReadWatchStatus;

    public bool CanWaitForMappedReadWatchEnrollment => IsAvailable && CanEmitOrdinaryWriteDiagnostic &&
        _host is IHostMappedReadWatchMemory provider && provider.CanWaitForMappedReadWatchEnrollment;

    public CpuMappedCopyResult TryReadMappedCopyWithWriteWatchAdmissionWait(ulong footprintAddress,
        ulong footprintByteCount, ulong sourceAddress, Span<byte> destination,
        Func<bool> originalStillCurrent, out CpuMappedCopyObservation? observation)
    {
        ArgumentNullException.ThrowIfNull(originalStillCurrent);
        observation = null;
        var backing = _backing;
        if (_host is not IHostMappedReadWatchMemory provider || backing is null) return CpuMappedCopyResult.Unavailable;
        bool WrapperCurrent() => IsAvailable && ReferenceEquals(_backing, backing) &&
            CanWaitForMappedReadWatchEnrollment;
        bool OriginalWrapper() => WrapperCurrent() && originalStillCurrent() && WrapperCurrent();
        if (!OriginalWrapper() || !TryEnterAliasAccess()) return CpuMappedCopyResult.Rejected;
        try
        {
            var result = provider.TryReadMappedCopyWithWriteWatchAdmissionWait(backing, footprintAddress,
                footprintByteCount, sourceAddress, destination, OriginalWrapper, out observation);
            if (IsAvailable && ReferenceEquals(_backing, backing)) return result;
            if (observation is not null) provider.ReleaseMappedCopyWriteWatch(observation);
            observation = null; return CpuMappedCopyResult.Rejected;
        }
        finally { ExitAliasAccess(); }
    }

    public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation observation) =>
        IsAvailable && _backing is not null && _host is IHostMappedReadWatchMemory provider &&
        provider.IsMappedCopyWriteWatchCurrent(_backing, observation);
    public bool IsMappedCopyWriteWatchCurrent(CpuMappedCopyObservation observation,
        out CpuMappedReadWatchDiagnostic diagnostic)
    {
        if (!IsAvailableForReadWatchDiagnostic(true, out var reason))
        { diagnostic = new() { SharedClosure = reason, BooleanCurrent = false }; return false; }
        if (_backing is not { } backing)
        { diagnostic = new() { SharedClosure = "shared-backing-missing", BooleanCurrent = false }; return false; }
        if (_host is not IHostMappedReadWatchMemory provider)
        { diagnostic = new() { SharedClosure = "shared-pre-capability-unavailable", BooleanCurrent = false }; return false; }
        return provider.IsMappedCopyWriteWatchCurrent(backing, observation, out diagnostic);
    }
    public CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(CpuMappedCopyObservation observation)
    {
        if (!IsAvailable || _backing is not { } backing || _host is not IHostMappedReadWatchMemory provider)
            return CpuMappedReadWatchStatus.Stale;
        var status = provider.GetMappedCopyWriteWatchStatus(backing, observation);
        return IsAvailable && ReferenceEquals(_backing, backing) ? status : CpuMappedReadWatchStatus.Stale;
    }
    public void ReleaseMappedCopyWriteWatch(CpuMappedCopyObservation observation)
    { if (_host is IHostMappedReadWatchMemory provider) provider.ReleaseMappedCopyWriteWatch(observation); }

    private bool IsAvailableForReadWatchDiagnostic(bool before, out string? reason)
    {
        reason = _backing is null ? "shared-backing-missing" :
            Volatile.Read(ref _disposeRequested) ? before ? "shared-pre-dispose-requested" : "shared-post-dispose-requested" :
            Volatile.Read(ref _disposed) ? before ? "shared-pre-disposed" : "shared-post-disposed" :
            _backing.IsClosing ? before ? "shared-pre-backing-closing" : "shared-post-backing-closing" :
            _backing.IsDisposed ? before ? "shared-pre-backing-disposed" : "shared-post-backing-disposed" : null;
        return reason is null;
    }
    public CpuMappedReadWatchStatus GetMappedCopyWriteWatchStatus(CpuMappedCopyObservation observation,
        out CpuMappedReadWatchDiagnostic diagnostic)
    {
        if (!IsAvailableForReadWatchDiagnostic(true, out var reason))
        { diagnostic = new() { SharedClosure = reason }; return CpuMappedReadWatchStatus.Stale; }
        if (_backing is not { } backing)
        { diagnostic = new() { SharedClosure = "shared-backing-missing" }; return CpuMappedReadWatchStatus.Stale; }
        if (_host is not IHostMappedReadWatchMemory provider)
        { diagnostic = new() { SharedClosure = "shared-pre-capability-unavailable" }; return CpuMappedReadWatchStatus.Stale; }
        var status = provider.GetMappedCopyWriteWatchStatus(backing, observation, out diagnostic);
        if (!IsAvailableForReadWatchDiagnostic(false, out reason))
        { diagnostic = diagnostic with { SharedClosure = reason }; return CpuMappedReadWatchStatus.Stale; }
        if (!ReferenceEquals(_backing, backing))
        { diagnostic = diagnostic with { SharedClosure = "shared-post-backing-replaced" }; return CpuMappedReadWatchStatus.Stale; }
        return status;
    }

    public CpuMappedCopyResult TryReadMappedCopy(ulong footprintAddress, ulong footprintByteCount,
        ulong sourceAddress, Span<byte> destination, out CpuMappedCopyObservation? observation)
    {
        observation = null;
        if (_host is not IHostMappedCopyMemory provider || _backing is null)
            return CpuMappedCopyResult.Unavailable;
        if (!TryEnterAliasAccess()) return CpuMappedCopyResult.Rejected;
        try
        {
            var result = provider.TryReadMappedCopy(_backing, footprintAddress, footprintByteCount,
                sourceAddress, destination, out observation);
            if (!IsAvailable) { observation = null; return CpuMappedCopyResult.Rejected; }
            return result;
        }
        finally { ExitAliasAccess(); }
    }

    public bool IsMappedCopyObservationCurrent(CpuMappedCopyObservation observation) =>
        IsAvailable && _backing is not null && _host is IHostMappedCopyMemory provider &&
        provider.IsMappedCopyObservationCurrent(_backing, observation);

    public bool Clear(ulong offset, ulong size)
    {
        if (size == 0) return ClearCore(offset, size);
        // One admission spans the complete multi-chunk operation, including
        // staged/memmove reads. Publication cannot enter between its chunks.
        var interval = _backing?.BeginManagedWriteOperation() ?? default;
        if (!HostManagedWriteIntervals.AllowsBytes(interval)) return false;
        var succeeded = false;
        try { succeeded = ClearCore(offset, size); return succeeded; }
        finally { HostBackingObject.EndManagedWrite(interval, succeeded); }
    }

    private bool ClearCore(ulong offset, ulong size)
    {
        lock (_lock)
        {
            if (!TryEnterAliasAccess()) return false;
            try
            {
                if (!IsAvailable || !IsWithinBacking(offset, size))
                {
                    return false;
                }

                ClearCanonicalAlias(offset, size);
                return true;
            }
            finally { ExitAliasAccess(); }
        }
    }

    public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data)
        => TryWriteBackingAdmission(address, data, false, out _, out _);

    public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data, out OrdinaryBackingWriteReservation? busy)
        => TryWriteBackingAdmission(address, data, true, out busy, out _);

    public bool TryWriteBacking(ulong address, ReadOnlySpan<byte> data, out OrdinaryBackingWriteReservation? busy,
        out SharedWriteFailure failure) => TryWriteBackingAdmission(address, data, true, out busy, out failure);

    private bool TryWriteBackingAdmission(ulong address, ReadOnlySpan<byte> data, bool captureBusy,
        out OrdinaryBackingWriteReservation? busy, out SharedWriteFailure failure)
    {
        busy = null; failure = default;
        if (data.IsEmpty) return TryWriteBackingCore(address, data);
        // One admission spans the complete multi-chunk operation, including
        // staged/memmove reads. Publication cannot enter between its chunks.
        var snapshot = Volatile.Read(ref _snapshot);
        // Once enrolled, retain ORIGINAL provider mapping/guest access before
        // first admission. A later lookup may never repair a busy refusal.
        string? originalCaptureReason = null;
        HostMappedCopyLedger.OrdinaryWriteIdentity? originalMapping = null;
        if (captureBusy && _backing?.ManagedWrites is not null &&
            _host is IHostMappedPublicationMemory originalProvider && originalProvider.CanWaitForOrdinaryWriteAdmission)
        {
            originalMapping = OrdinaryWriteFailureDiagnostics.Enabled
                ? originalProvider.CaptureOrdinaryWriteIdentity(_backing, address, (ulong)data.Length, out originalCaptureReason)
                : originalProvider.CaptureOrdinaryWriteIdentity(_backing, address, (ulong)data.Length);
        }
        var interval = _backing?.BeginManagedWriteOperation() ?? default;
        if (!HostManagedWriteIntervals.AllowsBytes(interval))
        {
            // Exact rejected token is the provenance, never a later admission sample.
            // Save the FIRST existing short-circuit guard; never borrow a
            // later admission or original-currentness sample for diagnostics.
            var originalIdentity = originalMapping.GetValueOrDefault();
            var refusal = !captureBusy ? "busy-capture-disabled"
                : !originalMapping.HasValue ? "original-identity-unavailable"
                : !originalIdentity.IsCurrent ? "original-identity-changed"
                : !interval.IsPublicationBusy ? "admission-not-publication-busy"
                : _host is not IHostMappedPublicationMemory provider ? "provider-unavailable"
                : !provider.CanWaitForOrdinaryWriteAdmission ? "provider-wait-gate-or-unsupported"
                : !TryEnterAliasAccess() ? "alias-pin-refused" : null;
            var pinAcquired = refusal is null;
            var snapshotMatched = false;
            if (pinAcquired)
            {
                try
                {
                    if (!IsAvailable) refusal = "views-unavailable";
                    else if (!(snapshotMatched = ReferenceEquals(snapshot, AliasSnapshot))) refusal = "original-snapshot-changed";
                    else busy = new(this, _backing!, snapshot, originalIdentity, interval.RejectedAdmission);
                }
                finally { if (busy is null) ExitAliasAccess(); }
            }
            if (OrdinaryWriteFailureDiagnostics.Enabled)
                failure = new(refusal ?? "publication-busy-receipt-prepared", interval.RejectedAdmission,
                    originalMapping.HasValue, pinAcquired, snapshotMatched, originalCaptureReason);
            return false; // No destination bytes, not a retryable partial operation.
        }
        var succeeded = false;
        try
        {
            succeeded = TryWriteBackingCore(address, data);
            if (!succeeded && OrdinaryWriteFailureDiagnostics.Enabled) failure = new("backing-access-failed-after-admission", 0, originalMapping.HasValue, false, false, originalCaptureReason);
            return succeeded;
        }
        finally { HostBackingObject.EndManagedWrite(interval, succeeded); }
    }

    internal bool CanEmitOrdinaryWriteDiagnostic =>
        !Monitor.IsEntered(_lock) && !Monitor.IsEntered(_disposeGate) &&
        (_backing is null || !Monitor.IsEntered(_backing.Gate)) &&
        (_host is IHostMappedPublicationMemory provider && provider.CanWaitForOrdinaryWriteAdmission);

    internal void RequireOrdinaryWriteWaitOutsideGates()
    {
        if (_host is not IHostMappedPublicationMemory provider || !provider.CanWaitForOrdinaryWriteAdmission ||
            Monitor.IsEntered(_lock) || Monitor.IsEntered(_disposeGate) || (_backing is not null && Monitor.IsEntered(_backing.Gate)))
            throw new InvalidOperationException("Ordinary write wait cannot hold view/backing/disposal gates.");
    }

    private bool TryWriteBackingCore(ulong address, ReadOnlySpan<byte> data)
    {
        if (TryAccessSingleView(address, (ulong)data.Length, out var target))
        {
            try
            {
                CopyToCanonicalAlias(target, data);
                return true;
            }
            finally
            {
                ExitAliasAccess();
            }
        }

        lock (_lock)
        {
            if (!TryEnterAliasAccess()) return false;
            try
            {
                if (IsAvailable && TryFindRecord(address, (ulong)data.Length, out var record))
                {
                    var offset = record.Offset + address - record.Address;
                    if (!IsWithinBacking(offset, (ulong)data.Length))
                    {
                        return false;
                    }

                    CopyToCanonicalAlias(AliasBase + offset, data);
                    return true;
                }

                if (!TryCollectBackingSegments(address, (ulong)data.Length, out var pieces))
                {
                    return false;
                }

                foreach (var (backing, dataOffset, bytes) in pieces)
                {
                    CopyToCanonicalAlias(backing, data.Slice(dataOffset, bytes));
                }

                return true;
            }
            finally { ExitAliasAccess(); }
        }
    }

    // Each destination chunk keeps its original copy/overlap semantics and
    // existing alias pin. These helpers add ordinary bookkeeping only: an
    // incomplete ledger never prevents the legacy write or authorizes content.
    private void ClearCanonicalAlias(ulong offset, ulong count)
    {
        var interval = _backing!.BeginManagedWrite(offset, count); var succeeded = false;
        if (!HostManagedWriteIntervals.AllowsBytes(interval)) throw new InvalidOperationException("Participating write lost admission.");
        try { NativeMemory.Clear((void*)(AliasBase + offset), (nuint)count); succeeded = true; }
        finally { HostBackingObject.EndManagedWrite(interval, succeeded); }
    }

    private void CopyToCanonicalAlias(ulong destination, ReadOnlySpan<byte> source)
    {
        var interval = _backing!.BeginManagedWrite(destination - AliasBase, (ulong)source.Length);
        var succeeded = false;
        if (!HostManagedWriteIntervals.AllowsBytes(interval)) throw new InvalidOperationException("Participating write lost admission.");
        try
        {
            _beforeCanonicalAliasCopyForControl?.Invoke(destination, source.Length);
            source.CopyTo(new Span<byte>((void*)destination, source.Length));
            succeeded = true;
        }
        finally { HostBackingObject.EndManagedWrite(interval, succeeded); }
    }

    private void CopyCanonicalAliasChunk(ulong source, ulong destination, int bytes)
    {
        var interval = _backing!.BeginManagedWrite(destination - AliasBase, (ulong)bytes);
        var succeeded = false;
        if (!HostManagedWriteIntervals.AllowsBytes(interval)) throw new InvalidOperationException("Participating write lost admission.");
        try { Buffer.MemoryCopy((void*)source, (void*)destination, bytes, bytes); succeeded = true; }
        finally { HostBackingObject.EndManagedWrite(interval, succeeded); }
    }

    // The lock-free single-view read only; false (with nothing read) for anything else.
    public bool TryReadSingleView(ulong address, Span<byte> data)
    {
        if (!TryAccessSingleView(address, (ulong)data.Length, out var source))
        {
            return false;
        }

        try
        {
            new ReadOnlySpan<byte>((void*)source, data.Length).CopyTo(data);
            return true;
        }
        finally
        {
            ExitAliasAccess();
        }
    }

    public bool TryReadBacking(ulong address, Span<byte> data)
    {
        if (TryAccessSingleView(address, (ulong)data.Length, out var source))
        {
            try
            {
                new ReadOnlySpan<byte>((void*)source, data.Length).CopyTo(data);
                return true;
            }
            finally
            {
                ExitAliasAccess();
            }
        }

        lock (_lock)
        {
            if (!TryEnterAliasAccess()) return false;
            try
            {
                // A read inside one mapping needs no temporary segment list.
                if (IsAvailable && TryFindRecord(address, (ulong)data.Length, out var record))
                {
                    var offset = record.Offset + address - record.Address;
                    if (!IsWithinBacking(offset, (ulong)data.Length))
                    {
                        return false;
                    }

                    new ReadOnlySpan<byte>((void*)(AliasBase + offset), data.Length).CopyTo(data);
                    return true;
                }

                if (!TryCollectBackingSegments(address, (ulong)data.Length, out var pieces))
                {
                    return false;
                }

                foreach (var (backing, dataOffset, bytes) in pieces)
                {
                    new ReadOnlySpan<byte>((void*)backing, bytes).CopyTo(data.Slice(dataOffset, bytes));
                }

                return true;
            }
            finally { ExitAliasAccess(); }
        }
    }

    // Use temporary storage if a copy segment can overwrite another segment's source.
    public bool TryCopyBacking(ulong destination, ulong source, ulong size)
    {
        if (size == 0) return TryCopyBackingCore(destination, source, size);
        // One admission spans the complete multi-chunk operation, including
        // staged/memmove reads. Publication cannot enter between its chunks.
        var interval = _backing?.BeginManagedWriteOperation() ?? default;
        if (!HostManagedWriteIntervals.AllowsBytes(interval)) return false;
        var succeeded = false;
        try { succeeded = TryCopyBackingCore(destination, source, size); return succeeded; }
        finally { HostBackingObject.EndManagedWrite(interval, succeeded); }
    }

    private bool TryCopyBackingCore(ulong destination, ulong source, ulong size)
    {
        lock (_lock)
        {
            if (!TryEnterAliasAccess()) return false;
            try
            {
                if (IsAvailable && size <= int.MaxValue &&
                    TryFindRecord(source, size, out var sourceRecord) &&
                    TryFindRecord(destination, size, out var destinationRecord))
                {
                    var sourceOffset = sourceRecord.Offset + source - sourceRecord.Address;
                    var destinationOffset = destinationRecord.Offset + destination - destinationRecord.Address;
                    if (!IsWithinBacking(sourceOffset, size) || !IsWithinBacking(destinationOffset, size))
                    {
                        return false;
                    }

                    // Use the backing alias so CopyTo can detect overlap between different guest views.
                    CopyToCanonicalAlias(AliasBase + destinationOffset,
                        new ReadOnlySpan<byte>((void*)(AliasBase + sourceOffset), (int)size));
                    return true;
                }

                if (!TryCollectBackingSegments(source, size, out var from) || !TryCollectBackingSegments(destination, size, out var to))
                {
                    return false;
                }

                var chunks = CreateCopySegments(from, to);
                if (!HasCrossSegmentOverlap(chunks))
                {
                    foreach (var (fromPtr, toPtr, bytes) in chunks)
                    {
                        CopyCanonicalAliasChunk(fromPtr, toPtr, bytes);
                    }

                    return true;
                }

                var staging = new byte[size];
                foreach (var (backing, dataOffset, bytes) in from)
                {
                    new ReadOnlySpan<byte>((void*)backing, bytes).CopyTo(staging.AsSpan(dataOffset, bytes));
                }

                foreach (var (backing, dataOffset, bytes) in to)
                {
                    CopyToCanonicalAlias(backing, staging.AsSpan(dataOffset, bytes));
                }

                return true;
            }
            finally { ExitAliasAccess(); }
        }
    }

    public bool TryMapReservedRange(ulong address, ulong size, ulong offset, HostPageProtection protection, out HostViewFailure failure)
        => TryMapReservedRange(address, size, offset, protection, false, out failure);

    private int _overlapReports;

    /// <summary>How many mappings were recorded over a range another one still holds.</summary>
    public int OverlappingViewRecords => Volatile.Read(ref _overlapReports);

    // Must be called holding _lock. Reports, once per distinct pair shape, a new view
    // whose guest range intersects a record that has not been unmapped.
    private void WarnOnOverlappingView(ulong address, ulong size, ulong offset)
    {
        if (size == 0)
        {
            return;
        }

        var end = address + size;
        var index = RangeSearch.FindLastIndexAtOrBelow(_views, address);
        // The record at or below can reach into this range, and every record that starts
        // inside it overlaps by construction.
        for (var candidate = Math.Max(index, 0); candidate < _views.Count; candidate++)
        {
            var record = _views.Values[candidate];
            if (record.Address >= end)
            {
                break;
            }

            if (record.Address + record.Size <= address)
            {
                continue;
            }

            if (++_overlapReports <= 32)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] mapping overlaps a live one: new=0x{address:X16}+0x{size:X}@0x{offset:X} " +
                    $"existing=0x{record.Address:X16}+0x{record.Size:X}@0x{record.Offset:X}");
                Console.Error.Flush();
            }

            return;
        }
    }

    private bool TryMapReservedRange(ulong address, ulong size, ulong offset, HostPageProtection protection,
        bool wasRestored, out HostViewFailure failure)
    {
        failure = HostViewFailure.BackingUnavailable;
        if (!TryBeginViewOperation(wasRestored)) return false;
        Exception? primary = null;
        try
        {
            if (_backing is null || _backing.IsClosing || _backing.IsDisposed || !IsWithinBacking(offset, size))
            { failure = HostViewFailure.OffsetOutOfBounds; return false; }
            lock (_lock)
            {
                if (_views.ContainsKey(address)) OnFatal($"A backing view record already exists at 0x{address:X16}.");
                // An exact duplicate was already fatal; a *partial* overlap was not, and it
                // is the worse of the two. The guest is handed two allocations that share
                // bytes, and whichever writes second rewrites the other's data with no
                // fault at the time and no way, later, to say who did it.
                WarnOnOverlappingView(address, size, offset);
            }
            bool mapped;
            try { mapped = _host.TryMapView(_backing, address, offset, size, protection, out failure); }
            catch (Exception error)
            {
                // Provider threw before returning an outcome: retain the attempted
                // exact scope as unknown; do not guess whether MAP_FIXED occurred.
                lock (_lock)
                {
                    _views[address] = new ViewRecord(address, size, offset, protection);
                    _cleanupFailure = error;
                    _disposeRequested = true;
                    PublishSnapshot();
                }
                throw;
            }
            if (!mapped) return false;
            lock (_lock)
            {
                // Nested remaps belong to an already admitted unmap/rollback
                // transaction. Finish them before requested disposal captures.
                if (!_disposeRequested || wasRestored)
                {
                    _views[address] = new ViewRecord(address, size, offset, protection) { WasRestored = wasRestored };
                    PublishSnapshot(); failure = HostViewFailure.None; return true;
                }
            }
            try
            {
                if (!_host.UnmapView(address, size))
                    throw new InvalidOperationException("Late backing view cleanup outcome unknown.");
            }
            catch (Exception error)
            {
                lock (_lock)
                {
                    _views[address] = new ViewRecord(address, size, offset, protection);
                    _cleanupFailure = error;
                }
                throw;
            }
            failure = HostViewFailure.BackingUnavailable; return false;
        }
        catch (Exception error) { primary = error; throw; }
        finally { EndViewOperation(primary); }
    }

    public bool Unmap(ulong address, ulong size, out bool holePreserved)
    {
        holePreserved = false;
        if (!TryBeginViewOperation(continuation: false)) return false;
        Exception? primary = null;
        try
        {
            holePreserved = false;
            if (_backing is null || _backing.IsClosing || size == 0 || ulong.MaxValue - address < size)
            {
                return false;
            }

            var end = address + size;
            var targets = new List<ViewRecord>();
            lock (_lock)
            {
                var current = address;
                while (current < end)
                {
                    if (!TryFindRecord(current, 1, out var record))
                    {
                        return false;
                    }

                    var partSize = Math.Min(end, record.Address + record.Size) - current;
                    targets.Add(new ViewRecord(current, partSize, record.Offset + current - record.Address, record.Protection));
                    current += partSize;
                }
            }

            var removed = new List<ViewRecord>();
            foreach (var target in targets)
            {
                if (!TryUnmapSingleView(target.Address, target.Size, out var partPreserved))
                {
                    RestoreMappingsInReverseOrder(removed);
                    return false;
                }

                if (!partPreserved)
                {
                    OnFatal($"The address range was not kept reserved after unmapping the view at 0x{target.Address:X16}.");
                }

                removed.Add(target);
            }

            // Each removed view leaves a reserved range. Join these ranges into one free range.
            if (removed.Count > 1 && !_host.JoinHoles(address, size))
            {
                RestoreMappingsInReverseOrder(removed);
                return false;
            }

            holePreserved = true;
            return true;

        }
        catch (Exception error) { primary = error; throw; }
        finally { EndViewOperation(primary); }
    }

    private bool TryUnmapSingleView(ulong address, ulong size, out bool holePreserved)
    {
        holePreserved = false;
        var old = default(ViewRecord);
        lock (_lock)
        {
            var index = RangeSearch.FindLastIndexAtOrBelow(_views, address);
            if (index >= 0)
            {
                var record = _views.Values[index];
                if (address + size <= record.Address + record.Size)
                {
                    old = record;
                    _views.RemoveAt(index);
                    PublishSnapshot();
                }
            }
        }

        if (old.Size == 0)
        {
            return false;
        }

        bool unmapped;
        try { unmapped = _host.UnmapView(old.Address, old.Size); }
        catch (Exception error)
        {
            lock (_lock)
            {
                _views[old.Address] = old; // attempted scope retained, not a physical mapping assertion
                _cleanupFailure = error; _disposeRequested = true; PublishSnapshot();
            }
            throw;
        }
        if (!unmapped) { RestoreViewRecord(old); return false; }

        if (address == old.Address && size == old.Size)
        {
            holePreserved = true;
            return true;
        }

        var leftSize = address - old.Address;
        var rightAddress = address + size;
        var rightSize = old.Address + old.Size - rightAddress;
        var ok = true;
        if (leftSize != 0 && leftSize != old.Size)
        {
            ok = _host.SplitHole(old.Address, leftSize) && ok;
        }

        if (rightSize != 0)
        {
            ok = _host.SplitHole(address, size) && ok;
        }

        if (leftSize != 0)
        {
            ok = TryMapReservedRange(old.Address, leftSize, old.Offset, old.Protection, true, out _) && ok;
        }

        if (rightSize != 0)
        {
            ok = TryMapReservedRange(rightAddress, rightSize, old.Offset + (rightAddress - old.Address), old.Protection, true, out _) && ok;
        }

        if (ok)
        {
            holePreserved = true;
            return true;
        }

        if (leftSize != 0 && Contains(old.Address, leftSize) && !TryUnmapSingleView(old.Address, leftSize, out _))
        {
            OnFatal($"Could not remove the partial backing view at 0x{old.Address:X16} during recovery.");
        }

        if (rightSize != 0 && Contains(rightAddress, rightSize) && !TryUnmapSingleView(rightAddress, rightSize, out _))
        {
            OnFatal($"Could not remove the partial backing view at 0x{rightAddress:X16} during recovery.");
        }

        if (!_host.JoinHoles(old.Address, old.Size))
        {
            OnFatal($"Could not join the reserved ranges at 0x{old.Address:X16} during recovery.");
        }

        RestoreViewMapping(old);
        return false;
    }

    internal bool IsRestoredView(ulong address)
    {
        lock (_lock)
        {
            return IsAvailable && TryFindRecord(address, 1, out var record) && record.WasRestored;
        }
    }

    // Contains over the published snapshot, without the lock: descriptor validation asks
    // this for every bound buffer, and the lock is contended by guest threads. A true answer
    // is as current as a locked one would be once the lock were released.
    public bool ContainsWithoutLock(ulong address, ulong size)
    {
        if (size == 0 || ulong.MaxValue - address < size || Volatile.Read(ref _disposed))
        {
            return false;
        }

        var snapshot = Volatile.Read(ref _snapshot);
        var end = address + size;
        var current = address;
        while (current < end)
        {
            var low = 0;
            var high = snapshot.Length - 1;
            var found = -1;
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                if (snapshot[middle].Address <= current)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (found < 0 || current >= snapshot[found].Address + snapshot[found].Size)
            {
                return false;
            }

            current = Math.Min(end, snapshot[found].Address + snapshot[found].Size);
        }

        return true;
    }

    public bool Contains(ulong address, ulong size)
    {
        if (size == 0 || ulong.MaxValue - address < size)
        {
            return false;
        }

        lock (_lock)
        {
            var end = address + size;
            var current = address;
            while (current < end)
            {
                if (!TryFindRecord(current, 1, out var record))
                {
                    return false;
                }

                current = Math.Min(end, record.Address + record.Size);
            }

            return true;
        }
    }

    private bool TryBeginViewOperation(bool continuation)
    {
        lock (_lock)
        {
            var thread = Environment.CurrentManagedThreadId;
            if (_operationDepth != 0 && _operationThread == thread)
            {
                // Thread identity alone cannot admit a fresh callback operation
                // after close. Only private existing-transaction remaps continue.
                if (_disposed || (_disposeRequested && !continuation)) return false;
                _operationDepth++; return true;
            }
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            while (_operationDepth != 0 && !_disposeRequested)
            {
                var remaining = TimeSpan.FromSeconds(2) - System.Diagnostics.Stopwatch.GetElapsedTime(start);
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_lock, remaining)) return false;
            }
            if (_disposeRequested || _disposed) return false;
            _operationThread = thread; _operationDepth = 1; return true;
        }
    }

    private void EndViewOperation(Exception? primary)
    {
        bool close;
        lock (_lock)
        {
            if (_operationThread != Environment.CurrentManagedThreadId || _operationDepth == 0)
                throw new InvalidOperationException("Unbalanced view operation.");
            close = --_operationDepth == 0 && _disposeRequested;
            if (_operationDepth == 0) { _operationThread = 0; Monitor.PulseAll(_lock); }
        }
        if (!close) return;
        try { Dispose(); }
        catch (Exception error) when (primary is not null && error is not OutOfMemoryException and not AccessViolationException)
        { primary.Data["BackingDeferredCleanupFailure"] = error; }
    }

    /// <summary>
    /// Ordinary disposal. Provider reentrancy requests close without waiting for
    /// its own pending operation; CleanupComplete remains false. The outer map/
    /// unmap discharges cleanup before returning, or retains quarantined ownership.
    /// </summary>
    public void Dispose()
    {
        if (Monitor.IsEntered(_lock) || Monitor.IsEntered(_disposeGate))
            throw new InvalidOperationException("Backing disposal cannot drain under the view or cleanup gate.");
        lock (_lock)
        {
            _disposeRequested = true; PublishSnapshot();
            if (_operationDepth != 0 && _operationThread == Environment.CurrentManagedThreadId) return;
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            while (_operationDepth != 0)
            {
                var remaining = TimeSpan.FromSeconds(2) - System.Diagnostics.Stopwatch.GetElapsedTime(start);
                // Monitor.Wait releases the only held gate; pending operation
                // completion never needs the cleanup gate and cannot wait on us.
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(_lock, remaining))
                    throw new TimeoutException("Backing view operation did not finish; storage remains owned.");
            }
        }
        if (!Monitor.TryEnter(_disposeGate, TimeSpan.FromSeconds(2)))
            throw new TimeoutException("Backing cleanup attempt remains in flight.");
        try
        {
            List<ViewRecord> views;
            lock (_lock)
            {
                if (_cleanupComplete) return;
                Volatile.Write(ref _disposed, true); PublishSnapshot();
                if (_cleanupFailure is not null)
                    throw new InvalidOperationException("Backing view cleanup is quarantined.", _cleanupFailure);
                views = new List<ViewRecord>(_views.Values);
            }
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            while (Volatile.Read(ref _activeAccesses) != 0)
            {
                if (System.Diagnostics.Stopwatch.GetElapsedTime(start) >= TimeSpan.FromSeconds(2))
                    throw new TimeoutException("Backing alias accesses did not drain; storage remains owned.");
                Thread.Sleep(1);
            }
            foreach (var view in views)
            {
                try
                {
                    if (!_host.UnmapView(view.Address, view.Size))
                        throw new InvalidOperationException($"Backing view cleanup outcome unknown at 0x{view.Address:X16}.");
                }
                catch (Exception error) { lock (_lock) _cleanupFailure = error; throw; }
                lock (_lock) _views.Remove(view.Address);
            }
            _backing?.Dispose();
            lock (_lock) Volatile.Write(ref _cleanupComplete, true);
        }
        finally { Monitor.Exit(_disposeGate); }
    }

    private bool TryCollectBackingSegments(ulong address, ulong size, out List<(ulong Backing, int DataOffset, int Bytes)> pieces)
    {
        pieces = new List<(ulong, int, int)>();
        if (!IsAvailable || size == 0 || ulong.MaxValue - address < size)
        {
            return false;
        }

        var end = address + size;
        var current = address;
        while (current < end)
        {
            if (!TryFindRecord(current, 1, out var record))
            {
                return false;
            }

            var bytes = Math.Min(end, record.Address + record.Size) - current;
            var offset = record.Offset + current - record.Address;
            if (!IsWithinBacking(offset, bytes))
            {
                return false;
            }

            pieces.Add((AliasBase + offset, (int)(current - address), (int)bytes));
            current += bytes;
        }

        return true;
    }

    private static List<(ulong From, ulong To, int Bytes)> CreateCopySegments(
        List<(ulong Backing, int DataOffset, int Bytes)> from,
        List<(ulong Backing, int DataOffset, int Bytes)> to)
    {
        var chunks = new List<(ulong, ulong, int)>();
        var i = 0;
        var j = 0;
        var offset = 0;
        while (i < from.Count && j < to.Count)
        {
            var fromEnd = from[i].DataOffset + from[i].Bytes;
            var toEnd = to[j].DataOffset + to[j].Bytes;
            var end = Math.Min(fromEnd, toEnd);
            chunks.Add((
                from[i].Backing + (ulong)(offset - from[i].DataOffset),
                to[j].Backing + (ulong)(offset - to[j].DataOffset),
                end - offset));
            offset = end;
            i += fromEnd == end ? 1 : 0;
            j += toEnd == end ? 1 : 0;
        }

        return chunks;
    }

    private static bool HasCrossSegmentOverlap(List<(ulong From, ulong To, int Bytes)> chunks)
    {
        for (var a = 0; a < chunks.Count; a++)
        {
            for (var b = 0; b < chunks.Count; b++)
            {
                if (a != b && chunks[a].From < chunks[b].To + (ulong)chunks[b].Bytes &&
                    chunks[b].To < chunks[a].From + (ulong)chunks[a].Bytes)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryFindRecord(ulong address, ulong size, out ViewRecord record)
    {
        record = default;
        if (size == 0 || ulong.MaxValue - address < size)
        {
            return false;
        }

        var index = RangeSearch.FindLastIndexAtOrBelow(_views, address);
        if (index < 0)
        {
            return false;
        }

        var candidate = _views.Values[index];
        if (address + size > candidate.Address + candidate.Size)
        {
            return false;
        }

        record = candidate;
        return true;
    }

    // Must be called under _lock after every change to _views.
    private void PublishSnapshot()
    {
        if (_disposed || _disposeRequested) { Volatile.Write(ref _snapshot, []); return; }
        var snapshot = new ViewRecord[_views.Count];
        _views.Values.CopyTo(snapshot, 0);
        Volatile.Write(ref _snapshot, snapshot);
    }

    // Resolves an access that lies inside one view to its alias address and registers
    // it as in flight; the caller must decrement _activeAccesses when done. Anything
    // else (spanning views, unmapped, disposed) is left to the locked path.
    private bool TryAccessSingleView(ulong address, ulong size, out ulong target)
    {
        target = 0;
        if (size == 0 || ulong.MaxValue - address < size)
        {
            return false;
        }

        if (!TryEnterAliasAccess()) return false;
        if (TryFindAlias(Volatile.Read(ref _snapshot), address, size, out target)) return true;
        ExitAliasAccess();
        return false;
    }

    public object AliasSnapshot => Volatile.Read(ref _snapshot);

    public bool TryEnterAliasAccess()
    {
        Interlocked.Increment(ref _activeAccesses);
        if (!Volatile.Read(ref _disposed) && !Volatile.Read(ref _disposeRequested) && _backing is not null && _backing.TryEnterAccess())
        {
            if (!Volatile.Read(ref _disposed) && !Volatile.Read(ref _disposeRequested)) return true;
            _backing.ExitAccess();
        }
        Interlocked.Decrement(ref _activeAccesses);
        return false;
    }

    public void ExitAliasAccess()
    {
        _backing!.ExitAccess();
        Interlocked.Decrement(ref _activeAccesses);
    }

    public bool TryResolveAlias(ulong address, ulong size, out ulong target)
    {
        target = 0;
        return size != 0 && ulong.MaxValue - address >= size && TryFindAlias(Volatile.Read(ref _snapshot), address, size, out target);
    }

    /// <summary>
    /// The alias of a range that may span several views, as long as those views are
    /// adjacent in guest space and in the backing. A guest allocation mapped in
    /// several calls is one contiguous run of backing bytes, so a buffer that crosses
    /// a view edge still has one host address — the single-record test cannot see
    /// that, and a caller that maps the backing no-copy would otherwise have to copy.
    /// </summary>
    public bool TryResolveAliasSpan(ulong address, ulong size, out ulong target)
    {
        target = 0;
        if (size == 0 || ulong.MaxValue - address < size)
        {
            return false;
        }

        var snapshot = Volatile.Read(ref _snapshot);
        if (TryFindAlias(snapshot, address, size, out target))
        {
            return true;
        }

        var index = FindRecordIndex(snapshot, address);
        if (index < 0 || Volatile.Read(ref _disposed) || _backing is null)
        {
            target = 0;
            return false;
        }

        var record = snapshot[index];
        if (address >= record.Address + record.Size)
        {
            target = 0;
            return false;
        }

        var offset = record.Offset + address - record.Address;
        var covered = record.Address + record.Size - address;
        while (covered < size)
        {
            if (++index >= snapshot.Length)
            {
                target = 0;
                return false;
            }

            var next = snapshot[index];
            // Adjacent in guest space and continuing at the next backing byte.
            if (next.Address != record.Address + record.Size || next.Offset != record.Offset + record.Size)
            {
                target = 0;
                return false;
            }

            record = next;
            covered += record.Size;
        }

        if (!IsWithinBacking(offset, size))
        {
            target = 0;
            return false;
        }

        target = AliasBase + offset;
        return true;
    }

    // The last record whose address is at or below this one, or -1.
    private static int FindRecordIndex(ViewRecord[] snapshot, ulong address)
    {
        var low = 0;
        var high = snapshot.Length - 1;
        var found = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            if (snapshot[middle].Address <= address)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }

    private bool TryFindAlias(ViewRecord[] snapshot, ulong address, ulong size, out ulong target)
    {
        target = 0;
        var low = 0;
        var high = snapshot.Length - 1;
        var found = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            if (snapshot[middle].Address <= address)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (found >= 0 && !Volatile.Read(ref _disposed) && _backing != null)
        {
            var record = snapshot[found];
            if (address + size <= record.Address + record.Size)
            {
                var offset = record.Offset + address - record.Address;
                if (IsWithinBacking(offset, size))
                {
                    target = AliasBase + offset;
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsWithinBacking(ulong offset, ulong size) =>
        size != 0 && offset < Size && size <= Size - offset;

    private void RestoreViewRecord(ViewRecord record)
    {
        lock (_lock)
        {
            _views[record.Address] = record;
            PublishSnapshot();
        }
    }

    private void RestoreViewMapping(ViewRecord record)
    {
        if (!TryMapReservedRange(record.Address, record.Size, record.Offset, record.Protection, true, out _))
        {
            OnFatal($"Could not restore the backing view at 0x{record.Address:X16}.");
        }
    }

    private void RestoreMappingsInReverseOrder(List<ViewRecord> records)
    {
        for (var index = records.Count - 1; index >= 0; index--)
        {
            RestoreViewMapping(records[index]);
        }
    }
}
