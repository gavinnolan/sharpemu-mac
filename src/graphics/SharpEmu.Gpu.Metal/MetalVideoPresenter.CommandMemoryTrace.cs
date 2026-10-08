// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands;

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    private static readonly bool _commandMemoryTraceEnabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_COMMAND_MEMORY_TRACE"), "1", StringComparison.Ordinal);
    private static int _commandMemoryTraceLines;

    internal static void TraceCommandMemoryEop(string phase, in EndOfPipeWrite write, long? sequence = null)
    {
        if (!_commandMemoryTraceEnabled) return;
        var bytes = write.Kind switch
        {
            EndOfPipeWriteKind.Write32 or EndOfPipeWriteKind.WriteBack32 or EndOfPipeWriteKind.Interrupt32 or
            EndOfPipeWriteKind.InterruptWriteBack32 or EndOfPipeWriteKind.FlipWithWrite32 or EndOfPipeWriteKind.FlipWithInterruptWriteBack32 => 4UL,
            EndOfPipeWriteKind.Write64 or EndOfPipeWriteKind.WriteBack64 or EndOfPipeWriteKind.Interrupt64 or
            EndOfPipeWriteKind.InterruptWriteBack64 or EndOfPipeWriteKind.ClockWrite or EndOfPipeWriteKind.ClockWriteBack => 8UL,
            _ => 0UL,
        };
        TraceCommandMemory(phase, write.Kind.ToString(), write.Destination, bytes, write.Value,
            sequence ?? Volatile.Read(ref _executingGuestWorkSequence));
    }

    internal static ulong CommandMemoryTraceScalar(ReadOnlySpan<byte> bytes)
    {
        if (!_commandMemoryTraceEnabled || bytes.Length is < 1 or > 8) return 0;
        Span<byte> scalar = stackalloc byte[8];
        scalar.Clear();
        bytes.CopyTo(scalar);
        return BinaryPrimitives.ReadUInt64LittleEndian(scalar);
    }

    internal static void TraceCommandMemory(string phase, string kind, ulong address, ulong bytes, ulong value, long sequence)
    {
        if (!_commandMemoryTraceEnabled) return;
        if (Interlocked.Increment(ref _commandMemoryTraceLines) > 128) return;
        try
        {
            var queue = _submittingGuestQueue ?? GuestQueueIdentity.Default;
            Console.Error.WriteLine($"[METAL][COMMAND_MEMORY] monotonic_ticks={System.Diagnostics.Stopwatch.GetTimestamp()} tick_frequency={System.Diagnostics.Stopwatch.Frequency} phase={phase} kind={kind} queue='{queue.Name}' submission={queue.SubmissionId} sequence={sequence} address=0x{address:X16} bytes={bytes} scalar=0x{value:X16}");
        }
        catch (Exception) { } // optional diagnostics never replace the memory effect or its original failure
    }
    // Three records for each of the first 64 accepted work identities. This
    // independent bound remains useful after the memory-event cap is exhausted.
    private static void TraceGuestWork(string phase, in PendingGuestWork work)
    {
        if (!_commandMemoryTraceEnabled || work.Sequence is < 1 or > 64) return;
        try
        {
            Console.Error.WriteLine($"[METAL][GUEST_WORK] monotonic_ticks={System.Diagnostics.Stopwatch.GetTimestamp()} tick_frequency={System.Diagnostics.Stopwatch.Frequency} phase={phase} kind={work.Work.GetType().Name} queue='{work.Queue.Name}' submission={work.Queue.SubmissionId} sequence={work.Sequence}");
        }
        catch (Exception) { } // optional metadata cannot change execution/failure
    }
    private static int _guestWorkStageTraceLines;

    private static void TraceGuestWorkStage(string stage)
    {
        if (!_commandMemoryTraceEnabled) return;
        var sequence = Volatile.Read(ref _executingGuestWorkSequence);
        if (sequence is < 1 or > 64 || Interlocked.Increment(ref _guestWorkStageTraceLines) > 1024) return;
        try
        {
            var queue = _submittingGuestQueue ?? GuestQueueIdentity.Default;
            Console.Error.WriteLine($"[METAL][GUEST_WORK_STAGE] monotonic_ticks={System.Diagnostics.Stopwatch.GetTimestamp()} tick_frequency={System.Diagnostics.Stopwatch.Frequency} stage={stage} queue='{queue.Name}' submission={queue.SubmissionId} sequence={sequence}");
        }
        catch (Exception) { } // diagnostic failures cannot replace execution cause
    }

}
