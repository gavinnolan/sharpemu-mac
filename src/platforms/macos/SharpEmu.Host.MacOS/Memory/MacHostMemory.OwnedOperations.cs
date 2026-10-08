// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;

namespace SharpEmu.Host.MacOS;

internal sealed unsafe partial class MacHostMemory
{
    // Dormant control factory. Not called by composition, facade or recovery.
    // No public provider contract lets a caller adopt an address or forge a receipt.
    private bool TryCreateOwnedAnonymousControl(int pages, bool reserveOnly, uint rawProtection,
        out Darwin.OwnedAnonymousAllocation? allocation, out Darwin.AllocationResult result) =>
        Darwin.TryCreateOwned(this, pages, reserveOnly, rawProtection, out allocation, out result);

    private static partial class Darwin
    {
        private const int MaximumOwnedPages = 64;
        private static int _exclusiveAllocationCount;
        private static readonly List<OwnedAnonymousAllocation> ExclusiveAllocations = new();
        private static long _nextOwnedIdentity;

        public enum OwnedState { Active, Pending, Quarantined, Released }
        public enum OperationKind { Protect, Commit, Release, RestrictMaximum }
        public enum OperationPhase { Prepared, Draining, Ready, Executing, Finished }
        public enum NativeOutcome { NoInvocation, AppliedWholeScope, Failed, Partial, Unknown }
        public enum NativeErrorDomain { None, PosixErrno, MachKernel }
        public enum PageOutcome { Unattempted, Applied, Failed, AlreadyReleased, Unknown }

        public readonly record struct AllocationResult(NativeOutcome Outcome, ulong Address,
            ulong ByteSize, ulong NativePageSize, int NativeReturn, int Errno, int CleanupReturn, int CleanupErrno);
        public readonly record struct NativePageResult(ulong Address, ulong ByteSize,
            uint PreviousProtection, uint RequestedProtection, PageOutcome Outcome,
            NativeErrorDomain ErrorDomain, int NativeReturn, int NativeError);

        public sealed class OperationResult
        {
            internal readonly NativePageResult[] _pages;
            internal OperationResult(long allocation, long operation, ulong epoch, OperationKind kind,
                ulong address, ulong size, ulong nativePageSize, uint protection, int pageCount)
            {
                AllocationIdentity = allocation; OperationIdentity = operation; DomainEpoch = epoch;
                Kind = kind; Address = address; Size = size; NativePageSize = nativePageSize;
                RequestedProtection = protection;
                _pages = new NativePageResult[pageCount];
                Pages = Array.AsReadOnly(_pages);
            }
            public long AllocationIdentity { get; }
            public long OperationIdentity { get; }
            public ulong DomainEpoch { get; }
            public OperationKind Kind { get; }
            public ulong Address { get; }
            public ulong Size { get; }
            public ulong NativePageSize { get; }
            public uint RequestedProtection { get; }
            public ReadOnlyCollection<NativePageResult> Pages { get; }
            public NativeOutcome Outcome { get; internal set; } = NativeOutcome.NoInvocation;
            public int NativeInvocations { get; internal set; }
            public int SuccessfulPages { get; internal set; }
            public Exception? BookkeepingFailure { get; internal set; }
        }

        public sealed class OwnedAnonymousAllocation
        {
            internal readonly MacHostMemory _provider;
            internal readonly Region _region;
            internal readonly bool[] _mapped, _unknown;
            internal readonly uint[] _protection;
            internal HostRecoveryOwner? _domain;
            internal OwnedOperation? _pending;
            private long _nextOperation;
            internal OwnedState _state = OwnedState.Quarantined;
            internal bool _registered;

            internal OwnedAnonymousAllocation(MacHostMemory provider, int pages, ulong pageSize, uint protection, bool reserveOnly)
            {
                _provider = provider; ReserveOnly = reserveOnly; NativePageSize = pageSize; ByteSize = checked((ulong)pages * pageSize);
                _mapped = new bool[pages]; _unknown = new bool[pages]; _protection = new uint[pages]; Array.Fill(_protection, protection);
                _region = new Region { Size = ByteSize, DefaultProtect = protection,
                    // Reserve all logical entries before native work. Subsequent updates allocate nothing.
                    PageProtects = new Dictionary<ulong, uint>(checked((int)(ByteSize / PageSize))) };
                if (_nextOwnedIdentity == long.MaxValue) throw new InvalidOperationException("Owned allocation identity exhausted.");
                Identity = ++_nextOwnedIdentity; // caller holds Gate
            }
            public long Identity { get; }
            public bool ReserveOnly { get; }
            public ulong Address => _region.Base;
            public ulong ByteSize { get; }
            public ulong NativePageSize { get; }
            public OwnedState State { get { lock (Gate) return _state; } }
            // Mapping-retention scaffold only. Not a write-permission authorization.
            public HostRecoveryOwner? CurrentDomain { get { lock (Gate) return _state == OwnedState.Active ? _domain : null; } }
            public Exception? SetupFailure { get; internal set; }
            public Exception? SetupCleanupFailure { get; internal set; }
            public int SetupCleanupReturn { get; internal set; }
            public int SetupCleanupErrno { get; internal set; }

            public bool TryPrepareProtection(int firstPage, int pages, uint rawProtection, bool commit,
                out OwnedOperation? operation) => Prepare(firstPage, pages, rawProtection,
                    commit ? OperationKind.Commit : OperationKind.Protect, out operation);
            public bool TryPrepareRelease(out OwnedOperation? operation) =>
                Prepare(0, _mapped.Length, 0, OperationKind.Release, out operation);
            // Controlled partial-unmap qualifier; every release still passes Close/drain.
            public bool TryPreparePageRelease(int firstPage, int pages, out OwnedOperation? operation) =>
                Prepare(firstPage, pages, 0, OperationKind.Release, out operation);
            // Fixture-only prerequisite for a real permission error on anonymous pages.
            // Mach maximum protection is irrevocable; this is not a repair API.
            public bool TryPrepareMaximumRestriction(int firstPage, int pages, uint rawProtection,
                out OwnedOperation? operation) => Prepare(firstPage, pages, rawProtection,
                    OperationKind.RestrictMaximum, out operation);

            private bool Prepare(int first, int pages, uint protection, OperationKind kind,
                out OwnedOperation? operation)
            {
                operation = null;
                if (!ValidPageSlice(first, pages, _mapped.Length) ||
                    kind != OperationKind.Release && !SupportedOwnedProtection(protection) ||
                    kind == OperationKind.RestrictMaximum && protection != PAGE_READONLY) return false;
                lock (Gate)
                {
                    if (!Canonical() || !RegisteredExact() || _pending is not null || _state is OwnedState.Pending or OwnedState.Released ||
                        kind != OperationKind.Release && _state != OwnedState.Active || _nextOperation == long.MaxValue) return false;
                    if (kind != OperationKind.Release && _mapped.Any(static mapped => !mapped)) return false;
                    for (var i = first; i < first + pages; i++) if (_unknown[i]) return false;
                    if (kind == OperationKind.Release && !_mapped.Skip(first).Take(pages).Any(static mapped => mapped)) return false;
                    var epoch = _domain?.DomainEpoch ?? 0;
                    if (epoch == ulong.MaxValue) return false;
                    // Result slots, replacement domain and immutable expectations all exist before Close.
                    var nextDomain = kind == OperationKind.Release ? null :
                        new HostRecoveryOwner(this, Address, ByteSize, NativePageSize, epoch + 1);
                    var result = new OperationResult(Identity, _nextOperation + 1, epoch, kind,
                        Address + (ulong)first * NativePageSize, (ulong)pages * NativePageSize, NativePageSize, protection, pages);
                    for (var i = 0; i < pages; i++)
                        result._pages[i] = new(Address + (ulong)(first + i) * NativePageSize, NativePageSize,
                            _protection[first + i], protection, _mapped[first + i] ? PageOutcome.Unattempted : PageOutcome.AlreadyReleased,
                            NativeErrorDomain.None, 0, 0);
                    operation = new OwnedOperation(this, ++_nextOperation, first, pages, protection, kind, _domain, nextDomain, result);
                    _pending = operation; _state = OwnedState.Pending;
                    _domain?.Close();
                    return true;
                }
            }
            internal bool Canonical() => HostMemoryAuthority.IsInitialized && ReferenceEquals(_provider, HostMemoryAuthority.Current);
            internal bool RegisteredExact() => _registered && Regions.TryGetValue(Address, out var found) &&
                ReferenceEquals(found, _region) && ReferenceEquals(found.ExclusiveOwner, this);
            public bool IsUnknownAt(ulong address) => address >= Address && address - Address < ByteSize &&
                _unknown[(address - Address) / NativePageSize];
            public bool IsMappedAt(ulong address) => address >= Address && address - Address < ByteSize &&
                _mapped[(address - Address) / NativePageSize]; // caller holds Gate
            public ulong UnmappedRunEnd(ulong address)
            {
                var page = (int)((address - Address) / NativePageSize);
                while (page < _mapped.Length && !_mapped[page]) page++;
                return Address + (ulong)page * NativePageSize;
            }
        }

        public sealed class OwnedOperation
        {
            private readonly OwnedAnonymousAllocation _allocation;
            private readonly int _first, _pageCount;
            private readonly uint _protection;
            private readonly OperationKind _kind;
            private readonly HostRecoveryOwner? _oldDomain, _nextDomain;
            private OperationPhase _phase = OperationPhase.Prepared;
            // Reflection-only ordinary-worker seam. No callback or native-path hook.
            private int _bookkeepingFailurePageForControl = -1;
            private Exception? _bookkeepingFailureForControl = null;
            internal OwnedOperation(OwnedAnonymousAllocation allocation, long identity, int first, int pages,
                uint protection, OperationKind kind, HostRecoveryOwner? oldDomain, HostRecoveryOwner? nextDomain, OperationResult result)
            {
                _allocation = allocation; Identity = identity; _first = first; _pageCount = pages;
                _protection = protection; _kind = kind; _oldDomain = oldDomain; _nextDomain = nextDomain; Result = result;
            }
            public long Identity { get; }
            public OperationResult Result { get; }
            public OperationPhase Phase { get { lock (Gate) return _phase; } }
            public bool TryDrain(TimeSpan timeout)
            {
                if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
                // Never hold provider/registry/recovery gates during the ordinary-thread drain.
                if (Monitor.IsEntered(Gate)) throw new InvalidOperationException("Owned drain cannot run under provider Gate.");
                lock (Gate)
                {
                    if (!ExactPending() || _phase != OperationPhase.Prepared) return false;
                    _phase = OperationPhase.Draining;
                }
                var drained = _oldDomain is null || _oldDomain.TryDrain(timeout);
                lock (Gate)
                {
                    if (!ExactPending() || _phase != OperationPhase.Draining) return false;
                    _phase = drained ? OperationPhase.Ready : OperationPhase.Prepared;
                    // Timeout is retained, closed and pending; release can permit a later drain.
                    return drained;
                }
            }
            public bool QuarantineBeforeNative()
            {
                lock (Gate)
                {
                    if (!ExactPending() || _phase is OperationPhase.Executing or OperationPhase.Finished) return false;
                    _allocation._state = OwnedState.Quarantined; _allocation._pending = null;
                    _phase = OperationPhase.Finished; return true;
                }
            }
            // true means this exact ready operation reached its terminal result;
            // it NEVER means that native permission/release work succeeded.
            public bool ExecuteOnce()
            {
                lock (Gate)
                {
                    if (!ExactPending() || _phase != OperationPhase.Ready || !(_oldDomain?.IsClosed ?? true) ||
                        (_oldDomain?.ActivePins ?? 0) != 0) return false;
                    _phase = OperationPhase.Executing;
                    var inFlightPage = -1;
                    try
                    {
                        for (var i = 0; i < _pageCount; i++)
                        {
                            var page = _first + i; var address = _allocation.Address + (ulong)page * _allocation.NativePageSize;
                            var old = _allocation._protection[page];
                            if (!_allocation._mapped[page])
                            {
                                Result._pages[i] = new(address, _allocation.NativePageSize, old, _protection,
                                    PageOutcome.AlreadyReleased, NativeErrorDomain.None, 0, 0);
                                continue;
                            }
                            int nativeReturn, error; NativeErrorDomain errorDomain;
                            inFlightPage = page;
                            if (_kind == OperationKind.RestrictMaximum)
                            {
                                nativeReturn = mach_vm_protect(mach_task_self(), address, _allocation.NativePageSize, 1, ToPosixProtect(_protection));
                                error = nativeReturn; errorDomain = NativeErrorDomain.MachKernel;
                            }
                            else
                            {
                                nativeReturn = _kind == OperationKind.Release
                                    ? munmap((nint)address, (nuint)_allocation.NativePageSize)
                                    : mprotect((nint)address, (nuint)_allocation.NativePageSize, ToPosixProtect(_protection));
                                error = nativeReturn == 0 ? 0 : Marshal.GetLastPInvokeError(); // before any other call
                                errorDomain = NativeErrorDomain.PosixErrno;
                            }
                            Result.NativeInvocations++;
                            inFlightPage = -1; // the exact native return is now known
                            Result._pages[i] = new(address, _allocation.NativePageSize, old, _protection,
                                nativeReturn == 0 ? PageOutcome.Applied : PageOutcome.Failed, errorDomain, nativeReturn, error);
                            if (nativeReturn != 0)
                            {
                                if (_kind == OperationKind.Release) _allocation._unknown[page] = true;
                                Result.Outcome = Result.SuccessfulPages == 0 ? NativeOutcome.Failed : NativeOutcome.Partial;
                                Finish(false); return true;
                            }
                            Result.SuccessfulPages++;
                            if (_kind == OperationKind.Release) _allocation._mapped[page] = false;
                            else
                            {
                                // A maximum restriction cannot raise current access. NoAccess
                                // stays NoAccess; read/write is lowered to read-only.
                                _allocation._protection[page] = _kind == OperationKind.RestrictMaximum
                                    ? old == PAGE_NOACCESS ? PAGE_NOACCESS : PAGE_READONLY : _protection;
                            }
                            for (var logical = address; logical < address + _allocation.NativePageSize; logical += PageSize)
                                _allocation._region.PageProtects![logical] = _kind == OperationKind.Release ? PAGE_NOACCESS : _allocation._protection[page];
                            HostMemory.OnMappingChanged();
                            if (i == _bookkeepingFailurePageForControl && _bookkeepingFailureForControl is { } controlledFailure)
                                throw controlledFailure;
                        }
                        Result.Outcome = NativeOutcome.AppliedWholeScope;
                        Finish(true); return true;
                    }
                    catch (Exception failure)
                    {
                        if (inFlightPage >= 0)
                        {
                            _allocation._unknown[inFlightPage] = true;
                            var i = inFlightPage - _first;
                            Result._pages[i] = Result._pages[i] with { Outcome = PageOutcome.Unknown };
                        }
                        Result.Outcome = NativeOutcome.Unknown; Result.BookkeepingFailure = failure;
                        Finish(false); throw;
                    }
                }
            }
            private bool ExactPending() => _allocation.Canonical() && _allocation.RegisteredExact() &&
                ReferenceEquals(_allocation._pending, this) && _allocation._state == OwnedState.Pending;
            private void Finish(bool allRequestedSucceeded)
            {
                // Every successful release is tracked before publication/cleanup. Never
                // release the original full extent across a deliberately removed page.
                if (_kind == OperationKind.Release)
                {
                    if (allRequestedSucceeded && _allocation._mapped.All(static mapped => !mapped) && !_allocation._unknown.Any(static unknown => unknown))
                    {
                        Regions.Remove(_allocation.Address); _allocation._registered = false;
                        ExclusiveAllocations.Remove(_allocation); _exclusiveAllocationCount--; _allocation._state = OwnedState.Released;
                    }
                    else _allocation._state = OwnedState.Quarantined;
                }
                else if (allRequestedSucceeded)
                {
                    _allocation._domain = _nextDomain; _allocation._state = OwnedState.Active;
                }
                else _allocation._state = OwnedState.Quarantined;
                _allocation._pending = null; _phase = OperationPhase.Finished;
            }
        }

        private static bool ValidPageSlice(int first, int count, int total) => first >= 0 && count > 0 &&
            first < total && count <= total - first;
        private static bool SupportedOwnedProtection(uint protection) => protection is PAGE_NOACCESS or PAGE_READONLY or PAGE_READWRITE;
        private static bool OwnedGeometry(int pages, ulong nativePageSize, out ulong size)
        {
            size = 0;
            if (pages is < 1 or > MaximumOwnedPages || nativePageSize is not (4096 or 16384)) return false;
            size = (ulong)pages * nativePageSize; return true;
        }
        // Exact conservative native effect envelope, including legacy 4KiB logical inputs.
        private static bool NativeEffectEnvelope(ulong address, ulong size, ulong pageSize, out ulong start, out ulong end)
        {
            start = end = 0;
            if (size == 0 || pageSize == 0 || (pageSize & (pageSize - 1)) != 0 || size > ulong.MaxValue - address) return false;
            var requestedEnd = address + size;
            if (requestedEnd > ulong.MaxValue - (pageSize - 1)) return false;
            start = address & ~(pageSize - 1); end = (requestedEnd + pageSize - 1) & ~(pageSize - 1);
            return end > start;
        }
        private static bool NewAnonymousPlacementExcludedLocked()
        {
            // A kernel-chosen address could select a released/uncertain hole in
            // a retained full-extent cookie. No reservation/alias support exists here.
            if (_exclusiveAllocationCount == 0) return false;
            foreach (var owner in ExclusiveAllocations)
                for (var page = 0; page < owner._mapped.Length; page++)
                    if (!owner._mapped[page] || owner._unknown[page]) return true;
            return false;
        }
        private static bool ContainsUnknownOwnedPageLocked(ulong address)
        {
            if (_exclusiveAllocationCount == 0) return false;
            foreach (var owner in ExclusiveAllocations) if (owner.IsUnknownAt(address)) return true;
            return false;
        }
        private static bool OverlapsOwnedEffectLocked(ulong address, ulong size)
        {
            if (_exclusiveAllocationCount == 0) return false; // unchanged dormant production route
            if (!NativeEffectEnvelope(address, size, (ulong)Environment.SystemPageSize, out var start, out var end)) return true;
            foreach (var owner in ExclusiveAllocations)
            {
                if (owner.ByteSize > ulong.MaxValue - owner.Address) return true;
                if (start < owner.Address + owner.ByteSize && owner.Address < end) return true;
            }
            return false;
        }
        public static bool TryCreateOwned(MacHostMemory provider, int pages, bool reserveOnly, uint protection,
            out OwnedAnonymousAllocation? allocation, out AllocationResult result)
        {
            allocation = null; result = default;
            var pageSize = (ulong)Environment.SystemPageSize;
            if (!HostMemoryAuthority.IsInitialized || !ReferenceEquals(provider, HostMemoryAuthority.Current) ||
                !OwnedGeometry(pages, pageSize, out var bytes) || !SupportedOwnedProtection(protection)) return false;
            lock (Gate)
            {
                if (NewAnonymousPlacementExcludedLocked()) return false;
                var owner = new OwnedAnonymousAllocation(provider, pages, pageSize, protection, reserveOnly);
                if (Regions.Count == int.MaxValue) return false;
                if (Regions.Capacity < Regions.Count + 1) Regions.Capacity = Regions.Count + 1;
                if (ExclusiveAllocations.Capacity < ExclusiveAllocations.Count + 1) ExclusiveAllocations.Capacity = ExclusiveAllocations.Count + 1;
                var address = mmap(0, (nuint)bytes, ToPosixProtect(protection), MAP_PRIVATE | MAP_ANON, -1, 0);
                var error = address == MAP_FAILED ? Marshal.GetLastPInvokeError() : 0;
                if (address == MAP_FAILED)
                { result = new(NativeOutcome.Failed, 0, bytes, pageSize, -1, error, 0, 0); return false; }
                owner._region.Base = (ulong)address; Array.Fill(owner._mapped, true);
                // Still private under Gate: retain a preallocated quarantine cookie
                // before address-bearing setup can fail, including logical table insertion.
                ExclusiveAllocations.Add(owner); _exclusiveAllocationCount++;
                allocation = owner; // retain exact ownership even if later setup/cleanup fails
                try
                {
                    if (!NativeEffectEnvelope((ulong)address, bytes, pageSize, out var nativeStart, out var nativeEnd) ||
                        nativeStart != (ulong)address || nativeEnd - nativeStart != bytes)
                        throw new InvalidOperationException("Owned mmap returned an unqualified native extent.");
                    for (var logical = (ulong)address; logical < (ulong)address + bytes; logical += PageSize)
                        owner._region.PageProtects![logical] = protection;
                    owner._domain = new HostRecoveryOwner(owner, (ulong)address, bytes, pageSize, 1);
                    owner._region.ExclusiveOwner = owner;
                    Regions.Add((ulong)address, owner._region); owner._registered = true;
                    owner._state = OwnedState.Active;
                    HostMemory.OnMappingChanged();
                    result = new(NativeOutcome.AppliedWholeScope, (ulong)address, bytes, pageSize, 0, 0, 0, 0);
                    return true;
                }
                catch (Exception failure)
                {
                    owner.SetupFailure = failure; owner._domain?.Close(); owner._state = OwnedState.Quarantined;
                    var cleanup = int.MinValue; var cleanupError = 0;
                    try
                    {
                        cleanup = munmap(address, (nuint)bytes);
                        cleanupError = cleanup == 0 ? 0 : Marshal.GetLastPInvokeError();
                    }
                    catch (Exception cleanupFailure) when (cleanupFailure is not OutOfMemoryException and not AccessViolationException)
                    { owner.SetupCleanupFailure = cleanupFailure; }
                    owner.SetupCleanupReturn = cleanup; owner.SetupCleanupErrno = cleanupError;
                    result = new(NativeOutcome.Unknown, (ulong)address, bytes, pageSize, 0, 0, cleanup, cleanupError);
                    if (cleanup == 0)
                    {
                        Array.Fill(owner._mapped, false);
                        if (owner._registered) { Regions.Remove((ulong)address); owner._registered = false; }
                        ExclusiveAllocations.Remove(owner); _exclusiveAllocationCount--;
                        owner._state = OwnedState.Released; allocation = null;
                    }
                    else
                    {
                        // Failed cleanup is an unknown release receipt. Keep the
                        // independent cookie marker; never free these addresses again.
                        Array.Fill(owner._unknown, true);
                    }
                    throw; // preserve primary failure; cleanup diagnostic remains on retained owner/result
                }
            }
        }

        [DllImport("libSystem.B.dylib")]
        private static extern int mach_vm_protect(uint target, ulong address, ulong size, int setMaximum, int protection);
    }
}
