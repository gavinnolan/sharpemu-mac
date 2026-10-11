// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.Versioning;
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Platform.MacOS.Windowing;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// The window and the GPU worker of the v2 backend. Two threads: the AppKit main
/// thread owns the CAMetalLayer and presents, and the GPU worker owns the command
/// stream and all recording. A flip hands the cached image's MTLTexture over, so a
/// presented frame never travels through the CPU.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class MetalV2Presenter
{
    private const ulong PixelFormatBgra8Unorm = 80;

    private static readonly object _gate = new();
    private static readonly object _frameGate = new();

    private static nint _device;
    private static nint _queue;
    private static MetalRenderHost? _host;
    private static MetalGpuWorkerRelay? _relay;
    private static Thread? _worker;
    private static Thread? _windowThread;
    private static IMetalHostWindow? _hostWindow;
    private static HostVideoOptions _videoOptions = HostVideoOptions.Default;
    private static nint _metalLayer;
    private static nint _presentQueue;
    private static nint _presentPipeline;
    private static nint _presentSampler;
    private static double _drawableWidth;
    private static double _drawableHeight;
    private static bool _closed;
    private static bool _closeRequested;
    private static bool _splashHidden;
    private static bool _windowStarted;
    private static Exception? _startupFailure;

    // The frame the next present shows: a guest texture plus the serial it needs.
    // Published flips waiting for their submission and their refresh, oldest first.
    private readonly record struct PendingFrame(
        nint Texture, uint Width, uint Height, ulong RequestId, ulong Serial, bool IsHdr);

    private const int MaxPendingFrames = 4;
    private static readonly Queue<PendingFrame> _pendingFrames = new(MaxPendingFrames);
    private static nint _presentTexture;
    private static uint _presentWidth;
    private static uint _presentHeight;
    private static bool _presentIsHdr;
    private static byte[]? _pendingHostFrame;
    private static uint _pendingHostWidth;
    private static uint _pendingHostHeight;
    private static nint _hostFrameTexture;
    private static uint _hostFrameWidth;
    private static uint _hostFrameHeight;
    private static long _guestFrameSequence;
    private static long _presentedGuestFrames;
    private static long _publishedGuestFrames;
    private static long _adoptionsBlocked;
    private static long _hostPresents;
    private static long _statsTimestamp;

    public static bool TryConfigureVideo(HostVideoOptions options)
    {
        lock (_gate)
        {
            if (_windowStarted)
            {
                return false;
            }

            _videoOptions = options.Normalize();
            return true;
        }
    }

    public static MetalRenderHost Host =>
        _host ?? throw GpuFatal.Fatal("The Metal v2 render host has not been created.");

    public static bool IsStarted => Volatile.Read(ref _host) is not null;

    // MTLGPUFamily.Metal3, the family that has the mesh stage.
    private const nint GpuFamilyMetal3 = 5001;

    /// <summary>Whether this device can build mesh render pipelines.</summary>
    public static bool SupportsMeshPipelines()
    {
        lock (_gate)
        {
            try
            {
                EnsureDeviceLocked();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[METAL-V2][WARN] The Metal device is unavailable: {exception.Message}");
                return false;
            }

            var supportsFamily = MetalNative.Selector("supportsFamily:");
            if (MetalNative.Class("MTLMeshRenderPipelineDescriptor") == 0 ||
                !MetalNative.SendBool(_device, MetalNative.Selector("respondsToSelector:"), supportsFamily) ||
                !MetalNative.SendBool(_device, supportsFamily, GpuFamilyMetal3))
            {
                Console.Error.WriteLine("[METAL-V2][INFO] Mesh render pipelines are unavailable on this device.");
                return false;
            }

            return true;
        }
    }

    /// <summary>Creates the device, the render host and the GPU worker, once.</summary>
    public static MetalRenderHost EnsureHost()
    {
        lock (_gate)
        {
            if (_startupFailure is { } failure)
            {
                throw new InvalidOperationException("The Metal v2 backend could not start.", failure);
            }

            if (_host is { } existing)
            {
                return existing;
            }

            try
            {
                EnsureDeviceLocked();
                if (GuestGpuMemoryHook.Current is not { AddressSpace: ICpuMemory guest and IGuestBackedSpace backing } memory)
                {
                    throw GpuFatal.Fatal("Metal v2 requires the guest GPU memory manager with backed virtual memory.");
                }

                _relay = new MetalGpuWorkerRelay(Wake);
                var host = new MetalRenderHost(_device, _queue, guest, backing, memory.Pages, _relay);
                host.InitializeCommandStream(new FlipTarget());
                _host = host;
                // v2 has no buffer store: guest memory is the GPU's memory, so only the
                // image store answers a CPU write fault. The relay lets an unmap wait
                // for the worker to stop touching the range.
                memory.AttachStores(null, host.ImageCache);
                memory.AttachGpuQueue(_relay, null);
                _worker = new Thread(RunWorker)
                {
                    IsBackground = true,
                    Name = "SharpEmu Metal v2 GPU worker",
                };
                _worker.Start();
                new Thread(RunFlipPacer)
                {
                    IsBackground = true,
                    Name = "SharpEmu Metal v2 flip pacer",
                }.Start();
                Console.Error.WriteLine("[METAL-V2][INFO] GPU worker started.");
                return host;
            }
            catch (Exception exception)
            {
                _startupFailure = exception;
                throw;
            }
        }
    }

    private static void EnsureDeviceLocked()
    {
        if (_device != 0)
        {
            return;
        }

        MetalNative.EnsureFrameworksLoaded();
        _device = MetalNative.MTLCreateSystemDefaultDevice();
        if (_device == 0)
        {
            throw GpuFatal.Fatal("No Metal device is available.");
        }

        _queue = MetalNative.Send(_device, MetalNative.Selector("newCommandQueue"));
        _presentQueue = MetalNative.Send(_device, MetalNative.Selector("newCommandQueue"));
        if (_queue == 0 || _presentQueue == 0)
        {
            throw GpuFatal.Fatal("A Metal command queue could not be created.");
        }

        var name = MetalNative.ReadNsString(MetalNative.Send(_device, MetalNative.Selector("name")));
        if (!string.IsNullOrEmpty(name))
        {
            VideoOutBackendServices.SetSelectedGpuName(name);
        }
    }

    public static void EnsureStarted(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed || _windowStarted)
            {
                return;
            }

            EnsureDeviceLocked();
            _windowStarted = true;
            if (HostMainThread.IsAvailable)
            {
                HostMainThread.SetShutdownRequestHandler(RequestClose);
                HostMainThread.Post(RunWindow);
                return;
            }

            _windowThread = new Thread(RunWindow)
            {
                IsBackground = true,
                Name = "SharpEmu Metal v2 VideoOut",
            };
            _windowThread.Start();
        }
    }

    public static void HideSplashScreen()
    {
        lock (_frameGate)
        {
            _splashHidden = true;
            _pendingHostFrame = null;
        }
    }

    /// <summary>Presents one CPU-produced BGRA frame (the splash and the fallbacks).</summary>
    public static void Submit(byte[] bgraFrame, uint width, uint height)
    {
        if (bgraFrame.Length != checked((int)(width * height * 4)))
        {
            return;
        }

        lock (_frameGate)
        {
            if (_splashHidden && _presentTexture != 0)
            {
                return;
            }

            _pendingHostFrame = bgraFrame;
            _pendingHostWidth = width;
            _pendingHostHeight = height;
        }

        EnsureStarted(width, height);
    }

    public static void RequestClose()
    {
        Volatile.Write(ref _closeRequested, true);
        // The guest leaves right after this, and the runtime then frees the page
        // guard, which refuses to go while an image watch is outstanding. Dropping
        // the watches here does not depend on the worker reaching its own teardown.
        Volatile.Read(ref _host)?.ImageCache.StopWatching();
        try
        {
            Volatile.Read(ref _hostWindow)?.Close();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[METAL-V2][WARN] The window could not be closed: {exception.Message}");
        }

        StopWorker();
    }

    /// <summary>
    /// Stops the GPU worker and waits for its teardown. The page watches the image
    /// cache holds have to come off before the runtime releases the page guard, and
    /// only the worker may touch the cache, so the caller waits for it here.
    /// </summary>
    public static void StopWorker()
    {
        Thread? worker;
        lock (_gate)
        {
            worker = _worker;
            _worker = null;
        }

        Volatile.Write(ref _closeRequested, true);
        _host?.CommandStream.Wake();
        if (worker is null || worker == Thread.CurrentThread)
        {
            return;
        }

        if (!worker.Join(TimeSpan.FromSeconds(2)))
        {
            Console.Error.WriteLine("[METAL-V2][WARN] The GPU worker did not stop within five seconds.");
        }
    }

    private static void Wake() => _host?.CommandStream.Wake();

    // ---- the GPU worker ----

    private static void RunWorker()
    {
        var host = _host!;
        _ = SharpEmu.HLE.Host.HostThreadQualityOfService.TryApplyToCurrentThread(SharpEmu.HLE.Host.HostThreadQos.UserInteractive);
        _relay!.BindCurrentThread();
        try
        {
            while (!Volatile.Read(ref _closeRequested) && !HostSessionControl.IsShutdownRequested)
            {
                host.RunPendingCommands();
                host.RunCommandStreamSlices(64);
                host.ReportCounters();
                if (!host.CommandStream.HasUnblockedPending && !_relay.HasPendingCommands)
                {
                    host.PollGpuCompletions();
                    host.KeepQueueWarmIfIdle();
                    var idleStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    _ = host.CommandStream.WaitForWork(CommandStreamQueue.AllBlockedRetryMilliseconds);
                    host.NoteIdle(System.Diagnostics.Stopwatch.GetTimestamp() - idleStarted);
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[METAL-V2][ERROR] The GPU worker stopped: {exception}");
            host.CommandStream.Fail(exception);
        }
        finally
        {
            Shutdown(host);
        }
    }

    // The worker owns the teardown: the page watches have to come off before the
    // page guard is released, and only this thread may touch the image cache.
    private static void Shutdown(MetalRenderHost host)
    {
        try
        {
            host.CommandStream.StopAccepting();
            host.CommandStream.DiscardAll();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[METAL-V2][WARN] The command stream could not stop: {exception.Message}");
        }

        try
        {
            _relay?.StopAcceptingWork();
            host.ImageCache.StopWatching();
            host.FlushAndWaitForGpu();
            host.ImageCache.Shutdown();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[METAL-V2][WARN] The image cache could not drain: {exception.Message}");
        }
        finally
        {
            GuestGpuMemoryHook.Current?.AttachStores(null, null);
            GuestGpuMemoryHook.Current?.AttachGpuQueue(null, null);
            VideoOutBackendServices.CancelOutstandingFlips();
        }

        Console.Error.WriteLine("[METAL-V2][INFO] GPU worker stopped.");
    }

    // ---- the window and presentation ----

    private static void RunWindow()
    {
        try
        {
            using var window = new MacHostWindow(VideoOutBackendServices.GetWindowTitle(), _videoOptions);
            Volatile.Write(ref _hostWindow, window);
            var pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                _metalLayer = window.CreateMetalLayer();
                ConfigureMetalLayer(_device, _metalLayer);
                if (!TryCreatePresentPipeline(_device, out _presentPipeline, out var error))
                {
                    Console.Error.WriteLine($"[METAL-V2][ERROR] The present pipeline failed: {error}");
                    return;
                }

                _presentSampler = CreateLinearSampler(_device);
                InitializeHdrOutput(window);
                SyncDrawableSize();
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }

            Console.Error.WriteLine("[METAL-V2][INFO] VideoOut presenter started.");
            window.Run(static () => { }, static _ => PresentFrame(), static () => { });
            if (window.ClosedByUser)
            {
                Console.Error.WriteLine("[METAL-V2][WARN] The window closed; requesting emulator shutdown.");
                VideoOutBackendServices.NotifyPresentationWindowClosed();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[METAL-V2][ERROR] The VideoOut presenter failed: {exception}");
        }
        finally
        {
            Volatile.Write(ref _hostWindow, null);
            _metalLayer = 0;
            lock (_gate)
            {
                _closed = true;
            }

            ReleaseHdrSupportWaiters();
        }
    }

    private static void ConfigureMetalLayer(nint device, nint layer)
    {
        MetalNative.SendVoid(layer, MetalNative.Selector("setDevice:"), device);
        MetalV2Native.SendVoidValue(layer, MetalNative.Selector("setPixelFormat:"), (nuint)PixelFormatBgra8Unorm);
        MetalNative.SendVoidBool(layer, MetalNative.Selector("setOpaque:"), true);
        MetalNative.SendVoidBool(layer, MetalNative.Selector("setFramebufferOnly:"), true);
        MetalV2Native.SendVoidValue(layer, MetalNative.Selector("setMaximumDrawableCount:"), 3);
        var displaySync = MetalNative.Selector("setDisplaySyncEnabled:");
        if (MetalNative.SendBool(layer, MetalNative.Selector("respondsToSelector:"), displaySync))
        {
            MetalNative.SendVoidBool(layer, displaySync, _videoOptions.VSync);
        }
    }

    private static void SyncDrawableSize()
    {
        var window = Volatile.Read(ref _hostWindow);
        if (window is null || _metalLayer == 0)
        {
            return;
        }

        var (pixelWidth, pixelHeight) = window.PixelSize;
        if (pixelWidth == _drawableWidth && pixelHeight == _drawableHeight)
        {
            return;
        }

        _drawableWidth = pixelWidth;
        _drawableHeight = pixelHeight;
        MetalNative.SendVoidSize(
            _metalLayer,
            MetalNative.Selector("setDrawableSize:"),
            new CGSize { Width = pixelWidth, Height = pixelHeight });
    }

    private static void PresentFrame()
    {
        if (Volatile.Read(ref _closeRequested))
        {
            return;
        }

        var pool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            var window = Volatile.Read(ref _hostWindow);
            if (window is null || window.IsMinimized)
            {
                Thread.Sleep(8);
                return;
            }

            if (window.ConsumeSurfaceRestore())
            {
                _drawableWidth = 0;
            }

            SyncDrawableSize();
            AdoptPendingFrame();
            UploadPendingHostFrame();
            UpdateOutputColor(window);
            var drawable = MetalNative.Send(_metalLayer, MetalNative.Selector("nextDrawable"));
            if (drawable == 0)
            {
                return;
            }

            var drawableTexture = MetalNative.Send(drawable, MetalNative.Selector("texture"));
            var commandBuffer = MetalNative.Send(_presentQueue, MetalNative.Selector("commandBuffer"));
            if (drawableTexture == 0 || commandBuffer == 0)
            {
                return;
            }

            var pass = CreateClearPass(drawableTexture);
            var encoder = MetalNative.Send(
                commandBuffer, MetalNative.Selector("renderCommandEncoderWithDescriptor:"), pass);
            if (encoder == 0)
            {
                return;
            }

            var texture = _presentTexture != 0 ? _presentTexture : _hostFrameTexture;
            var width = _presentTexture != 0 ? _presentWidth : _hostFrameWidth;
            var height = _presentTexture != 0 ? _presentHeight : _hostFrameHeight;
            if (texture != 0 && width != 0 && height != 0)
            {
                EncodePresent(encoder, texture, width, height, _presentTexture != 0 && _presentIsHdr);
            }

            MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
            MetalNative.SendVoid(commandBuffer, MetalNative.Selector("presentDrawable:"), drawable);
            MetalNative.SendVoid(commandBuffer, MetalNative.Selector("commit"));
            PerfOverlay.RecordPresent();
            ReportPresentationStatistics();
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(pool);
        }
    }

    // A published flip becomes the presented frame once its submission completes.
    // Promotion also happens on publication, so a frame that finished while the guest
    // was already drawing the next one is still shown instead of being replaced
    // unseen: with one slot and a completion wait, the newest frame is never ready
    // and the display starves.
    private static void AdoptPendingFrame()
    {
        lock (_frameGate)
        {
            if (!TryPromotePendingLocked())
            {
                Interlocked.Increment(ref _adoptionsBlocked);
            }
        }
    }

    private static readonly AutoResetEvent _flipPacerSignal = new(false);

    // Wakes the pacer: a frame was published or a flip submission completed.
    internal static void SignalFlipPacer() => _flipPacerSignal.Set();

    // Presents each queued flip at the refresh video-out allows, independent of the
    // window's drawable cadence: the guest's flip event and buffer release follow
    // presentation, so a frame promoted a host tick late costs the guest a refresh.
    private static void RunFlipPacer()
    {
        _ = SharpEmu.HLE.Host.HostThreadQualityOfService.TryApplyToCurrentThread(
            SharpEmu.HLE.Host.HostThreadQos.UserInteractive);
        while (Volatile.Read(ref _host) is { } host)
        {
            var wakeAt = long.MaxValue;
            var waitingForGpu = false;
            lock (_frameGate)
            {
                while (TryPromotePendingLocked())
                {
                }

                if (_pendingFrames.TryPeek(out var frame))
                {
                    if (host.Completions.SignaledValue < frame.Serial)
                    {
                        waitingForGpu = true;
                    }
                    else
                    {
                        wakeAt = VideoOutBackendServices.NextFlipPresentTimestamp(
                            frame.RequestId, System.Diagnostics.Stopwatch.GetTimestamp());
                    }
                }
            }

            if (waitingForGpu)
            {
                // The completion thread signals flips; the short timeout covers frames
                // whose submission carries no flip completion.
                _flipPacerSignal.WaitOne(1);
            }
            else if (wakeAt == long.MaxValue)
            {
                _flipPacerSignal.WaitOne(wakeAt == long.MaxValue && _pendingFrames.Count > 0 ? 1 : 100);
            }
            else
            {
                SleepUntil(wakeAt);
            }
        }
    }

    // Coarse sleep for the bulk (macOS overshoots by up to ~1.5 ms), then a spin.
    private static void SleepUntil(long target)
    {
        var frequency = System.Diagnostics.Stopwatch.Frequency;
        while (true)
        {
            var remaining = target - System.Diagnostics.Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return;
            }

            var microseconds = remaining * 1_000_000 / frequency;
            if (microseconds > 2000)
            {
                _flipPacerSignal.WaitOne((int)((microseconds - 1500) / 1000));
                return;
            }

            Thread.SpinWait(64);
        }
    }

    // Caller holds the frame gate. Moves the oldest pending frame into the presented
    // slot once the GPU finished the submission that drew it and video-out lets it
    // reach the display (vblank, flip rate and flip mode).
    private static bool TryPromotePendingLocked()
    {
        if (!_pendingFrames.TryPeek(out var frame))
        {
            return false;
        }

        var host = Volatile.Read(ref _host);
        if (host is not null && host.Completions.SignaledValue < frame.Serial)
        {
            return false;
        }

        if (frame.RequestId != 0 &&
            !VideoOutBackendServices.CanPresentFlip(frame.RequestId, System.Diagnostics.Stopwatch.GetTimestamp()))
        {
            return false;
        }

        _ = _pendingFrames.Dequeue();
        if (_presentTexture != 0)
        {
            MetalNative.SendVoid(_presentTexture, MetalNative.Selector("release"));
        }

        _presentTexture = frame.Texture;
        _presentWidth = frame.Width;
        _presentHeight = frame.Height;
        _presentIsHdr = frame.IsHdr;
        Interlocked.Increment(ref _presentedGuestFrames);
        PerfOverlay.RecordGuestPresent();
        if (frame.RequestId != 0)
        {
            VideoOutBackendServices.MarkFlipPresented(frame.RequestId);
            // A flip packet suspended on a full queue can run now. On the worker
            // itself the caller is the slot check, which proceeds anyway.
            if (_relay is { IsGpuQueueThread: false } relay)
            {
                relay.TryPost(static () => Volatile.Read(ref _host)?.RetryBlockedFlips());
            }
        }

        return true;
    }

    private static unsafe void UploadPendingHostFrame()
    {
        byte[]? frame;
        uint width;
        uint height;
        lock (_frameGate)
        {
            frame = _pendingHostFrame;
            width = _pendingHostWidth;
            height = _pendingHostHeight;
            _pendingHostFrame = null;
        }

        if (frame is null || width == 0 || height == 0)
        {
            return;
        }

        if (_hostFrameTexture == 0 || _hostFrameWidth != width || _hostFrameHeight != height)
        {
            if (_hostFrameTexture != 0)
            {
                MetalNative.SendVoid(_hostFrameTexture, MetalNative.Selector("release"));
            }

            var descriptor = MetalNative.SendTextureDescriptor(
                MetalNative.Class("MTLTextureDescriptor"),
                MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
                (nuint)PixelFormatBgra8Unorm,
                width,
                height,
                mipmapped: false);
            _hostFrameTexture = MetalNative.Send(
                _device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
            _hostFrameWidth = width;
            _hostFrameHeight = height;
        }

        if (_hostFrameTexture == 0)
        {
            return;
        }

        fixed (byte* pixels = frame)
        {
            MetalNative.SendReplaceRegion(
                _hostFrameTexture,
                MetalNative.Selector("replaceRegion:mipmapLevel:withBytes:bytesPerRow:"),
                new MtlRegion { Width = width, Height = height, Depth = 1 },
                0,
                (nint)pixels,
                width * 4);
        }
    }

    private static nint CreateClearPass(nint targetTexture)
    {
        var pass = MetalNative.Send(
            MetalNative.Class("MTLRenderPassDescriptor"), MetalNative.Selector("renderPassDescriptor"));
        var attachment = MetalNative.SendAtIndex(
            MetalNative.Send(pass, MetalNative.Selector("colorAttachments")),
            MetalNative.Selector("objectAtIndexedSubscript:"),
            0);
        MetalNative.SendVoid(attachment, MetalNative.Selector("setTexture:"), targetTexture);
        MetalV2Native.SendVoidValue(attachment, MetalNative.Selector("setLoadAction:"), Mtl.LoadActionClear);
        MetalV2Native.SendVoidValue(attachment, MetalNative.Selector("setStoreAction:"), Mtl.StoreActionStore);
        MetalNative.SendVoidClearColor(
            attachment,
            MetalNative.Selector("setClearColor:"),
            new MtlClearColor { Red = 0, Green = 0, Blue = 0, Alpha = 1 });
        return pass;
    }

    private static void EncodePresent(nint encoder, nint texture, uint frameWidth, uint frameHeight, bool isHdr)
    {
        if (_layerHdrActive)
        {
            EncodeHdrPresentState(encoder, _hdrPresentPipeline, isHdr, _edrCurrentHeadroom);
        }
        else
        {
            MetalNative.SendVoid(encoder, MetalNative.Selector("setRenderPipelineState:"), _presentPipeline);
        }

        var scaleX = _drawableWidth / frameWidth;
        var scaleY = _drawableHeight / frameHeight;
        var viewportWidth = _drawableWidth;
        var viewportHeight = _drawableHeight;
        if (_videoOptions.ScalingMode != HostScalingMode.Stretch)
        {
            var scale = _videoOptions.ScalingMode == HostScalingMode.Cover
                ? Math.Max(scaleX, scaleY)
                : Math.Min(scaleX, scaleY);
            if (_videoOptions.ScalingMode == HostScalingMode.Integer && scale >= 1)
            {
                scale = Math.Floor(scale);
            }

            viewportWidth = frameWidth * scale;
            viewportHeight = frameHeight * scale;
        }

        MetalNative.SendVoidViewport(
            encoder,
            MetalNative.Selector("setViewport:"),
            new MtlViewport
            {
                OriginX = (_drawableWidth - viewportWidth) * 0.5,
                OriginY = (_drawableHeight - viewportHeight) * 0.5,
                Width = viewportWidth,
                Height = viewportHeight,
                ZNear = 0,
                ZFar = 1,
            });
        MetalNative.SendSetAtIndex(
            encoder, MetalNative.Selector("setFragmentTexture:atIndex:"), texture, 0);
        MetalNative.SendSetAtIndex(
            encoder, MetalNative.Selector("setFragmentSamplerState:atIndex:"), _presentSampler, 0);
        MetalNative.SendDrawPrimitives(
            encoder, MetalNative.Selector("drawPrimitives:vertexStart:vertexCount:"), Mtl.PrimitiveTriangle, 0, 3);
    }

    private static bool TryCreatePresentPipeline(
        nint device,
        out nint pipeline,
        out string error,
        string? fragmentSource = null,
        string fragmentEntry = "present_fs",
        ulong pixelFormat = PixelFormatBgra8Unorm)
    {
        pipeline = 0;
        error = string.Empty;
        var options = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLCompileOptions"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        // The present shaders are hand-written and keep Metal's default fast math;
        // only the language version is pinned, so every library in the backend is
        // compiled against the same MSL version.
        MetalNative.SetMslLanguageVersion(options);
        nint vertexLibrary = 0;
        nint fragmentLibrary = 0;
        try
        {
            nint compileError = 0;
            vertexLibrary = MetalNative.Send(
                device,
                MetalNative.Selector("newLibraryWithSource:options:error:"),
                MetalNative.NsString(MslFixedShaders.CreateFullscreenVertex(1)),
                options,
                ref compileError);
            fragmentLibrary = MetalNative.Send(
                device,
                MetalNative.Selector("newLibraryWithSource:options:error:"),
                MetalNative.NsString(fragmentSource ?? MslFixedShaders.CreatePresentFragment()),
                options,
                ref compileError);
            if (vertexLibrary == 0 || fragmentLibrary == 0)
            {
                error = MetalNative.DescribeError(compileError);
                return false;
            }

            var newFunction = MetalNative.Selector("newFunctionWithName:");
            var vertexFunction = MetalNative.Send(vertexLibrary, newFunction, MetalNative.NsString("fullscreen_vs"));
            var fragmentFunction = MetalNative.Send(fragmentLibrary, newFunction, MetalNative.NsString(fragmentEntry));
            if (vertexFunction == 0 || fragmentFunction == 0)
            {
                error = "the present entry points are missing from the compiled libraries";
                return false;
            }

            var descriptor = MetalNative.Send(
                MetalNative.Send(MetalNative.Class("MTLRenderPipelineDescriptor"), MetalNative.Selector("alloc")),
                MetalNative.Selector("init"));
            MetalNative.SendVoid(descriptor, MetalNative.Selector("setVertexFunction:"), vertexFunction);
            MetalNative.SendVoid(descriptor, MetalNative.Selector("setFragmentFunction:"), fragmentFunction);
            var attachment = MetalNative.SendAtIndex(
                MetalNative.Send(descriptor, MetalNative.Selector("colorAttachments")),
                MetalNative.Selector("objectAtIndexedSubscript:"),
                0);
            MetalV2Native.SendVoidValue(
                attachment, MetalNative.Selector("setPixelFormat:"), (nuint)pixelFormat);
            nint pipelineError = 0;
            pipeline = MetalNative.Send(
                device,
                MetalNative.Selector("newRenderPipelineStateWithDescriptor:error:"),
                descriptor,
                ref pipelineError);
            MetalNative.SendVoid(descriptor, MetalNative.Selector("release"));
            MetalNative.SendVoid(vertexFunction, MetalNative.Selector("release"));
            MetalNative.SendVoid(fragmentFunction, MetalNative.Selector("release"));
            if (pipeline == 0)
            {
                error = MetalNative.DescribeError(pipelineError);
                return false;
            }

            return true;
        }
        finally
        {
            if (options != 0)
            {
                MetalNative.SendVoid(options, MetalNative.Selector("release"));
            }

            if (vertexLibrary != 0)
            {
                MetalNative.SendVoid(vertexLibrary, MetalNative.Selector("release"));
            }

            if (fragmentLibrary != 0)
            {
                MetalNative.SendVoid(fragmentLibrary, MetalNative.Selector("release"));
            }
        }
    }

    private static nint CreateLinearSampler(nint device)
    {
        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLSamplerDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        try
        {
            MetalV2Native.SendVoidValue(
                descriptor, MetalNative.Selector("setMinFilter:"), (nuint)Mtl.SamplerFilterLinear);
            MetalV2Native.SendVoidValue(
                descriptor, MetalNative.Selector("setMagFilter:"), (nuint)Mtl.SamplerFilterLinear);
            return MetalNative.Send(device, MetalNative.Selector("newSamplerStateWithDescriptor:"), descriptor);
        }
        finally
        {
            MetalNative.SendVoid(descriptor, MetalNative.Selector("release"));
        }
    }

    private static void ReportPresentationStatistics()
    {
        Interlocked.Increment(ref _hostPresents);
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
        var guestFrames = Interlocked.Exchange(ref _presentedGuestFrames, 0);
        var hostPresents = Interlocked.Exchange(ref _hostPresents, 0);
        _statsTimestamp = now;
        var guestFps = (guestFrames / seconds).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        var hostHz = (hostPresents / seconds).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        var window = seconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        var published = Interlocked.Exchange(ref _publishedGuestFrames, 0);
        var blocked = Interlocked.Exchange(ref _adoptionsBlocked, 0);
        // Where a flip stops. published=0 on its own cannot tell a title that never flips
        // from one whose every flip is released or discarded on the way to the presenter,
        // and those want opposite investigations.
        var flips = _host?.TakeFlipCounters() ?? default;
        Console.Error.WriteLine(
            $"[METAL-PRESENT] guest_frames={guestFrames} guest_fps={guestFps} host_presents={hostPresents} " +
            $"host_hz={hostHz} seconds={window} published={published} adopt_blocked={blocked} " +
            $"flip_attempts={flips.Attempts} flip_no_buffer={flips.WithoutBuffer} " +
            $"flip_discarded={flips.Discarded} flip_published={flips.Published} " +
            $"flip_refreshed={flips.Refreshed}");
    }

    // The flip seam the command stream publishes through.
    private sealed class FlipTarget : IMetalFlipTarget
    {
        // Flip packets suspend while the queue is full, as in the Vulkan presenter.
        public bool HasFlipSlot()
        {
            lock (_frameGate)
            {
                return _pendingFrames.Count < MaxPendingFrames || TryPromotePendingLocked();
            }
        }

        public void PublishGuestFrame(nint texture, uint width, uint height, ulong requestId, ulong serial, bool isHdr)
        {
            // The presenter owns one retain per queued or presented frame.
            MetalNative.Send(texture, MetalNative.Selector("retain"));
            lock (_frameGate)
            {
                _pendingFrames.Enqueue(new PendingFrame(texture, width, height, requestId, serial, isHdr));
                _ = TryPromotePendingLocked();
                _splashHidden = true;
            }

            SignalFlipPacer();

            Interlocked.Increment(ref _guestFrameSequence);
            Interlocked.Increment(ref _publishedGuestFrames);
        }
    }
}
