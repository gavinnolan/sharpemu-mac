// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Gpu.Metal;

// The command interpreter host of the Metal backend: managed transfers mirrored into the
// snapshot images, draws and completions posted to the presenter's ordered queue in stream order.
internal sealed partial class MetalCommandStreamHost : AgcExports.TranslatingCommandStreamHost, ICommandSubmissionCompletionHost
{
    private readonly IGuestImageSnapshotBackend _snapshots;
    private readonly IGuestGpuBackend _backend;
    private IDisposable? _queueScope;
    private int _vertexPrefixQueueId;
    private bool _directIndexedVertexPrefix;

    public MetalCommandStreamHost(ICpuMemory memory, IGuestGpuBackend backend, IGuestImageSnapshotBackend snapshots)
        : base(memory)
    {
        MetalVertexPrefixProfile.WarmUp();
        _backend = backend;
        _snapshots = snapshots;
        _context = new CpuContext(memory, Generation.Gen5);
    }

    public override void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots)
    {
        _vertexPrefixQueueId = queueId;
        _queueScope?.Dispose();
        var queueName = queueId == 0 ? "dcb.graphics" : $"acb.compute[{GpuCommandInterpreter.ComputeQueueBase + queueId - 1}]";
        _queueScope = MetalVideoPresenter.EnterGuestQueue(queueName, submissionId);
        base.BeginSubmission(queueId, submissionId, geometrySnapshots);
    }

    public void BeginSubmissionCompletionCapture(int queueId, ulong submissionId) =>
        MetalVideoPresenter.BeginGuestSubmissionPrefixCapture(queueId, submissionId);

    public IGuestSubmissionPrefixCompletion? EndSubmissionCompletionCapture(bool complete) =>
        MetalVideoPresenter.EndGuestSubmissionPrefixCapture(complete);

    // Only this synchronous packet→executor invocation may qualify an indexed prefix.
    // Retained replay and indirect/resolved-indirect draws do not carry that proof.
    public override void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments)
    {
        var previous = _directIndexedVertexPrefix;
        var user = Queue.GetInterpreter(_vertexPrefixQueueId).TypedRegisters.UserConfig;
        _directIndexedVertexPrefix = IsDirectIndexedVertexPrefix(in arguments, user.PrimitiveType, user.PrimitiveResetControl);
        try { base.DrawIndexed(submitId, in arguments); }
        finally { _directIndexedVertexPrefix = previous; }
    }

    private static bool IsDirectIndexedVertexPrefix(in DrawIndexedArguments arguments, uint primitiveType, uint restartControl) =>
        arguments.OffsetSource == DrawOffsetSource.Packet && arguments.IndirectArgumentsAddress == 0 &&
        !arguments.UnboundedIndexBuffer && arguments.IndexTypeAndSize is 0 or 1 && arguments.BaseVertex == 0 &&
        arguments.FirstInstance == 0 && (restartControl & 1) == 0 && primitiveType is 1 or 2 or 3 or 4 or 5 or 6;

    public override void DrawAuto(ulong submitId, in DrawAutoArguments arguments)
    {
        var previous = _directIndexedVertexPrefix;
        _directIndexedVertexPrefix = false;
        try { base.DrawAuto(submitId, in arguments); }
        finally { _directIndexedVertexPrefix = previous; }
    }

    // The global data share lives on the GPU; its transfers queue behind the draws before them.
    public override void FillBuffer(ulong address, ulong size, uint value, bool isGds)
    {
        if (isGds) FillBufferAtQueuePosition(address, size, value, true);
        else ExecuteGuestMemoryOperation(() => FillBufferAtQueuePosition(address, size, value, false), "FILL", address, size, value);
    }

    private void FillBufferAtQueuePosition(ulong address, ulong size, uint value, bool isGds)
    {
        if (isGds)
        {
            if ((address & 3) != 0 || (size & 3) != 0)
            {
                throw Fatal($"The fill range is not dword aligned: address=0x{address:X16} size=0x{size:X}.");
            }

            if (address > GdsBytes || size > (ulong)GdsBytes - address)
            {
                throw Fatal($"The GDS fill range is outside the buffer: offset=0x{address:X} size=0x{size:X}.");
            }

            // A blit fill repeats one byte, so a pattern with unequal bytes is expanded and copied.
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            if (bytes[0] == bytes[1] && bytes[1] == bytes[2] && bytes[2] == bytes[3])
            {
                _ = _snapshots.SubmitGlobalDataShareFill(address, size, bytes[0]);
                return;
            }

            var expanded = new byte[size];
            for (var offset = 0; offset < expanded.Length; offset += sizeof(uint))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(expanded.AsSpan(offset), value);
            }

            _ = _snapshots.SubmitGlobalDataShareCopyFromGuest(address, expanded);
            return;
        }

        base.FillBuffer(address, size, value, isGds);
        MirrorTransferToSnapshotImage(address, size, value);
    }

    public override void CopyBuffer(ulong destination, ulong source, ulong size, bool destinationIsGds, bool sourceIsGds)
    {
        ExecuteGuestMemoryOperation(() => CopyBufferAtQueuePosition(destination, source, size, destinationIsGds, sourceIsGds), "COPY", destination, size, source);
    }

    private void CopyBufferAtQueuePosition(ulong destination, ulong source, ulong size, bool destinationIsGds, bool sourceIsGds)
    {
        if (size == 0)
        {
            return;
        }

        if (destinationIsGds && sourceIsGds)
        {
            throw Fatal($"A GDS-to-GDS copy is not supported: destination=0x{destination:X} source=0x{source:X} size=0x{size:X}.");
        }

        if ((destinationIsGds && (destination > GdsBytes || size > (ulong)GdsBytes - destination)) ||
            (sourceIsGds && (source > GdsBytes || size > (ulong)GdsBytes - source)) ||
            (!destinationIsGds && destination == 0) ||
            (!sourceIsGds && source == 0))
        {
            throw Fatal($"The copy range is invalid: destination=0x{destination:X16} source=0x{source:X16} size=0x{size:X} dstGds={destinationIsGds} srcGds={sourceIsGds}.");
        }

        if (destinationIsGds)
        {
            var bytes = new byte[checked((int)size)];
            if (!Memory.TryRead(source, bytes))
            {
                throw Fatal($"The copy cannot read guest memory: address=0x{source:X16} size=0x{size:X}.");
            }

            _ = _snapshots.SubmitGlobalDataShareCopyFromGuest(destination, bytes);
            return;
        }

        if (sourceIsGds)
        {
            _ = _snapshots.SubmitGlobalDataShareCopyToGuest(destination, source, size);
            return;
        }

        base.CopyBuffer(destination, source, size, destinationIsGds, sourceIsGds);
        MirrorTransferToSnapshotImage(destination, size, null);
    }

    // Reached only after SynchronizeGpu, so the shared buffer holds every queued transfer and shader write.
    public override void ReadGds(Span<uint> destination, uint wordOffset, uint wordCount) =>
        _snapshots.ReadGlobalDataShare(destination, wordOffset, wordCount);

    // A transfer that covers a whole snapshot image replaces that image's pixels.
    private void MirrorTransferToSnapshotImage(ulong destination, ulong byteCount, uint? fillValue)
    {
        if (!_snapshots.TryGetGuestImageExtent(destination, out _, out _, out var imageBytes) ||
            imageBytes == 0 || byteCount < imageBytes)
        {
            return;
        }

        if (fillValue is { } fill)
        {
            _snapshots.SubmitGuestImageFill(destination, fill);
            return;
        }

        var pixels = new byte[imageBytes];
        if (Memory.TryRead(destination, pixels))
        {
            _snapshots.SubmitGuestImageWrite(destination, pixels);
        }
    }

    public override bool DefersEndOfPipeLabels => true;

    // One scalar payload owns label+acknowledgement at its logical queue position.
    public override void RecordEndOfPipe(in EndOfPipeWrite write)
    {
        var completion = write;
        var sequence = MetalVideoPresenter.SubmitOrderedGuestCompletion(() =>
        {
            MetalVideoPresenter.RequireGuestCompletionHealthy();
            MetalVideoPresenter.TraceCommandMemoryEop("executing", completion);
            WriteDeferredEndOfPipeLabel(in completion);
            MetalVideoPresenter.RequireGuestCompletionHealthy();
            base.RecordEndOfPipe(in completion);
            MetalVideoPresenter.TraceCommandMemoryEop("acknowledged", completion);
        }, $"command_stream completion {write.Kind}");
        MetalVideoPresenter.TraceCommandMemoryEop("admitted", completion, sequence);
    }

    public override ulong PrepareFlip(int handle, int index, int flipMode, long flipArgument)
    {
        var requestId = base.PrepareFlip(handle, index, flipMode, flipArgument);
        if (VideoOutBackendServices.TryGetDisplayBufferInfo(handle, index, out var displayBuffer))
        {
            _ = MetalVideoPresenter.TrySubmitOrderedGuestImageFlip(
                handle,
                index,
                displayBuffer.Address,
                displayBuffer.Width,
                displayBuffer.Height,
                displayBuffer.PitchInPixel);
        }

        return requestId;
    }

    // A video-out export flip at its place in the graphics queue; the capture counts as presented.
    public override void PrepareCpuFlip(int handle, int index, ulong requestId)
    {
        if (VideoOutBackendServices.TryGetDisplayBufferInfo(handle, index, out var displayBuffer))
        {
            _ = MetalVideoPresenter.TrySubmitOrderedGuestImageFlip(
                handle,
                index,
                displayBuffer.Address,
                displayBuffer.Width,
                displayBuffer.Height,
                displayBuffer.PitchInPixel);
        }

        VideoOutBackendServices.MarkFlipPresented(requestId);
    }

    // The ordered queue runs every earlier record and then waits for the GPU to complete them.
    public override void SynchronizeGpu()
    {
        long sequence;
        try { sequence = _snapshots.SubmitGpuSynchronization("command_stream synchronize"); }
        catch (Exception exception) when (exception is not SharpEmu.Libs.Gpu.Scheduling.GpuFatalException)
        { throw MetalVideoPresenter.FailGuestCompletion("command_stream synchronize admission failed", exception); }
        if (sequence == 0)
            MetalVideoPresenter.RequireEmptyGuestCompletion("command_stream synchronize was not admitted");
        else if (!MetalVideoPresenter.WaitForGuestWork(sequence, Timeout.Infinite))
            throw MetalVideoPresenter.FailGuestCompletion("command_stream synchronize did not complete");
    }
}
