// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

public sealed unsafe partial class HostMappedCopyLedger
{
    private Validation? validations;
    private int validationCount;

    public CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(HostBackingObject backing,
        CpuMappedCopyObservation original, out CpuMappedPublicationValidation? validation)
        => TryPrepareMappedPublicationValidation(backing, original, out validation, out _);

    public CpuMappedPublicationPreparationResult TryPrepareMappedPublicationValidation(HostBackingObject backing,
        CpuMappedCopyObservation original, out CpuMappedPublicationValidation? validation,
        out CpuMappedPublicationValidationRefusal refusal)
    {
        validation = null; refusal = default;
        if (Monitor.IsEntered(gate)) throw new InvalidOperationException("Validation cannot prepare under the provider gate.");
        if (original is null || original.OwnerIdentity is not Capture capture || !ReferenceEquals(capture.Owner, this) ||
            !HostViewMemory.IsValidRange(original.SourceAddress, original.SourceByteCount) ||
            original.SourceByteCount > MaximumPublicationBytes || capture.States.Length is < 1 or > MaximumPublicationRuns)
            return CpuMappedPublicationPreparationResult.Rejected;
        var writers = backing.ManagedWrites;
        if (writers is null) return CpuMappedPublicationPreparationResult.Rejected;
        var physical = new HostManagedWriteIntervals.PhysicalRun[capture.States.Length];
        var transaction = new Validation(this, backing, original, writers, physical);
        var wrapper = new CpuMappedPublicationValidation(transaction);
        if (!backing.TryEnterAccess()) return CpuMappedPublicationPreparationResult.Rejected;
        var installed = false;
        var reserved = false;
        try
        {
            lock (gate)
            {
                if (!IsCurrent(backing, original) || publicationCount + validationCount + readValidationCount >= MaximumPublications ||
                    HasOverlappingPublicationLocked(original.FootprintAddress, original.FootprintByteCount))
                    return CpuMappedPublicationPreparationResult.Rejected;
                var cursor = original.SourceAddress;
                var end = cursor + original.SourceByteCount;
                var count = 0;
                foreach (var view in capture.States)
                {
                    var first = Math.Max(cursor, view.Address);
                    var last = Math.Min(end, view.Address + view.Size);
                    if (last <= first) continue;
                    if (first != cursor || !ReferenceEquals(view.Backing, backing) ||
                        (view.Access & (GuestPageProtection.Read | GuestPageProtection.Write)) !=
                            (GuestPageProtection.Read | GuestPageProtection.Write))
                        return CpuMappedPublicationPreparationResult.Rejected;
                    var offset = view.Offset + first - view.Address;
                    var length = last - first;
                    if (offset > backing.Size || length > backing.Size - offset)
                        return CpuMappedPublicationPreparationResult.Rejected;
                    for (var previous = 0; previous < count; previous++)
                        if (Overlaps(new(offset, length), new(physical[previous].Offset, physical[previous].Count)))
                            return CpuMappedPublicationPreparationResult.Rejected;
                    physical[count++] = new(offset, length);
                    cursor = last;
                    if (cursor == end) break;
                }
                if (cursor != end || count == 0) return CpuMappedPublicationPreparationResult.Rejected;
                var originalRevision = Volatile.Read(ref capture.WriterRevision);
                reserved = writers.TryReservePublication(originalRevision, physical.AsSpan(0, count), capture.ReadWatch, transaction.Admission,
                    out var rejectedAdmission);
                if (!reserved)
                {
                    if (rejectedAdmission > 0)
                        refusal = new(CpuMappedPublicationValidationRefusalKind.AdmissionBusy, originalRevision, rejectedAdmission);
                    // Busy precedes the history/watch exclusion check. No such
                    // completion/content authority is certified by this result.
                    return CpuMappedPublicationPreparationResult.Rejected;
                }
                if (!IsCurrent(backing, original)) return CpuMappedPublicationPreparationResult.Rejected;
                transaction.PhysicalCount = count; // exact resolved original prefix, never trailing entries
                transaction.Next = validations; validations = transaction; validationCount++;
                transaction.Admission?.SetPhase(CpuMappedAdmissionOwnerPhase.Prepared);
                installed = true; validation = wrapper;
                return CpuMappedPublicationPreparationResult.Prepared;
            }
        }
        finally
        {
            if (!installed)
            {
                try
                {
                    if (reserved) writers.ReleasePublication(transaction.Admission);
                    backing.ExitAccess();
                    HostManagedWriteIntervals.CompleteAdmissionRelease(transaction.Admission);
                }
                catch { transaction.Admission?.Unknown(); throw; }
            }
        }
    }

    private sealed class Validation(HostMappedCopyLedger owner, HostBackingObject backing,
        CpuMappedCopyObservation original, HostManagedWriteIntervals writers,
        HostManagedWriteIntervals.PhysicalRun[] physical) : CpuMappedPublicationValidationTransaction
    {
        internal readonly HostManagedWriteIntervals.AdmissionOwner? Admission = writers.CreateAdmissionOwner(CpuMappedAdmissionOwnerKind.PublicationValidation);
        internal readonly CpuMappedCopyObservation Original = original;
        internal Validation? Next;
        private int consumed;
        internal int PhysicalCount; // installed once before publication under provider gate
        internal override bool IsOriginalDisjointReadWatchCurrent(CpuMappedCopyObservation watchedOriginal) =>
            Volatile.Read(ref consumed) == 0 && PhysicalCount > 0 &&
            owner.IsOriginalDisjointReadWatchCurrent(backing, Original, writers,
                physical.AsSpan(0, PhysicalCount), watchedOriginal) && Volatile.Read(ref consumed) == 0;
        internal override bool IsCurrent
        {
            get
            {
                if (Monitor.IsEntered(owner.gate)) throw new InvalidOperationException("Validation must be outside provider gates.");
                lock (owner.gate) return Volatile.Read(ref consumed) == 0 && owner.IsCurrent(backing, Original);
            }
        }
        internal override void Cancel()
        {
            if (Monitor.IsEntered(owner.gate)) throw new InvalidOperationException("Validation release must be outside provider gates.");
            if (Interlocked.CompareExchange(ref consumed, 1, 0) != 0) return;
            Admission?.SetPhase(CpuMappedAdmissionOwnerPhase.Releasing);
            lock (owner.gate)
            {
                Validation? previous = null;
                var found = owner.validations;
                while (found is not null && !ReferenceEquals(found, this)) { previous = found; found = found.Next; }
                // Unknown bookkeeping retains the exact exclusion and pins.
                if (found is null) { Admission?.Unknown(); throw new InvalidOperationException("Exact validation reservation was lost."); }
                if (previous is null) owner.validations = found.Next; else previous.Next = found.Next;
                owner.validationCount--;
            }
            try
            {
                try { writers.ReleasePublication(Admission); }
                finally { backing.ExitAccess(); }
                HostManagedWriteIntervals.CompleteAdmissionRelease(Admission);
            }
            catch { Admission?.Unknown(); throw; }
        }
    }
}
