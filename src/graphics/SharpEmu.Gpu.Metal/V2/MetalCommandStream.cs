// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// The command-stream half of the render host: the packet interpreter's view of the
/// backend. The command translation layer builds its RenderExecutor and
/// ShaderPipelineCache only when one object implements ICommandStreamHost,
/// IRenderHost and IShaderPipelineHost at once, which is why this is a partial of
/// MetalRenderHost rather than a type of its own.
///
/// Everything here runs on the GPU worker thread. Completion is a shared event:
/// a committed command buffer signals its serial, and the completion thread runs
/// the interrupts and flip completions of that submission in order.
/// </summary>
internal sealed partial class MetalRenderHost : ICommandStreamHost
{
    private CommandStreamQueue? _commandStream;
    private AgcExports.CommandStreamTranslation? _translation;
    private IMetalFlipTarget? _flipTarget;
    private long _lastProgressTicks;
    private ulong _submissionsStarted;
    private ulong _endOfPipeRecords;
    private ulong _frameBoundaries;

    private static readonly long _blockedRetryTicks =
        CommandStreamQueue.AllBlockedRetryMilliseconds * System.Diagnostics.Stopwatch.Frequency / 1000L;

    public CommandStreamQueue CommandStream =>
        _commandStream ?? throw Fatal("The Metal command stream has not been created.");

    /// <summary>Creates the interpreter queue and the draw translation over this host.</summary>
    public void InitializeCommandStream(IMetalFlipTarget flipTarget)
    {
        _flipTarget = flipTarget;
        _commandStream = new CommandStreamQueue(this);
        _translation = AgcBackendServices.CreateCommandStreamTranslation(_memory, this);
        _lastProgressTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    ICpuMemory ICommandStreamHost.Memory => _memory;

    public void RunPendingCommands() => _relay.RunPendingCommands();

    public void BeginSubmission(int queueId, ulong submissionId, object? geometrySnapshots)
    {
        _submissionsStarted++;
        _ = EnsureCommandBuffer();
        _translation!.BeginSubmission(queueId, submissionId, geometrySnapshots, CommandStream.GetInterpreter(queueId));
    }

    void ICommandStreamHost.Flush()
    {
        _frameBoundaries++;
        FlushCommandBuffer();
    }

    void ICommandStreamHost.FlushAndWait() => FlushAndWaitForGpu(MetalWaitReason.CommandProcessor);

    public void SynchronizeGpu()
    {
        _imageCache.FlushAllGpuWrites();
        FlushAndWaitForGpu(MetalWaitReason.Synchronize);
    }

    public void RunGarbageCollector() => _imageCache.Collect();

    public void FillBuffer(ulong address, ulong size, uint value, bool isGds)
    {
        if (size == 0)
        {
            return;
        }

        if (isGds)
        {
            if (address > MetalGuestMemory.GdsBytes || size > MetalGuestMemory.GdsBytes - address)
            {
                throw Fatal($"The GDS fill range is outside the buffer: offset=0x{address:X} size=0x{size:X}.");
            }

            // The data share is a shared-storage buffer, so the host fill is visible to
            // the GPU without a transfer; a shader write to it is ordered by Metal.
            SynchronizeGpu();
            var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                _guestMemory.GdsWords().Slice((int)address, (int)size));
            words.Fill(value);
            return;
        }

        FillGuestMemory(address, size, value);
    }

    public void CopyBuffer(ulong destination, ulong source, ulong size, bool destinationIsGds, bool sourceIsGds)
    {
        if (size == 0)
        {
            return;
        }

        if (destinationIsGds && sourceIsGds)
        {
            throw Fatal($"A GDS-to-GDS copy is not supported: destination=0x{destination:X} source=0x{source:X} size=0x{size:X}.");
        }

        if (!destinationIsGds && !sourceIsGds)
        {
            CopyGuestMemory(destination, source, size);
            return;
        }

        SynchronizeGpu();
        var gds = _guestMemory.GdsWords();
        if (sourceIsGds)
        {
            if (source > MetalGuestMemory.GdsBytes || size > MetalGuestMemory.GdsBytes - source)
            {
                throw Fatal($"The GDS copy source is outside the buffer: offset=0x{source:X} size=0x{size:X}.");
            }

            if (!_memory.TryWrite(destination, gds.Slice((int)source, (int)size)))
            {
                throw Fatal($"The GDS copy cannot write guest memory: address=0x{destination:X16} size=0x{size:X}.");
            }

            _imageCache.InvalidateMemory(destination, size);
            return;
        }

        if (destination > MetalGuestMemory.GdsBytes || size > MetalGuestMemory.GdsBytes - destination)
        {
            throw Fatal($"The GDS copy destination is outside the buffer: offset=0x{destination:X} size=0x{size:X}.");
        }

        if (!TryReadGuest(source, gds.Slice((int)destination, (int)size)))
        {
            throw Fatal($"The GDS copy cannot read guest memory: address=0x{source:X16} size=0x{size:X}.");
        }
    }

    public void ReadGds(Span<uint> destination, uint wordOffset, uint wordCount)
    {
        SynchronizeGpu();
        GlobalDataShare.ReadWords(_guestMemory.GdsWords(), destination, wordOffset, wordCount);
    }

    // The interpreter writes the label itself; only the interrupt and the flip
    // completion of a submission wait for the GPU, which is what the shared event
    // orders. A completion is therefore always paired with a flush.
    public void RecordEndOfPipe(in EndOfPipeWrite write)
    {
        _endOfPipeRecords++;
        var eventId = write.EventId;
        var contextId = write.ContextId;
        var requestId = write.FlipRequestId;
        switch (write.Kind)
        {
            case EndOfPipeWriteKind.InterruptOnly:
            case EndOfPipeWriteKind.Interrupt32:
            case EndOfPipeWriteKind.Interrupt64:
            case EndOfPipeWriteKind.InterruptWriteBack32:
            case EndOfPipeWriteKind.InterruptWriteBack64:
                QueueCompletion(() => TriggerInterrupt(eventId, contextId));
                break;
            case EndOfPipeWriteKind.Flip:
            case EndOfPipeWriteKind.FlipWithWrite32:
                QueueCompletion(() =>
                {
                    VideoOutBackendServices.CompleteFlip(requestId);
                    MetalV2Presenter.SignalFlipPacer();
                });
                break;
            case EndOfPipeWriteKind.FlipWithInterruptWriteBack32:
                QueueCompletion(() =>
                {
                    VideoOutBackendServices.CompleteFlip(requestId);
                    MetalV2Presenter.SignalFlipPacer();
                    TriggerInterrupt(eventId, 0);
                });
                break;
            default:
                // A plain label write, a clock write or a GDS write has no GPU
                // completion: the interpreter already published its bytes.
                break;
        }
    }

    // The completion runs once the GPU finishes everything recorded so far, so the
    // command buffer is committed right away instead of waiting for the next flush.
    private void QueueCompletion(Action work)
    {
        var serial = SubmissionSerial;
        var queued = System.Diagnostics.Stopwatch.GetTimestamp();
        _completions.Enqueue(serial, () =>
        {
            Interlocked.Add(ref _completionLatencyTicks, System.Diagnostics.Stopwatch.GetTimestamp() - queued);
            Interlocked.Increment(ref _completionCount);
            work();
        });
        FlushCommandBuffer();
    }

    private long _completionLatencyTicks;
    private long _completionCount;

    /// <summary>How long end-of-pipe completions waited for the GPU since the last read.</summary>
    public (long Count, double Milliseconds) TakeCompletionLatency()
    {
        var count = Interlocked.Exchange(ref _completionCount, 0);
        var ticks = Interlocked.Exchange(ref _completionLatencyTicks, 0);
        return (count, ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }

    private readonly EndOfPipeEvents _endOfPipeEvents = new();

    public void TriggerInterrupt(int eventId, uint contextId) =>
        _endOfPipeEvents.TriggerInterrupt(eventId, contextId);

    public bool HasFlipSlot() => _flipTarget?.HasFlipSlot() ?? true;

    // Where each flip this interval ended up. Every flip that reaches CaptureFlip counts
    // as an attempt and then lands in exactly one of the three outcomes, so attempts
    // staying at zero means the guest never asked for a flip at all.
    private long _flipAttempts;
    private long _flipsWithoutBuffer;
    private long _flipsDiscarded;
    private long _flipsPublished;

    internal readonly record struct FlipCounters(long Attempts, long WithoutBuffer, long Discarded, long Published);

    internal FlipCounters TakeFlipCounters() => new(
        Interlocked.Exchange(ref _flipAttempts, 0),
        Interlocked.Exchange(ref _flipsWithoutBuffer, 0),
        Interlocked.Exchange(ref _flipsDiscarded, 0),
        Interlocked.Exchange(ref _flipsPublished, 0));

    public ulong PrepareFlip(int handle, int index, int flipMode, long flipArgument)
    {
        var result = VideoOutBackendServices.TryReserveFlipRequest(
            handle, index, flipMode, flipArgument, gpuQueued: true, out var requestId);
        if (result != 0)
        {
            throw Fatal(
                $"Could not submit the GPU flip: result=0x{result:X8} handle={handle} index={index} mode={flipMode} arg={flipArgument}.");
        }

        if (requestId == 0)
        {
            throw Fatal("The GPU flip submission returned a request id of zero.");
        }

        try
        {
            CaptureFlip(handle, index, requestId, flipMode, flipArgument);
        }
        catch
        {
            VideoOutBackendServices.CancelFlip(requestId);
            throw;
        }

        return requestId;
    }

    public bool IsFlipDone(int handle, int index) => VideoOutBackendServices.IsFlipDone(handle, index);

    public void PrepareCpuFlip(int handle, int index, ulong requestId) =>
        CaptureFlip(handle, index, requestId, flipMode: 0, flipArg: 0);

    // The display surface is a cached image like any other; the flip hands its
    // MTLTexture to the presenter, so no pixel ever travels through the CPU.
    // Where a flip stops before it reaches the presenter. A title that shows a
    // frame while guest_fps stays zero is presenting through the CPU path, and
    // these say which step dropped the GPU one.
    internal static long FlipsCaptured;
    internal static long FlipsWithoutBuffer;
    internal static long FlipsDiscarded;
    internal static long FlipsPublished;

    private void CaptureFlip(int handle, int index, ulong requestId, int flipMode, long flipArg)
    {
        Interlocked.Increment(ref _flipAttempts);
        Interlocked.Increment(ref FlipsCaptured);
        if (VideoOutBackendServices.ReleaseNoBufferFlip(index, requestId))
        {
            // The guest flipped without naming a new buffer. Legitimate on its own, but a
            // title whose every flip goes this way never publishes a frame, and that used
            // to look exactly like no flip arriving at all.
            Interlocked.Increment(ref _flipsWithoutBuffer);
            Interlocked.Increment(ref FlipsWithoutBuffer);
            MetalV2Diagnostics.InfoOnce(
                "flip-no-buffer",
                $"A flip named no display buffer and was released without a frame: " +
                $"handle={handle} index={index} request={requestId}.");
            return;
        }

        if (!VideoOutBackendServices.TryGetDisplayBufferInfo(handle, index, out var displayBuffer))
        {
            throw Fatal(
                $"The flip names a display buffer that is not registered: handle={handle} index={index} request={requestId}.");
        }

        var surface = new DisplaySurfaceWords(
            displayBuffer.Address,
            0,
            displayBuffer.PixelFormat,
            displayBuffer.Width,
            displayBuffer.Height,
            displayBuffer.TilingMode,
            displayBuffer.Option,
            0,
            0,
            false);
        var request = ImageRequestBuilders.DisplaySurface(surface);
        var imageIdentifier = _imageCache.FindImage(ref request);
        var image = _imageCache.GetImage(imageIdentifier);
        image.Uses.VideoOut = true;
        _imageCache.RefreshImage(imageIdentifier);
        var texture = PresentationViewOf(image, in request);
        var serial = SubmissionSerial;
        FlushCommandBuffer();
        if (_flipTarget is null || texture == 0)
        {
            // The display surface resolved to no presentable texture, so there is nothing
            // to hand the presenter. Silent until now, which made it indistinguishable
            // from a title that never flips at all.
            Interlocked.Increment(ref _flipsDiscarded);
            Interlocked.Increment(ref FlipsDiscarded);
            MetalV2Diagnostics.WarnOnce(
                "flip-discarded",
                $"A flip had no presentable texture and was discarded: handle={handle} index={index} " +
                $"request={requestId} target={(_flipTarget is null ? "none" : "present")} " +
                $"address=0x{displayBuffer.Address:X16} {displayBuffer.Width}x{displayBuffer.Height} " +
                $"format={displayBuffer.PixelFormat} tiling={displayBuffer.TilingMode} " +
                $"view={request.View.Format}.");
            VideoOutBackendServices.DiscardFlip(requestId);
            return;
        }

        Interlocked.Increment(ref _flipsPublished);
        Interlocked.Increment(ref FlipsPublished);
        _flipTarget.PublishGuestFrame(
            texture,
            Math.Max(displayBuffer.Width, 1),
            Math.Max(displayBuffer.Height, 1),
            requestId,
            serial,
            VideoOutBackendServices.IsHdrPixelFormat(displayBuffer.PixelFormat));
        VideoOutBackendServices.TraceGpuFlip(
            _memory, handle, index, flipMode, flipArg, displayBuffer.Address,
            VideoOutBackendServices.GetFlipEventCount(requestId));
    }

    // The presenter samples the flip texture and writes the result into a plain
    // BGRA8 drawable, so an sRGB surface has to be read through a linear view:
    // sampling it as sRGB would convert to linear and show the frame far too dark.
    private nint PresentationViewOf(MetalCachedImage image, in ImageRequest request)
    {
        var view = request.View;
        var linear = MetalV2Formats.LinearCounterpart(view.Format);
        if (linear != view.Format)
        {
            view = view with { Format = linear };
        }

        return image.GetOrCreateView(view);
    }

    public void DrawIndexed(ulong submitId, in DrawIndexedArguments arguments) =>
        _translation!.DrawIndexed(submitId, in arguments);

    public void DrawAuto(ulong submitId, in DrawAutoArguments arguments) =>
        _translation!.DrawAuto(submitId, in arguments);

    public void DispatchDirect(
        ulong submitId,
        uint groupsX,
        uint groupsY,
        uint groupsZ,
        uint dispatchInitiator,
        ulong indirectArgumentsAddress = 0) =>
        _translation!.Dispatch(submitId, groupsX, groupsY, groupsZ, dispatchInitiator, indirectArgumentsAddress);

    public bool ResolvesIndirectDispatchOnGpu => true;

    public bool ResolvesIndirectDrawOnGpu => true;

    public void OnQueueReset(int queueId) => _translation!.QueueReset(queueId);

    /// <summary>
    /// Runs interpreter slices until nothing is runnable or the budget ends; blocked
    /// heads retry on the shared interval, exactly as the Vulkan worker does.
    /// </summary>
    public void RunCommandStreamSlices(int maxSlices)
    {
        var stream = CommandStream;
        for (var slice = 0; slice < maxSlices; slice++)
        {
            var result = stream.ProcessOne();
            switch (result)
            {
                case SliceResult.NoWork:
                    return;
                case SliceResult.AllBlocked:
                    RetryBlockedIfDue();
                    return;
                case SliceResult.BlockedWithoutProgress:
                    if (!stream.HasUnblockedPending)
                    {
                        RetryBlockedIfDue();
                        return;
                    }

                    break;
                default:
                    _lastProgressTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    break;
            }
        }
    }

    // The presenter freed the flip slot: retry suspended heads now instead of at the
    // next blocked-retry interval. Runs on the GPU worker thread.
    internal void RetryBlockedFlips()
    {
        CommandStream.RetryBlocked();
        _lastProgressTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private void RetryBlockedIfDue()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _lastProgressTicks < _blockedRetryTicks)
        {
            return;
        }

        CommandStream.RetryBlocked();
        _lastProgressTicks = now;
    }

    public ulong SubmissionsStarted => _submissionsStarted;
}

// What a flip needs from the presenter: a slot check and the published frame.
internal interface IMetalFlipTarget
{
    bool HasFlipSlot();

    // The texture is presented once the submission serial completes; the presenter
    // retains it until the frame it belongs to has been shown.
    void PublishGuestFrame(nint texture, uint width, uint height, ulong requestId, ulong serial, bool isHdr);
}
