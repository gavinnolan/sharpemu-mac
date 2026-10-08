// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;

namespace SharpEmu.Libs.Gpu;

// Optional preparation of the first runnable prefix. A backend may attach an
// exact completed-prefix boundary; the caller waits for it outside AGC gates.
// Neither result certifies deferred packets or guest-memory content authority.
public enum GuestSubmissionPreparationResult
{
    // The accepted submission completed its first runnable interpreter slice;
    // translated libraries encountered in it were prepared before renderer admission.
    // An attached backend completion boundary has also succeeded before return.
    PreparedPrefix,
    // An unsatisfied dependency prevents certifying later packets/shaders. The
    // caller may continue CPU work/other submissions; this is not full readiness.
    DeferredDependency,
}

public interface IGuestSubmissionPreparation
{
    GuestSubmissionPreparationResult WaitUntilPrepared();
}

// Exact completion boundary captured by the accepted runnable prefix. Waiting
// is a caller operation, never a command-worker operation or early guest EOP.
public interface IGuestSubmissionPrefixCompletion
{
    void WaitUntilCompleted();
}

public interface ICommandSubmissionCompletionHost
{
    void BeginSubmissionCompletionCapture(int queueId, ulong submissionId);
    IGuestSubmissionPrefixCompletion? EndSubmissionCompletionCapture(bool complete);
}

public interface IGuestSubmissionPreparationBackend
{
    IGuestSubmissionPreparation SubmitCommandStreamWithPreparation(ICpuMemory memory,
        uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots);
}
