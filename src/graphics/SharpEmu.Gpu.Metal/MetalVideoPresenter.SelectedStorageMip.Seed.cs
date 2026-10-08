// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Initial CPU bytes have a one-use execution receipt. Only successful native
    // publication may separately transfer ORIGINAL mapping/content/write-watch
    // provenance into an exact native owner; no epoch/token is refreshed.
    private sealed class SelectedStorageMipSeed(ICpuMappedCopySource source,
        CpuMappedCopyObservation mapping, GuestImageWriteTracker.ReadSnapshot read)
    {
        internal readonly ICpuMappedCopySource Source = source;
        internal readonly CpuMappedCopyObservation Mapping = mapping;
        internal readonly GuestImageWriteTracker.ReadSnapshot Read = read;
        internal bool NativeOwnerTransferred;
        internal int Phase; // 0 original, 1 admitted, 2 exact execution, 3 consumed
        internal object? Work;
        internal long Sequence;
        internal Thread? Thread;
    }
    private static readonly ConditionalWeakTable<SelectedStorageMipAssociation, SelectedStorageMipSeed> _selectedStorageMipSeeds = new();
    [ThreadStatic] private static object? _selectedStorageMipSeedExecutingWork;
    private sealed record SelectedStorageMipSeedAdmission(SelectedStorageMipSeed Seed, object Work, long Sequence);
    internal static long CurrentSelectedStorageMipSeedSession => Volatile.Read(ref _selectedStorageMipSession);

    internal static bool TryReadInitialSelectedStorageMipSource(ICpuMemory memory, ulong address,
        ulong footprintBytes, ulong sourceAddress, byte[] destination,
        out ICpuMappedCopySource? mappedSource, out CpuMappedCopyObservation? mapping)
    {
        mappedSource = null;
        mapping = null;
        if (memory is ICpuMappedCopySource optional)
        {
            CpuMappedCopyObservation? original;
            var result = _selectedNativeOriginalReuseEnabled && optional is ICpuMappedReadWatchSource watched
                ? watched.TryReadMappedCopyWithWriteWatch(address, footprintBytes, sourceAddress, destination, out original)
                : optional.TryReadMappedCopy(address, footprintBytes, sourceAddress, destination, out original);
            if (result == CpuMappedCopyResult.Rejected) return false; // discard; never ordinary-read fallback
            if (result == CpuMappedCopyResult.Copied)
            {
                if (original is null || original.FootprintAddress != address || original.FootprintByteCount != footprintBytes ||
                    original.SourceAddress != sourceAddress || original.SourceByteCount != (ulong)destination.Length)
                {
                    ReleaseOriginalSelectedWriteWatch(optional, original);
                    throw new InvalidOperationException("Metal initial selected seed copy returned inconsistent original mapping metadata.");
                }
                if (!optional.IsMappedCopyObservationCurrent(original))
                { ReleaseOriginalSelectedWriteWatch(optional, original); return false; }
                mappedSource = optional;
                mapping = original;
                return true;
            }
            if (result != CpuMappedCopyResult.Unavailable)
                throw new InvalidOperationException("Metal initial selected seed copy returned an undefined result.");
        }
        // Unavailable preserves the complete legacy read and its strict native
        // owner policy. Any partly changed optional destination is overwritten.
        if (!memory.TryRead(sourceAddress, destination))
            throw new InvalidOperationException($"Metal initial selected storage seed is unreadable: addr=0x{address:X} source=0x{sourceAddress:X} bytes={destination.Length}.");
        return true;
    }

    internal static void ReleaseOriginalSelectedWriteWatch(ICpuMappedCopySource? source, CpuMappedCopyObservation? mapping)
    { if (source is ICpuMappedReadWatchSource watched && mapping is not null) watched.ReleaseMappedCopyWriteWatch(mapping); }

    internal static bool IsInitialSelectedStorageMipSeedStable(ulong address,
        in CpuTextureSnapshotObservation observation, ICpuMappedCopySource? source, CpuMappedCopyObservation? mapping)
    {
        if (source is null || mapping is null) return IsCpuTextureSnapshotAdmissionStable(address, observation);
        if (!source.IsMappedCopyObservationCurrent(mapping) ||
            _selectedNativeOriginalReuseEnabled && source is ICpuMappedReadWatchSource watched && !watched.IsMappedCopyWriteWatchCurrent(mapping)) return false;
        if (observation.MappingEpoch != HostMemory.MappingGeneration)
            return !TextureSourceGraphEnabled && observation.Observed &&
                IsCpuTextureSnapshotContentObservationStable(address, observation);
        return IsCpuTextureSnapshotAdmissionStable(address, observation);
    }

    internal static void RegisterInitialSelectedStorageMipSeed(GuestDrawTexture texture, ICpuMemory memory,
        ICpuMappedCopySource? source, CpuMappedCopyObservation? mapping, GuestImageWriteTracker.ReadSnapshot read, long originalSession)
    {
        if (source is null && mapping is null) return;
        if (source is null || mapping is null || !ReferenceEquals(memory, source) || texture.RgbaPixels.Length == 0 ||
            !_selectedStorageMipAssociations.TryGetValue(texture, out var association) ||
            association.Session != originalSession ||
            !IsInitialSelectedStorageMipSeedStable(texture.Address, association.Observation, source, mapping))
            throw new InvalidOperationException("Metal original selected seed lost its exact source/mapping association.");
        if (TextureSourceGraphEnabled) return; // dormant graph retains its original strict owner lifecycle
        _selectedStorageMipSeeds.Add(association, new(source, mapping, read));
    }

    private static bool IsSelectedStorageMipSeedCurrent(SelectedStorageMipAssociation owner, SelectedStorageMipSeed seed)
    {
        if (!GuestImageWriteTracker.IsReadSnapshotStable(seed.Read) ||
            !IsInitialSelectedStorageMipSeedStable(owner.Key.Resource.Address, owner.Observation, seed.Source, seed.Mapping)) return false;
        // The full original observed token is mandatory for any epoch override;
        // the weaker raw identity token is an additional veto, never authority.
        if (owner.MappingEpoch != HostMemory.MappingGeneration && (!owner.HasToken || !owner.Observation.Observed)) return false;
        return !owner.HasToken || GuestImageWriteTracker.TryGetContentToken(owner.Key.Resource.Address, out var token) && token == owner.Token;
    }

    private static bool CanUseOriginalSelectedStorageMipSeed(SelectedStorageMipAssociation owner, string stage)
    {
        if (!_selectedStorageMipSeeds.TryGetValue(owner, out var seed)) return false;
        var phaseAllowed = stage == "admission" ? seed.Phase == 0 :
            seed.Phase == 2 && ReferenceEquals(seed.Thread, System.Threading.Thread.CurrentThread) &&
            ReferenceEquals(seed.Work, _selectedStorageMipSeedExecutingWork) &&
            seed.Sequence > 0 && seed.Sequence == Volatile.Read(ref _executingGuestWorkSequence);
        return phaseAllowed && IsSelectedStorageMipSeedCurrent(owner, seed);
    }

    private static void ValidateOriginalSelectedStorageMipSeedStage(SelectedStorageMipAssociation owner,
        bool requireObserved, string stage)
    {
        if (requireObserved || !_selectedStorageMipSeeds.TryGetValue(owner, out var seed) || seed.Phase == 3) return;
        // Revocation/unknown outcome can occur even without an epoch increment.
        // Every initial admission/materialization check therefore validates the
        // original mapping, not only the branch overriding an epoch mismatch.
        if (stage == "admission" && seed.Phase != 0) return; // strict legacy policy for a second admission
        if (!CanUseOriginalSelectedStorageMipSeed(owner, stage))
            throw new InvalidOperationException("Metal original selected seed requires its exact current first-use receipt.");
    }

    private static SelectedStorageMipSeedAdmission? PrepareInitialSelectedStorageMipSeedAdmission(
        SelectedStorageMipAssociation owner, object work, long sequence)
    {
        if (!_selectedStorageMipSeeds.TryGetValue(owner, out var seed) || seed.Phase != 0 ||
            _selectedStorageMipOwners.ContainsKey(owner.Key)) return null;
        if (!IsSelectedStorageMipSeedCurrent(owner, seed))
            throw new InvalidOperationException("Metal original selected seed changed before its exact first admission.");
        return new(seed, work, sequence);
    }

    private static void PublishInitialSelectedStorageMipSeedAdmission(SelectedStorageMipSeedAdmission? admission)
    {
        if (admission is null) return;
        admission.Seed.Work = admission.Work;
        admission.Seed.Sequence = admission.Sequence;
        admission.Seed.Phase = 1;
    }

    private readonly struct SelectedStorageMipSeedExecutionScope(object work, SelectedStorageMipSeed[] seeds) : IDisposable
    {
        public void Dispose()
        {
            if (seeds is null) return;
            lock (_gate)
                foreach (var seed in seeds)
                    if (seed.Phase == 2 && ReferenceEquals(seed.Work, work) && ReferenceEquals(seed.Thread, System.Threading.Thread.CurrentThread))
                    {
                        seed.Phase = 3;
                        if (!seed.NativeOwnerTransferred) ReleaseOriginalSelectedWriteWatch(seed.Source, seed.Mapping);
                        seed.Thread = null;
                        seed.Work = null;
                    }
            if (ReferenceEquals(_selectedStorageMipSeedExecutingWork, work)) _selectedStorageMipSeedExecutingWork = null;
            // No callback/revalidation/throw during unwinding; preserve the
            // first dispatch failure. Consumed proofs can never be rearmed.
        }
    }

    private static SelectedStorageMipSeedExecutionScope BeginSelectedStorageMipSeedExecution(ComputeGuestDispatch dispatch)
    {
        var possible = false;
        foreach (var texture in dispatch.Textures)
            if (_selectedStorageMipAssociations.TryGetValue(texture, out var candidate) &&
                _selectedStorageMipSeeds.TryGetValue(candidate, out var original) && Volatile.Read(ref original.Phase) is 1 or 2)
            { possible = true; break; }
        if (!possible) return default;
        lock (_gate)
        {
            // No arrays/scopes allocated for legacy or native-only owners.
            var found = false;
            foreach (var texture in dispatch.Textures)
                if (_selectedStorageMipAssociations.TryGetValue(texture, out var owner) &&
                    _selectedStorageMipSeeds.TryGetValue(owner, out var seed))
                {
                    if (_selectedStorageMipSeedExecutingWork is not null)
                        throw new InvalidOperationException("Metal original selected seed cannot enter a nested dispatch scope.");
                    if (seed.Phase == 1 && ReferenceEquals(seed.Work, dispatch)) { found = true; break; }
                }
            if (!found) return default;
            try
            {
                var selected = new List<SelectedStorageMipSeed>();
                foreach (var texture in dispatch.Textures)
                    if (_selectedStorageMipAssociations.TryGetValue(texture, out var owner) &&
                        _selectedStorageMipSeeds.TryGetValue(owner, out var seed) && seed.Phase == 1 && ReferenceEquals(seed.Work, dispatch) &&
                        !selected.Contains(seed))
                    {
                        selected.Add(seed);
                        if (seed.Sequence <= 0 || seed.Sequence != Volatile.Read(ref _executingGuestWorkSequence) ||
                            owner.Session != Volatile.Read(ref _selectedStorageMipSession) || !IsSelectedStorageMipSeedCurrent(owner, seed))
                            throw new InvalidOperationException("Metal original selected seed lost its exact first execution receipt.");
                    }
                var seeds = selected.ToArray();
                foreach (var seed in seeds) { seed.Thread = System.Threading.Thread.CurrentThread; seed.Phase = 2; }
                _selectedStorageMipSeedExecutingWork = dispatch;
                return new(dispatch, seeds);
            }
            catch
            {
                foreach (var texture in dispatch.Textures)
                    if (_selectedStorageMipAssociations.TryGetValue(texture, out var owner) &&
                        _selectedStorageMipSeeds.TryGetValue(owner, out var seed) && ReferenceEquals(seed.Work, dispatch))
                    { seed.Phase = 3; if (!seed.NativeOwnerTransferred) ReleaseOriginalSelectedWriteWatch(seed.Source, seed.Mapping);
                      seed.Thread = null; seed.Work = null; }
                throw;
            }
        }
    }
}
