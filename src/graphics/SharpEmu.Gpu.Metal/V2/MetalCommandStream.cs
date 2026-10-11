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

            var watch = _guard?.Begin(destination, size, "gds-copy") ?? -1;
            var wrote = _memory.TryWrite(destination, gds.Slice((int)source, (int)size));
            _guard?.End(watch);
            if (!wrote)
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

    // A label is published after the work it reports, as the hardware publishes it. The
    // GPU writes it itself where the packet sits in the stream, so no completion and no
    // flush are needed for the bytes; only an interrupt or a flip, which the host has to
    // deliver, still waits for the shared event — and that completion then also carries
    // the label whenever the GPU could not write it.
    public void RecordEndOfPipe(in EndOfPipeWrite write)
    {
        _endOfPipeRecords++;
        var label = write;
        var eventId = write.EventId;
        var contextId = write.ContextId;
        var requestId = write.FlipRequestId;
        var encoded = TryEncodeEndOfPipeLabel(in write);
        switch (write.Kind)
        {
            case EndOfPipeWriteKind.InterruptOnly:
            case EndOfPipeWriteKind.Interrupt32:
            case EndOfPipeWriteKind.Interrupt64:
            case EndOfPipeWriteKind.InterruptWriteBack32:
            case EndOfPipeWriteKind.InterruptWriteBack64:
                QueueCompletion(() =>
                {
                    if (!encoded) PublishEndOfPipeLabel(in label);
                    TriggerInterrupt(eventId, contextId);
                });
                break;
            case EndOfPipeWriteKind.Flip:
            case EndOfPipeWriteKind.FlipWithWrite32:
                QueueCompletion(() =>
                {
                    if (!encoded) PublishEndOfPipeLabel(in label);
                    VideoOutBackendServices.CompleteFlip(requestId);
                    MetalV2Presenter.SignalFlipPacer();
                });
                break;
            case EndOfPipeWriteKind.FlipWithInterruptWriteBack32:
                QueueCompletion(() =>
                {
                    if (!encoded) PublishEndOfPipeLabel(in label);
                    VideoOutBackendServices.CompleteFlip(requestId);
                    MetalV2Presenter.SignalFlipPacer();
                    TriggerInterrupt(eventId, 0);
                });
                break;
            default:
                // A plain label or clock write has no interrupt behind it, but it is
                // still an end-of-pipe write: the guest polls it to learn that the work
                // recorded before it has finished, and reuses what that work reads once
                // it sees it. The GPU writing it needs nothing further; a label outside
                // every window has to be published by a completion, which costs a flush.
                if (!encoded)
                {
                    QueueCompletion(() => PublishEndOfPipeLabel(in label));
                }

                break;
        }
    }

    /// <summary>
    /// Encodes a label's bytes into the command buffer where its packet sits, so the GPU
    /// writes the guest window itself once it reaches them.
    /// </summary>
    /// <remarks>
    /// This is what the label means: the work recorded before it has finished. Handing the
    /// bytes to a completion says the same thing but costs a command buffer flush per label
    /// and then waits for the host to be told the submission finished, which on a title that
    /// writes a label per render pass was measured at 24 fps down to 9.5. A copy from a
    /// staging ring into the window has neither cost: blit encoders run in the order they
    /// were created, after the encoders of the work the label reports, which is the same
    /// ordering the untracked windows already rely on everywhere else on this host.
    ///
    /// The bytes land when the command buffer is committed, so the label is recorded as GPU
    /// work: a command processor read of those bytes waits for the submission, and the
    /// worker commits what it has before it parks (<see cref="FlushPendingLabelWrites"/>).
    /// </remarks>
    private bool TryEncodeEndOfPipeLabel(in EndOfPipeWrite write)
    {
        var bytes = EndOfPipeLabelBytes(write.Kind);
        if (bytes == 0)
        {
            // A GDS write was copied where the data share lives, and an interrupt or a
            // bare flip carries no payload at all.
            return false;
        }

        if (write.Destination == 0 || (ulong)bytes > ulong.MaxValue - write.Destination)
        {
            throw Fatal(
                $"An end-of-pipe label has an invalid range: address=0x{write.Destination:X16} bytes={bytes}.");
        }

        if (!GpuTimedLabels || !DefersEndOfPipeLabel(write.Kind))
        {
            // The interpreter published these bytes as it parsed the packet.
            return false;
        }

        var destination = _guestMemory.ResolveWritable(write.Destination, (ulong)bytes);
        if (!destination.IsValid)
        {
            Interlocked.Increment(ref _labelsWithoutWindow);
            return false;
        }

        var staging = _guestMemory.Allocate((ulong)bytes, sizeof(ulong), out var payload);
        if (!staging.IsValid)
        {
            return false;
        }

        Span<byte> value = stackalloc byte[sizeof(ulong)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(value, write.Value);
        value[..bytes].CopyTo(payload);

        var context = ((IMetalImageCacheOwner)this).BeginTransfer(MetalTransferCause.EndOfPipeLabel);
        var encoder = MetalNative.Send(context.CommandBuffer, SelBlitEncoder);
        if (encoder == 0)
        {
            throw Fatal("A Metal blit encoder could not be created for an end-of-pipe label.");
        }

        try
        {
            MetalNative.SendCopyBuffer(
                encoder,
                SelCopyBuffer,
                staging.Buffer,
                (nuint)staging.Offset,
                destination.Buffer,
                (nuint)destination.Offset,
                (nuint)bytes);
        }
        finally
        {
            MetalNative.SendVoid(encoder, SelEndEncoding);
        }

        // Beginning the transfer marked this submission as carrying GPU work, which is what
        // keeps a completion queued behind it — the interrupt of this same packet — from
        // running inline ahead of the bytes.
        context.ProfileEncoded($"eop label bytes={bytes}");
        // A command processor polling this label suspends until something says the bytes
        // changed, and the only thing that knows is the completion of this submission. The
        // worker reports completions when it next flushes, which is never while every head
        // is suspended, so one wake per submission is queued here: it costs no flush, and
        // without it a poll waits out the blocked-retry timer instead of a millisecond.
        var serial = context.SubmissionSerial;
        if (_labelWakeSerial != serial)
        {
            _labelWakeSerial = serial;
            _completions.Enqueue(serial, () => _commandStream?.NotifyCompletedGpuTick(serial));
        }

        NoteGuestMemoryWrite(write.Destination, (ulong)bytes, MetalGpuWriteOrigin.EndOfPipeLabel);
        _pendingLabelWrites++;
        Interlocked.Increment(ref _gpuTimedLabels);
        return true;
    }

    private static int EndOfPipeLabelBytes(EndOfPipeWriteKind kind) => kind switch
    {
        EndOfPipeWriteKind.Write32 or EndOfPipeWriteKind.WriteBack32 or
        EndOfPipeWriteKind.Interrupt32 or EndOfPipeWriteKind.InterruptWriteBack32 or
        EndOfPipeWriteKind.FlipWithWrite32 or EndOfPipeWriteKind.FlipWithInterruptWriteBack32 => 4,
        EndOfPipeWriteKind.Write64 or EndOfPipeWriteKind.WriteBack64 or
        EndOfPipeWriteKind.Interrupt64 or EndOfPipeWriteKind.InterruptWriteBack64 or
        EndOfPipeWriteKind.ClockWrite or EndOfPipeWriteKind.ClockWriteBack => 8,
        _ => 0,
    };

    /// <summary>
    /// Commits a command buffer that carries label bytes nothing else will flush.
    /// </summary>
    /// <remarks>
    /// A label written by the GPU lands when its command buffer is committed, and the guest
    /// thread polling it is not something this host can see. The worker calls this when it
    /// runs out of runnable work: a guest waiting on a label is exactly the case where the
    /// stream stops advancing, so one commit per park publishes every label at once instead
    /// of one commit per label.
    /// </remarks>
    private void FlushPendingLabelWrites()
    {
        if (_pendingLabelWrites == 0)
        {
            return;
        }

        FlushCommandBuffer();
    }

    private ulong _pendingLabelWrites;
    private ulong _labelWakeSerial;
    private long _gpuTimedLabels;
    private long _labelsWithoutWindow;

    /// <summary>Labels the GPU wrote itself, and labels with no window to write.</summary>
    public (long GpuTimed, long WithoutWindow) TakeLabelCounters() =>
        (Interlocked.Exchange(ref _gpuTimedLabels, 0), Interlocked.Exchange(ref _labelsWithoutWindow, 0));

    /// <summary>
    /// Writes an end-of-pipe label's bytes into guest memory. The interpreter leaves
    /// them to the host (<see cref="DefersEndOfPipeLabels"/>) because it runs ahead of
    /// the GPU by design: publishing a label when its packet is parsed tells the guest
    /// that work still queued has finished, and the guest then overwrites the constants
    /// and vertex data that work has yet to read. Superliminal's level rendered from
    /// whatever had replaced its view-projection matrix by the time the GPU got to it,
    /// which came out as a degenerate clip position and an empty G-buffer.
    /// </summary>
    private void PublishEndOfPipeLabel(in EndOfPipeWrite write)
    {
        var bytes = EndOfPipeLabelBytes(write.Kind);
        if (bytes == 0 || !DefersEndOfPipeLabel(write.Kind))
        {
            // No payload, or the interpreter already wrote these bytes.
            return;
        }

        if (write.Destination == 0 || (ulong)bytes > ulong.MaxValue - write.Destination)
        {
            throw Fatal(
                $"An end-of-pipe label has an invalid range: address=0x{write.Destination:X16} bytes={bytes}.");
        }

        Span<byte> payload = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(payload, write.Value);
        if (!_memory.TryWrite(write.Destination, payload[..bytes]))
        {
            throw Fatal(
                $"An end-of-pipe label cannot be written to guest memory: " +
                $"address=0x{write.Destination:X16} bytes={bytes}.");
        }
    }

    // SHARPEMU_METAL_V2_GPU_LABELS=0 goes back to publishing every label from the
    // completion that reports its work. That is correct and it is what V1 does, but a
    // completion costs a command buffer flush and then waits to be told the submission
    // finished, so a title that writes a label per render pass pays both per pass:
    // measured at 9.5 fps against 24. The GPU writing the label where its packet sits
    // says the same thing and costs neither, which is why it is the default.
    private static readonly bool GpuTimedLabels =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_GPU_LABELS") != "0";

    /// <summary>
    /// The interpreter must never publish label bytes itself on this host: it parses a
    /// packet long before the GPU runs the work the label reports, and a guest told that
    /// work has finished reuses the memory it still has to read. See
    /// <see cref="TryEncodeEndOfPipeLabel"/> and <see cref="PublishEndOfPipeLabel"/>.
    /// </summary>
    public bool DefersEndOfPipeLabels => !PublishLabelsWhenParsed;

    /// <summary>
    /// A timestamp keeps the value it was sampled with and is published as the packet is
    /// parsed; every other label waits for the work it reports.
    /// </summary>
    /// <remarks>
    /// SHARPEMU_METAL_V2_EOP_CLOCKS=defer treats them like the rest. A GPU clock write is
    /// read for its value — the guest divides by it and prints it — not as permission to
    /// reuse memory, and deferring it only delays whoever reads it.
    /// </remarks>
    public bool DefersEndOfPipeLabel(EndOfPipeWriteKind kind) =>
        DefersEndOfPipeLabels &&
        (DeferClockLabels || kind is not (EndOfPipeWriteKind.ClockWrite or EndOfPipeWriteKind.ClockWriteBack));

    private static readonly bool DeferClockLabels =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_EOP_CLOCKS") == "defer";

    // SHARPEMU_METAL_V2_EOP_IMMEDIATE=1 restores publishing a label as its packet is
    // parsed. It is wrong — it tells the guest that work still queued has finished — and
    // it is here to tell a title that depends on the early value apart from one that
    // merely runs slower without it.
    private static readonly bool PublishLabelsWhenParsed =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_EOP_IMMEDIATE") == "1";

    // The completion runs once the GPU finishes everything recorded so far, so the
    // command buffer is committed right away instead of waiting for the next flush.
    // An end-of-pipe write the guest waits on only has to wait for the GPU work
    // recorded before it. When every submission that carries work has already
    // finished, "after the GPU drains" is true now, and running the completion here
    // saves the guest a whole submission round trip: on this host that is tens of
    // milliseconds even when nothing is wrong, and far more when something is.
    // Ordering is kept by only doing it while no earlier completion is outstanding.
    private static readonly bool InlineCompletions =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_INLINE_EOP") != "0";

    private long _inlineCompletionCount;

    /// <summary>End-of-pipe completions that ran without a GPU round trip.</summary>
    public long TakeInlineCompletions() => Interlocked.Exchange(ref _inlineCompletionCount, 0);

    private void QueueCompletion(Action work)
    {
        if (InlineCompletions && LastWorkSerial <= CompletedSerial && _completions.PendingCount == 0)
        {
            Interlocked.Increment(ref _inlineCompletionCount);
            work();
            return;
        }

        // Nothing has been recorded since the last flush, so everything this completion
        // waits for is in the submission already committed: it rides that one's serial
        // and needs no flush of its own. A run of end-of-pipe labels with no work
        // between them is the common shape, and flushing once per label turned a frame
        // into hundreds of empty submissions.
        var pending = LastWorkSerial;
        var queued = System.Diagnostics.Stopwatch.GetTimestamp();
        if (pending != 0 && pending <= _previousSerial)
        {
            Enqueue(pending);
            return;
        }

        var serial = SubmissionSerial;
        Enqueue(serial);
        FlushCommandBuffer();
        return;

        void Enqueue(ulong on) => _completions.Enqueue(on, () =>
        {
            var ran = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Add(ref _completionLatencyTicks, ran - queued);
            Interlocked.Increment(ref _completionCount);
            ReportSlowCompletion(on, queued, ran);
            work();
        });
    }

    // An end-of-pipe completion the guest is waiting on that took long enough to cost
    // frames. The two halves are what separate the causes: the GPU not having
    // finished the submission, and the host not having noticed that it had.
    private void ReportSlowCompletion(ulong serial, long queued, long ran)
    {
        var frequency = System.Diagnostics.Stopwatch.Frequency;
        if (ran - queued <= frequency / 20 || _slowCompletionReports >= 4)
        {
            return;
        }

        Interlocked.Increment(ref _slowCompletionReports);
        var observed = _completions.LastSignalObservedTimestamp;
        var noticedMs = observed == 0 ? -1.0 : (ran - observed) * 1000.0 / frequency;
        Console.Error.WriteLine(
            $"[METAL-V2][SLOW-EOP] serial={serial} latency_ms={(ran - queued) * 1000.0 / frequency:F1} " +
            $"gpu_ms={(observed == 0 ? -1.0 : (observed - queued) * 1000.0 / frequency):F1} " +
            $"noticed_ms={noticedMs:F1} signalled={_completions.SignaledValue} " +
            $"pending={_completions.PendingCount}");
    }

    private int _slowCompletionReports;

    /// <summary>Resets the per-window budget for slow end-of-pipe reports.</summary>
    public void ResetSlowCompletionReports() => Interlocked.Exchange(ref _slowCompletionReports, 0);

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
    private long _flipsRefreshed;

    internal readonly record struct FlipCounters(
        long Attempts, long WithoutBuffer, long Discarded, long Published, long Refreshed);

    internal FlipCounters TakeFlipCounters() => new(
        Interlocked.Exchange(ref _flipAttempts, 0),
        Interlocked.Exchange(ref _flipsWithoutBuffer, 0),
        Interlocked.Exchange(ref _flipsDiscarded, 0),
        Interlocked.Exchange(ref _flipsPublished, 0),
        Interlocked.Exchange(ref _flipsRefreshed, 0));

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
        // The one point in the frame that is outside a flush, so the safe place to read
        // back everything the passes since the last flip wrote.
        DrainPendingDumps();
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
        // A display surface the GPU just composited into must not be refilled from guest
        // memory on the way to the screen: those bytes are whatever was there before the
        // composite, and uploading them replaces the frame with the old one. Report it
        // when it happens rather than presenting the result silently.
        if (image.IsCpuDirty)
        {
            Interlocked.Increment(ref _flipsRefreshed);
            MetalV2Diagnostics.WarnOnce(
                "flip-refresh",
                $"A flip's display surface was CPU-dirty, so it is uploaded from guest memory before " +
                $"presenting: cause={image.DirtyCause} address=0x{displayBuffer.Address:X16} " +
                $"{displayBuffer.Width}x{displayBuffer.Height} gpuWritten={image.IsGpuModified}.");
        }

        // Before and after, so an upload that replaces a composited frame with the bytes
        // that were in guest memory before it shows up as the two differing.
        if (_dumpThisFlip) { DumpTexture(image.Texture, "flip-before-refresh"); }
        _imageCache.RefreshImage(imageIdentifier);
        if (_dumpThisFlip) { DumpTexture(image.Texture, "flip-after-refresh"); }
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
        ReportFlipSurface(in displayBuffer, in request, imageIdentifier, image, texture);
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
        // The shared phase profile measures the whole draw translation path, which v2
        // runs on this thread exactly as the Vulkan render loop does on its own. A
        // flip is this host's frame boundary, so SHARPEMU_PROFILE_RENDER=1 reports the
        // same breakdown here without any Metal-specific instrumentation.
        SharpEmu.Libs.VideoOut.RenderPhaseProfile.RecordFrame();
    }

    // What the flip actually resolved to. A title whose frames come out black has either
    // drawn nothing or had its flip resolve to a different cached image from the one the
    // render passes wrote, and the flip counters alone cannot tell those apart: this says
    // which image, which view, and whether the GPU ever wrote it.
    private void ReportFlipSurface(
        in VideoOutExports.DisplayBufferInfo displayBuffer,
        in ImageRequest request,
        SharpEmu.Libs.Gpu.Buffers.ResourceSlotIdentifier identifier,
        MetalCachedImage image,
        nint texture)
    {
        MetalV2Diagnostics.InfoOnce(
            "flip-surface",
            $"The first published flip resolved to image={identifier} texture=0x{texture:X} " +
            $"address=0x{displayBuffer.Address:X16} {displayBuffer.Width}x{displayBuffer.Height} " +
            $"guestFormat={displayBuffer.PixelFormat} tiling={displayBuffer.TilingMode} " +
            $"hdr={VideoOutBackendServices.IsHdrPixelFormat(displayBuffer.PixelFormat)} " +
            $"description={image.Description.PixelFormat} tile={image.Description.TileMode} " +
            $"extent={image.Description.Extent.Width}x{image.Description.Extent.Height} " +
            $"view={request.View.Format} gpuWritten={image.IsGpuModified} renderTarget={image.Uses.RenderTarget}.");
        if (_dumpThisFlip) { DumpTexture(texture, "flip"); }
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
        // A title stuck before its first flip never reaches the frame boundary below, and
        // that is the title whose breakdown is wanted: the report is driven from here too.
        SharpEmu.Libs.VideoOut.RenderPhaseProfile.RecordProgress();
        for (var slice = 0; slice < maxSlices; slice++)
        {
            // The shared profile only charges its detail phases inside an enclosing
            // scope, so that cache work on other threads stays out of the render
            // thread's counters. This is the v2 worker's equivalent of the Vulkan
            // render loop's outer scope, and it is what makes
            // SHARPEMU_PROFILE_RENDER=1 attribute the draw path on this backend.
            using var sliceScope = SharpEmu.Libs.VideoOut.RenderPhaseProfile.Measure(
                SharpEmu.Libs.VideoOut.RenderPhaseProfile.Phase.CommandStream);
            var result = stream.ProcessOne();
            switch (result)
            {
                case SliceResult.NoWork:
                    FlushPendingLabelWrites();
                    return;
                case SliceResult.AllBlocked:
                    RetryBlockedIfDue();
                    FlushPendingLabelWrites();
                    return;
                case SliceResult.BlockedWithoutProgress:
                    if (!stream.HasUnblockedPending)
                    {
                        RetryBlockedIfDue();
                        FlushPendingLabelWrites();
                        return;
                    }

                    break;
                default:
                    _lastProgressTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    break;
            }
        }

        // The batch can also end with its slice budget spent, and the worker then parks
        // without coming back through the cases above. A label left in an uncommitted
        // command buffer at that point is one the GPU never writes and the guest waits
        // for: Quake's engine gave up and called abort.
        FlushPendingLabelWrites();
    }

    // The presenter freed the flip slot: retry suspended heads now instead of at the
    // next blocked-retry interval. Runs on the GPU worker thread.
    internal void RetryBlockedFlips()
    {
        CommandStream.NotifyFlipSlotAvailable();
        _lastProgressTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// The GPU finished everything up to this serial, so a compare against bytes a
    /// shader, a fill or an image writeback produced may pass now. Without this a
    /// wait on GPU-written memory would only be retried by the blocked-retry timer.
    /// </summary>
    internal void NotifyGpuCompleted(ulong completedSerial)
    {
        if (_commandStream is { } stream)
        {
            stream.NotifyCompletedGpuTick(completedSerial);
        }
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
