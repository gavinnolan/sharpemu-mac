// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private static Action? _commandMemoryBeforeOriginalWriteForControl;
    // A failed queue can drop its DTO. Retain the exact first uncertain scope,
    // including memory/backing ownership, independently of canceled callbacks.
    private static OrderedCommandMemoryOperation? _uncertainCommandMemoryWrite;
    // The callback is minted only from the sealed actual Metal command host and
    // the closed packet payload. Public arbitrary Action admission stays unknown.
    private sealed record OrderedCommandMemoryOperation(
        CommandMemoryOperation Operation, ICpuMemory Memory, long Session, GuestQueueIdentity Queue,
        ICpuMappedCopySource? Source, CpuMappedCopyObservation? Mapping, Action Action);

    private static OrderedCommandMemoryOperation PrepareCommandMemoryOperation(
        MetalCommandStreamHost host, CommandMemoryOperation operation, Action execute)
    {
        var memory = host.Memory;
        var session = CurrentNativeDepthFaceSession; // first session, never refreshed after a probe
        var queue = _submittingGuestQueue ?? GuestQueueIdentity.Default;
        ICpuMappedCopySource? source = null; CpuMappedCopyObservation? mapping = null;
        if (NativeColorBorrowEnabled && operation.Kind == CommandMemoryOperation.OperationKind.Write &&
            operation.TryGetGuestWriteRange(out var address, out var bytes) && bytes is > 0 and <= CommandMemoryOperation.MaximumOriginalWriteBytes &&
            memory is ICpuMappedCopySource mapped && memory is ICpuMappedPublicationSource)
        {
            try
            {
                // Publication writes are bounded by Original.SourceByteCount.
                // Discard ALL copied destination bytes; they are mapping setup,
                // never native/CPU pixel authority or command payload storage.
                var probe = new byte[(int)bytes];
                if (mapped.TryReadMappedCopy(address, bytes, address, probe, out var original) == CpuMappedCopyResult.Copied &&
                    original is not null && original.FootprintAddress == address && original.FootprintByteCount == bytes &&
                    original.SourceAddress == address && original.SourceByteCount == bytes &&
                    mapped.IsMappedCopyObservationCurrent(original) && session == CurrentNativeDepthFaceSession)
                { source = mapped; mapping = original; }
            }
            catch { } // optional attribution failure leaves the writer UNKNOWN, never rebased
        }
        return new(operation, memory, session, queue, source, mapping, execute);
    }

    internal static void ExecuteTypedGuestMemoryOperation(MetalCommandStreamHost host, CommandMemoryOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        bool self;
        lock (_gate) self = ReferenceEquals(_renderOwner, Thread.CurrentThread) || _guestWorkDrainDepth != 0;
        if (self) throw FailGuestCompletion("command-stream memory operation cannot wait on its render thread");
        // Capture before admission and outside presenter/provider locks. This
        // strong owner also preserves detached packet bytes through failure.
        OrderedCommandMemoryOperation? owned = null;
        Action execute = () =>
        {
            lock (_gate) RequireCommandMemoryExecutionLocked(owned!);
            if (owned!.Mapping is { } mapping)
            {
                lock (_gate) RequireCommandMemoryOriginalLocked(owned, mapping);
            }
            TraceCommandMemory("executing", operation.DebugName, operation.Address, operation.ByteCount,
                operation.Value, Volatile.Read(ref _executingGuestWorkSequence));
            if (owned.Mapping is { } original)
            {
                _commandMemoryBeforeOriginalWriteForControl?.Invoke(); // default null; outside all presenter/provider gates
                try
                {
                    operation.ExecuteOriginalWrite((ICpuMappedPublicationSource)owned.Memory, original,
                        () => { lock (_gate) { RequireCommandMemoryExecutionLocked(owned); RequireCommandMemoryOriginalLocked(owned, original); } });
                }
                catch
                {
                    if (operation.RetainsUncertainOriginalWrite)
                        lock (_gate) _uncertainCommandMemoryWrite ??= owned;
                    throw;
                }
            }
            else host.ExecuteCommandMemoryOperationAtQueuePosition(operation);
            TraceCommandMemory("applied", operation.DebugName, operation.Address, operation.ByteCount,
                operation.Value, Volatile.Read(ref _executingGuestWorkSequence));
        };
        owned = PrepareCommandMemoryOperation(host, operation, execute);
        var sequence = SubmitOrderedGuestCompletionCore(execute, "command_stream memory operation", owned);
        TraceCommandMemory("admitted", operation.DebugName, operation.Address, operation.ByteCount, operation.Value, sequence);
        if (sequence != 0 && !WaitForGuestWork(sequence, Timeout.Infinite))
            throw FailGuestCompletion("command-stream memory operation did not complete");
        RequireGuestCompletionHealthy();
    }

    private static void RequireCommandMemoryExecutionLocked(OrderedCommandMemoryOperation original)
    {
        if (_closed || _guestWorkFailure is not null)
            throw new NotSupportedException("Command memory operation lost its completion owner.");
        if (_initialGuestCompletion is { } initial && ReferenceEquals(initial.Owner, Thread.CurrentThread)) return;
        // The queue clones only Action to install the GPU completion wrapper.
        // Every immutable effect/provenance reference must still be the first
        // accepted record, owned by this exact taken/executing receipt.
        if (_guestWorkDrainDepth == 0 || _executingGuestWorkSequence == 0 ||
            _takenGuestWorkSequence != _executingGuestWorkSequence ||
            _takenDepthCubeProducerWork is not OrderedCommandMemoryOperation taken ||
            !ReferenceEquals(taken.Operation, original.Operation) || !ReferenceEquals(taken.Memory, original.Memory) ||
            !ReferenceEquals(taken.Source, original.Source) || !ReferenceEquals(taken.Mapping, original.Mapping) ||
            taken.Session != original.Session || taken.Queue != original.Queue || _submittingGuestQueue != original.Queue)
            throw new NotSupportedException("Command memory operation lacks its exact taken/executing original receipt.");
    }

    private static void RequireCommandMemoryOriginalLocked(OrderedCommandMemoryOperation work, CpuMappedCopyObservation mapping)
    {
        if (_uncertainCommandMemoryWrite is not null)
            throw new NotSupportedException("An uncertain ORIGINAL command memory write remains process-owned; further certified writes refuse across presenter reset.");
        if (_closed || _guestWorkFailure is not null || !ReferenceEquals(_guestMemory, work.Memory) ||
            work.Session != CurrentNativeDepthFaceSession || work.Queue != (_submittingGuestQueue ?? GuestQueueIdentity.Default) || work.Source is null ||
            !work.Source.IsMappedCopyObservationCurrent(mapping) ||
            !work.Operation.TryGetGuestWriteRange(out var address, out var bytes) || bytes == 0 ||
            mapping.FootprintAddress != address || mapping.FootprintByteCount != bytes ||
            work.Operation.Kind != CommandMemoryOperation.OperationKind.Write || work.Memory is not ICpuMappedPublicationSource ||
            mapping.SourceAddress != address || mapping.SourceByteCount != bytes)
            throw new NotSupportedException("Command memory operation lost its ORIGINAL complete writable mapping/session.");
        foreach (var segment in mapping.Segments)
            if ((segment.GuestAccess & GuestPageProtection.Write) == 0)
                throw new NotSupportedException("Command memory operation lacks its ORIGINAL full write entitlement.");
    }

    private static void RequireNativeColorCommandMemoryLocked(
        OrderedCommandMemoryOperation work, NativeColorProducer producer, PendingGuestWork? pending)
    {
        if (work.Operation.Kind != CommandMemoryOperation.OperationKind.Write ||
            !work.Operation.TryGetGuestWriteRange(out var address, out var bytes))
            throw new NotSupportedException("Native color command memory operation has unknown/unbounded effects.");
        var queue = pending?.Queue;
        if (queue is null && _takenDepthCubeProducerWork is { } taken && ReferenceEquals(taken, work))
            queue = _submittingGuestQueue;
        if (queue != producer.Queue || queue != work.Queue || !ReferenceEquals(work.Memory, producer.Original.Placement.Memory) ||
            work.Session != producer.Original.Session || work.Session != CurrentNativeDepthFaceSession)
            throw new NotSupportedException("Native color command memory operation has another original memory/session/queue.");
        if (bytes == 0) return; // closed zero-byte write only; never arbitrary callbacks
        var mapping = work.Mapping ?? throw new NotSupportedException("Native color command memory writer lacks its ORIGINAL complete mapping.");
        RequireCommandMemoryOriginalLocked(work, mapping);
        if (NativeColorPhysicalOverlap(producer.Original.Mapping, producer.Original.Placement.PhysicalAddress,
            producer.Original.Placement.ByteCount, mapping, address, bytes))
            throw new NotSupportedException("Native color command memory writer overlaps the ORIGINAL physical image.");
    }
}
