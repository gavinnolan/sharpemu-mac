// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

public sealed unsafe partial class HostMappedCopyLedger
{
    private OriginalReadValidation? readValidations;
    private int readValidationCount;

    public CpuMappedPublicationPreparationResult TryPrepareMappedReadValidation(HostBackingObject backing,
        CpuMappedCopyObservation original, out CpuMappedReadValidation? validation)
    {
        validation = null;
        if (Monitor.IsEntered(gate)) throw new InvalidOperationException("Original read validation cannot prepare under the provider gate.");
        if (original is null || original.OwnerIdentity is not Capture capture || !ReferenceEquals(capture.Owner, this) ||
            capture.ReadWatch is not { } watch || capture.States.Length is < 1 or > MaximumPublicationRuns ||
            !HostViewMemory.IsValidRange(original.SourceAddress, original.SourceByteCount) ||
            original.SourceByteCount > MaximumPublicationBytes || watch.Runs.Length is < 1 or > MaximumPublicationRuns)
            return CpuMappedPublicationPreparationResult.Rejected;
        var writers = backing.ManagedWrites;
        if (writers is null) return CpuMappedPublicationPreparationResult.Rejected;
        var physical = new HostManagedWriteIntervals.PhysicalRun[capture.States.Length];
        var transaction = new OriginalReadValidation(this, backing, original, writers, watch, physical);
        var wrapper = new CpuMappedReadValidation(transaction);
        if (!backing.TryEnterAccess()) return CpuMappedPublicationPreparationResult.Rejected;
        var installed = false; var reserved = false;
        try
        {
            lock (gate)
            {
                if (!IsCurrent(backing, original) || publicationCount + validationCount + readValidationCount >= MaximumPublications ||
                    HasOverlappingPublicationLocked(original.FootprintAddress, original.FootprintByteCount))
                    return CpuMappedPublicationPreparationResult.Rejected;
                var cursor = original.SourceAddress; var end = cursor + original.SourceByteCount; var count = 0;
                foreach (var view in capture.States)
                {
                    var first = Math.Max(cursor, view.Address); var last = Math.Min(end, view.Address + view.Size);
                    if (last <= first) continue;
                    if (first != cursor || !ReferenceEquals(view.Backing, backing) || (view.Access & GuestPageProtection.Read) == 0)
                        return CpuMappedPublicationPreparationResult.Rejected;
                    var offset = view.Offset + first - view.Address; var length = last - first;
                    if (offset > backing.Size || length > backing.Size - offset)
                        return CpuMappedPublicationPreparationResult.Rejected;
                    for (var previous = 0; previous < count; previous++)
                        if (Overlaps(new(offset, length), new(physical[previous].Offset, physical[previous].Count)))
                            return CpuMappedPublicationPreparationResult.Rejected;
                    physical[count++] = new(offset, length); cursor = last;
                    if (cursor == end) break;
                }
                if (cursor != end || count == 0 || Volatile.Read(ref capture.WriterRevision) != watch.EnrollmentRevision)
                    return CpuMappedPublicationPreparationResult.Rejected;
                // Acquire the existing real writer exclusion; this ticket has
                // no write method and never advances the original revision.
                reserved = writers.TryReservePublication(watch.EnrollmentRevision, physical.AsSpan(0, count), watch);
                if (!reserved || !IsCurrent(backing, original) || !ReferenceEquals(capture.ReadWatch, watch))
                    return CpuMappedPublicationPreparationResult.Rejected;
                transaction.PhysicalCount = count;
                transaction.Next = readValidations; readValidations = transaction; readValidationCount++;
                installed = true; validation = wrapper;
                return CpuMappedPublicationPreparationResult.Prepared;
            }
        }
        finally
        {
            if (!installed)
            {
                try { if (reserved) writers.ReleasePublication(); }
                finally { backing.ExitAccess(); }
            }
        }
    }

    private sealed class OriginalReadValidation(HostMappedCopyLedger owner, HostBackingObject backing,
        CpuMappedCopyObservation original, HostManagedWriteIntervals writers, HostManagedWriteIntervals.ReadWatch watch,
        HostManagedWriteIntervals.PhysicalRun[] physical) : CpuMappedReadValidationTransaction
    {
        internal readonly CpuMappedCopyObservation Original = original;
        internal OriginalReadValidation? Next;
        internal int PhysicalCount;
        private int consumed;
        internal override bool IsOriginalCurrent(CpuMappedCopyObservation candidate) =>
            ReferenceEquals(candidate, Original) && Volatile.Read(ref consumed) == 0 && PhysicalCount > 0 &&
            Original.OwnerIdentity is Capture capture && ReferenceEquals(capture.Owner, owner) &&
            ReferenceEquals(capture.ReadWatch, watch) && Volatile.Read(ref capture.WriterRevision) == watch.EnrollmentRevision &&
            owner.IsCurrent(backing, Original) && watch.CoversDuringPublication(writers, physical.AsSpan(0, PhysicalCount)) &&
            owner.IsCurrent(backing, Original) && Volatile.Read(ref consumed) == 0;

        internal override void Cancel()
        {
            if (Monitor.IsEntered(owner.gate)) throw new InvalidOperationException("Original read validation release requires a released provider gate.");
            if (Interlocked.CompareExchange(ref consumed, 1, 0) != 0) return;
            lock (owner.gate)
            {
                OriginalReadValidation? previous = null; var found = owner.readValidations;
                while (found is not null && !ReferenceEquals(found, this)) { previous = found; found = found.Next; }
                // Lost bookkeeping keeps the actual exclusion and pin alive.
                if (found is null) throw new InvalidOperationException("Exact original read validation reservation was lost.");
                if (previous is null) owner.readValidations = found.Next; else previous.Next = found.Next;
                owner.readValidationCount--;
            }
            try { writers.ReleasePublication(); }
            finally { backing.ExitAccess(); }
        }
    }
}
