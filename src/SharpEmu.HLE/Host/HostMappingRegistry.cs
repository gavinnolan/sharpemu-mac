// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

/// <summary>Caller-described mapping; backing reference is a canonical identity, not a native handle receipt.</summary>
public readonly record struct HostMappingSpec(ulong Address, ulong Size, object BackingIdentity,
    ulong BackingSize, ulong BackingOffset, HostPageProtection Protection);

/// <summary>Immutable registered segment. DomainEpoch is independent of tracker arm/page epochs.</summary>
public sealed record HostMappingRecord(HostMappingSpec Mapping, HostRecoveryOwner Domain);

/// <summary>
/// Unused safe-side registry contract. No native provider or fault callback is
/// connected. Registry metadata and caller-recorded outcomes do not certify OS ownership.
/// </summary>
public sealed class HostMappingRegistry
{
    private readonly object _gate = new();
    private readonly ulong _pageSize;
    private HostMappingRecord[] _records = [];
    private HostMappingMutation? _pending;
    private ulong _nextEpoch;
    private long _nextOperation;

    public HostMappingRegistry(ulong nativePageSize)
    {
        if (nativePageSize == 0 || (nativePageSize & (nativePageSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(nativePageSize));
        _pageSize = nativePageSize;
    }

    /// <summary>Safe-side initial registration only; caller must already own the exact native mapping.</summary>
    public bool TryPublishInitial(HostMappingSpec mapping)
    {
        lock (_gate)
        {
            if (_pending is not null || !Valid(mapping) ||
                _records.Any(r => Overlaps(r.Mapping, mapping.Address, mapping.Size)) || !ConsistentBacking(mapping))
                return false;
            var record = Create(mapping);
            _records = _records.Append(record).OrderBy(r => r.Mapping.Address).ToArray();
            return true;
        }
    }

    /// <summary>Ordinary-thread lookup, never a signal-safe Query replacement.</summary>
    public bool TryFind(ulong address, out HostMappingRecord? record)
    {
        lock (_gate)
        {
            record = _records.FirstOrDefault(r => address >= r.Mapping.Address &&
                address - r.Mapping.Address < r.Mapping.Size);
            if (record is not null && !record.Domain.IsClosed) return true;
            record = null;
            return false;
        }
    }

    /// <summary>Diagnostic safe-side copy, including quarantined/closed records.</summary>
    public IReadOnlyList<HostMappingRecord> Snapshot()
    { lock (_gate) return Array.AsReadOnly((HostMappingRecord[])_records.Clone()); }

    /// <summary>
    /// Preallocates a complete range replacement plan, then revokes affected
    /// identities. Empty replacements mean unmap; otherwise replacements must
    /// tile the entire requested range. Call outside every provider/recovery gate.
    /// </summary>
    public bool TryBeginChange(HostAddressRange range, IReadOnlyList<HostMappingSpec> replacements,
        out HostMappingMutation? mutation)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        lock (_gate)
        {
            mutation = null;
            if (_pending is not null || !ValidRange(range.Address, range.Size)) return false;
            var captured = replacements.ToArray(); // ordinary setup owns immutable plan bytes
            if (captured.Length != 0)
            {
                var cursor = range.Address;
                foreach (var spec in captured)
                {
                    if (!Valid(spec) || !ConsistentBacking(spec) || spec.Address != cursor ||
                        spec.Size > range.Address + range.Size - cursor) return false;
                    cursor += spec.Size;
                }
                if (cursor != range.Address + range.Size) return false;
                for (var i = 0; i < captured.Length; i++)
                    for (var j = 0; j < i; j++)
                        if (ReferenceEquals(captured[i].BackingIdentity, captured[j].BackingIdentity) &&
                            captured[i].BackingSize != captured[j].BackingSize) return false;
            }
            var affected = _records.Where(r => Overlaps(r.Mapping, range.Address, range.Size)).ToArray();
            // Unknown closed coverage cannot be reopened through a partial repair.
            if (affected.Any(r => r.Domain.IsClosed && (range.Address > r.Mapping.Address ||
                range.Address + range.Size < r.Mapping.Address + r.Mapping.Size))) return false;
            var next = _records.Except(affected).ToList();
            foreach (var old in affected)
            {
                var map = old.Mapping;
                if (map.Address < range.Address)
                    next.Add(Create(map with { Size = range.Address - map.Address }));
                var end = range.Address + range.Size;
                if (map.Address + map.Size > end)
                    next.Add(Create(map with { Address = end, Size = map.Address + map.Size - end,
                        BackingOffset = map.BackingOffset + end - map.Address }));
            }
            foreach (var spec in captured) next.Add(Create(spec));
            return Reserve(affected, next.OrderBy(r => r.Mapping.Address).ToArray(), [range], out mutation);
        }
    }

    /// <summary>
    /// Closes every registered alias of one canonical backing identity. Native
    /// alias mappings must all be registered before any backing release hook is enabled.
    /// </summary>
    public bool TryBeginBackingRetirement(object backingIdentity, out HostMappingMutation? mutation)
    {
        ArgumentNullException.ThrowIfNull(backingIdentity);
        lock (_gate)
        {
            mutation = null;
            if (_pending is not null) return false;
            var affected = _records.Where(r => ReferenceEquals(r.Mapping.BackingIdentity, backingIdentity)).ToArray();
            if (affected.Length == 0) return false;
            return Reserve(affected, _records.Except(affected).ToArray(),
                affected.Select(r => new HostAddressRange(r.Mapping.Address, r.Mapping.Size)).ToArray(), out mutation);
        }
    }

    private bool Reserve(HostMappingRecord[] affected, HostMappingRecord[] next,
        HostAddressRange[] ranges, out HostMappingMutation? mutation)
    {
        // All setup allocations precede revocation. Registry gate is released
        // by the caller before any drain/native operation; signal Release never needs it.
        mutation = new(this, affected, next, ranges);
        _pending = mutation;
        foreach (var record in affected) record.Domain.Close();
        return true;
    }

    internal bool Drain(HostMappingMutation mutation, TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, mutation) || mutation.State != MutationState.Prepared) return false;
            mutation.State = MutationState.Draining;
        }
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var drained = true;
        // No registry/provider/recovery gate is held here. Caller must not own a signal pin.
        foreach (var record in mutation.Affected)
        {
            var remaining = timeout - System.Diagnostics.Stopwatch.GetElapsedTime(start);
            if (!record.Domain.TryDrain(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
            { drained = false; break; }
        }
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, mutation) || mutation.State != MutationState.Draining) return false;
            mutation.State = drained ? MutationState.Ready : MutationState.Prepared;
            return drained;
        }
    }

    internal bool Start(HostMappingMutation mutation, out HostMappingOperation? operation)
    {
        lock (_gate)
        {
            operation = null;
            if (!ReferenceEquals(_pending, mutation) || mutation.State != MutationState.Ready ||
                _nextOperation == long.MaxValue) return false;
            operation = new(++_nextOperation, mutation.Ranges);
            mutation.Operation = operation;
            mutation.State = MutationState.KernelInFlight;
            return true;
        }
    }

    internal bool Record(HostMappingMutation mutation, HostMappingOperation operation,
        IReadOnlyList<HostAddressRange> actualRanges, HostMappingKernelOutcome outcome, int nativeError)
    {
        ArgumentNullException.ThrowIfNull(actualRanges);
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, mutation) || mutation.State != MutationState.KernelInFlight ||
                !ReferenceEquals(mutation.Operation, operation) ||
                (uint)outcome > (uint)HostMappingKernelOutcome.Unknown ||
                (outcome == HostMappingKernelOutcome.AppliedWholeScope && nativeError != 0) ||
                actualRanges.Count != mutation.Ranges.Length) return false;
            for (var i = 0; i < actualRanges.Count; i++)
                if (actualRanges[i] != mutation.Ranges[i]) return false;
            mutation.Outcome = outcome;
            mutation.NativeError = nativeError;
            mutation.State = MutationState.Recorded;
            return true;
        }
    }

    internal bool Complete(HostMappingMutation mutation, HostMappingOperation operation)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, mutation) || mutation.State != MutationState.Recorded ||
                !ReferenceEquals(mutation.Operation, operation)) return false;
            if (mutation.Outcome == HostMappingKernelOutcome.AppliedWholeScope)
            {
                _records = mutation.Next;
                mutation.State = MutationState.Committed;
            }
            else mutation.State = MutationState.Quarantined; // old records stay closed, never reopened
            _pending = null;
            return mutation.State == MutationState.Committed;
        }
    }

    internal bool Abandon(HostMappingMutation mutation)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_pending, mutation) || mutation.State is
                MutationState.KernelInFlight or MutationState.Recorded) return false;
            mutation.State = MutationState.Quarantined;
            _pending = null;
            return true;
        }
    }

    private HostMappingRecord Create(HostMappingSpec spec)
    {
        if (_nextEpoch == ulong.MaxValue) throw new InvalidOperationException("Mapping epoch exhausted.");
        return new(spec, new(spec.BackingIdentity, spec.Address, spec.Size, _pageSize, ++_nextEpoch));
    }

    private bool Valid(HostMappingSpec spec) => spec.BackingIdentity is not null &&
        ValidRange(spec.Address, spec.Size) && spec.BackingOffset % _pageSize == 0 &&
        spec.BackingOffset < spec.BackingSize && spec.Size <= spec.BackingSize - spec.BackingOffset &&
        (uint)spec.Protection <= (uint)HostPageProtection.ExecuteWriteCopy;
    private bool ValidRange(ulong address, ulong size) => HostViewMemory.IsValidRange(address, size) &&
        address % _pageSize == 0 && size % _pageSize == 0;
    private bool ConsistentBacking(HostMappingSpec spec) => _records.All(r =>
        !ReferenceEquals(r.Mapping.BackingIdentity, spec.BackingIdentity) || r.Mapping.BackingSize == spec.BackingSize);
    private static bool Overlaps(HostMappingSpec map, ulong address, ulong size) =>
        map.Address < address + size && address < map.Address + map.Size;
    internal enum MutationState { Prepared, Draining, Ready, KernelInFlight, Recorded, Committed, Quarantined }
}

/// <summary>Safe-side caller-recorded result, not an independently verified kernel permission receipt.</summary>
public enum HostMappingKernelOutcome { AppliedWholeScope, FailedUnchanged, Partial, Unknown }

/// <summary>One-use safe mutation operation with immutable expected native scopes.</summary>
public sealed class HostMappingOperation
{
    internal HostMappingOperation(long identity, HostAddressRange[] ranges)
    { Identity = identity; Ranges = Array.AsReadOnly(ranges); }
    public long Identity { get; }
    public IReadOnlyList<HostAddressRange> Ranges { get; }
}

/// <summary>Safe-side reservation. No disposal/finalizer can release an in-flight kernel operation.</summary>
public sealed class HostMappingMutation
{
    private readonly HostMappingRegistry _registry;
    internal HostMappingMutation(HostMappingRegistry registry, HostMappingRecord[] affected,
        HostMappingRecord[] next, HostAddressRange[] ranges)
    { _registry = registry; Affected = affected; Next = next; Ranges = ranges; }
    internal HostMappingRecord[] Affected { get; }
    internal HostMappingRecord[] Next { get; }
    internal HostAddressRange[] Ranges { get; }
    internal HostMappingRegistry.MutationState State;
    internal HostMappingOperation? Operation;
    internal HostMappingKernelOutcome Outcome;
    internal int NativeError;
    public bool TryDrain(TimeSpan timeout) => _registry.Drain(this, timeout);
    public bool TryStartKernelOperation(out HostMappingOperation? operation) => _registry.Start(this, out operation);
    public bool RecordKernelOutcome(HostMappingOperation operation, IReadOnlyList<HostAddressRange> actualRanges,
        HostMappingKernelOutcome outcome, int nativeError) => _registry.Record(this, operation, actualRanges, outcome, nativeError);
    public bool CompleteRecordedOperation(HostMappingOperation operation) => _registry.Complete(this, operation);
    public bool TryAbandonBeforeKernel() => _registry.Abandon(this);
}
