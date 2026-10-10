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
    // Set when recorded work may store into guest memory. A CPU read only has to
    // wait for the GPU while it is set; without it every packet read would stall.
    private bool _gpuWroteGuestMemory;
    private long _drawCount;
    private long _drawTicks;
    private long _pipelineCreations;
    private long _shaderCompilations;
    private long _flushes;
    private long _gpuWaits;
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
        _guestMemory.BeginSubmission(_submissionSerial, CompletedSerial);
        return _commandBuffer;
    }

    // Everything the cache and the transfers encode needs the encoders closed first.
    IMetalSubmissionContext IMetalImageCacheOwner.BeginTransfer()
    {
        _ = EnsureCommandBuffer();
        EndEncoders();
        return this;
    }

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
        MetalNative.SendVoid(_commandBuffer, SelCommit);
        // A command buffer that fails never signals its serial, and the completion
        // queue would then wait out its timeout on every submission behind it. The
        // previous buffer is far enough ahead to have a status by now, so checking
        // it here costs one message send and turns a silent stall into a message.
        ReportSubmissionFailure();
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
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/QuartzCore.framework/QuartzCore")]
    private static extern double CACurrentMediaTime();

    private int _slowWaitReports;

    private void ReportSubmissionFailure()
    {
        if (_previousCommandBuffer == 0)
        {
            return;
        }

        var buffer = _previousCommandBuffer;
        var serial = _previousSerial;
        _previousCommandBuffer = 0;
        var status = MetalV2Native.SendValue(buffer, MetalNative.Selector("status"));
        if (status == CommandBufferStatusError)
        {
            Interlocked.Increment(ref _failedSubmissions);
            var error = MetalNative.Send(buffer, MetalNative.Selector("error"));
            MetalV2Diagnostics.WarnOnce(
                "submission-failed",
                $"A Metal submission failed and will never signal its serial: serial={serial} " +
                $"{MetalNative.DescribeError(error)}");
        }
        else if (status == CommandBufferStatusCompleted)
        {
            // The GPU's own execution time, which separates "the GPU is saturated"
            // from "the completion took a long time to reach this thread".
            var start = MetalNative.SendDouble(buffer, SelGpuStartTime);
            var end = MetalNative.SendDouble(buffer, SelGpuEndTime);
            if (end > start)
            {
                _gpuBusySeconds += end - start;
                _gpuBusySamples++;
            }
        }

        MetalNative.SendVoid(buffer, SelRelease);
    }

    /// <summary>Commits the current command buffer and waits for the GPU to finish it.</summary>
    public void FlushAndWaitForGpu()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var serial = FlushCommandBuffer();
        var waited = _previousCommandBuffer;
        var committedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _completions.WaitFor(serial);
        var finished = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _gpuWaits);
        Interlocked.Add(ref _gpuWaitTicks, finished - started);
        var frequency = System.Diagnostics.Stopwatch.Frequency;
        if (finished - started > frequency / 50 && waited != 0 && _slowWaitReports < 64)
        {
            // A slow host wait: separate the GPU's own execution from scheduling.
            _slowWaitReports++;
            var now = CACurrentMediaTime();
            var gpuStart = MetalNative.SendDouble(waited, SelGpuStartTime);
            var gpuEnd = MetalNative.SendDouble(waited, SelGpuEndTime);
            Console.Error.WriteLine(
                $"[METAL-V2][SLOW-WAIT] serial={serial} total_ms={(finished - started) * 1000.0 / frequency:F1} " +
                $"flush_ms={(committedAt - started) * 1000.0 / frequency:F1} " +
                $"gpu_exec_ms={(gpuEnd - gpuStart) * 1000.0:F2} gpu_end_age_ms={(now - gpuEnd) * 1000.0:F1} " +
                $"gpu_start_age_ms={(now - gpuStart) * 1000.0:F1}");
        }
        _guestMemory.NoteCompleted(serial);
        _gpuWroteGuestMemory = false;
    }

    /// <summary>Counts the time the worker spent with nothing runnable.</summary>
    public void NoteIdle(long ticks) => Interlocked.Add(ref _idleTicks, ticks);

    /// <summary>Recorded work may now store into guest memory.</summary>
    public void NoteGuestMemoryWrite() => _gpuWroteGuestMemory = true;

    void IMetalImageCacheOwner.WaitForGpu() => FlushAndWaitForGpu();

    void IMetalImageCacheOwner.FillGuestRange(ulong address, ulong size, uint value) =>
        FillGuestMemory(address, size, value);

    /// <summary>Fills a guest range with a repeating dword through its window.</summary>
    public void FillGuestMemory(ulong address, ulong size, uint value)
    {
        if (size == 0)
        {
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

        _imageCache.InvalidateMemoryFromGpu(address, size);
        NoteGuestMemoryWrite();
    }

    private void WriteGuestPattern(ulong address, ulong size, uint value)
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

        FlushGpuWrites(source, size);
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

        _imageCache.InvalidateMemoryFromGpu(destination, size);
        NoteGuestMemoryWrite();
    }

    private void CopyGuestBytesOnHost(ulong destination, ulong source, ulong size)
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
    public void FlushGpuWrites(ulong address, ulong size) => _imageCache.FlushGpuWrites(address, size);

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
        // may still be writing it: an image that owns these bytes, or any recorded
        // shader store. Without that test every command-memory read would stall.
        var size = (ulong)destination.Length;
        if (_imageCache.HasGpuModifiedImageBytes(address, size))
        {
            FlushGpuWrites(address, size);
            FlushAndWaitForGpu();
        }
        else if (_gpuWroteGuestMemory)
        {
            FlushAndWaitForGpu();
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
        FlushGpuWrites(address, size);
        var range = _guestMemory.Resolve(address, size);
        if (!range.IsValid)
        {
            return NullBuffer;
        }

        if (isWritten)
        {
            _imageCache.InvalidateMemoryFromGpu(address, size);
            NoteGuestMemoryWrite();
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
        var draws = Interlocked.Exchange(ref _drawCount, 0);
        var drawTicks = Interlocked.Exchange(ref _drawTicks, 0);
        var flushes = Interlocked.Exchange(ref _flushes, 0);
        var passes = Interlocked.Exchange(ref _renderPasses, 0);
        var gpuBusyMs = _gpuBusySeconds * 1000.0;
        var gpuBusySamples = _gpuBusySamples;
        _gpuBusySeconds = 0;
        _gpuBusySamples = 0;
        var waits = Interlocked.Exchange(ref _gpuWaits, 0);
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
            $"draws={draws} passes={passes} flushes={flushes} " +
            $"draw_ms={(drawTicks * 1000.0 / frequency).ToString("F1", culture)} " +
            $"gpu_busy_ms={gpuBusyMs.ToString("F1", culture)} " +
            $"completions={completions} completion_ms={completionMs.ToString("F1", culture)} " +
            $"host_waits={waits} host_wait_ms={(waitTicks * 1000.0 / frequency).ToString("F1", culture)} " +
            $"idle_ms={(idleTicks * 1000.0 / frequency).ToString("F0", culture)} pipelines={pipelines} " +
            $"images={_imageCache.ImageCount} image_mb={_imageCache.TotalUsedMemory / (1024 * 1024)} " +
            $"uploads={uploads} writebacks={writebacks} evictions={evictions} failed={failed}");
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

            ReportSubmissionFailure();
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
