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
        MetalNative.SendVoid(_commandBuffer, SelRelease);
        _commandBuffer = 0;
        _recordedCommands = 0;
        Interlocked.Increment(ref _flushes);
        _completions.Publish(serial);
        return serial;
    }

    /// <summary>Commits the current command buffer and waits for the GPU to finish it.</summary>
    public void FlushAndWaitForGpu()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var serial = FlushCommandBuffer();
        _completions.WaitFor(serial);
        Interlocked.Increment(ref _gpuWaits);
        Interlocked.Add(ref _gpuWaitTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        _guestMemory.NoteCompleted(serial);
        _gpuWroteGuestMemory = false;
    }

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

    /// <summary>One periodic line with everything the v2 host counts; cheap to read.</summary>
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
        var waits = Interlocked.Exchange(ref _gpuWaits, 0);
        var waitTicks = Interlocked.Exchange(ref _gpuWaitTicks, 0);
        var pipelines = Interlocked.Exchange(ref _pipelineCreations, 0);
        var (created, uploads, writebacks, evictions) = _imageCache.Counters;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var drawMs = (drawTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1", culture);
        var waitMs = (waitTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1", culture);
        Console.Error.WriteLine(
            $"[METAL-V2][PERF] seconds={seconds.ToString("F2", culture)} submissions={_submissionsStarted} " +
            $"draws={draws} draw_ms={drawMs} flushes={flushes} gpu_waits={waits} gpu_wait_ms={waitMs} " +
            $"pipelines={pipelines} images={_imageCache.ImageCount} created={created} uploads={uploads} " +
            $"writebacks={writebacks} evictions={evictions} image_mb={_imageCache.TotalUsedMemory / (1024 * 1024)} " +
            $"ring_fallbacks={_guestMemory.RingFallbacks}");
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
