// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    internal static void ExecuteOrderedGuestMemoryOperation(Action operation, string kind, ulong address, ulong bytes, ulong value)
    {
        ArgumentNullException.ThrowIfNull(operation);
        bool self;
        lock (_gate) self = ReferenceEquals(_renderOwner, Thread.CurrentThread) || _guestWorkDrainDepth != 0;
        if (self)
            throw FailGuestCompletion("command-stream memory operation cannot wait on its render thread");
        var sequence = SubmitOrderedGuestCompletion(() =>
        {
            TraceCommandMemory("executing", kind, address, bytes, value, Volatile.Read(ref _executingGuestWorkSequence));
            operation();
            TraceCommandMemory("applied", kind, address, bytes, value, Volatile.Read(ref _executingGuestWorkSequence));
        }, "command_stream memory operation");
        TraceCommandMemory("admitted", kind, address, bytes, value, sequence);
        // Initial-empty completion already ran under its exact reservation.
        // Otherwise finish the admitted operation before later snapshots or
        // atomic/semaphore results consume its bytes. This is correctness-first
        // scheduling, not a claim about final per-packet performance.
        if (sequence != 0 && !WaitForGuestWork(sequence, Timeout.Infinite))
            throw FailGuestCompletion("command-stream memory operation did not complete");
        RequireGuestCompletionHealthy();
    }
}
