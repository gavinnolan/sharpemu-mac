// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Libs.Gpu.GpuCommands;

public sealed class CommandSubmissionPreparation : IGuestSubmissionPreparation
{
    private readonly CommandStreamQueue _owner;
    internal enum State { Pending, PreparedPrefix, DeferredDependency, Failed, Cancelled }
    internal State Status;
    internal Exception? Failure;
    internal IGuestSubmissionPrefixCompletion? Completion;
    public int QueueId { get; }
    public ulong SubmissionId { get; }

    internal CommandSubmissionPreparation(CommandStreamQueue owner, int queueId, ulong submissionId)
    { _owner = owner; QueueId = queueId; SubmissionId = submissionId; }
    public GuestSubmissionPreparationResult WaitUntilPrepared() => _owner.WaitForPreparation(this);
    // Only the exact accepting queue owns these one-way state transitions.
    internal void Resolve(State state, Exception? failure = null, IGuestSubmissionPrefixCompletion? completion = null)
    { if (Status != State.Pending) return; Failure = failure; Completion = completion; Status = state; }
}
