// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private const int MaximumFiniteWritableBuffers = 64;
    private const int MaximumFiniteBufferBytes = 4 * 1024 * 1024;
    private sealed record FiniteStorageBufferOrigin(ICpuMemory Memory, ICpuMappedCopySource Source,
        CpuMappedCopyObservation Mapping, long Session, byte[] OriginalBytes);
    private static readonly ConditionalWeakTable<GuestMemoryBuffer, FiniteStorageBufferOrigin> _finiteStorageBufferOrigins = new();

    internal static bool WantsFiniteStorageBufferCapture(GuestDrawTexture[] textures) =>
        RequiresFiniteStoragePublication(textures);

    // Called ONLY at the actual host snapshot producer, before its bytes are
    // exposed to the submitted work. Never constructs provenance from a reread.
    internal static CpuMappedCopyResult TryReadFiniteStorageBuffer(ICpuMemory memory, ulong address,
        Span<byte> destination, out CpuMappedCopyObservation? mapping, out long session)
    {
        mapping = null;
        session = CurrentStorageCpuSeedSession;
        if (destination.Length is <= 0 or > MaximumFiniteBufferBytes || memory is not ICpuMappedCopySource source)
            return CpuMappedCopyResult.Unavailable;
        var result = source.TryReadMappedCopy(address, (ulong)destination.Length, address, destination, out mapping);
        if (result != CpuMappedCopyResult.Copied) return result;
        if (mapping is null || session != CurrentStorageCpuSeedSession ||
            mapping.SourceAddress != address || mapping.SourceByteCount != (ulong)destination.Length ||
            mapping.FootprintAddress != address || mapping.FootprintByteCount != (ulong)destination.Length ||
            !source.IsMappedCopyObservationCurrent(mapping))
        { mapping = null; return CpuMappedCopyResult.Rejected; }
        return CpuMappedCopyResult.Copied;
    }

    internal static void RegisterFiniteStorageBufferOrigin(GuestMemoryBuffer buffer, ICpuMemory memory,
        CpuMappedCopyObservation mapping, long session)
    {
        if (memory is not ICpuMappedCopySource source || !buffer.Writable || !buffer.WriteBackToGuest ||
            buffer.Pooled || buffer.Length <= 0 || buffer.Length != buffer.Data.Length ||
            buffer.Size != (ulong)buffer.Length || buffer.BaseAddress != mapping.SourceAddress ||
            mapping.SourceByteCount != (ulong)buffer.Length || mapping.FootprintAddress != buffer.BaseAddress ||
            mapping.FootprintByteCount != (ulong)buffer.Length || session != CurrentStorageCpuSeedSession ||
            !source.IsMappedCopyObservationCurrent(mapping))
            throw new InvalidOperationException("Finite writable buffer lost its original complete producer copy.");
        // Bounded clone is an input-integrity check, not another guest read.
        var original = new FiniteStorageBufferOrigin(memory, source, mapping, session, (byte[])buffer.Data.Clone());
        _finiteStorageBufferOrigins.Add(buffer, original);
        if (session != CurrentStorageCpuSeedSession)
        { _finiteStorageBufferOrigins.Remove(buffer); throw new InvalidOperationException("Finite buffer copy crossed a presenter session."); }
    }

    private static FiniteStorageBufferOrigin RequireFiniteStorageBufferOrigin(GuestMemoryBuffer buffer)
    {
        if (!_finiteStorageBufferOrigins.TryGetValue(buffer, out var origin) ||
            !ReferenceEquals(_guestMemory, origin.Memory) || origin.Session != CurrentStorageCpuSeedSession ||
            buffer.Pooled || !buffer.Writable || !buffer.WriteBackToGuest ||
            buffer.Length <= 0 || buffer.Length != buffer.Data.Length || buffer.Size != (ulong)buffer.Length ||
            buffer.BaseAddress != origin.Mapping.SourceAddress || origin.Mapping.SourceByteCount != (ulong)buffer.Length ||
            !buffer.Data.AsSpan().SequenceEqual(origin.OriginalBytes) ||
            !origin.Source.IsMappedCopyObservationCurrent(origin.Mapping))
            throw new NotSupportedException("Finite writable buffer has no unchanged original complete mapping/session/input proof.");
        return origin;
    }

    private static bool PhysicalMappingOverlaps(CpuMappedCopyObservation left, CpuMappedCopyObservation right)
    {
        foreach (var a in left.Segments)
        foreach (var b in right.Segments)
        {
            if (a.BackingIdentity != b.BackingIdentity) continue;
            var aStart = checked(a.MappingBackingOffset + a.CoveredAddress - a.MappingAddress);
            var bStart = checked(b.MappingBackingOffset + b.CoveredAddress - b.MappingAddress);
            if ((UInt128)aStart < (UInt128)bStart + b.CoveredByteCount &&
                (UInt128)bStart < (UInt128)aStart + a.CoveredByteCount) return true;
        }
        return false;
    }

    private static void ValidateFiniteStorageBuffersAgainstImage(List<FiniteStorageWriteback>? receipts,
        GuestMemoryBuffer[] buffers)
    {
        if (receipts is null || receipts.Count != 1) throw new InvalidOperationException("Finite mixed work lost its exact image receipt.");
        var image = receipts[0];
        var originals = new List<FiniteStorageBufferOrigin>();
        var bytes = 0;
        foreach (var buffer in buffers)
        {
            if (!buffer.Writable || !buffer.WriteBackToGuest || buffer.Length == 0) continue;
            if (originals.Count == MaximumFiniteWritableBuffers || buffer.Length > MaximumFiniteBufferBytes - bytes)
                throw new NotSupportedException("Finite mixed writable buffers exceed their bounded complete inventory.");
            var origin = RequireFiniteStorageBufferOrigin(buffer);
            if (PhysicalMappingOverlaps(origin.Mapping, image.Origin.Mapping) ||
                originals.Any(previous => PhysicalMappingOverlaps(previous.Mapping, origin.Mapping)))
                throw new NotSupportedException("Finite image/buffer physical publication footprints overlap.");
            originals.Add(origin); bytes += buffer.Length;
        }
        ValidateFiniteStorageReceipt(image);
    }

    private static unsafe void PublishFiniteStorageBuffers(List<(nint Pointer, GuestMemoryBuffer Guest)> uploads,
        List<FiniteStorageWriteback>? receipts, GuestMemoryBuffer[] buffers)
    {
        // The caller has waited for the SAME successfully committed command
        // buffer. Revalidate every input and disjoint footprint before any bytes.
        ValidateFiniteStorageBuffersAgainstImage(receipts, buffers);
        foreach (var (pointer, buffer) in uploads)
        {
            if (buffer.Length == 0) continue;
            if (pointer == 0) throw new InvalidOperationException("Completed finite buffer has no retained staging pointer.");
            var origin = RequireFiniteStorageBufferOrigin(buffer);
            var publisher = origin.Memory as ICpuMappedPublicationSource ??
                throw new NotSupportedException("Finite writable buffer has no canonical provider.");
            var bytes = new ReadOnlySpan<byte>((void*)pointer, buffer.Length).ToArray();
            CpuMappedPublication? transaction = null;
            Exception? primary = null;
            try
            {
                var result = publisher.TryPrepareMappedPublication(origin.Mapping,
                    PrepareFiniteStorageMappingRuns(origin.Mapping, buffer.BaseAddress, bytes), out transaction);
                if (result != CpuMappedPublicationPreparationResult.Prepared || transaction is null)
                    throw new InvalidOperationException($"Finite buffer canonical preparation was refused: result={result}.");
                _ = RequireFiniteStorageBufferOrigin(buffer);
                foreach (var image in receipts!) ValidateFiniteStorageReceipt(image);
                if (transaction.TryCommit() != CpuMappedPublicationResult.Applied || transaction.SuccessfulObservation is not { } successor)
                    throw new InvalidOperationException("Finite buffer publication has no exact Applied receipt.");
                ValidateFiniteStorageMappingContinuation(origin.Mapping, successor);
                // Participating provider history records the actual disjoint
                // write. Legacy virtual observers still receive precise completed
                // notification; this does not repair/rearm host permissions.
                GuestImageWriteTracker.NotifyCompletedMappedGpuWrite(buffer.BaseAddress, (ulong)buffer.Length);
                _finiteStorageBufferOrigins.Remove(buffer); // consumed; never replay old payload
            }
            catch (Exception failure) { primary = failure; throw; }
            finally { CancelFiniteStoragePublication(transaction, primary); }
        }
        foreach (var image in receipts!) ValidateFiniteStorageReceipt(image);
    }
}
