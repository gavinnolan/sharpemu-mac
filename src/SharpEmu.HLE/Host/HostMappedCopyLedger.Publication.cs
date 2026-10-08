// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.HLE.Host;

public sealed unsafe partial class HostMappedCopyLedger
{
    public const int MaximumPublicationBytes = 8;
    private const int MaximumPublications = 128;
    private Publication? publications;
    private int publicationCount;
    private readonly record struct DetachedRun(ulong Address, byte[] Bytes);
    private readonly record struct AliasRun(ulong Offset, byte[] Bytes);

    // Same provider gate as every participating pre-kernel mutation. Reservations
    // exclude the ORIGINAL full footprint, not just the few scattered bytes.
    public bool HasOverlappingPublicationLocked(ulong address, ulong size)
    {
        RequireGate();
        if (!HostViewMemory.IsValidRange(address, size)) return true;
        var effect = new HostAddressRange(address, size);
        for (var item = publications; item is not null; item = item.Next)
            if (Overlaps(effect, new(item.Original.FootprintAddress, item.Original.FootprintByteCount))) return true;
        return false;
    }

    public CpuMappedPublicationPreparationResult TryPrepareMappedPublication(HostBackingObject backing,
        CpuMappedCopyObservation original, IReadOnlyList<CpuMappedWriteRun> runs,
        out CpuMappedPublication? publication)
    {
        publication = null;
        if (Monitor.IsEntered(gate)) throw new InvalidOperationException("Publication cannot prepare under the provider gate.");
        if (original is null || runs is null || original.OwnerIdentity is not Capture capture ||
            !ReferenceEquals(capture.Owner, this) || runs.Count is < 1 or > MaximumPublicationBytes)
            return CpuMappedPublicationPreparationResult.Rejected;
        // Caller memory is detached before any admission. No retained caller
        // buffers, callbacks or caller-generated native outcome receipts.
        var detached = new DetachedRun[runs.Count];
        var aliasesToWrite = new AliasRun[runs.Count];
        var bytes = 0;
        for (var index = 0; index < detached.Length; index++)
        {
            var run = runs[index];
            if (run.Bytes.Length == 0 || run.Bytes.Length > MaximumPublicationBytes - bytes ||
                !HostViewMemory.IsValidRange(run.Address, (ulong)run.Bytes.Length) ||
                run.Address < original.SourceAddress || run.Address - original.SourceAddress > original.SourceByteCount ||
                (ulong)run.Bytes.Length > original.SourceByteCount - (run.Address - original.SourceAddress))
                return CpuMappedPublicationPreparationResult.Rejected;
            bytes += run.Bytes.Length;
            detached[index] = new(run.Address, run.Bytes.ToArray());
        }
        var writers = backing.ManagedWrites;
        if (writers is null) return CpuMappedPublicationPreparationResult.Rejected;
        var reserved = false;
        var installed = false;
        // Preallocate continuity from the SAME immutable original view identities,
        // before admission. Its revision is filled only from this exact commit.
        var successfulCapture = new Capture(this, capture.States);
        var successfulObservation = new CpuMappedCopyObservation(successfulCapture,
            original.FootprintAddress, original.FootprintByteCount, original.SourceAddress,
            original.SourceByteCount, original.Segments.ToArray());
        var transaction = new Publication(this, backing, original, writers, aliasesToWrite, successfulObservation);
        var wrapper = new CpuMappedPublication(transaction);
        if (!backing.TryEnterAccess()) return CpuMappedPublicationPreparationResult.Rejected;
        try
        {
            lock (gate)
            {
                if (!IsCurrent(backing, original) || publicationCount >= MaximumPublications ||
                    HasOverlappingPublicationLocked(original.FootprintAddress, original.FootprintByteCount))
                    return CpuMappedPublicationPreparationResult.Rejected;
                for (var index = 0; index < detached.Length; index++)
                {
                    var run = detached[index];
                    View? selected = null;
                    foreach (var view in capture.States)
                        if (run.Address >= view.Address && run.Address - view.Address < view.Size &&
                            (ulong)run.Bytes.Length <= view.Size - (run.Address - view.Address)) { selected = view; break; }
                    if (selected is null || (selected.Access & GuestPageProtection.Write) == 0 ||
                        !ReferenceEquals(selected.Backing, backing)) return CpuMappedPublicationPreparationResult.Rejected;
                    var offset = selected.Offset + run.Address - selected.Address;
                    if (offset > backing.Size || (ulong)run.Bytes.Length > backing.Size - offset)
                        return CpuMappedPublicationPreparationResult.Rejected;
                    for (var previous = 0; previous < index; previous++)
                        if (Overlaps(new(offset, (ulong)run.Bytes.Length),
                            new(aliasesToWrite[previous].Offset, (ulong)aliasesToWrite[previous].Bytes.Length)))
                            return CpuMappedPublicationPreparationResult.Rejected;
                    aliasesToWrite[index] = new(offset, run.Bytes);
                }
                reserved = writers.TryReservePublication(Volatile.Read(ref capture.WriterRevision));
                if (!reserved || !IsCurrent(backing, original)) return CpuMappedPublicationPreparationResult.Rejected;
                transaction.Next = publications; publications = transaction; publicationCount++;
                installed = true; publication = wrapper;
                return CpuMappedPublicationPreparationResult.Prepared;
            }
        }
        finally
        {
            if (!installed)
            {
                if (reserved) writers.ReleasePublication();
                backing.ExitAccess();
            }
        }
    }

    private void RemovePublicationLocked(Publication exact)
    {
        RequireGate();
        Publication? previous = null;
        for (var found = publications; found is not null; found = found.Next)
        {
            if (!ReferenceEquals(found, exact)) { previous = found; continue; }
            if (previous is null) publications = found.Next; else previous.Next = found.Next;
            publicationCount--; return;
        }
        throw new InvalidOperationException("No exact publication reservation remains.");
    }

    private sealed class Publication(HostMappedCopyLedger owner, HostBackingObject backing,
        CpuMappedCopyObservation original, HostManagedWriteIntervals writers, AliasRun[] runs,
        CpuMappedCopyObservation successfulObservation)
        : CpuMappedPublicationTransaction
    {
        internal readonly CpuMappedCopyObservation Original = original;
        internal Publication? Next;
        private int state; // prepared, committing, completed, unknown, cancelled
        private int released, applied;
        internal override CpuMappedCopyObservation? SuccessfulObservation =>
            Volatile.Read(ref applied) == 1 ? successfulObservation : null;

        internal override CpuMappedPublicationResult TryCommit()
        {
            if (Monitor.IsEntered(owner.gate)) throw new InvalidOperationException("Publication bytes cannot run under the provider gate.");
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return CpuMappedPublicationResult.Rejected;
            var touched = false;
            try
            {
                lock (owner.gate)
                    if (!owner.IsCurrent(backing, Original))
                    { owner.RemovePublicationLocked(this); state = 2; }
                if (state == 2)
                { Release(); return CpuMappedPublicationResult.Rejected; }
                foreach (var run in runs)
                {
                    touched = true; // even a throwing copy cannot authorize retry
                    run.Bytes.AsSpan().CopyTo(new Span<byte>((void*)(backing.AliasBase + run.Offset), run.Bytes.Length));
                }
                lock (owner.gate)
                {
                    if (!owner.IsCurrent(backing, Original)) return QuarantineLocked();
                    var revision = writers.RecordPublicationWrite();
                    Volatile.Write(ref ((Capture)successfulObservation.OwnerIdentity).WriterRevision, revision);
                    owner.RemovePublicationLocked(this); state = 2;
                }
                Release(); Volatile.Write(ref applied, 1); return CpuMappedPublicationResult.Applied;
            }
            catch
            {
                // Unknown effects retain strong ledger ownership, mapping
                // exclusion, writer exclusion AND alias pin until process exit.
                lock (owner.gate)
                {
                    if (state == 1 && touched) _ = QuarantineLocked();
                    else if (state == 1) { owner.RemovePublicationLocked(this); state = 2; }
                }
                if (state == 2) Release();
                throw;
            }
        }
        private CpuMappedPublicationResult QuarantineLocked()
        {
            state = 3; writers.QuarantinePublication();
            if (Original.OwnerIdentity is Capture capture)
                foreach (var view in capture.States) Volatile.Write(ref view.Revoked, 1);
            return CpuMappedPublicationResult.PartialUnknown;
        }
        internal override void Cancel()
        {
            if (Monitor.IsEntered(owner.gate)) throw new InvalidOperationException("Publication cancellation cannot run under the provider gate.");
            if (Interlocked.CompareExchange(ref state, 4, 0) != 0) return;
            lock (owner.gate) owner.RemovePublicationLocked(this);
            Release();
        }
        private void Release()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            try { writers.ReleasePublication(); }
            finally { backing.ExitAccess(); }
        }
    }
}
