// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>Provider-private participation ledger. The provider holds Gate across each
/// native mapping effect and its publication. Copies release Gate before touching bytes.
/// Public scalar metadata never authorizes an operation on a different ledger.</summary>
public sealed unsafe class HostMappedCopyLedger
{
    public const int MaximumMappings = 65536;
    public const int MaximumCopyBytes = 64 * 1024 * 1024;
    private readonly object gate;
    private readonly ulong pageSize;
    private readonly SortedList<ulong, View> views = new();
    private sealed class Alias(HostAddressRange range)
    {
        internal readonly HostAddressRange Range = range;
        internal int ReleaseState; // 0 active, 1 pending, 2 consumed/unknown
    }
    private readonly Dictionary<HostBackingObject, Alias> aliases = new();
    private sealed class Capture(HostMappedCopyLedger owner, View[] states)
    {
        internal readonly HostMappedCopyLedger Owner = owner;
        internal readonly View[] States = states;
    }
    private long nextIncarnation;
    private bool unknownAlias;
    private int aliasEffectsInFlight;
    // Ordinary test-only pause, default null. Never called under provider Gate,
    // on a signal path, or by legacy reads. No public setter/callback contract.
    private Action? afterCaptureForControl = null;

    public HostMappedCopyLedger(object providerGate, ulong nativePageSize)
    {
        ArgumentNullException.ThrowIfNull(providerGate);
        if (nativePageSize == 0 || (nativePageSize & (nativePageSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(nativePageSize));
        gate = providerGate; pageSize = nativePageSize;
    }

    public ulong NativePageSize => pageSize;

    public sealed class View
    {
        internal View(HostMappedCopyLedger owner, HostBackingObject backing, ulong address,
            ulong size, ulong offset, GuestPageProtection access, long incarnation)
        { Owner = owner; Backing = backing; Address = address; Size = size; Offset = offset; Access = access; Incarnation = incarnation; }
        internal readonly HostMappedCopyLedger Owner;
        internal readonly HostBackingObject Backing;
        internal readonly ulong Address, Size, Offset;
        internal readonly GuestPageProtection Access;
        internal readonly long Incarnation;
        internal int Revoked;
    }

    private void RequireGate()
    {
        if (!Monitor.IsEntered(gate)) throw new InvalidOperationException("Provider mapping gate is required.");
    }

    public bool CanMakeAnonymousPlacement() { RequireGate(); return !unknownAlias && aliasEffectsInFlight == 0; }
    public bool IsInternalAliasAddress(ulong address)
    {
        RequireGate();
        foreach (var alias in aliases.Values)
            if (address >= alias.Range.Address && address - alias.Range.Address < alias.Range.Size) return true;
        return false;
    }

    public void PreflightBackingPublication()
    { RequireGate(); aliases.EnsureCapacity(aliases.Count + 1); }

    public void RegisterBacking(HostBackingObject backing)
    {
        RequireGate();
        if (!backing.HasOwnedLifetime || backing.Identity <= 0 || backing.IsClosing || backing.IsDisposed ||
            !TryEffectRange(backing.AliasBase, backing.Size, out var range))
            throw new ArgumentException("Canonical backing alias has no complete native footprint.", nameof(backing));
        aliases.Add(backing, new(range));
    }

    // Setup's own cleanup may have an unknown native outcome before a backing
    // object was publishable. Do not allocate to retain this quarantine.
    public void QuarantineUnknownAlias() { RequireGate(); unknownAlias = true; RevokeAll(); }

    public View? PrepareView(HostBackingObject backing, ulong address, ulong size,
        ulong offset, HostPageProtection protection)
    {
        RequireGate();
        var access = AccessOf(protection);
        if (unknownAlias || !aliases.ContainsKey(backing) || backing.IsClosing || backing.IsDisposed ||
            !HostViewMemory.IsValidRange(address, size) || address % pageSize != 0 || size % pageSize != 0 ||
            offset % pageSize != 0 || offset > backing.Size || size > backing.Size - offset ||
            access == GuestPageProtection.None || views.Count >= MaximumMappings)
            return null;
        if (nextIncarnation == long.MaxValue)
        { RevokeAll(); return null; } // No process-local wrap/reuse.
        if (views.Capacity < views.Count + 1)
            views.Capacity = Math.Min(MaximumMappings, Math.Max(4, views.Capacity * 2));
        return new(this, backing, address, size, offset, access, ++nextIncarnation);
    }

    public void PublishView(View? prepared)
    {
        RequireGate();
        if (prepared is null) return;
        if (!ReferenceEquals(prepared.Owner, this) || prepared.Revoked != 0 ||
            prepared.Backing.IsClosing || prepared.Backing.IsDisposed)
            throw new InvalidOperationException("Mapping publication has no exact live provider owner.");
        views.Add(prepared.Address, prepared);
    }

    /// <summary>Before ANY participating kernel effect. Internal aliases are
    /// excluded; failed effects do not reopen old view incarnations.</summary>
    public bool BeforeNativeEffect(ulong address, ulong size)
    {
        RequireGate();
        if (unknownAlias || !TryEffectRange(address, size, out var effect)) return false;
        foreach (var alias in aliases.Values)
            if (Overlaps(effect, alias.Range)) return false;
        Revoke(effect);
        return true;
    }

    // The provider's private callback alone owns this exact backing object.
    // It revokes under Gate, executes its alias unmap OUTSIDE Gate after the
    // existing backing pins drain, then records only the actual munmap outcome.
    public bool BeginBackingRelease(HostBackingObject backing)
    {
        RequireGate();
        if (!backing.IsClosing || !aliases.TryGetValue(backing, out var alias) || alias.ReleaseState != 0) return false;
        alias.ReleaseState = 1;
        for (var index = views.Count - 1; index >= 0; index--)
            if (ReferenceEquals(views.Values[index].Backing, backing)) RevokeAt(index);
        aliasEffectsInFlight++;
        return true;
    }

    public void CompleteBackingAliasRelease(HostBackingObject backing, bool nativeSuccess)
    {
        RequireGate();
        if (aliasEffectsInFlight <= 0 || !aliases.TryGetValue(backing, out var alias) || alias.ReleaseState != 1)
            throw new InvalidOperationException("No exact canonical alias effect is in flight.");
        // Consume the exact pending alias before either successful removal or
        // sticky unknown quarantine. A replay cannot steal another alias count.
        alias.ReleaseState = 2;
        aliasEffectsInFlight--;
        if (nativeSuccess) aliases.Remove(backing);
        else QuarantineUnknownAlias();
    }

    public CpuMappedCopyResult TryReadMappedCopy(HostBackingObject backing, ulong footprintAddress,
        ulong footprintByteCount, ulong sourceAddress, Span<byte> destination,
        out CpuMappedCopyObservation? observation)
    {
        observation = null;
        if (!HostViewMemory.IsValidRange(footprintAddress, footprintByteCount) || destination.Length == 0 ||
            destination.Length > MaximumCopyBytes || !HostViewMemory.IsValidRange(sourceAddress, (ulong)destination.Length) ||
            sourceAddress < footprintAddress || sourceAddress - footprintAddress > footprintByteCount ||
            (ulong)destination.Length > footprintByteCount - (sourceAddress - footprintAddress))
            return CpuMappedCopyResult.Rejected;
        if (Monitor.IsEntered(gate)) throw new InvalidOperationException("Mapped copy must not run under the provider gate.");
        if (!backing.TryEnterAccess()) return CpuMappedCopyResult.Rejected;
        try
        {
            // Enrollment does not certify pre-existing/native writers, serialize
            // the copy, or change mapping-only IsCurrent semantics.
            backing.EnrollManagedWrites();
            View[] selected;
            CpuMappedCopyObservation captured;
            lock (gate)
            {
                if (unknownAlias || !aliases.ContainsKey(backing)) return CpuMappedCopyResult.Unavailable;
                var states = new List<View>();
                var segments = new List<CpuMappingFootprintSegment>();
                var cursor = footprintAddress; var end = footprintAddress + footprintByteCount;
                while (cursor < end)
                {
                    var index = FindAtOrBelow(cursor);
                    if (index < 0) return CpuMappedCopyResult.Rejected;
                    var view = views.Values[index];
                    if (view.Revoked != 0 || !ReferenceEquals(view.Backing, backing) ||
                        cursor < view.Address || cursor - view.Address >= view.Size ||
                        (view.Access & GuestPageProtection.Read) == 0 || states.Count == CpuMappedCopyObservation.MaximumSegments)
                        return CpuMappedCopyResult.Rejected;
                    var bytes = Math.Min(end - cursor, view.Size - (cursor - view.Address));
                    states.Add(view);
                    segments.Add(new(view.Address, view.Size, cursor, bytes, view.Incarnation,
                        backing.Identity, backing.Size, view.Offset, pageSize, view.Access));
                    cursor += bytes;
                }
                selected = states.ToArray();
                captured = new(new Capture(this, selected), footprintAddress, footprintByteCount, sourceAddress,
                    (ulong)destination.Length, segments.ToArray());
            }
            // No virtual pointer, permission repair, provider query, callback or
            // mapping gate is used here. The exact backing pin owns the alias.
            afterCaptureForControl?.Invoke();
            var sourceEnd = sourceAddress + (ulong)destination.Length;
            foreach (var view in selected)
            {
                var first = Math.Max(sourceAddress, view.Address);
                var last = Math.Min(sourceEnd, view.Address + view.Size);
                if (first >= last) continue;
                var offset = view.Offset + first - view.Address;
                new ReadOnlySpan<byte>((void*)(backing.AliasBase + offset), checked((int)(last - first)))
                    .CopyTo(destination.Slice(checked((int)(first - sourceAddress))));
            }
            if (!IsCurrent(backing, captured)) return CpuMappedCopyResult.Rejected;
            observation = captured;
            return CpuMappedCopyResult.Copied;
        }
        finally { backing.ExitAccess(); }
    }

    // Bounded scalar validation, without taking Gate or consulting native state.
    public bool IsCurrent(HostBackingObject backing, CpuMappedCopyObservation observation)
    {
        if (observation is null || observation.OwnerIdentity is not Capture captured ||
            !ReferenceEquals(captured.Owner, this) || Volatile.Read(ref unknownAlias) || backing.IsClosing || backing.IsDisposed) return false;
        foreach (var view in captured.States)
            if (!ReferenceEquals(view.Backing, backing) || Volatile.Read(ref view.Revoked) != 0) return false;
        return true;
    }

    private bool TryEffectRange(ulong address, ulong size, out HostAddressRange effect)
    {
        effect = default;
        if (!HostViewMemory.IsValidRange(address, size)) return false;
        var start = address & ~(pageSize - 1);
        var end = address + size;
        if (end > ulong.MaxValue - (pageSize - 1)) return false;
        end = (end + pageSize - 1) & ~(pageSize - 1);
        effect = new(start, end - start); return true;
    }
    private static bool Overlaps(HostAddressRange a, HostAddressRange b) =>
        a.Address < b.Address + b.Size && b.Address < a.Address + a.Size;
    private void Revoke(HostAddressRange effect)
    {
        for (var index = FindAtOrBelow(effect.Address + effect.Size - 1); index >= 0; index--)
        {
            var view = views.Values[index];
            if (view.Address + view.Size <= effect.Address) break;
            RevokeAt(index);
        }
    }
    private void RevokeAt(int index) { Volatile.Write(ref views.Values[index].Revoked, 1); views.RemoveAt(index); }
    private void RevokeAll() { for (var index = views.Count - 1; index >= 0; index--) RevokeAt(index); }
    private int FindAtOrBelow(ulong address)
    {
        var low = 0; var high = views.Count - 1; var found = -1;
        while (low <= high)
        { var middle = low + (high - low) / 2; if (views.Keys[middle] <= address) { found = middle; low = middle + 1; } else high = middle - 1; }
        return found;
    }
    private static GuestPageProtection AccessOf(HostPageProtection protection) => protection switch
    {
        HostPageProtection.ReadOnly => GuestPageProtection.Read,
        HostPageProtection.ReadWrite => GuestPageProtection.Read | GuestPageProtection.Write,
        HostPageProtection.Execute => GuestPageProtection.Execute,
        HostPageProtection.ReadExecute => GuestPageProtection.Read | GuestPageProtection.Execute,
        HostPageProtection.ReadWriteExecute or HostPageProtection.ExecuteWriteCopy => GuestPageProtection.Read | GuestPageProtection.Write | GuestPageProtection.Execute,
        _ => GuestPageProtection.None,
    };
}
