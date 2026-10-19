// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Allocated with the exact pre-encoder inventory. This stores an OWN buffer
    // effect, not a new input copy or post-effect content/permission authority.
    private sealed class FiniteStorageBufferPublication(GuestMemoryBuffer buffer, FiniteStorageBufferOrigin origin)
    {
        internal readonly GuestMemoryBuffer Buffer = buffer;
        internal readonly FiniteStorageBufferOrigin Origin = origin;
        internal readonly byte[] Data = buffer.Data;
        internal readonly ulong Address = buffer.BaseAddress, Size = buffer.Size;
        internal readonly int Length = buffer.Length;
        internal CpuMappedPublication? Transaction;
        internal CpuMappedCopyObservation? Applied;
        internal bool Started, Complete, Unknown, DisposalAttempted, AppliedRecorded;
    }

    private static FiniteStorageBufferPublication[] CaptureFiniteStorageBufferPublications(
        GuestMemoryBuffer[] buffers, FiniteStorageBufferOrigin[] originals)
    {
        var slots = new FiniteStorageBufferPublication[originals.Length];
        var index = 0;
        foreach (var buffer in buffers)
        {
            if (!buffer.Writable || !buffer.WriteBackToGuest || buffer.Length == 0) continue;
            if (index >= slots.Length || !ReferenceEquals(RequireFiniteStorageBufferOrigin(buffer), originals[index]))
                throw new InvalidOperationException("Finite buffer publication inventory changed before encoding.");
            slots[index] = new(buffer, originals[index]); index++;
        }
        if (index != slots.Length) throw new InvalidOperationException("Finite buffer publication inventory is incomplete.");
        return slots;
    }

    private static void ValidateFiniteStorageBufferPublicationInput(FiniteStorageBufferPublication slot)
    {
        var buffer = slot.Buffer; var origin = slot.Origin;
        if (!ReferenceEquals(_guestMemory, origin.Memory) || origin.Session != CurrentStorageCpuSeedSession ||
            !ReferenceEquals(buffer.Data, slot.Data) || buffer.BaseAddress != slot.Address || buffer.Size != slot.Size ||
            buffer.Length != slot.Length || slot.Length <= 0 || buffer.Length != buffer.Data.Length ||
            buffer.Pooled || !buffer.Writable || !buffer.WriteBackToGuest || buffer.Size != (ulong)slot.Length ||
            buffer.BaseAddress != origin.Mapping.SourceAddress || origin.Mapping.SourceByteCount != (ulong)slot.Length ||
            !buffer.Data.AsSpan().SequenceEqual(origin.OriginalBytes) || !origin.Source.IsMappedCopyObservationCurrent(origin.Mapping))
            throw new InvalidOperationException("Finite buffer publication lost its exact original DTO/input/session/mapping.");
    }

    private static void BeginFiniteStorageBufferPublication(FiniteStorageBufferPublication slot, CpuMappedPublication transaction)
    {
        ValidateFiniteStorageBufferPublicationInput(slot);
        if (slot.Started || slot.Complete || slot.Unknown || slot.DisposalAttempted || slot.AppliedRecorded ||
            !ReferenceEquals(slot.Transaction, transaction) || slot.Applied is not null ||
            !ReferenceEquals(RequireFiniteStorageBufferOrigin(slot.Buffer), slot.Origin))
            throw new InvalidOperationException("Finite buffer publication can begin only once from its original input.");
        slot.Started = true;
    }

    private static void RecordFiniteStorageBufferApplied(FiniteStorageBufferPublication slot,
        CpuMappedPublication transaction, CpuMappedCopyObservation? observation)
    {
        // The trusted single commit call saved this scalar immediately on its
        // Applied return, before entering any throwing continuation checks.
        if (!slot.Started || slot.Complete || slot.Unknown || slot.AppliedRecorded ||
            !ReferenceEquals(slot.Applied, observation) || !ReferenceEquals(slot.Transaction, transaction))
            throw new InvalidOperationException("Finite buffer Applied continuation is not its exact first transaction.");
        slot.AppliedRecorded = true;
        if (!_finiteStorageBufferOrigins.Remove(slot.Buffer)) // consumed; never re-enroll old payload
            throw new InvalidOperationException("Finite buffer Applied input was already consumed.");
        if (observation is null) throw new InvalidOperationException("Finite buffer publication has no exact Applied receipt.");
        ValidateFiniteStorageMappingContinuation(slot.Origin.Mapping, observation);
    }

    private static void CompleteFiniteStorageBufferPublication(FiniteStorageBufferPublication slot)
    {
        if (!slot.Started || slot.Complete || slot.Unknown || slot.DisposalAttempted ||
            !slot.AppliedRecorded || slot.Transaction is null || slot.Applied is null)
            throw new InvalidOperationException("Finite buffer publication has no known once-only completion.");
        slot.DisposalAttempted = true;
        slot.Transaction.Dispose(); // failure retains exact transaction and rooted original
        slot.Transaction = null;
        slot.Complete = true;
    }

    private static void ValidateFiniteStorageCompletedBuffers(FiniteStoragePreencode phase)
    {
        var index = 0;
        foreach (var buffer in phase.Dispatch.GlobalMemoryBuffers)
        {
            if (!buffer.Writable || !buffer.WriteBackToGuest || buffer.Length == 0) continue;
            if (index >= phase.BufferPublications.Length) throw new InvalidOperationException("Finite completed buffer inventory grew.");
            var slot = phase.BufferPublications[index];
            if (!ReferenceEquals(slot.Buffer, buffer) || !ReferenceEquals(slot.Origin, phase.Buffers[index]))
                throw new InvalidOperationException("Finite completed buffer lost its consumed exact Applied continuation.");
            var successor = ValidateFiniteStorageCompletedBuffer(slot);
            if (PhysicalMappingOverlaps(successor, phase.Receipt.Origin.Mapping))
                throw new NotSupportedException("Finite completed image/buffer physical footprints overlap.");
            for (var prior = 0; prior < index; prior++)
                if (PhysicalMappingOverlaps(successor, phase.BufferPublications[prior].Origin.Mapping))
                    throw new NotSupportedException("Finite completed buffer physical footprints overlap.");
            index++;
        }
        if (index != phase.BufferPublications.Length || index != phase.Buffers.Length)
            throw new InvalidOperationException("Finite completed buffer inventory shrank.");
    }

    private static CpuMappedCopyObservation ValidateFiniteStorageCompletedBuffer(FiniteStorageBufferPublication slot)
    {
        if (!slot.Started || !slot.Complete || slot.Unknown || !slot.AppliedRecorded || !slot.DisposalAttempted || slot.Transaction is not null ||
            slot.Applied is not { } successor || _finiteStorageBufferOrigins.TryGetValue(slot.Buffer, out _))
            throw new InvalidOperationException("Finite completed buffer lost its consumed exact Applied continuation.");
        ValidateFiniteStorageBufferPublicationInput(slot);
        ValidateFiniteStorageMappingContinuation(slot.Origin.Mapping, successor);
        if (!slot.Origin.Source.IsMappedCopyObservationCurrent(successor))
            throw new InvalidOperationException("Finite completed buffer original Applied mapping is no longer current.");
        return successor;
    }
}
