// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.DsTrace;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;

namespace SharpEmu.Libs.Ampr;

/// <summary>
/// The APR completion engine. A submission captures the command buffer on the
/// submitting thread and returns; one worker per priority executes the
/// submissions in order. Completion publishes the commands' effects first,
/// then the result record, then the done state, then wakes
/// sceKernelAprWaitCommandBuffer waiters.
/// </summary>
public static partial class AmprExports
{
    private const int AprPriorityCount = 4;
    private const int AprMaxRetainedCompleted = 4096;
    internal const string AprWaitWakeKey = "sceKernelAprWaitCommandBuffer";

    private sealed class AprSubmission
    {
        public required uint Id { get; init; }
        public required CapturedCommandBuffer Captured { get; init; }
        public required ICpuMemory Memory { get; init; }
        public required Generation Generation { get; init; }
        public required ulong ResultAddress { get; init; }
        public required bool Retained { get; init; }
        public required long Sequence { get; init; }
        public required ulong DsCaller { get; init; }
        public required ulong DsReturnAddress { get; init; }
        public required long SubmitTimestamp { get; init; }

        // Guarded by _aprGate.
        public bool Done;
        public int ExecutionResult;
        public uint ErrorOffset;
    }

    private sealed class AprWorker
    {
        public readonly Queue<AprSubmission> Pending = new();
        public Thread? Thread;
    }

    private static readonly object _aprGate = new();
    private static readonly Dictionary<uint, AprSubmission> _aprSubmissions = new();
    private static readonly Queue<AprSubmission> _aprCompletedOrder = new();
    private static readonly AprWorker[] _aprWorkers =
        [new AprWorker(), new AprWorker(), new AprWorker(), new AprWorker()];
    private static int _nextAprSubmissionId;
    private static long _aprSequence;

    /// <summary>
    /// Captures and queues a command buffer. With <paramref name="outIdAddress"/>
    /// the submission id is written there and the submission is kept until
    /// sceKernelAprWaitCommandBuffer collects it; without one nothing can wait
    /// for it, so it is dropped when it completes.
    /// </summary>
    internal static int SubmitCommandBuffer(
        CpuContext ctx,
        ulong commandBuffer,
        ulong priority,
        ulong resultAddress,
        ulong outIdAddress,
        ulong dsReturnAddress,
        out uint submissionId)
    {
        submissionId = 0;
        var captureResult = CaptureCommandBuffer(ctx, commandBuffer, out var captured);
        if (captureResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return captureResult;
        }

        submissionId = unchecked((uint)Interlocked.Increment(ref _nextAprSubmissionId));
        if (submissionId == 0)
        {
            submissionId = unchecked((uint)Interlocked.Increment(ref _nextAprSubmissionId));
        }

        if (outIdAddress != 0 && !ctx.TryWriteUInt32(outIdAddress, submissionId))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var submission = new AprSubmission
        {
            Id = submissionId,
            Captured = captured!,
            Memory = ctx.Memory,
            Generation = ctx.TargetGeneration,
            ResultAddress = resultAddress,
            Retained = outIdAddress != 0,
            Sequence = Interlocked.Increment(ref _aprSequence),
            DsCaller = DsTrace.CurrentRa1,
            DsReturnAddress = dsReturnAddress,
            SubmitTimestamp = Stopwatch.GetTimestamp(),
        };
        if (submission.Retained)
        {
            lock (_aprGate)
            {
                _aprSubmissions[submissionId] = submission;
            }
        }

        var worker = _aprWorkers[(int)Math.Min(priority, AprPriorityCount - 1)];
        lock (worker)
        {
            worker.Pending.Enqueue(submission);
            if (worker.Thread is null)
            {
                var index = Array.IndexOf(_aprWorkers, worker);
                worker.Thread = new Thread(() => RunAprWorker(worker))
                {
                    IsBackground = true,
                    Name = $"SharpEmu apr p{index}",
                };
                worker.Thread.Start();
            }

            Monitor.Pulse(worker);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static void RunAprWorker(AprWorker worker)
    {
        while (true)
        {
            AprSubmission submission;
            lock (worker)
            {
                while (worker.Pending.Count == 0)
                {
                    Monitor.Wait(worker);
                }

                submission = worker.Pending.Dequeue();
            }

            CompleteAprSubmission(submission);
        }
    }

    private static void CompleteAprSubmission(AprSubmission submission)
    {
        var dsApr = DsTrace.Io && DsTrace.Armed;
        if (dsApr)
        {
            DsAprAcc.Reset();
        }

        var ctx = new CpuContext(submission.Memory, submission.Generation);
        ExecuteCapturedCommandBuffer(ctx, submission.Captured, out var executionResult, out var errorOffset);
        if (submission.ResultAddress != 0)
        {
            _ = TryWriteAprResult(submission.Memory, submission.ResultAddress, executionResult, errorOffset);
        }

        lock (_aprGate)
        {
            submission.ExecutionResult = executionResult;
            submission.ErrorOffset = errorOffset;
            submission.Done = true;
            if (submission.Retained)
            {
                _aprCompletedOrder.Enqueue(submission);
                // A guest that never waits must not grow the table without bound.
                while (_aprCompletedOrder.Count > AprMaxRetainedCompleted)
                {
                    var oldest = _aprCompletedOrder.Dequeue();
                    if (_aprSubmissions.TryGetValue(oldest.Id, out var current) && ReferenceEquals(current, oldest))
                    {
                        _aprSubmissions.Remove(oldest.Id);
                    }
                }
            }

            Monitor.PulseAll(_aprGate);
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(AprWaitWakeKey);
        if (dsApr)
        {
            DsTrace.Emit(
                DsEv.APR,
                submission.DsCaller,
                submission.DsReturnAddress,
                (ulong)DsAprAcc.Cmds,
                (ulong)DsTrace.TicksToNs(Stopwatch.GetTimestamp() - submission.SubmitTimestamp),
                (ulong)DsTrace.TicksToNs(DsAprAcc.PreadTicks),
                (ulong)DsTrace.TicksToNs(DsAprAcc.WaitTicks),
                (ulong)DsAprAcc.WaitIters,
                (ulong)DsAprAcc.Bytes);
        }
    }

    private sealed class AprWaiter : IGuestThreadBlockWaiter
    {
        public required CpuContext Ctx { get; init; }
        public required AprSubmission Submission { get; init; }

        public bool TryWake()
        {
            lock (_aprGate)
            {
                return Submission.Done;
            }
        }

        public int Resume() => CollectAprSubmission(Ctx, Submission);
    }

    /// <summary>
    /// sceKernelAprWaitCommandBuffer: parks the caller until the submission
    /// completes, then reports its result and forgets the id.
    /// </summary>
    internal static int WaitCommandBuffer(CpuContext ctx, uint submissionId, out ulong commandBuffer)
    {
        commandBuffer = 0;
        AprSubmission? submission;
        lock (_aprGate)
        {
            if (!_aprSubmissions.TryGetValue(submissionId, out submission))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            commandBuffer = submission.Captured.CommandBuffer;
            if (submission.Done)
            {
                return CollectAprSubmission(ctx, submission);
            }
        }

        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "sceKernelAprWaitCommandBuffer",
                AprWaitWakeKey,
                new AprWaiter { Ctx = ctx, Submission = submission },
                0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        lock (_aprGate)
        {
            while (!submission.Done)
            {
                Monitor.Wait(_aprGate);
            }
        }

        return CollectAprSubmission(ctx, submission);
    }

    private static int CollectAprSubmission(CpuContext ctx, AprSubmission submission)
    {
        int executionResult;
        uint errorOffset;
        lock (_aprGate)
        {
            if (_aprSubmissions.TryGetValue(submission.Id, out var current) && ReferenceEquals(current, submission))
            {
                _aprSubmissions.Remove(submission.Id);
            }

            executionResult = submission.ExecutionResult;
            errorOffset = submission.ErrorOffset;
        }

        if (submission.ResultAddress != 0 &&
            !TryWriteAprResult(ctx.Memory, submission.ResultAddress, executionResult, errorOffset))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryWriteAprResult(ICpuMemory memory, ulong resultAddress, int executionResult, uint errorOffset)
    {
        Span<byte> result = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteInt32LittleEndian(result, executionResult);
        BinaryPrimitives.WriteUInt32LittleEndian(result[sizeof(int)..], errorOffset);
        return memory.TryWrite(resultAddress, result);
    }
}
