// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private static readonly bool _selectedNativeOriginalReuseEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_SELECTED_NATIVE_REUSE") == "1";
    private sealed class SelectedNativeOriginalProof(SelectedStorageMipAssociation owner,
        SelectedStorageMipSeed seed, GuestImage image)
    {
        internal readonly SelectedStorageMipAssociation Owner = owner;
        internal readonly SelectedStorageMipSeed Seed = seed;
        internal readonly GuestImage Image = image;
        internal readonly nint Texture = image.Texture;
        internal int Version = image.ContentVersion;
        internal long ProducerSequence;
    }
    private static readonly Dictionary<SelectedStorageMipKey, SelectedNativeOriginalProof> _selectedNativeOriginalProofs = new();
    private static Exception? _selectedNativeOriginalCleanupFailure;

    private static bool OriginalSelectedNativeContentCurrent(SelectedStorageMipAssociation owner, SelectedStorageMipSeed seed) =>
        _selectedNativeOriginalReuseEnabled && !TextureSourceGraphEnabled && !_closed && _guestWorkFailure is null &&
        ReferenceEquals(_guestMemory, seed.Source) && owner.HasToken && owner.Observation.Observed &&
        seed.Source is ICpuMappedReadWatchSource watched && watched.IsMappedCopyWriteWatchCurrent(seed.Mapping) &&
        seed.Source.IsMappedCopyObservationCurrent(seed.Mapping) && GuestImageWriteTracker.IsReadSnapshotStable(seed.Read) &&
        GuestImageWriteTracker.TryGetProtectedObservedContentToken(owner.Key.Resource.Address,
            owner.Observation.ByteCount, out var token) && token == owner.Observation.Token && token == owner.Token;

    // All objects/capacity are prepared before transferring this construction's
    // retain. No fresh tracker/mapping observation or owner epoch is installed.
    private static SelectedNativeOriginalProof? PrepareSelectedNativeOriginalProofLocked(SelectedStorageMipAssociation owner, GuestImage image)
    {
        if (!_selectedStorageMipSeeds.TryGetValue(owner, out var seed) || seed.Phase != 2 ||
            !ReferenceEquals(seed.Thread, Thread.CurrentThread) || !ReferenceEquals(seed.Work, _selectedStorageMipSeedExecutingWork) ||
            seed.Sequence != _executingGuestWorkSequence || owner.Session != _selectedStorageMipSession ||
            !OriginalSelectedNativeContentCurrent(owner, seed)) return null; // Legacy owners keep strict policy.
        if (_selectedNativeOriginalProofs.Count >= 128) throw new NotSupportedException("Metal selected original native-owner limit exceeded.");
        _selectedNativeOriginalProofs.EnsureCapacity(_selectedNativeOriginalProofs.Count + 1);
        return new(owner, seed, image);
    }

    private static void PublishSelectedNativeOriginalProofLocked(SelectedStorageMipAssociation owner, GuestImage image,
        SelectedNativeOriginalProof? proof)
    {
        if (proof is null) return;
        if (!ReferenceEquals(proof.Owner, owner) || !ReferenceEquals(proof.Image, image) ||
            !_selectedStorageMipImages.TryGetValue(owner.Key, out var current) || !ReferenceEquals(current, image) ||
            !_selectedStorageMipOwners.TryGetValue(owner.Key, out var accepted) || !ReferenceEquals(accepted, owner) ||
            owner.Session != _selectedStorageMipSession || !OriginalSelectedNativeContentCurrent(owner, proof.Seed))
            throw new InvalidOperationException("Metal selected native publication lost its original mapping/content proof.");
        _selectedNativeOriginalProofs.Add(owner.Key, proof);
        proof.Seed.NativeOwnerTransferred = true;
    }

    private static bool HasCurrentSelectedNativeOriginalProofLocked(SelectedStorageMipAssociation owner)
    {
        if (!_selectedNativeOriginalProofs.TryGetValue(owner.Key, out var proof))
        {
            if (_selectedStorageMipSeeds.TryGetValue(owner, out var consumed) && consumed.NativeOwnerTransferred)
                throw new InvalidOperationException("Metal selected original native watch ownership has already been consumed.");
            return false;
        }
        if (!ReferenceEquals(proof.Owner, owner) || owner.Session != _selectedStorageMipSession ||
            !_selectedStorageMipImages.TryGetValue(owner.Key, out var current) || !ReferenceEquals(current, proof.Image) ||
            !_selectedStorageMipOwners.TryGetValue(owner.Key, out var accepted) || !ReferenceEquals(accepted, owner) ||
            current.Texture != proof.Texture || !current.Initialized || current.ContentVersion != proof.Version ||
            !OriginalSelectedNativeContentCurrent(owner, proof.Seed))
            throw new InvalidOperationException("Metal selected native owner lost its ORIGINAL mapping/content/version observation.");
        return true;
    }

    private static void AdvanceSelectedNativeOriginalVersionLocked(SelectedStorageMipAssociation owner, GuestImage image)
    {
        if (!_selectedNativeOriginalProofs.TryGetValue(owner.Key, out var proof)) return;
        if (!ReferenceEquals(proof.Image, image) || image.Texture != proof.Texture ||
            proof.Version == int.MaxValue || image.ContentVersion != proof.Version + 1 ||
            _executingGuestWorkSequence <= 0 || owner.Session != _selectedStorageMipSession ||
            !OriginalSelectedNativeContentCurrent(owner, proof.Seed))
            throw new InvalidOperationException("Metal selected original native-version transition has no exact known producer.");
        proof.Version = image.ContentVersion;
        proof.ProducerSequence = _executingGuestWorkSequence;
    }

    private static Exception? ReleaseSelectedNativeOriginalProofsLocked()
    {
        if (_selectedNativeOriginalProofs.Count == 0) return null;
        var proofs = _selectedNativeOriginalProofs.Values.ToArray();
        _selectedNativeOriginalProofs.Clear();
        Exception? cleanup = null;
        foreach (var proof in proofs) TryReleaseSelectedSeedWatch(proof.Seed, ref cleanup);
        _selectedNativeOriginalCleanupFailure ??= cleanup;
        return cleanup;
    }
}
