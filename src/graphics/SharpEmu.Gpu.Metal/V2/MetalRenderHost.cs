// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// The Metal v2 render host: one object behind the three shared contracts the
/// engine drives it with — <see cref="IRenderHost"/> (RenderExecutor),
/// <see cref="IShaderPipelineHost"/> (ShaderPipelineCache) and, in
/// MetalCommandStream.cs, <c>ICommandStreamHost</c> (CommandStreamQueue). The
/// command translation layer requires one object for all three, which is why this
/// type spans those files, exactly as the Vulkan <c>Presenter</c> does.
///
/// Everything records on the GPU worker thread straight into the current
/// MTLCommandBuffer. Metal binds resources per encoder while the shared executor
/// records them before the render pass opens, so the host buffers the pending draw
/// state in fixed fields and applies it to the encoder at draw time. No hot path
/// allocates.
/// </summary>
internal sealed partial class MetalRenderHost :
    IRenderHost, IShaderPipelineHost, IMetalImageCacheOwner, IMetalSubmissionContext, IImageFormatSupport, IDisposable
{
    private const uint MaxFramebufferDimension = 16384;

    private static readonly nint SelCommandBuffer = MetalNative.Selector("commandBuffer");
    private static readonly nint SelCommit = MetalNative.Selector("commit");
    private static readonly nint SelEndEncoding = MetalNative.Selector("endEncoding");
    private static readonly nint SelWaitUntilCompleted = MetalNative.Selector("waitUntilCompleted");
    private static readonly nint SelEncodeSignalEvent = MetalNative.Selector("encodeSignalEvent:value:");
    private static readonly nint SelAddCompletedHandler = MetalNative.Selector("addCompletedHandler:");
    private static readonly nint SelSignaledValue = MetalNative.Selector("signaledValue");
    private static readonly nint SelNewSharedEvent = MetalNative.Selector("newSharedEvent");
    private static readonly nint SelBlitEncoder = MetalNative.Selector("blitCommandEncoder");
    private static readonly nint SelComputeEncoder = MetalNative.Selector("computeCommandEncoder");
    private static readonly nint SelFillBuffer = MetalNative.Selector("fillBuffer:range:value:");
    private static readonly nint SelCopyBuffer =
        MetalNative.Selector("copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:");
    private static readonly nint SelSetLabel = MetalNative.Selector("setLabel:");
    private static readonly nint SelRelease = MetalNative.Selector("release");

    private readonly nint _device;
    private readonly nint _queue;
    private readonly nint _sharedEvent;
    private readonly ICpuMemory _memory;
    private readonly IGuestBackedSpace _backing;
    private readonly MetalGuestMemory _guestMemory;
    private readonly MetalImageCache _imageCache;
    private readonly IMetalImageTransfer _transfer;
    private readonly MetalGpuWorkerRelay _relay;
    private readonly MetalCompletionQueue _completions;

    private nint _commandBuffer;
    private nint _previousCommandBuffer;
    private ulong _previousSerial;
    private long _failedSubmissions;
    private double _gpuBusySeconds;
    private long _gpuBusySamples;
    private ulong _submissionSerial;
    private ulong _nextSerial;
    private long _recordedCommands;
    // The guest byte ranges recorded work may still be writing, with the submission
    // serial that finishes each. A CPU read waits only when it overlaps one whose
    // serial has not completed, and then only for that serial.
    private readonly MetalGpuWriteRanges _gpuWrites = new();

    // SHARPEMU_METAL_V2_GUARD_WRITES=1 only: checks that every declared GPU-side write
    // leaves the bytes either side of its range alone.
    private readonly MetalGuestWriteGuard? _guard;
    // Without ranges every write is "somewhere in guest memory", which is what the
    // first version of this host did; kept for A/B measurement.
    private static readonly bool ConservativeReads =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_CONSERVATIVE_READS") == "1";

    // Waits in -[MTLCommandBuffer waitUntilCompleted] instead of on the shared event
    // the submission signals. Measured slower on this host: the driver still took
    // 100 ms and more to schedule the submission, and the fps halved, so the host
    // waits on the event and this is kept only for comparison.
    private static readonly bool WaitOnCommandBuffer =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_WAIT_ON_COMMAND_BUFFER") == "1";
    private long _drawCount;
    private long _drawTicks;
    private long _pipelineCreations;
    private long _shaderCompilations;
    private long _flushes;
    private long _gpuWaits;
    private long _partialWaits;
    private long _gpuWaitTicks;
    private long _idleTicks;
    private long _statsTimestamp;
    private bool _disposed;

    public MetalRenderHost(
        nint device,
        nint queue,
        ICpuMemory memory,
        IGuestBackedSpace backing,
        PageGuard pages,
        MetalGpuWorkerRelay relay)
    {
        _device = device;
        _queue = queue;
        _memory = memory;
        _backing = backing;
        _relay = relay;
        _sharedEvent = MetalNative.Send(device, SelNewSharedEvent);
        if (_sharedEvent == 0)
        {
            throw GpuFatal.Fatal("Metal v2 needs an MTLSharedEvent for submission completion.");
        }

        _guard = MetalGuestWriteGuard.Enabled ? new MetalGuestWriteGuard(memory) : null;
        _guestMemory = new MetalGuestMemory(device, backing, memory);
        _guestMemory.AttachToQueue(queue);
        _transfer = new MetalImageTransfer(device);
        _imageCache = new MetalImageCache(this, _transfer, _guestMemory, pages, backing);
        _completions = new MetalCompletionQueue(_sharedEvent);
    }

    public MetalGuestMemory GuestMemory => _guestMemory;

    public MetalImageCache ImageCache => _imageCache;

    public MetalCompletionQueue Completions => _completions;

    public MetalGpuWorkerRelay Relay => _relay;

    nint IMetalSubmissionContext.Device => _device;

    nint IMetalImageCacheOwner.Device => _device;

    ulong IMetalImageCacheOwner.CurrentSerial => SubmissionSerial;

    public nint CommandBuffer => EnsureCommandBuffer();

    public ulong SubmissionSerial
    {
        get
        {
            _ = EnsureCommandBuffer();
            return _submissionSerial;
        }
    }

    public ulong CompletedSerial => MetalV2Native.SendValue64(_sharedEvent, SelSignaledValue);

    public RenderHostLimits Limits { get; } =
        new(MaxFramebufferDimension, MaxFramebufferDimension, MaxFramebufferDimension, MaxFramebufferDimension);

    public IImageFormatSupport FormatSupport => this;

    public uint MaxPushDescriptors => 0;

    // Metal threadgroups are always 32-wide on Apple silicon; a wave64 compute
    // program is emitted as two wave32 halves by the translator.
    public bool ComputeWave64Supported => false;

    public bool GraphicsSubgroupOperationsEnabled => false;

    public bool SharedInt64AtomicsEnabled => false;

    public GpuSampleCount NoAttachmentSampleCounts => GpuSampleCount.Count1Bit;

    public bool IsRecording => !_disposed && EnsureCommandBuffer() != 0;

    /// <summary>The command buffer being recorded; one is opened on demand.</summary>
    private nint EnsureCommandBuffer()
    {
        if (_commandBuffer != 0)
        {
            return _commandBuffer;
        }

        _commandBuffer = MetalNative.Send(_queue, SelCommandBuffer);
        if (_commandBuffer == 0)
        {
            throw GpuFatal.Fatal("A Metal command buffer could not be created.");
        }

        // Retain it: the queue hands back an autoreleased buffer and nothing here
        // runs inside an autorelease pool the whole time it is being recorded.
        MetalNative.Send(_commandBuffer, MetalNative.Selector("retain"));
        _submissionSerial = ++_nextSerial;
        var completed = CompletedSerial;
        _guestMemory.BeginSubmission(_submissionSerial, completed);
        // Every watch whose submission the GPU has finished is checked here, on the one
        // thread that records them, rather than from the completion handler.
        _guard?.Verify(completed);
        return _commandBuffer;
    }

    // Everything the cache and the transfers encode needs the encoders closed first.
    IMetalSubmissionContext IMetalImageCacheOwner.BeginTransfer()
    {
        _ = EnsureCommandBuffer();
        NoteRecordedWork();
        EndEncoders();
        NoteProfileTransfer();
        return this;
    }

    // The newest submission that has GPU work in it. An end-of-pipe completion only
    // has to wait for the GPU while this is ahead of what the GPU has finished.
    private ulong _lastWorkSerial;

    /// <summary>Marks the submission being recorded as carrying GPU work.</summary>
    public void NoteRecordedWork() => _lastWorkSerial = _submissionSerial;

    /// <summary>The newest submission that carries GPU work.</summary>
    public ulong LastWorkSerial => _lastWorkSerial;

    /// <summary>Ends whatever encoder is open; the next record opens the one it needs.</summary>
    private void EndEncoders()
    {
        if (_renderEncoder != 0)
        {
            _passEndTransfer++;
            EndRendering();
        }

        if (_computeEncoder != 0)
        {
            MetalNative.SendVoid(_computeEncoder, SelEndEncoding);
            _computeEncoder = 0;
            _boundComputePipeline = 0;
        }
    }

    /// <summary>Signals the shared event, commits, and opens no new command buffer.</summary>
    public ulong FlushCommandBuffer()
    {
        if (_commandBuffer == 0)
        {
            return _nextSerial;
        }

        EndEncoders();
        var serial = _submissionSerial;
        MetalV2Native.SendEventValue(_commandBuffer, SelEncodeSignalEvent, _sharedEvent, serial);
        if (_completions.WantsCompletionHandler)
        {
            // The handler is a global block with no state: it only wakes whoever waits
            // on the shared event, which the signal above has already advanced.
            MetalNative.SendVoid(_commandBuffer, SelAddCompletedHandler, MetalCompletionSignal.Block);
        }

        MetalNative.SendVoid(_commandBuffer, SelCommit);
        _previousCommitTime = CACurrentMediaTime();
        _lastCommitTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        // A command buffer that fails never signals its serial, and the completion
        // queue would then wait out its timeout on every submission behind it, so
        // every submission's status is read once it has finished.
        DrainFinishedSubmissions();
        ReportStepCaps();
        RetainInFlight(_commandBuffer, serial);
        _previousCommandBuffer = _commandBuffer;
        _previousSerial = serial;
        _commandBuffer = 0;
        _recordedCommands = 0;
        Interlocked.Increment(ref _flushes);
        _completions.Publish(serial);
        return serial;
    }

    // MTLCommandBufferStatus.
    private const nuint CommandBufferStatusCompleted = 4;
    private const nuint CommandBufferStatusError = 5;

    private static readonly nint SelGpuStartTime = MetalNative.Selector("GPUStartTime");
    private static readonly nint SelGpuEndTime = MetalNative.Selector("GPUEndTime");
    private static readonly nint SelKernelStartTime = MetalNative.Selector("kernelStartTime");
    private static readonly nint SelKernelEndTime = MetalNative.Selector("kernelEndTime");
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/QuartzCore.framework/QuartzCore")]
    private static extern double CACurrentMediaTime();

    private int _slowWaitReports;
    private double _previousCommitTime;
    private ulong _lastReadAddress;
    private ulong _lastReadSize;
    private readonly long[] _waitsByReason = new long[MetalWaitReasons.Count];
    private readonly long[] _waitTicksByReason = new long[MetalWaitReasons.Count];

    // Committed submissions whose GPU time has not been read yet. Sampling only the
    // one buffer that happened to be finished at the next flush missed almost every
    // submission while any were in flight, which is why a title whose GPU was busy
    // for a hundred milliseconds a frame could report gpu_busy_ms=0.0 and look as
    // though it were waiting on nothing.
    private const int MaxInFlightSubmissions = 64;

    private readonly nint[] _inFlightBuffers = new nint[MaxInFlightSubmissions];
    private readonly ulong[] _inFlightSerials = new ulong[MaxInFlightSubmissions];
    private int _inFlightHead;
    private int _inFlightCount;
    private long _unsampledSubmissions;
    private ulong _notifiedCompletedSerial;

    private void RetainInFlight(nint buffer, ulong serial)
    {
        if (_inFlightCount == MaxInFlightSubmissions)
        {
            // The oldest has not finished and the ring is full: let it go unsampled
            // rather than block the worker on it.
            var oldest = _inFlightBuffers[_inFlightHead];
            _inFlightHead = (_inFlightHead + 1) % MaxInFlightSubmissions;
            _inFlightCount--;
            _unsampledSubmissions++;
            MetalNative.SendVoid(oldest, SelRelease);
        }

        var slot = (_inFlightHead + _inFlightCount) % MaxInFlightSubmissions;
        _inFlightBuffers[slot] = buffer;
        _inFlightSerials[slot] = serial;
        _inFlightCount++;
    }

    /// <summary>Reads and releases every submission the GPU has finished.</summary>
    private void DrainFinishedSubmissions()
    {
        if (_inFlightCount == 0 && _notifiedCompletedSerial >= _nextSerial)
        {
            return;
        }

        var completed = CompletedSerial;
        if (completed > _notifiedCompletedSerial)
        {
            _notifiedCompletedSerial = completed;
            NotifyGpuCompleted(completed);
        }

        while (_inFlightCount > 0 && _inFlightSerials[_inFlightHead] <= completed)
        {
            var buffer = _inFlightBuffers[_inFlightHead];
            var serial = _inFlightSerials[_inFlightHead];
            _inFlightBuffers[_inFlightHead] = 0;
            _inFlightHead = (_inFlightHead + 1) % MaxInFlightSubmissions;
            _inFlightCount--;
            SampleSubmission(buffer, serial);
            MetalNative.SendVoid(buffer, SelRelease);
        }
    }

    private void SampleSubmission(nint buffer, ulong serial)
    {
        var status = MetalV2Native.SendValue(buffer, MetalNative.Selector("status"));
        if (status == CommandBufferStatusError)
        {
            Interlocked.Increment(ref _failedSubmissions);
            var error = MetalNative.Send(buffer, MetalNative.Selector("error"));
            MetalV2Diagnostics.WarnOnce(
                "submission-failed",
                $"A Metal submission failed and will never signal its serial: serial={serial} " +
                $"{MetalNative.DescribeError(error)}");
            return;
        }

        if (status != CommandBufferStatusCompleted)
        {
            _unsampledSubmissions++;
            return;
        }

        // The GPU's own execution time, which separates "the GPU is saturated" from
        // "the completion took a long time to reach this thread", and the driver's
        // scheduling gap before it.
        var gpuStart = MetalNative.SendDouble(buffer, SelGpuStartTime);
        var gpuEnd = MetalNative.SendDouble(buffer, SelGpuEndTime);
        if (gpuEnd > gpuStart)
        {
            _gpuBusySeconds += gpuEnd - gpuStart;
            _gpuBusySamples++;
        }

        var kernelStart = MetalNative.SendDouble(buffer, SelKernelStartTime);
        var kernelEnd = MetalNative.SendDouble(buffer, SelKernelEndTime);
        if (kernelEnd > kernelStart)
        {
            _kernelSeconds += kernelEnd - kernelStart;
        }

        if (gpuStart > kernelEnd && gpuStart - kernelEnd < 10.0)
        {
            _scheduleGapSeconds += gpuStart - kernelEnd;
        }
    }

    private double _kernelSeconds;
    private double _scheduleGapSeconds;

    private void ReleaseInFlightSubmissions()
    {
        while (_inFlightCount > 0)
        {
            var buffer = _inFlightBuffers[_inFlightHead];
            _inFlightBuffers[_inFlightHead] = 0;
            _inFlightHead = (_inFlightHead + 1) % MaxInFlightSubmissions;
            _inFlightCount--;
            if (buffer != 0)
            {
                MetalNative.SendVoid(buffer, SelRelease);
            }
        }
    }

    /// <summary>Commits the current command buffer and waits for the GPU to finish it.</summary>
    public void FlushAndWaitForGpu() => FlushAndWaitForGpu(MetalWaitReason.Other);

    /// <summary>
    /// Commits the current command buffer and waits for the GPU to finish it. The
    /// reason is counted, because the only way to remove a host wait is to know which
    /// packet asked for it.
    /// </summary>
    public void FlushAndWaitForGpu(MetalWaitReason reason)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var serial = FlushCommandBuffer();
        var waited = _previousCommandBuffer;
        var committedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (waited != 0 && WaitOnCommandBuffer)
        {
            // Wait on the command buffer the driver knows about rather than on the
            // shared event it signals. Measured on this host, a committed submission
            // sat 160 to 250 ms between -commit and the driver scheduling it
            // (`commit_to_kernel_ms`) while the GPU then ran it in ten microseconds:
            // nothing on our queue was pushing the submission out. Asking the driver
            // to wait for that command buffer makes it flush and schedule it now.
            MetalNative.SendVoid(waited, SelWaitUntilCompleted);
        }

        var profile = _completions.WaitFor(serial);
        ReportWait(serial, started, committedAt, waited, in profile, reason, partial: false);
        _guestMemory.NoteCompleted(serial);
        _gpuWrites.Clear(serial);
    }

    /// <summary>
    /// Waits for one submission that is already committed, leaving what is being
    /// recorded open. A command-processor read of bytes an earlier submission wrote
    /// needs that submission and nothing else, and on a tile-based GPU committing the
    /// open pass instead would store and reload the whole framebuffer.
    /// </summary>
    private void WaitForCommittedSerial(ulong serial, MetalWaitReason reason)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var profile = _completions.WaitFor(serial);
        ReportWait(serial, started, started, waited: 0, in profile, reason, partial: true);
        _guestMemory.NoteCompleted(serial);
        _gpuWrites.Retire(CompletedSerial);
        Interlocked.Increment(ref _partialWaits);
    }

    private void ReportWait(
        ulong serial,
        long started,
        long committedAt,
        nint waited,
        in MetalCompletionWait profile,
        MetalWaitReason reason,
        bool partial)
    {
        var finished = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _gpuWaits);
        Interlocked.Add(ref _gpuWaitTicks, finished - started);
        _waitsByReason[(int)reason]++;
        _waitTicksByReason[(int)reason] += finished - started;
        var frequency = System.Diagnostics.Stopwatch.Frequency;
        if (finished - started <= frequency / 50 || _slowWaitReports >= 4)
        {
            return;
        }

        // A slow host wait: separate the GPU's own execution from the scheduling
        // latency before it and from the time the completion took to reach us.
        _slowWaitReports++;
        var now = CACurrentMediaTime();
        var gpuStart = waited == 0 ? 0 : MetalNative.SendDouble(waited, SelGpuStartTime);
        var gpuEnd = waited == 0 ? 0 : MetalNative.SendDouble(waited, SelGpuEndTime);
        var kernelStart = waited == 0 ? 0 : MetalNative.SendDouble(waited, SelKernelStartTime);
        var kernelEnd = waited == 0 ? 0 : MetalNative.SendDouble(waited, SelKernelEndTime);
        Console.Error.WriteLine(
            $"[METAL-V2][SLOW-WAIT] serial={serial} reason={reason} partial={(partial ? 1 : 0)} " +
            $"read=0x{_lastReadAddress:X16}+0x{_lastReadSize:X} " +
            $"range=0x{_gpuWrites.LastMatch.Start:X16}..0x{_gpuWrites.LastMatch.End:X16} " +
            $"range_from={_gpuWrites.LastMatch.Origin} range_serial={_gpuWrites.LastMatch.Serial} " +
            $"total_ms={(finished - started) * 1000.0 / frequency:F1} " +
            $"flush_ms={(committedAt - started) * 1000.0 / frequency:F1} " +
            $"signal_ms={profile.SignalTicks * 1000.0 / frequency:F1} " +
            $"drain_ms={profile.DrainTicks * 1000.0 / frequency:F1} " +
            // commit → the driver starting on it → the GPU starting on it: a long
            // commit-to-kernel gap is host or driver scheduling, not GPU work.
            $"commit_to_kernel_ms={(kernelStart - _previousCommitTime) * 1000.0:F1} " +
            $"kernel_to_gpu_ms={(gpuStart - kernelEnd) * 1000.0:F1} " +
            $"gpu_exec_ms={(gpuEnd - gpuStart) * 1000.0:F2} gpu_end_age_ms={(now - gpuEnd) * 1000.0:F1} " +
            $"gpu_start_age_ms={(now - gpuStart) * 1000.0:F1} " +
            $"kernel_ms={(kernelEnd - kernelStart) * 1000.0:F2} " +
            $"kernel_end_age_ms={(now - kernelEnd) * 1000.0:F1} last={LastWorkDescription}");
    }

    /// <summary>Counts the time the worker spent with nothing runnable.</summary>
    public void NoteIdle(long ticks) => Interlocked.Add(ref _idleTicks, ticks);

    // A queue that has been idle schedules its next submission late. Measured on this
    // host: titles that commit two thousand buffers per two seconds see their
    // submissions start within a millisecond of the commit, while titles that commit
    // a hundred wait three hundred milliseconds and more for the driver to pick one
    // up (commit_to_kernel_ms), with the GPU running them in microseconds and only
    // one guest window resident. The presenter's own queue is busy throughout, so it
    // is the guest queue that goes cold, not the device.
    //
    // The worker commits an empty command buffer while it has nothing else to do, so
    // the queue it will submit the next frame on is never cold. The buffer carries no
    // work, no event signal and no completion handler.
    //
    // Off by default: measured on Octopath it fired only thirteen times per two
    // seconds, because the worker reaches its idle path at the blocked-retry interval
    // rather than every two milliseconds, and the completion latency did not move.
    // The switch keeps the experiment runnable without carrying it on every title.
    private static readonly bool KeepQueueWarm =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_KEEP_QUEUE_WARM") == "1";

    private const long KeepWarmIntervalMicroseconds = 2000;

    private long _lastCommitTimestamp;
    private long _keepWarmSubmissions;

    /// <summary>
    /// Reads whatever the GPU has finished and tells the command stream, so a wait on
    /// bytes the GPU wrote is retried as soon as the write lands rather than at the
    /// blocked-retry interval. The worker calls it before it parks.
    /// </summary>
    public void PollGpuCompletions() => DrainFinishedSubmissions();

    /// <summary>Commits an empty command buffer when the queue has gone quiet.</summary>
    public void KeepQueueWarmIfIdle()
    {
        if (!KeepQueueWarm || _commandBuffer != 0)
        {
            return;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var interval = KeepWarmIntervalMicroseconds * System.Diagnostics.Stopwatch.Frequency / 1_000_000;
        if (_lastCommitTimestamp != 0 && now - _lastCommitTimestamp < interval)
        {
            return;
        }

        _lastCommitTimestamp = now;
        var buffer = MetalNative.Send(_queue, SelCommandBuffer);
        if (buffer == 0)
        {
            return;
        }

        MetalNative.SendVoid(buffer, SelCommit);
        _keepWarmSubmissions++;
    }

    /// <summary>Recorded work stores into this guest range.</summary>
    public void NoteGuestMemoryWrite(ulong address, ulong size, MetalGpuWriteOrigin origin)
    {
        if (_guard is not null && size != 0)
        {
            // A fill, copy or writeback has just been encoded, so the last transfer label
            // is its own; a shader store names the draw or dispatch that bound it.
            var producer = origin is MetalGpuWriteOrigin.Fill or MetalGpuWriteOrigin.Copy
                    or MetalGpuWriteOrigin.ImageWriteback
                ? $"{MetalGpuWriteOrigins.Names[(int)origin]} [{LastTransferDescription}]"
                : $"{MetalGpuWriteOrigins.Names[(int)origin]} [{LastWorkDescription}]";
            _guard.Capture(address, size, SubmissionSerial, producer);
        }

        if (ConservativeReads)
        {
            _gpuWrites.NoteUnbounded(SubmissionSerial);
            return;
        }

        _gpuWrites.Note(address, size, SubmissionSerial, origin);
    }

    /// <summary>
    /// Recorded work stores into guest memory without a known destination. Only the
    /// paths that genuinely cannot name their range use this: it makes every later
    /// read of any address wait for the submission.
    /// </summary>
    public void NoteUnboundedGuestMemoryWrite() => _gpuWrites.NoteUnbounded(SubmissionSerial);

    void IMetalImageCacheOwner.WaitForGpu() => FlushAndWaitForGpu(MetalWaitReason.ImageCache);

    void IMetalImageCacheOwner.FillGuestRange(ulong address, ulong size, uint value) =>
        FillGuestMemory(address, size, value);

    void IMetalImageCacheOwner.NoteGuestWrite(ulong address, ulong size) =>
        NoteGuestMemoryWrite(address, size, MetalGpuWriteOrigin.ImageWriteback);

    // A fill or copy the host can do itself is one no GPU-owned image covers and no
    // recorded store is still writing. Doing it on the GPU instead is what makes the
    // command processor's own poll of the dword it just wrote wait for a whole
    // submission: measured on Quake, a four-byte fill followed by a WAIT_REG_MEM on
    // it cost 20 to 65 ms of host wait each, 150 times per two seconds.
    //
    // A packet-sized range stays on the host even while a command buffer is being
    // recorded. The command processor runs ahead of the GPU by design and the
    // interpreter already publishes its own label writes straight into guest memory,
    // so a dword of command memory is never what a recorded shader is reading.
    private const ulong MaxHostFillBytes = 64 * 1024;
    private const ulong MaxRecordingHostFillBytes = 4096;

    private static readonly bool HostFillsDisabled =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_HOST_FILLS") == "0";

    private static readonly bool ClearFillsDisabled =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_CLEAR_FILLS") == "0";

    private bool CanWriteGuestOnHost(ulong address, ulong size)
    {
        var limit = _recordedCommands == 0 ? MaxHostFillBytes : MaxRecordingHostFillBytes;
        return !HostFillsDisabled && size <= limit &&
            // The GPU path writes through the window, which is mapped whatever the
            // guest's own protections say; the host path goes through guest memory and
            // would quietly write only the mapped prefix.
            _memory.CanRead(address, size) &&
            !_imageCache.HasGpuModifiedImageBytes(address, size) &&
            _gpuWrites.PendingSerial(address, size, CompletedSerial) == 0;
    }

    private bool TryFillGuestMemoryOnHost(ulong address, ulong size, uint value)
    {
        if (!CanWriteGuestOnHost(address, size))
        {
            return false;
        }

        WriteGuestPattern(address, size, value);
        _imageCache.InvalidateMemory(address, size);
        Interlocked.Increment(ref _hostFills);
        return true;
    }

    private long _hostFills;
    private long _gpuFills;
    private long _absorbedFills;
    private long _hostCopies;

    /// <summary>Fills a guest range with a repeating dword through its window.</summary>
    public void FillGuestMemory(ulong address, ulong size, uint value)
    {
        if (size == 0)
        {
            return;
        }

        if (TryFillGuestMemoryOnHost(address, size, value))
        {
            return;
        }

        // A fill whose range is exactly one cached image is that image being cleared.
        // Writing its guest bytes instead costs the writeback of whatever the GPU had
        // put there, a retile of the fill and then a detile back into the texture on
        // the next bind — measured on Quake's menu as two of each per frame.
        if (!ClearFillsDisabled && _imageCache.TryClearImageFromBuffer(address, size, value))
        {
            Interlocked.Increment(ref _absorbedFills);
            return;
        }

        var range = _guestMemory.ResolveWritable(address, size);
        if (!range.IsValid)
        {
            // Not backed: the managed path writes it, which is what a non-backed range
            // means on this backend (device-local allocations are all backed).
            WriteGuestPattern(address, size, value);
            return;
        }

        var context = ((IMetalImageCacheOwner)this).BeginTransfer();
        var encoder = MetalNative.Send(context.CommandBuffer, SelBlitEncoder);
        if (encoder == 0)
        {
            throw GpuFatal.Fatal("A Metal blit encoder could not be created for a guest fill.");
        }

        try
        {
            var bytes = (byte)value;
            if (value == bytes * 0x01010101u)
            {
                MetalNative.SendFillBuffer(
                    encoder,
                    SelFillBuffer,
                    range.Buffer,
                    new NsRange { Location = (nuint)range.Offset, Length = (nuint)range.Size },
                    bytes);
            }
            else
            {
                // A non-uniform dword needs a pattern source; one ring page is enough
                // and the blit repeats it across the destination.
                var pattern = _guestMemory.Allocate(Math.Min(size, 64 * 1024), 256, out var span);
                var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(span);
                words.Fill(value);
                for (var written = 0UL; written < size;)
                {
                    var chunk = Math.Min(pattern.Size, size - written);
                    MetalNative.SendCopyBuffer(
                        encoder,
                        SelCopyBuffer,
                        pattern.Buffer,
                        (nuint)pattern.Offset,
                        range.Buffer,
                        (nuint)(range.Offset + written),
                        (nuint)chunk);
                    written += chunk;
                }
            }
        }
        finally
        {
            MetalNative.SendVoid(encoder, SelEndEncoding);
        }

        ((IMetalSubmissionContext)this).ProfileEncoded($"fill bytes=0x{size:X}");
        _imageCache.InvalidateMemoryFromGpu(address, size);
        NoteGuestMemoryWrite(address, size, MetalGpuWriteOrigin.Fill);
        Interlocked.Increment(ref _gpuFills);
    }

    private void WriteGuestPattern(ulong address, ulong size, uint value)
    {
        if (_guard is null)
        {
            WriteGuestPatternCore(address, size, value);
            return;
        }

        var watch = _guard.Begin(address, size, $"host-fill value=0x{value:X8}");
        try
        {
            WriteGuestPatternCore(address, size, value);
        }
        finally
        {
            _guard.End(watch);
        }
    }

    private void WriteGuestPatternCore(ulong address, ulong size, uint value)
    {
        Span<byte> chunk = stackalloc byte[4096];
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(chunk);
        words.Fill(value);
        for (var written = 0UL; written < size;)
        {
            var count = (int)Math.Min((ulong)chunk.Length, size - written);
            if (!_memory.TryWrite(address + written, chunk[..count]))
            {
                break;
            }

            written += (ulong)count;
        }
    }

    /// <summary>Copies guest bytes on the GPU through the windows.</summary>
    public void CopyGuestMemory(ulong destination, ulong source, ulong size)
    {
        if (size == 0)
        {
            return;
        }

        var target = _guestMemory.ResolveWritable(destination, size);
        var origin = _guestMemory.Resolve(source, size);
        if (!target.IsValid || !origin.IsValid)
        {
            CopyGuestBytesOnHost(destination, source, size);
            return;
        }

        // A packet-sized copy the host can do itself costs nothing and records no GPU
        // write, so the poll that usually follows it does not wait for a submission.
        if (CanWriteGuestOnHost(destination, size))
        {
            CopyGuestBytesOnHost(destination, source, size);
            _imageCache.InvalidateMemory(destination, size);
            Interlocked.Increment(ref _hostCopies);
            return;
        }

        FlushGpuWrites(source, size, MetalWritebackCause.GuestCopy);
        var context = ((IMetalImageCacheOwner)this).BeginTransfer();
        var encoder = MetalNative.Send(context.CommandBuffer, SelBlitEncoder);
        if (encoder == 0)
        {
            throw GpuFatal.Fatal("A Metal blit encoder could not be created for a guest copy.");
        }

        try
        {
            MetalNative.SendCopyBuffer(
                encoder,
                SelCopyBuffer,
                origin.Buffer,
                (nuint)origin.Offset,
                target.Buffer,
                (nuint)target.Offset,
                (nuint)size);
        }
        finally
        {
            MetalNative.SendVoid(encoder, SelEndEncoding);
        }

        context.ProfileEncoded($"copy bytes=0x{size:X}");
        _imageCache.InvalidateMemoryFromGpu(destination, size);
        NoteGuestMemoryWrite(destination, size, MetalGpuWriteOrigin.Copy);
    }

    private void CopyGuestBytesOnHost(ulong destination, ulong source, ulong size)
    {
        if (_guard is null)
        {
            CopyGuestBytesOnHostCore(destination, source, size);
            return;
        }

        var watch = _guard.Begin(destination, size, $"host-copy from=0x{source:X16}");
        try
        {
            CopyGuestBytesOnHostCore(destination, source, size);
        }
        finally
        {
            _guard.End(watch);
        }
    }

    private void CopyGuestBytesOnHostCore(ulong destination, ulong source, ulong size)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent((int)Math.Min(size, 64 * 1024));
        try
        {
            for (var copied = 0UL; copied < size;)
            {
                var count = (int)Math.Min((ulong)buffer.Length, size - copied);
                var span = buffer.AsSpan(0, count);
                if (!TryReadGuest(source + copied, span) || !_memory.TryWrite(destination + copied, span))
                {
                    break;
                }

                copied += (ulong)count;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Publishes every GPU-owned image byte in the range into guest memory.</summary>
    public void FlushGpuWrites(ulong address, ulong size, MetalWritebackCause cause) =>
        _imageCache.FlushGpuWrites(address, size, cause);

    public void RunPendingOperations() => _relay.RunPendingCommands();

    public void SetDebugInformation(
        RecordedOperation operation,
        ulong submitId,
        uint argument0,
        uint argument1,
        uint argument2,
        uint argument3,
        ulong argument4)
    {
        if (!MetalV2Diagnostics.Verbose)
        {
            return;
        }

        MetalV2Diagnostics.Trace(
            $"{operation} submit={submitId} args=({argument0},{argument1},{argument2},{argument3}) address=0x{argument4:X16}");
    }

    public bool TryReadGuest(ulong address, Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return true;
        }

        // Guest memory is the GPU's memory, so a read only has to wait for work that
        // may still be writing these bytes: an image that owns them, or a recorded
        // store into them. Without that test every command-memory read would stall.
        var size = (ulong)destination.Length;
        // The command stream reads a packet out of guest memory for every packet it
        // runs, and almost none of them land anywhere the GPU has written. Two
        // comparisons answer that, and skipping the full test keeps a message send
        // and two range scans off the per-packet path.
        if (!_gpuWrites.MayCover(address, size) && !_imageCache.MayOwn(address, size))
        {
            return _memory.TryRead(address, destination);
        }

        _lastReadAddress = address;
        _lastReadSize = size;
        if (_imageCache.HasGpuModifiedImageBytes(address, size))
        {
            // The writeback is recorded into the buffer being built, so this one has
            // to be committed before the bytes can be read.
            FlushGpuWrites(address, size, MetalWritebackCause.HostRead);
            FlushAndWaitForGpu(MetalWaitReason.ReadImage);
            return _memory.TryRead(address, destination);
        }

        var pending = _gpuWrites.PendingSerial(address, size, CompletedSerial);
        if (pending != 0)
        {
            if (_commandBuffer != 0 && pending >= _submissionSerial)
            {
                FlushAndWaitForGpu(MetalWaitReason.ReadRange);
            }
            else
            {
                WaitForCommittedSerial(pending, MetalWaitReason.ReadRange);
            }
        }

        return _memory.TryRead(address, destination);
    }

    // The part of the range that is mapped from its start; an unmapped start is fatal
    // because the executor cannot bind it.
    public ulong ClampMappedSize(ulong address, ulong size)
    {
        if (address == 0 || size == 0 || size > ulong.MaxValue - address || !_memory.CanRead(address, 1))
        {
            throw Fatal($"The buffer range starts in unmapped memory: address=0x{address:X16} size=0x{size:X16}.");
        }

        if (_memory.CanRead(address, size))
        {
            return size;
        }

        var clamped = 1UL;
        var rejected = size;
        while (rejected - clamped > 1)
        {
            var candidate = clamped + ((rejected - clamped) / 2);
            if (_memory.CanRead(address, candidate))
            {
                clamped = candidate;
            }
            else
            {
                rejected = candidate;
            }
        }

        return clamped;
    }

    public BufferBinding NullBuffer => new((ulong)_guestMemory.NullBuffer, 0);

    public BufferBinding ObtainBuffer(ulong address, ulong size, bool isWritten)
    {
        // A buffer binding sees guest memory, so any image that owns these bytes
        // publishes them first; a written binding also invalidates those images.
        FlushGpuWrites(address, size, MetalWritebackCause.BufferBinding);
        var range = _guestMemory.Resolve(address, size);
        if (!range.IsValid)
        {
            return NullBuffer;
        }

        if (isWritten)
        {
            _imageCache.InvalidateMemoryFromGpu(address, size);
            NoteGuestMemoryWrite(address, size, MetalGpuWriteOrigin.WrittenBinding);
        }

        return new BufferBinding((ulong)range.Buffer, range.Offset);
    }

    public BufferBinding UploadTransient(ReadOnlySpan<byte> data, uint alignment)
    {
        var range = _guestMemory.Allocate((ulong)data.Length, Math.Max(alignment, 16), out var span);
        data.CopyTo(span);
        return new BufferBinding((ulong)range.Buffer, range.Offset);
    }

    public bool TryGetImageFormatProperties(
        GpuFormat format,
        GpuImageType type,
        GpuImageTiling tiling,
        GpuImageUsage usage,
        GpuImageCreateFlags flags,
        out GpuImageFormatProperties properties)
    {
        _ = type;
        _ = tiling;
        _ = usage;
        _ = flags;
        properties = new GpuImageFormatProperties
        {
            MaxExtent = new GpuExtent3D(MaxFramebufferDimension, MaxFramebufferDimension, 2048),
            MaxMipLevels = ImageDescription.MaxLevels,
            MaxArrayLayers = 2048,
            SampleCounts = GpuSampleCount.Count1Bit | GpuSampleCount.Count2Bit | GpuSampleCount.Count4Bit |
                GpuSampleCount.Count8Bit,
            MaxResourceSize = 1UL << 32,
        };
        return MetalV2Formats.IsSupported(format);
    }

    public (long Draws, double DrawMs, long Pipelines, long ShaderCompilations) ReadAndResetPerfCounters()
    {
        var draws = Interlocked.Exchange(ref _drawCount, 0);
        var ticks = Interlocked.Exchange(ref _drawTicks, 0);
        var pipelines = Interlocked.Exchange(ref _pipelineCreations, 0);
        var compilations = Interlocked.Exchange(ref _shaderCompilations, 0);
        return (draws, ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, pipelines, compilations);
    }

    public void CountShaderCompilation() => Interlocked.Increment(ref _shaderCompilations);

    /// <summary>
    /// One periodic line of host counters. The compact form is always printed; the
    /// render-pass breakdown and the completion timings that found the residency-set
    /// stall follow it under SHARPEMU_METAL_V2_TRACE=1.
    /// </summary>
    public void ReportCounters()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_statsTimestamp == 0)
        {
            _statsTimestamp = now;
            return;
        }

        if (now - _statsTimestamp < 2 * System.Diagnostics.Stopwatch.Frequency)
        {
            return;
        }

        var seconds = (now - _statsTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        _statsTimestamp = now;
        // Read every submission that has finished since the last report, so the GPU
        // time below covers them all and not only the one that happened to be done.
        DrainFinishedSubmissions();
        var draws = Interlocked.Exchange(ref _drawCount, 0);
        var drawTicks = Interlocked.Exchange(ref _drawTicks, 0);
        var flushes = Interlocked.Exchange(ref _flushes, 0);
        var passes = Interlocked.Exchange(ref _renderPasses, 0);
        var gpuBusyMs = _gpuBusySeconds * 1000.0;
        var gpuBusySamples = _gpuBusySamples;
        var kernelMs = _kernelSeconds * 1000.0;
        var scheduleMs = _scheduleGapSeconds * 1000.0;
        var unsampled = _unsampledSubmissions;
        _gpuBusySeconds = 0;
        _gpuBusySamples = 0;
        _kernelSeconds = 0;
        _scheduleGapSeconds = 0;
        _unsampledSubmissions = 0;
        var waits = Interlocked.Exchange(ref _gpuWaits, 0);
        var partial = Interlocked.Exchange(ref _partialWaits, 0);
        var waitTicks = Interlocked.Exchange(ref _gpuWaitTicks, 0);
        var idleTicks = Interlocked.Exchange(ref _idleTicks, 0);
        var pipelines = Interlocked.Exchange(ref _pipelineCreations, 0);
        var failed = Interlocked.Exchange(ref _failedSubmissions, 0);
        var (completions, completionMs) = TakeCompletionLatency();
        var (created, uploads, writebacks, evictions) = _imageCache.Counters;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var frequency = System.Diagnostics.Stopwatch.Frequency;
        Console.Error.WriteLine(
            $"[METAL-V2][PERF] seconds={seconds.ToString("F2", culture)} submissions={_submissionsStarted} " +
            // Slices are what the queue actually ran: a submission that suspends is
            // re-queued and re-run, so slices far above guest submissions is retry churn,
            // not the guest sending more work.
            $"guest_submissions={CommandStream.SubmissionsStarted} slices={CommandStream.SlicesRun} " +
            $"draws={draws} passes={passes} flushes={flushes} " +
            $"draw_ms={(drawTicks * 1000.0 / frequency).ToString("F1", culture)} " +
            $"gpu_busy_ms={gpuBusyMs.ToString("F1", culture)} " +
            $"gpu_n={gpuBusySamples} gpu_unsampled={unsampled} " +
            $"kernel_ms={kernelMs.ToString("F1", culture)} " +
            $"schedule_ms={scheduleMs.ToString("F1", culture)} " +
            $"completions={completions} completion_ms={completionMs.ToString("F1", culture)} " +
            $"host_waits={waits} partial_waits={partial} " +
            $"host_wait_ms={(waitTicks * 1000.0 / frequency).ToString("F1", culture)} " +
            $"idle_ms={(idleTicks * 1000.0 / frequency).ToString("F0", culture)} pipelines={pipelines} " +
            $"images={_imageCache.ImageCount} image_mb={_imageCache.TotalUsedMemory / (1024 * 1024)} " +
            $"sampled_dcc_clears={_imageCache.TakeSampledDccClears()} " +
            $"uploads={uploads} writebacks={writebacks} evictions={evictions} failed={failed}");
        _slowWaitReports = 0;
        ResetSlowCompletionReports();
        var reasons = new System.Text.StringBuilder();
        for (var index = 0; index < MetalWaitReasons.Count; index++)
        {
            var count = _waitsByReason[index];
            if (count == 0)
            {
                continue;
            }

            var ticks = _waitTicksByReason[index];
            _waitsByReason[index] = 0;
            _waitTicksByReason[index] = 0;
            reasons.Append(' ').Append(MetalWaitReasons.Names[index]).Append('=').Append(count)
                .Append('/').Append((ticks * 1000.0 / frequency).ToString("F0", culture)).Append("ms");
        }

        for (var index = 0; index < MetalGpuWriteOrigins.Count; index++)
        {
            var records = _gpuWrites.RecordsFrom((MetalGpuWriteOrigin)index);
            if (records != 0)
            {
                reasons.Append(' ').Append(MetalGpuWriteOrigins.Names[index]).Append("_records=").Append(records);
            }
        }

        reasons.Append(" host_fills=").Append(Interlocked.Exchange(ref _hostFills, 0))
            .Append(" gpu_fills=").Append(Interlocked.Exchange(ref _gpuFills, 0))
            .Append(" cleared_fills=").Append(Interlocked.Exchange(ref _absorbedFills, 0))
            .Append(" host_copies=").Append(Interlocked.Exchange(ref _hostCopies, 0))
            .Append(" inline_eop=").Append(TakeInlineCompletions())
            .Append(" keep_warm=").Append(Interlocked.Exchange(ref _keepWarmSubmissions, 0))
            .Append(" residency_skipped=").Append(TakeSkippedResidencyCalls())
            .Append(" device_address_writes=").Append(DeviceAddressWrites)
            .Append(" resident=").Append(_guestMemory.ResidentBuffers.Length)
            .Append(" windows_touched=").Append(System.Numerics.BitOperations.PopCount(_guestMemory.TouchedWindows));
        Console.Error.WriteLine($"[METAL-V2][WAITS]{reasons}");
        var causes = _imageCache.TakeTransferCauses();
        var aliases = _imageCache.AliasCounters;
        Console.Error.WriteLine(
            $"[METAL-V2][CHURN] up_new={causes.Created} up_cpu={causes.CpuWrite} up_gpu={causes.GpuWrite} " +
            $"up_other={causes.Invalidated} up_alias={causes.Alias} " +
            $"wb_bind={causes.Binding} wb_store={causes.Storage} " +
            $"wb_read={causes.HostRead} wb_copy={causes.GuestCopy} wb_overlap={causes.Overlap} " +
            $"wb_release={causes.Release} wb_drain={causes.Drain}{ReleaseCauses()} " +
            $"alias_kept={aliases.Kept} alias_published={aliases.Published} " +
            $"write_ranges={_gpuWrites.Count} write_records={_gpuWrites.Recorded} " +
            $"write_folds={_gpuWrites.Folds} handler_wakeups={_completions.HandlerWakeups} " +
            $"rings={_guestMemory.RingCount} rings_freed={_guestMemory.RingsReleased} " +
            $"ring_mb={_guestMemory.RingFallbackBytes / (1024 * 1024)}");
        if (_guard is not null)
        {
            Console.Error.WriteLine($"[METAL-V2][GUARD] {_guard.Report()}");
        }

        if (!MetalV2Diagnostics.Verbose)
        {
            return;
        }

        var (endClear, endTarget, endTransfer, endCompute, targetless) = TakePassBreakdown();
        var (eventReady, eventWaited, eventWaitMs, eventWakeMs) = _completions.TakeWaitProfile();
        var blocked = CommandStream.SnapshotBlocked();
        Console.Error.WriteLine(
            $"[METAL-V2][TRACE] created={created} end_clear={endClear} end_target={endTarget} " +
            $"end_transfer={endTransfer} end_compute={endCompute} targetless={targetless} " +
            $"gpu_busy_n={gpuBusySamples} event_ready={eventReady} event_waited={eventWaited} " +
            $"event_wait_ms={eventWaitMs.ToString("F1", culture)} " +
            $"event_wake_ms={eventWakeMs.ToString("F1", culture)} " +
            $"slices={CommandStream.SlicesRun} blocked={blocked.Outstanding} " +
            $"blocked_wait=0x{blocked.SampleWaitAddress:X16} " +
            $"ring_fallbacks={_guestMemory.RingFallbacks} ring_mb={_guestMemory.RingFallbackBytes / (1024 * 1024)}");
    }

    private string ReleaseCauses()
    {
        var builder = new System.Text.StringBuilder();
        _imageCache.AppendReleaseCauses(builder);
        return builder.ToString();
    }

    public Exception Fatal(string message) => GpuFatal.Fatal(message);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            EndEncoders();
            if (_commandBuffer != 0)
            {
                MetalNative.SendVoid(_commandBuffer, SelCommit);
                MetalNative.SendVoid(_commandBuffer, SelWaitUntilCompleted);
                MetalNative.SendVoid(_commandBuffer, SelRelease);
                _commandBuffer = 0;
            }

            _previousCommandBuffer = 0;
            ReleaseInFlightSubmissions();
            ReportStepCaps();
            DisposeStepReport();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[METAL-V2][WARN] The render host could not drain on shutdown: {exception.Message}");
        }

        _completions.Dispose();
        DisposePipelines();
        (_transfer as IDisposable)?.Dispose();
        _imageCache.Dispose();
        _guestMemory.Dispose();
        if (_sharedEvent != 0)
        {
            MetalNative.SendVoid(_sharedEvent, SelRelease);
        }
    }

    private void SetLabel(nint resource, string label)
    {
        var pool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            MetalNative.SendVoid(resource, SelSetLabel, MetalNative.NsString(label));
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(pool);
        }
    }
}
