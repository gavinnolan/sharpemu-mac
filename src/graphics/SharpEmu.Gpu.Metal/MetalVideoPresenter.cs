// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Platform.MacOS.Windowing;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Gpu.Metal;

/// <summary>
/// The Metal presenter renders into the CAMetalLayer of a native AppKit window
/// (<see cref="IMetalHostWindow"/>, implemented by
/// <c>SharpEmu.Platform.MacOS.Windowing.MacHostWindow</c>), which also owns keyboard
/// events and GameController gamepads. Nothing on this path touches SDL.
/// </summary>
internal static partial class MetalVideoPresenter
{
    private const nuint PixelFormatBgra8Unorm = (nuint)MtlPixelFormat.Bgra8Unorm;
    private const nuint LoadActionLoad = 1;
    private const nuint LoadActionClear = 2;
    private const nuint StoreActionStore = 1;
    private const nuint PrimitiveTypeTriangle = 3;
    private const nuint SamplerMinMagFilterLinear = 1;

    private sealed record Presentation(
        byte[]? Pixels,
        uint Width,
        uint Height,
        long Sequence,
        bool IsSplash,
        ulong GuestImageAddress = 0,
        long GuestImageVersion = 0,
        uint GuestImagePitch = 0,
        long RequiredGuestWorkSequence = 0,
        TranslatedGuestDraw? TranslatedDraw = null,
        GuestDrawKind DrawKind = GuestDrawKind.None,
        bool IsGuestFrame = false);

    private static readonly object _gate = new();
    private static Thread? _thread;
    private static bool _closed;
    private static bool _splashHidden;
    private static bool _closeRequested;
    private static Presentation? _latestPresentation;
    private static bool _loggedFirstPresentedFrame;
    private static int _titleRefreshCounter;
    private static string? _lastWindowTitle;

    // CPU-rasterized perf HUD (F1), blitted over the frame like the Vulkan
    // presenter does; the panel texture lives for the window's lifetime.
    private static nint _overlayTexture;
    private static readonly byte[] _overlayPixels =
        new byte[PerfOverlay.PixelBufferWidth * PerfOverlay.PixelBufferHeight * 4];

    // Presenter objects and per-frame present state, all used on the host window thread.
    private static nint _device;
    private static nint _commandQueue;
    private static nint _metalLayer;
    private static nint _presentPipeline;
    private static nint _presentSampler;
    private static IMetalHostWindow? _hostWindow;
    private static HostVideoOptions _videoOptions = HostVideoOptions.Default;
    private static double _drawableWidth;
    private static double _drawableHeight;
    private static nint _frameTexture;
    private static uint _frameTextureWidth;
    private static uint _frameTextureHeight;
    private static nint _presentTexture;
    private static uint _presentTextureWidth;
    private static uint _presentTextureHeight;
    private static nint _ownedVersionTexture;
    private static ulong _presentGuestAddress;
    private static long _presentedSequence = -1;
    private static long _guestFrameSequence = -1;
    private static long _lastCountedGuestFrameSequence = -1;
    private static long _presentationStatsTimestamp;
    private static long _guestFramesInStatsWindow;
    private static long _hostPresentsInStatsWindow;
    private static uint _windowWidth;
    private static uint _windowHeight;

    public static bool TryConfigureVideo(HostVideoOptions options)
    {
        lock (_gate)
        {
            if (_thread is not null)
            {
                return false;
            }

            _videoOptions = options.Normalize();
            return true;
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
            if (_closed || _thread is not null)
            {
                return;
            }
        }

        var hasSplash = PngSplashLoader.TryLoad(
            out var splashPixels,
            out var splashWidth,
            out var splashHeight);
        lock (_gate)
        {
            if (_closed || _thread is not null)
            {
                return;
            }

            WaitForInitialGuestCompletionLocked("presenter initialization");
            if (_closed || _thread is not null) return;
            _windowWidth = width;
            _windowHeight = height;
            if (_latestPresentation is null) PublishGuestPresentationLocked(_splashHidden
                ? new Presentation(CreateBlackFrame(width, height), width, height, 1, IsSplash: false)
                : hasSplash
                ? new Presentation(splashPixels, splashWidth, splashHeight, 1, IsSplash: true)
                : new Presentation(null, width, height, 0, IsSplash: false));
            StartPresenterLocked();
        }
    }

    public static void HideSplashScreen()
    {
        lock (_gate)
        {
            WaitForInitialGuestCompletionLocked("splash publication");
            _splashHidden = true;
            if (_closed || _latestPresentation is not { IsSplash: true } latest)
            {
                return;
            }

            PublishGuestPresentationLocked(new Presentation(
                CreateBlackFrame(latest.Width, latest.Height),
                latest.Width,
                latest.Height,
                latest.Sequence + 1,
                IsSplash: false));
            Console.Error.WriteLine("[LOADER][INFO] Metal VideoOut hid splash");
        }
    }

    public static void Submit(byte[] bgraFrame, uint width, uint height)
    {
        if (bgraFrame.Length != checked((int)(width * height * 4)))
        {
            return;
        }

        lock (_gate)
        {
            WaitForInitialGuestCompletionLocked("CPU presentation admission");
            if (_closed)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            PublishGuestPresentationLocked(new Presentation(bgraFrame, width, height, sequence, IsSplash: false, IsGuestFrame: true));
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            StartPresenterLocked();
        }
    }

    /// <summary>Asks a running presenter loop to close its window and return.</summary>
    public static void RequestClose()
    {
        Volatile.Write(ref _closeRequested, true);
        Volatile.Read(ref _hostWindow)?.Close();
    }

    private static void StartPresenterLocked()
    {
        WaitForInitialGuestCompletionLocked("presenter startup");
        if (_closed) return;
        if (_guestWorkFailure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (HostMainThread.IsAvailable)
        {
            _thread = Thread.CurrentThread;
            HostMainThread.SetShutdownRequestHandler(RequestClose);
            HostMainThread.Post(Run);
            return;
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SharpEmu Metal VideoOut",
        };
        _thread.Start();
    }

    private static void Run()
    {
        MetalPresentationProfile.Begin();
        try
        {
            if (_ownedFrameIdleEnabled)
                WriteOwnedIdleDiagnostic("[METAL-PRESENT] owned_frame_idle=enabled idle_ms=1 mutable_images=always_present");
            RunWindowLoop();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][ERROR] Metal VideoOut presenter failed: {exception}");
        }
        finally
        {
            Exception? shutdownFailure = null;
            try { CompletePresenterShutdown(); }
            catch (Exception failure) { shutdownFailure = failure; throw; }
            finally
            {
                try
                {
                    Exception? volumeCleanup = null;
                    try { ReleaseSingletonVolumeNativeOriginals(); }
                    catch (Exception failure) { volumeCleanup = failure; }
                    try { ReleaseSelectedStorageMipImages(); }
                    catch (Exception later) when (volumeCleanup is not null || shutdownFailure is not null)
                    {
                        try { (shutdownFailure ?? volumeCleanup)!.Data["MetalSelectedMipSecondaryCleanup"] = later; } catch { }
                    }
                    if (volumeCleanup is not null)
                    {
                        if (shutdownFailure is null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(volumeCleanup).Throw();
                        try { shutdownFailure.Data["MetalSingletonVolumeShutdownCleanup"] = volumeCleanup; } catch { }
                    }
                }
                finally
                {
                    MetalPresentationProfile.End();
                    if (_ownedFrameIdleEnabled)
                        WriteOwnedIdleDiagnostic($"[METAL-PRESENT] owned_frame_idle_summary skipped_ticks={_ownedFrameSkippedTicks}");
                }
            }
        }
    }

    private static void RunWindowLoop()
    {
        MetalNative.EnsureFrameworksLoaded();

        _device = MetalNative.MTLCreateSystemDefaultDevice();
        if (_device == 0)
        {
            Console.Error.WriteLine("[LOADER][ERROR] No Metal device available.");
            return;
        }

        // Mirror the Vulkan presenter: fold the selected GPU's name into the
        // window title. Without this the Metal title never gains the "· <GPU>"
        // suffix the Vulkan path shows.
        var deviceName = MetalNative.ReadNsString(
            MetalNative.Send(_device, MetalNative.Selector("name")));
        if (!string.IsNullOrEmpty(deviceName))
        {
            VideoOutBackendServices.SetSelectedGpuName(deviceName);
        }

        HostVideoOptions videoOptions;
        lock (_gate)
        {
            videoOptions = _videoOptions;
        }

        using var hostWindow = CreateHostWindow(VideoOutBackendServices.GetWindowTitle(), videoOptions);
        Volatile.Write(ref _hostWindow, hostWindow);
        var setupPool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            _metalLayer = hostWindow.CreateMetalLayer();
            ConfigureMetalLayer(_device, _metalLayer);
            _commandQueue = MetalNative.Send(_device, MetalNative.Selector("newCommandQueue"));
            if (!TryCreatePresentPipeline(_device, out _presentPipeline, out var pipelineError))
            {
                Console.Error.WriteLine($"[LOADER][ERROR] Metal present pipeline failed: {pipelineError}");
                return;
            }

            _presentSampler = CreateLinearSampler(_device);
            SyncDrawableSizeToLayer();
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(setupPool);
        }

        Console.Error.WriteLine("[LOADER][INFO] Metal VideoOut presenter started.");
        try
        {
            hostWindow.Run(
                static () => { },
                static _ => RenderFrameSafely(),
                static () => { },
                _ownedFrameIdleEnabled ? IdleAfterOwnedFrameSkip : null);
        }
        finally
        {
            Volatile.Write(ref _hostWindow, null);
            _metalLayer = 0;
            if (_overlayTexture != 0)
            {
                // Submitted command buffers retain the texture until their GPU work completes.
                MetalNative.SendVoid(_overlayTexture, MetalNative.Selector("release"));
                _overlayTexture = 0;
            }
        }

        if (hostWindow.ClosedByUser)
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Metal VideoOut window closed; requesting emulator shutdown.");
            VideoOutBackendServices.NotifyPresentationWindowClosed();
        }
    }

    /// <summary>
    /// Metal presentation is a macOS-only path (see
    /// <c>HostVideoHost.TryConfigureVideo</c>), and on macOS the window is always
    /// the native AppKit one — there is no SDL fallback to fall back to.
    /// </summary>
    private static IMetalHostWindow CreateHostWindow(string title, HostVideoOptions options)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "The Metal presenter requires macOS; other platforms use the Vulkan presenter.");
        }

        return new MacHostWindow(title, options, ToggleMetalPerformanceHud);
    }

    private static bool _metalHudVisible;

    /// <summary>
    /// Cmd+F1: Apple's Metal Performance HUD on the CAMetalLayer (plain F1 keeps
    /// the built-in CPU-rasterized perf overlay). Configured per Apple's
    /// "Customizing Metal Performance HUD": developerHUDProperties takes
    /// mode=default|disabled and logging=default, plus any MTL_HUD_* environment
    /// keys directly in the dictionary — all three HUD flags (enabled, per-frame
    /// logging, shader-compile logging) ride in one property set. Runs on the
    /// AppKit main thread, the same thread as the render loop.
    /// </summary>
    private static void ToggleMetalPerformanceHud()
    {
        var layer = _metalLayer;
        if (layer == 0)
        {
            return;
        }

        var setProperties = MetalNative.Selector("setDeveloperHUDProperties:");
        if (!MetalNative.SendBool(layer, MetalNative.Selector("respondsToSelector:"), setProperties))
        {
            Console.Error.WriteLine("[LOADER][WARN] Metal Performance HUD unavailable on this macOS.");
            return;
        }

        _metalHudVisible = !_metalHudVisible;
        var pool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            var properties = MetalNative.Send(
                MetalNative.Class("NSMutableDictionary"), MetalNative.Selector("dictionary"));
            var setObjectForKey = MetalNative.Selector("setObject:forKey:");
            if (_metalHudVisible)
            {
                var defaultValue = MetalNative.NsString("default");
                MetalNative.SendVoid(properties, setObjectForKey, defaultValue, MetalNative.NsString("mode"));
                MetalNative.SendVoid(properties, setObjectForKey, defaultValue, MetalNative.NsString("logging"));
                MetalNative.SendVoid(
                    properties,
                    setObjectForKey,
                    MetalNative.NsString("1"),
                    MetalNative.NsString("MTL_HUD_LOG_SHADER_ENABLED"));
            }
            else
            {
                MetalNative.SendVoid(
                    properties, setObjectForKey, MetalNative.NsString("disabled"), MetalNative.NsString("mode"));
            }

            MetalNative.SendVoid(layer, setProperties, properties);
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(pool);
        }

        Console.Error.WriteLine(
            $"[LOADER][INFO] Metal Performance HUD {(_metalHudVisible ? "shown" : "hidden")} (Cmd+F1).");
    }

    /// <summary>The native host loop drains guest work continuously. The method
    /// remains as the enqueue-side hook used by guest image synchronization.</summary>
    internal static void ScheduleGuestWorkDrain()
    {
    }

    private static void RenderFrameSafely() => RenderFrameSafely(RenderFrame);

    internal static void RenderFrameSafely(Action renderFrame)
    {
        try
        {
            renderFrame();
        }
        catch (Exception exception) when (_textureSourceGraphEnabled && _guestWorkFailure is null && exception is not GpuFatalException)
        {
            lock (_gate)
                if (_guestWorkFailure is null) LatchTextureSourceDrainFailureLocked(exception);
            throw GpuFatal.Fatal("Metal render frame failed.", exception);
        }
        catch (Exception exception) when (_guestWorkFailure is null && exception is not GpuFatalException)
        {
            // Minimized windows and unavailable drawables return normally.
            // An exception means execution failed, not a retryable frame miss.
            throw GpuFatal.Fatal("Metal render frame failed.", exception);
        }
    }

    private static void RenderFrame()
    {
        _idleAfterOwnedFrameSkip = false;
        PerfOverlay.UsePresentedGuestFrameTiming();
        var pool = MetalNative.objc_autoreleasePoolPush();
        GuestPresentationPayloadBorrow? presentationBorrow = null;
        Exception? frameFailure = null;
        try
        {
            if (Volatile.Read(ref _closeRequested))
            {
                return;
            }

            using (MetalPresentationProfile.Measure(MetalPresentationProfile.Phase.DrainGuestWork))
                DrainGuestWork(_device, _commandQueue);

            using (MetalPresentationProfile.Measure(MetalPresentationProfile.Phase.MaterializePresentation))
            {
                if (TryTakePresentationWithPayloadBorrow(_presentedSequence, out var presentation, out presentationBorrow))
                {
                    _presentedSequence = presentation.Sequence;
                    if (presentation.Pixels is not null)
                    {
                        UploadFrame(
                            _device,
                            presentation,
                            ref _frameTexture,
                            ref _frameTextureWidth,
                            ref _frameTextureHeight);
                        SwitchPresentSource(
                            _frameTexture,
                            _frameTextureWidth,
                            _frameTextureHeight,
                            ownsTexture: false,
                            ref _presentTexture,
                            ref _presentTextureWidth,
                            ref _presentTextureHeight,
                            ref _ownedVersionTexture);
                        _presentGuestAddress = 0;
                        _guestFrameSequence = presentation.IsGuestFrame && _frameTexture != 0 ? presentation.Sequence : -1;
                    }
                    else if (presentation.TranslatedDraw is not null ||
                             presentation.DrawKind != GuestDrawKind.None)
                    {
                        var drawTarget = ExecutePresentationDraw(_device, _commandQueue, presentation);
                        if (drawTarget != 0)
                        {
                            // Transient targets are pooled by the presenter; the
                            // present source borrows them.
                            SwitchPresentSource(
                                drawTarget,
                                presentation.Width,
                                presentation.Height,
                                ownsTexture: false,
                                ref _presentTexture,
                                ref _presentTextureWidth,
                                ref _presentTextureHeight,
                                ref _ownedVersionTexture);
                            _presentGuestAddress = 0;
                        }
                    }
                    else if (TryResolveGuestPresentation(
                                 _device,
                                 presentation,
                                 out var guestTexture,
                                 out var guestWidth,
                                 out var guestHeight,
                                 out var ownsGuestTexture))
                    {
                        // Captured versions are immutable and owned here; mutable
                        // address-keyed images are re-resolved at encode time so a
                        // write swapping the texture never leaves a stale handle.
                        SwitchPresentSource(
                            ownsGuestTexture ? guestTexture : 0,
                            guestWidth,
                            guestHeight,
                            ownsGuestTexture,
                            ref _presentTexture,
                            ref _presentTextureWidth,
                            ref _presentTextureHeight,
                            ref _ownedVersionTexture);
                        _presentGuestAddress = ownsGuestTexture ? 0 : presentation.GuestImageAddress;
                        _guestFrameSequence = presentation.IsGuestFrame ? presentation.Sequence : -1;
                    }
                }

                if (_presentGuestAddress != 0)
                {
                    // Re-resolve every frame: a guest-image write swaps the texture
                    // behind the address.
                    _presentTexture = 0;
                    lock (_gate)
                    {
                        if (_guestImages.TryGetValue(_presentGuestAddress, out var borrowed) &&
                            borrowed.Initialized)
                        {
                            Require2DGuestImage(borrowed, "per-frame guest presentation");
                            _presentTexture = borrowed.Texture;
                            _presentTextureWidth = borrowed.Width;
                            _presentTextureHeight = borrowed.Height;
                        }
                    }
                }

            }

            // The window title reflects late guest state (the game registers its
            // application name after boot) plus the GPU suffix; the Vulkan
            // presenter re-reads it, so refresh periodically here for parity.
            if ((++_titleRefreshCounter & 0x3F) == 0)
            {
                var title = VideoOutBackendServices.GetWindowTitle();
                if (!string.Equals(title, _lastWindowTitle, StringComparison.Ordinal))
                {
                    _lastWindowTitle = title;
                    Volatile.Read(ref _hostWindow)?.SetTitle(title);
                }
            }

            // The window is resizable, so the backing layer's bounds follow the
            // window while its drawable size does not — match them before asking
            // for a drawable, or nextDrawable keeps handing back the original
            // resolution and Core Animation stretches it (blurry, mis-scaled
            // overlay). No-op when the size is unchanged, i.e. almost every tick.
            var hostWindow = Volatile.Read(ref _hostWindow);
            var previousDrawableWidth = _drawableWidth;
            var previousDrawableHeight = _drawableHeight;
            var surfaceRestored = hostWindow?.ConsumeSurfaceRestore() == true;
            if (surfaceRestored)
            {
                _drawableWidth = 0;
                _drawableHeight = 0;
            }

            SyncDrawableSizeToLayer();
            if (_ownedFrameIdleEnabled && (surfaceRestored || previousDrawableWidth != _drawableWidth || previousDrawableHeight != _drawableHeight))
                _ownedFrameIdle.Invalidate(); // survives minimized / unavailable-drawable retries until a commit

            if (hostWindow?.IsMinimized == true)
            {
                if (_ownedFrameIdleEnabled) _ownedFrameIdle.Invalidate();
                return;
            }

            // Drain work, process host state and materialize a new source before
            // deciding this. Only this thread's unchanged CPU upload or immutable
            // captured version can keep the previous drawable. Mutable guest
            // images and pooled draw targets still acquire/encode every tick.
            var overlayVisible = PerfOverlay.DrawOnScreen;
            if (_ownedFrameIdleEnabled && _ownedFrameIdle.CanSkip(
                _guestFrameSequence, _presentTexture, _drawableWidth, _drawableHeight,
                _presentGuestAddress == 0 && _presentTexture != 0 &&
                    (_presentTexture == _frameTexture || _presentTexture == _ownedVersionTexture),
                surfaceRestored, overlayVisible, _metalHudVisible))
            {
                _ownedFrameSkippedTicks++;
                _idleAfterOwnedFrameSkip = true;
                return; // finally releases the CPU borrow and autorelease pool
            }

            nint drawable;
            using (MetalPresentationProfile.Measure(MetalPresentationProfile.Phase.AcquireDrawable))
                drawable = MetalNative.Send(_metalLayer, MetalNative.Selector("nextDrawable"));
            if (drawable == 0)
            {
                if (_ownedFrameIdleEnabled) _ownedFrameIdle.Invalidate();
                // No free drawable this tick; the next host iteration retries.
                return;
            }

            if (_presentTexture != 0 && !_loggedFirstPresentedFrame)
            {
                _loggedFirstPresentedFrame = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Metal VideoOut presenting {_presentTextureWidth}x{_presentTextureHeight}.");
            }

            using (MetalPresentationProfile.Measure(MetalPresentationProfile.Phase.EncodeAndCommit))
            {
                var drawableTexture = MetalNative.Send(drawable, MetalNative.Selector("texture"));
                var commandBuffer = MetalNative.Send(_commandQueue, MetalNative.Selector("commandBuffer"));
                if (drawableTexture == 0 || commandBuffer == 0)
                    throw new InvalidOperationException("Metal presentation requires a drawable texture and command buffer.");
                var pass = CreateClearPass(
                    drawableTexture,
                    new MtlClearColor { Red = 0, Green = 0, Blue = 0, Alpha = 1 });
                if (pass == 0)
                    throw new InvalidOperationException("Metal presentation cannot create its render pass.");
                var encoder = MetalNative.Send(
                    commandBuffer, MetalNative.Selector("renderCommandEncoderWithDescriptor:"), pass);
                if (encoder == 0)
                    throw new InvalidOperationException("Metal presentation cannot create its render encoder.");
                if (_presentTexture != 0)
                {
                    EncodePresent(
                        encoder,
                        _presentPipeline,
                        _presentSampler,
                        _presentTexture,
                        _presentTextureWidth,
                        _presentTextureHeight,
                        _drawableWidth,
                        _drawableHeight);
                }

                if (overlayVisible)
                {
                    EncodeOverlay(encoder);
                }

                MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
                MetalNative.SendVoid(commandBuffer, MetalNative.Selector("presentDrawable:"), drawable);
                MetalNative.SendVoid(commandBuffer, MetalNative.Selector("commit"));
            }
            if (_ownedFrameIdleEnabled)
                _ownedFrameIdle.Commit(_guestFrameSequence, _presentTexture, _drawableWidth, _drawableHeight, overlayVisible, _metalHudVisible);
            PerfOverlay.RecordPresent();
            if (_presentTexture != 0 && _guestFrameSequence >= 0 &&
                _guestFrameSequence != _lastCountedGuestFrameSequence)
            {
                _lastCountedGuestFrameSequence = _guestFrameSequence;
                PerfOverlay.RecordGuestPresent();
                MetalPresentationProfile.RecordGuestCommit(_guestFrameSequence);
                _guestFramesInStatsWindow++;
            }
            _hostPresentsInStatsWindow++;
            var statsNow = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_presentationStatsTimestamp == 0)
            {
                _presentationStatsTimestamp = statsNow;
            }
            else if (statsNow - _presentationStatsTimestamp >= 2 * System.Diagnostics.Stopwatch.Frequency)
            {
                var seconds = (statsNow - _presentationStatsTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
                Console.Error.WriteLine(FormattableString.Invariant(
                    $"[METAL-PRESENT] guest_frames={_guestFramesInStatsWindow} guest_fps={_guestFramesInStatsWindow / seconds:F2} host_presents={_hostPresentsInStatsWindow} host_hz={_hostPresentsInStatsWindow / seconds:F2} seconds={seconds:F3}"));
                _presentationStatsTimestamp = statsNow;
                _guestFramesInStatsWindow = 0;
                _hostPresentsInStatsWindow = 0;
            }
        }
        catch (Exception exception)
        {
            frameFailure = exception;
            throw;
        }
        finally
        {
            try { ReleaseGuestPresentationPayloadBorrow(presentationBorrow, frameFailure); }
            finally { MetalNative.objc_autoreleasePoolPop(pool); }
        }
    }

    // Uses the present pipeline with a viewport at the selected corner.
    private static void EncodeOverlay(nint encoder)
    {
        if (_overlayTexture == 0)
        {
            var descriptor = MetalNative.SendTextureDescriptor(
                MetalNative.Class("MTLTextureDescriptor"),
                MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
                PixelFormatBgra8Unorm,
                (nuint)PerfOverlay.DisplayWidth,
                (nuint)PerfOverlay.DisplayHeight,
                mipmapped: false);
            _overlayTexture = MetalNative.Send(
                _device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
            if (_overlayTexture == 0)
            {
                return;
            }
        }

        int pendingWork;
        lock (_gate)
        {
            pendingWork = _pendingGuestWorkCount;
        }

        PerfOverlay.Fill(_overlayPixels, pendingWork, 0);
        ReplaceTextureContents(
            _overlayTexture,
            (uint)PerfOverlay.DisplayWidth,
            (uint)PerfOverlay.DisplayHeight,
            _overlayPixels,
            PerfOverlay.PixelBufferWidth,
            bytesPerPixel: 4);

        var rectangle = PerfOverlay.GetRectangle((int)_drawableWidth, (int)_drawableHeight);
        var panelWidth = rectangle.Width;
        var panelHeight = rectangle.Height;
        if (panelWidth <= 0 || panelHeight <= 0)
        {
            return;
        }

        MetalNative.SendVoid(encoder, MetalNative.Selector("setRenderPipelineState:"), _presentPipeline);
        MetalNative.SendVoidViewport(
            encoder,
            MetalNative.Selector("setViewport:"),
            new MtlViewport
            {
                OriginX = rectangle.Left,
                OriginY = rectangle.Top,
                Width = panelWidth,
                Height = panelHeight,
                ZNear = 0,
                ZFar = 1,
            });
        MetalNative.SendSetAtIndex(
            encoder, MetalNative.Selector("setFragmentTexture:atIndex:"), _overlayTexture, 0);
        MetalNative.SendSetAtIndex(
            encoder, MetalNative.Selector("setFragmentSamplerState:atIndex:"), _presentSampler, 0);
        MetalNative.SendDrawPrimitives(
            encoder,
            MetalNative.Selector("drawPrimitives:vertexStart:vertexCount:"),
            PrimitiveTypeTriangle,
            0,
            3);
    }

    private static void ConfigureMetalLayer(nint device, nint layer)
    {
        MetalNative.SendVoid(layer, MetalNative.Selector("setDevice:"), device);
        MetalNative.Send(layer, MetalNative.Selector("setPixelFormat:"), (nint)PixelFormatBgra8Unorm);
        MetalNative.SendVoidBool(layer, MetalNative.Selector("setOpaque:"), true);
        MetalNative.SendVoidBool(layer, MetalNative.Selector("setFramebufferOnly:"), true);

        var setDisplaySync = MetalNative.Selector("setDisplaySyncEnabled:");
        if (MetalNative.SendBool(layer, MetalNative.Selector("respondsToSelector:"), setDisplaySync))
        {
            MetalNative.SendVoidBool(layer, setDisplaySync, _videoOptions.VSync);
        }
    }

    /// <summary>Keeps the host window's CAMetalLayer drawable in host pixels as the
    /// window resizes or moves between displays with different scale factors.</summary>
    private static void SyncDrawableSizeToLayer()
    {
        var hostWindow = Volatile.Read(ref _hostWindow);
        if (hostWindow is null || _metalLayer == 0)
        {
            return;
        }

        var pixelSize = hostWindow.PixelSize;
        var width = (double)pixelSize.Width;
        var height = (double)pixelSize.Height;

        // Retina hosts often present at 3840x2160 into a 1920x1080 window.
        // Cap is opt-in: defaulting it on silently drops present resolution for
        // every Metal title. SHARPEMU_METAL_CAP_DRAWABLE=1 enables the long-edge
        // cap; SHARPEMU_METAL_FULL_DRAWABLE=1 remains a no-op when the cap is off.
        if (string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_METAL_CAP_DRAWABLE"),
                "1",
                StringComparison.Ordinal) &&
            !string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_METAL_FULL_DRAWABLE"),
                "1",
                StringComparison.Ordinal))
        {
            const double maxLongEdge = 1920.0;
            var longEdge = Math.Max(width, height);
            if (longEdge > maxLongEdge)
            {
                var scale = maxLongEdge / longEdge;
                width = Math.Round(width * scale);
                height = Math.Round(height * scale);
            }
        }

        if (width == _drawableWidth && height == _drawableHeight)
        {
            return;
        }

        _drawableWidth = width;
        _drawableHeight = height;
        MetalNative.SendVoidSize(
            _metalLayer,
            MetalNative.Selector("setDrawableSize:"),
            new CGSize { Width = width, Height = height });
    }

    private static bool TryCreatePresentPipeline(nint device, out nint pipeline, out string error)
    {
        pipeline = 0;
        var dbg = Environment.GetEnvironmentVariable("SHARPEMU_METAL_DBG");
        var fragmentSource = dbg switch
        {
            "solid" => MslFixedShaders.CreateSolidFragment(0f, 1f, 0f, 1f),
            "uv" => MslFixedShaders.CreateAttributeFragment(0),
            _ => MslFixedShaders.CreatePresentFragment(),
        };
        if (!TryCompileLibrary(device, MslFixedShaders.CreateFullscreenVertex(1), out var vertexLibrary, out error) ||
            !TryCompileLibrary(device, fragmentSource, out var fragmentLibrary, out error))
        {
            return false;
        }

        var selNewFunction = MetalNative.Selector("newFunctionWithName:");
        var fragmentEntry = dbg switch { "solid" => "solid_fs", "uv" => "attribute_fs", _ => "present_fs" };
        var vertexFunction = MetalNative.Send(vertexLibrary, selNewFunction, MetalNative.NsString("fullscreen_vs"));
        var fragmentFunction = MetalNative.Send(fragmentLibrary, selNewFunction, MetalNative.NsString(fragmentEntry));
        if (vertexFunction == 0 || fragmentFunction == 0)
        {
            error = "present shader entry points missing from the compiled libraries";
            return false;
        }

        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLRenderPipelineDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setVertexFunction:"), vertexFunction);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setFragmentFunction:"), fragmentFunction);
        var colorAttachment = MetalNative.SendAtIndex(
            MetalNative.Send(descriptor, MetalNative.Selector("colorAttachments")),
            MetalNative.Selector("objectAtIndexedSubscript:"),
            0);
        MetalNative.Send(colorAttachment, MetalNative.Selector("setPixelFormat:"), (nint)PixelFormatBgra8Unorm);

        nint nsError = 0;
        pipeline = MetalNative.Send(
            device,
            MetalNative.Selector("newRenderPipelineStateWithDescriptor:error:"),
            descriptor,
            ref nsError);
        if (pipeline == 0)
        {
            error = MetalNative.DescribeError(nsError);
            return false;
        }

        return true;
    }

    private static bool TryCompileLibrary(nint device, string source, out nint library, out string error)
    {
        error = string.Empty;

        // Fast-math off everywhere for parity with translated guest shaders,
        // whose GCN float semantics do not survive it.
        var options = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLCompileOptions"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        MetalNative.SendVoidBool(options, MetalNative.Selector("setFastMathEnabled:"), false);

        nint nsError = 0;
        library = MetalNative.Send(
            device,
            MetalNative.Selector("newLibraryWithSource:options:error:"),
            MetalNative.NsString(source),
            options,
            ref nsError);
        if (library == 0)
        {
            error = MetalNative.DescribeError(nsError);
            return false;
        }

        return true;
    }

    private static nint CreateLinearSampler(nint device)
    {
        return CreateOwnedSamplerState(CreateNativeSamplerDescriptor, ConfigureLinearSamplerDescriptor,
            descriptor => MetalNative.Send(device, MetalNative.Selector("newSamplerStateWithDescriptor:"), descriptor),
            ReleaseOwnedSamplerObject);
    }

    private static void ConfigureLinearSamplerDescriptor(nint descriptor)
    {
        MetalNative.Send(descriptor, MetalNative.Selector("setMinFilter:"), (nint)SamplerMinMagFilterLinear);
        MetalNative.Send(descriptor, MetalNative.Selector("setMagFilter:"), (nint)SamplerMinMagFilterLinear);
    }

    private static void UploadFrame(
        nint device,
        Presentation presentation,
        ref nint frameTexture,
        ref uint textureWidth,
        ref uint textureHeight)
    {
        if (frameTexture == 0 || textureWidth != presentation.Width || textureHeight != presentation.Height)
        {
            if (frameTexture != 0)
            {
                MetalNative.SendVoid(frameTexture, MetalNative.Selector("release"));
            }

            var descriptor = MetalNative.SendTextureDescriptor(
                MetalNative.Class("MTLTextureDescriptor"),
                MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
                PixelFormatBgra8Unorm,
                presentation.Width,
                presentation.Height,
                mipmapped: false);
            // Shared (0): CPU-uploaded frame, GPU-sampled by the present pass;
            // the Managed default reads stale on unified memory.
            MetalNative.Send(descriptor, MetalNative.Selector("setStorageMode:"), (nint)0);
            frameTexture = MetalNative.Send(device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
            textureWidth = presentation.Width;
            textureHeight = presentation.Height;
        }

        ReplaceTextureContents(
            frameTexture,
            presentation.Width,
            presentation.Height,
            presentation.Pixels!,
            presentation.Width,
            bytesPerPixel: 4);
    }

    private static nint CreateClearPass(nint targetTexture, MtlClearColor clearColor)
    {
        var pass = MetalNative.Send(
            MetalNative.Class("MTLRenderPassDescriptor"),
            MetalNative.Selector("renderPassDescriptor"));
        var colorAttachment = MetalNative.SendAtIndex(
            MetalNative.Send(pass, MetalNative.Selector("colorAttachments")),
            MetalNative.Selector("objectAtIndexedSubscript:"),
            0);
        MetalNative.SendVoid(colorAttachment, MetalNative.Selector("setTexture:"), targetTexture);
        MetalNative.Send(colorAttachment, MetalNative.Selector("setLoadAction:"), (nint)LoadActionClear);
        MetalNative.Send(colorAttachment, MetalNative.Selector("setStoreAction:"), (nint)StoreActionStore);
        MetalNative.SendVoidClearColor(
            colorAttachment,
            MetalNative.Selector("setClearColor:"),
            clearColor);
        return pass;
    }

    private static void EncodePresent(
        nint encoder,
        nint pipeline,
        nint sampler,
        nint frameTexture,
        uint frameWidth,
        uint frameHeight,
        double drawableWidth,
        double drawableHeight)
    {
        MetalNative.SendVoid(encoder, MetalNative.Selector("setRenderPipelineState:"), pipeline);

        var scaleX = drawableWidth / frameWidth;
        var scaleY = drawableHeight / frameHeight;
        var viewportWidth = drawableWidth;
        var viewportHeight = drawableHeight;
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
                OriginX = (drawableWidth - viewportWidth) * 0.5,
                OriginY = (drawableHeight - viewportHeight) * 0.5,
                Width = viewportWidth,
                Height = viewportHeight,
                ZNear = 0,
                ZFar = 1,
            });

        MetalNative.SendSetAtIndex(
            encoder, MetalNative.Selector("setFragmentTexture:atIndex:"), frameTexture, 0);
        MetalNative.SendSetAtIndex(
            encoder, MetalNative.Selector("setFragmentSamplerState:atIndex:"), sampler, 0);
        MetalNative.SendDrawPrimitives(
            encoder,
            MetalNative.Selector("drawPrimitives:vertexStart:vertexCount:"),
            PrimitiveTypeTriangle,
            0,
            3);
    }

    private static byte[] CreateBlackFrame(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > 8192 || height > 8192)
        {
            width = 1;
            height = 1;
        }

        var pixels = GC.AllocateUninitializedArray<byte>(checked((int)(width * height * 4)));
        pixels.AsSpan().Clear();
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0xFF;
        }

        return pixels;
    }
}
