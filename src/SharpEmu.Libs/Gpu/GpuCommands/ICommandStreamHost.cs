// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Gpu.GpuCommands;

public enum DrawOffsetSource
{
    Packet,
    IndirectArguments,
}

// Arguments of an indexed draw as the packet stream describes them.
public readonly record struct DrawIndexedArguments(
    ulong PacketAddress,
    uint Opcode,
    uint IndexCount,
    ulong IndexAddress,
    uint IndexTypeAndSize,
    uint InstanceCount,
    int BaseVertex,
    uint FirstInstance,
    DrawOffsetSource OffsetSource,
    // Nonzero when the counts are still in guest memory at this address, laid out as
    // (indexCount, instanceCount, firstIndex, vertexOffset, firstInstance). IndexCount is
    // then the index buffer size and IndexAddress its base.
    ulong IndirectArgumentsAddress = 0,
    // The guest set no index buffer size: IndexCount is only the range seen so far and
    // must not clamp the count read from the arguments.
    bool UnboundedIndexBuffer = false);

// Arguments of a non-indexed draw as the packet stream describes them.
public readonly record struct DrawAutoArguments(
    ulong PacketAddress,
    uint Opcode,
    uint VertexCount,
    uint InstanceCount,
    uint FirstVertex,
    uint FirstInstance,
    DrawOffsetSource OffsetSource);

public enum EndOfPipeWriteKind
{
    Write32,
    Write64,
    WriteBack32,
    WriteBack64,
    InterruptOnly,
    Interrupt32,
    Interrupt64,
    InterruptWriteBack32,
    InterruptWriteBack64,
    GdsWrite32,
    ClockWrite,
    ClockWriteBack,
    Flip,
    FlipWithWrite32,
    FlipWithInterruptWriteBack32,
}

// A completion the host records on the current command buffer. The label itself is
// already in guest memory unless the host explicitly defers end-of-pipe labels.
public readonly record struct EndOfPipeWrite(
    EndOfPipeWriteKind Kind,
    ulong SubmitId,
    ulong Destination = 0,
    ulong Value = 0,
    int EventId = 0,
    uint ContextId = 0,
    uint GdsWordOffset = 0,
    uint GdsWordCount = 0,
    int FlipHandle = 0,
    int FlipIndex = 0,
    int FlipMode = 0,
    long FlipArgument = 0,
    ulong FlipRequestId = 0);

// Everything a packet handler needs from the renderer, the caches, the scheduler and video out.
public interface ICommandStreamHost
{
    ICpuMemory Memory { get; }

    // Reads guest memory the GPU may have written; the host synchronizes GPU-owned pages first.
    bool TryReadGuest(ulong address, Span<byte> destination);

    /// <summary>
    /// Reads bytes a wait packet is polling, as memory holds them now.
    /// </summary>
    /// <remarks>
    /// The hardware's command processor reads memory and stalls on the value it finds, so a
    /// poll must not wait for work that is about to write those bytes: an unmet compare
    /// suspends the submission and the host wakes it when the write lands. A host that puts
    /// the write in a command buffer has to make sure that buffer is on its way, or nothing
    /// will ever change the bytes. Hosts whose reads do not wait need no override.
    /// </remarks>
    bool TryPollGuest(ulong address, Span<byte> destination) => TryReadGuest(address, destination);

    // Immediate hosts retain their ordinary memory effects. An ordered host
    // must finish this owned operation before interpreter results are consumed.
    bool TryWriteGuest(ulong address, ReadOnlySpan<byte> source, string kind = "WRITE_BYTES") => Memory.TryWrite(address, source);
    void ExecuteGuestMemoryOperation(Action operation, string kind = "MEMORY_OPERATION", ulong address = 0, ulong bytes = 0, ulong value = 0) => operation();
    void ExecuteCommandMemoryOperation(CommandMemoryOperation operation) =>
        ExecuteGuestMemoryOperation(() => operation.Execute(this), operation.DebugName,
            operation.Address, operation.ByteCount, operation.Value);

    // Runs commands other threads posted to this worker. Called before every packet.
    void RunPendingCommands();

    // Starts a slice of a submission; the geometry snapshots were captured when it was submitted.
    void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots);

    void Flush();

    void FlushAndWait();

    void SynchronizeGpu();

    void RunGarbageCollector();

    void EmitGlobalBarrier();

    void FillBuffer(ulong address, ulong size, uint value, bool isGds);

    void CopyBuffer(ulong destination, ulong source, ulong size, bool destinationIsGds, bool sourceIsGds);

    void ReadGds(Span<uint> destination, uint wordOffset, uint wordCount);

    // A deferred host owns the scalar label payload and must publish its bytes
    // successfully after preceding GPU work, before any interrupt/flip acknowledgement.
    // Other hosts retain the existing immediate interpreter label writes.
    bool DefersEndOfPipeLabels => false;

    /// <summary>Whether this host publishes one kind of label itself, after its work.</summary>
    /// <remarks>
    /// Kinds differ in what the guest does with them. A fence the guest polls before reusing
    /// the memory the work reads has to be published after that work or the reuse races it;
    /// a timestamp is read for its value and not as permission to touch anything.
    /// </remarks>
    bool DefersEndOfPipeLabel(EndOfPipeWriteKind kind) => DefersEndOfPipeLabels;

    void RecordEndOfPipe(in EndOfPipeWrite write);

    // Delivers the interrupt now, without waiting for the GPU.
    void TriggerInterrupt(int eventId, uint contextId);

    bool HasFlipSlot();

    // Reserves and prepares a flip; the request id is never zero.
    ulong PrepareFlip(int handle, int index, int flipMode, long flipArgument);

    // True when no request still targets the buffer, so the stream may render into it.
    bool IsFlipDone(int handle, int index);

    // A video-out export flip reached its place in the graphics queue: capture the buffer now.
    void PrepareCpuFlip(int handle, int index, ulong requestId);

    void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments);

    void DrawAuto(ulong submitId, in DrawAutoArguments arguments);

    void DispatchDirect(ulong submitId, uint groupsX, uint groupsY, uint groupsZ, uint dispatchInitiator, ulong indirectArgumentsAddress = 0);

    // True when the draw or dispatch just handed over could not run because its
    // pipeline is still compiling. The packet then suspends and runs again when the
    // backend reports the pipeline ready; nothing is skipped.
    bool ConsumePipelineNotReady() => false;

    // True when an indirect dispatch in workgroup units reads its group counts on the
    // GPU, so the interpreter need not wait for the GPU to read them back first.
    bool ResolvesIndirectDispatchOnGpu => false;

    // True when an indexed indirect draw may be handed over with its arguments still in
    // guest memory (DrawIndexedArguments.IndirectArgumentsAddress).
    bool ResolvesIndirectDrawOnGpu => false;

    // Called when a queue reset packet clears the processor.
    void OnQueueReset(int queueId);

    Exception Fatal(string message);
}
