// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.ExceptionServices;
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
        ICpuMappedCopySource? Source, CpuMappedCopyObservation? Mapping, Action Action, CommandMemoryReadWatch? OriginalWatch = null);

    private sealed class CommandMemoryReadWatch(ICpuMappedReadWatchSource source, CpuMappedCopyObservation original)
    {
        private int releaseAttempted;
        internal void Release()
        {
            if (Interlocked.Exchange(ref releaseAttempted, 1) == 0) source.ReleaseMappedCopyWriteWatch(original);
        }
    }

    private static OrderedCommandMemoryOperation PrepareCommandMemoryOperation(
        MetalCommandStreamHost host, CommandMemoryOperation operation, Action execute)
    {
        var memory = host.Memory;
        var session = CurrentNativeDepthFaceSession; // first session, never refreshed after a probe
        var queue = _submittingGuestQueue ?? GuestQueueIdentity.Default;
        ICpuMappedCopySource? source = null; CpuMappedCopyObservation? mapping = null;
        CommandMemoryReadWatch? watch = null;
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
                CpuMappedCopyObservation? original = null;
                var watched = mapped as ICpuMappedReadWatchSource;
                var result = watched is null
                    ? mapped.TryReadMappedCopy(address, bytes, address, probe, out original)
                    : watched.TryReadMappedCopyWithWriteWatch(address, bytes, address, probe, out original);
                // The physical veto belongs to THIS first full copy. Never
                // enroll after an ordinary copy or retry with a fresh mapping.
                if (watched is not null && original is not null) watch = new(watched, original);
                if (result == CpuMappedCopyResult.Copied && original is not null &&
                    original.FootprintAddress == address && original.FootprintByteCount == bytes &&
                    original.SourceAddress == address && original.SourceByteCount == bytes &&
                    mapped.IsMappedCopyObservationCurrent(original) && session == CurrentNativeDepthFaceSession)
                { source = mapped; mapping = original; }
            }
            catch { } // optional attribution failure leaves the writer UNKNOWN, never rebased
        }
        var prepared = new OrderedCommandMemoryOperation(operation, memory, session, queue, source, mapping, execute, watch);
        if (mapping is null && watch is not null)
        {
            // A rejected copy still returns its original bookkeeping owner on
            // some providers. Consume it once; unknown cleanup stays rooted.
            try { watch.Release(); }
            catch { lock (_gate) _uncertainCommandMemoryWrite ??= prepared; throw; }
            prepared = prepared with { OriginalWatch = null };
        }
        return prepared;
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
                RequireCommandMemoryOriginal(owned, mapping, requireExecution: true);
            }
            TraceCommandMemory("executing", operation.DebugName, operation.Address, operation.ByteCount,
                operation.Value, Volatile.Read(ref _executingGuestWorkSequence));
            if (owned.Mapping is { } original)
            {
                _commandMemoryBeforeOriginalWriteForControl?.Invoke(); // default null; outside all presenter/provider gates
                try
                {
                    operation.ExecuteOriginalWrite((ICpuMappedPublicationSource)owned.Memory, original,
                        () => RequireCommandMemoryOriginal(owned, original, requireExecution: true),
                        originalHasWriteWatch: owned.OriginalWatch is not null);
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
        long sequence = 0; Exception? primary = null;
        try
        {
            sequence = SubmitOrderedGuestCompletionCore(execute, "command_stream memory operation", owned);
            TraceCommandMemory("admitted", operation.DebugName, operation.Address, operation.ByteCount, operation.Value, sequence);
            if (sequence != 0 && !WaitForGuestWork(sequence, Timeout.Infinite))
                throw FailGuestCompletion("command-stream memory operation did not complete");
            RequireGuestCompletionHealthy();
        }
        catch (Exception failure) { primary = failure; }
        finally { primary = ReleaseOriginalCommandMemoryWatch(owned, sequence, primary); }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private static Exception? ReleaseOriginalCommandMemoryWatch(OrderedCommandMemoryOperation owned,
        long sequence, Exception? primary)
    {
        if (owned.OriginalWatch is not { } watch) return primary;
        lock (_gate)
        {
            // A global failure can wake the submitting caller before its exact
            // taken callback exits. Preserve that original rather than releasing
            // its physical veto beneath an in-flight/uncertain transaction.
            if (owned.Operation.RetainsUncertainOriginalWrite ||
                _takenDepthCubeProducerWork is OrderedCommandMemoryOperation taken &&
                ReferenceEquals(taken.Operation, owned.Operation) ||
                _guestWorkFailure is null && (sequence == 0 || !IsGuestWorkCompletedLocked(sequence)) &&
                _pendingGuestWorkByQueue.Values.Any(items => items.Any(item =>
                    item.Work is OrderedCommandMemoryOperation pending &&
                    ReferenceEquals(pending.Operation, owned.Operation) &&
                    ReferenceEquals(pending.OriginalWatch, watch))) ||
                sequence != 0 && !IsGuestWorkCompletedLocked(sequence) && _guestWorkFailure is null)
            { _uncertainCommandMemoryWrite ??= owned; return primary; }
        }
        try { watch.Release(); }
        catch (Exception cleanup)
        {
            lock (_gate) _uncertainCommandMemoryWrite ??= owned;
            if (primary is null) return cleanup;
            try { primary.Data["CommandMemoryOriginalWatchCleanup"] = cleanup; } catch { }
        }
        return primary;
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
            !ReferenceEquals(taken.OriginalWatch, original.OriginalWatch) ||
            taken.Session != original.Session || taken.Queue != original.Queue || _submittingGuestQueue != original.Queue)
            throw new NotSupportedException("Command memory operation lacks its exact taken/executing original receipt.");
    }

    private static void RequireCommandMemoryOriginalLocked(OrderedCommandMemoryOperation work, CpuMappedCopyObservation mapping)
    {
        if (_uncertainCommandMemoryWrite is not null)
            throw new NotSupportedException("An uncertain ORIGINAL command memory write remains process-owned; further certified writes refuse across presenter reset.");
        if (_closed || _guestWorkFailure is not null || !ReferenceEquals(_guestMemory, work.Memory) ||
            work.Session != CurrentNativeDepthFaceSession || work.Queue != (_submittingGuestQueue ?? GuestQueueIdentity.Default) || work.Source is null ||
            !ReferenceEquals(work.Mapping, mapping) ||
            !work.Operation.TryGetGuestWriteRange(out var address, out var bytes) || bytes == 0 ||
            mapping.FootprintAddress != address || mapping.FootprintByteCount != bytes ||
            work.Operation.Kind != CommandMemoryOperation.OperationKind.Write || work.Memory is not ICpuMappedPublicationSource ||
            mapping.SourceAddress != address || mapping.SourceByteCount != bytes)
            throw new NotSupportedException("Command memory operation lost its ORIGINAL complete writable mapping/session.");
        foreach (var segment in mapping.Segments)
            if ((segment.GuestAccess & GuestPageProtection.Write) == 0)
                throw new NotSupportedException("Command memory operation lacks its ORIGINAL full write entitlement.");
    }

    private static void RequireCommandMemoryOriginal(OrderedCommandMemoryOperation work,
        CpuMappedCopyObservation mapping, bool requireExecution = false)
    {
        if (Monitor.IsEntered(_gate))
            throw new InvalidOperationException("Command memory provider currentness requires a released presenter gate.");
        lock (_gate)
        {
            if (requireExecution) RequireCommandMemoryExecutionLocked(work);
            RequireCommandMemoryOriginalLocked(work, mapping);
        }
        if (!work.Source!.IsMappedCopyObservationCurrent(mapping))
            throw new NotSupportedException("Command memory operation lost its ORIGINAL complete writable mapping/session.");
        lock (_gate)
        {
            if (requireExecution) RequireCommandMemoryExecutionLocked(work);
            RequireCommandMemoryOriginalLocked(work, mapping);
        }
        // This is admission/identity validation, not a pin. The actual closed
        // write still prepares and commits ONLY the same original transaction.
    }

    // Detached identities, not a currentness cache. A borrower can use these
    // reached provider checks only while the exact captured accepted/taken
    // inventory is unchanged. Nothing is recaptured on a mismatch.
    private sealed record NativeColorCommandMemoryInventory(long Enqueued, long TakenSequence,
        object? Taken, int PendingCount, PendingGuestWork[] Pending);

    private static NativeColorCommandMemoryInventory CaptureNativeColorCommandMemoryInventory()
    {
        if (Monitor.IsEntered(_gate))
            throw new InvalidOperationException("Native color command memory provider checks require a released presenter gate.");
        NativeColorCommandMemoryInventory inventory;
        lock (_gate)
        {
            if (_pendingGuestWorkCount > 128 || _pendingGuestWorkByQueue.Count > 128)
                throw new NotSupportedException("Native color command memory inventory exceeds its finite bound.");
            inventory = new(_enqueuedGuestWorkSequence, _takenGuestWorkSequence, _takenDepthCubeProducerWork,
                _pendingGuestWorkCount, _pendingGuestWorkByQueue.Values.SelectMany(items => items)
                    .Where(item => item.Work is OrderedCommandMemoryOperation).ToArray());
        }
        foreach (var pending in inventory.Pending)
        {
            var work = (OrderedCommandMemoryOperation)pending.Work;
            if (work.Operation.Kind == CommandMemoryOperation.OperationKind.Write && work.Operation.ByteCount != 0)
                RequireCommandMemoryOriginal(work, work.Mapping ??
                    throw new NotSupportedException("Native color command memory writer lacks its ORIGINAL complete mapping."));
        }
        if (inventory.Taken is OrderedCommandMemoryOperation taken &&
            taken.Operation.Kind == CommandMemoryOperation.OperationKind.Write && taken.Operation.ByteCount != 0)
            RequireCommandMemoryOriginal(taken, taken.Mapping ??
                throw new NotSupportedException("Native color taken command memory writer lacks its ORIGINAL complete mapping."));
        if (inventory.Pending.Length != 0 || inventory.Taken is OrderedCommandMemoryOperation)
            lock (_gate) RequireNativeColorCommandMemoryInventoryLocked(inventory);
        return inventory;
    }

    private static bool SameCommandMemoryPending(PendingGuestWork first, PendingGuestWork current) =>
        first == current && ReferenceEquals(first.Work, current.Work);

    private static void RequireNativeColorCommandMemoryInventoryLocked(NativeColorCommandMemoryInventory inventory)
    {
        if (_enqueuedGuestWorkSequence != inventory.Enqueued || _takenGuestWorkSequence != inventory.TakenSequence ||
            !ReferenceEquals(_takenDepthCubeProducerWork, inventory.Taken) || _pendingGuestWorkCount != inventory.PendingCount)
            throw new NotSupportedException("Native color ORIGINAL command memory inventory changed during provider validation.");
        var index = 0;
        foreach (var items in _pendingGuestWorkByQueue.Values)
        foreach (var pending in items)
        {
            if (pending.Work is not OrderedCommandMemoryOperation) continue;
            if (index >= inventory.Pending.Length || !SameCommandMemoryPending(pending, inventory.Pending[index++]))
                throw new NotSupportedException("Native color ORIGINAL accepted command memory receipt changed.");
        }
        if (index != inventory.Pending.Length)
            throw new NotSupportedException("Native color ORIGINAL accepted command memory inventory shrank.");
    }

    private static void RequireNativeColorCommandMemoryLocked(
        OrderedCommandMemoryOperation work, NativeColorProducer producer, PendingGuestWork? pending,
        NativeColorCommandMemoryInventory? inventory)
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
        if (inventory is null)
            throw new NotSupportedException("Native color command memory writer lacks its gate-free ORIGINAL inventory check.");
        RequireNativeColorCommandMemoryInventoryLocked(inventory);
        if (pending is not null ? !inventory.Pending.Any(item => SameCommandMemoryPending(item, pending.Value) && ReferenceEquals(item.Work, work)) :
            !ReferenceEquals(inventory.Taken, work) || inventory.TakenSequence == 0)
            throw new NotSupportedException("Native color command memory writer is not its checked accepted/taken original.");
        var mapping = work.Mapping ?? throw new NotSupportedException("Native color command memory writer lacks its ORIGINAL complete mapping.");
        RequireCommandMemoryOriginalLocked(work, mapping);
        if (NativeColorPhysicalOverlap(producer.Original.Mapping, producer.Original.Placement.PhysicalAddress,
            producer.Original.Placement.ByteCount, mapping, address, bytes))
            throw new NotSupportedException("Native color command memory writer overlaps the ORIGINAL physical image.");
    }
}
