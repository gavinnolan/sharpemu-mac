// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Gpu.Metal.V2;

// HDR output: the CAMetalLayer switches between the plain BGRA8 drawable and an
// RGBA16Float extended-linear-sRGB one with EDR enabled, following the guest's
// display buffer the way the Vulkan presenter switches its swapchain. The policy
// lives in MetalV2HdrOutput; this part owns the layer, the pipeline and the
// screen's EDR state.
internal static partial class MetalV2Presenter
{
    private const string CoreGraphicsFramework = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    private static readonly object _hdrGate = new();
    private static bool _edrStateKnown;
    private static double _edrPotentialHeadroom = 1.0;

    // Main thread only.
    private static double _edrCurrentHeadroom = 1.0;
    private static nint _hdrPresentPipeline;
    private static nint _extendedLinearSrgb;
    private static nint _sdrColorSpace;
    private static bool _layerHdrActive;

    /// <summary>
    /// Whether the guest is told the display takes HDR. Like the Vulkan presenter, the
    /// answer waits for the window, since Auto depends on the screen it opened on.
    /// </summary>
    public static bool QueryHdrOutputSupport()
    {
        var options = _videoOptions;
        if (options.HdrMode == HostHdrMode.Off)
        {
            return false;
        }

        EnsureStarted((uint)options.Width, (uint)options.Height);
        // Auto that this backend does not allow is SDR on any screen, so the answer does
        // not wait for the window: on a busy host that wait held Demon's Souls' init for
        // fourteen seconds in sceVideoOutGetOutputStatus.
        if (!options.CanUseHdr(displayHdrEnabled: true, surfaceSupportsHdr: true))
        {
            return false;
        }

        lock (_hdrGate)
        {
            while (!_edrStateKnown && !IsPresenterGone())
            {
                Monitor.Wait(_hdrGate, 100);
            }

            return _edrStateKnown && !IsPresenterGone() && _hdrPresentPipeline != 0 &&
                   MetalV2HdrOutput.GuestHdrSupported(options, _edrPotentialHeadroom);
        }
    }

    private static bool IsPresenterGone()
    {
        lock (_gate)
        {
            return _closed || _startupFailure is not null || _closeRequested ||
                   HostSessionControl.IsShutdownRequested;
        }
    }

    private static void ReleaseHdrSupportWaiters()
    {
        lock (_hdrGate)
        {
            Monitor.PulseAll(_hdrGate);
        }
    }

    // Main thread, once the layer exists. Without the pipeline or the colour space the
    // layer stays SDR and the guest is told so.
    private static void InitializeHdrOutput(IMetalHostWindow window)
    {
        if (!TryCreateHdrPresentPipeline(_device, out _hdrPresentPipeline, out var error))
        {
            Console.Error.WriteLine($"[METAL-V2][WARN] The HDR present pipeline failed; output stays SDR: {error}");
            _hdrPresentPipeline = 0;
        }
        else if ((_extendedLinearSrgb = CreateNamedColorSpace(MetalV2HdrOutput.ColorSpaceName)) == 0)
        {
            Console.Error.WriteLine(
                $"[METAL-V2][WARN] {MetalV2HdrOutput.ColorSpaceName} is unavailable; output stays SDR.");
            MetalNative.SendVoid(_hdrPresentPipeline, MetalNative.Selector("release"));
            _hdrPresentPipeline = 0;
        }

        // The SDR layer's own colour space, put back whenever the layer leaves EDR.
        _sdrColorSpace = RetainColorSpace(MetalNative.Send(_metalLayer, MetalNative.Selector("colorspace")));
        RecordEdrHeadroom(window);
        Console.Error.WriteLine(
            $"[METAL-V2][INFO] Display EDR: headroom={_edrCurrentHeadroom.ToString("F3", CultureInfo.InvariantCulture)} " +
            $"potential={_edrPotentialHeadroom.ToString("F3", CultureInfo.InvariantCulture)} " +
            $"edr_display={MetalV2HdrOutput.IsEdrDisplay(_edrPotentialHeadroom)} " +
            $"hdr_path={_hdrPresentPipeline != 0}.");
    }

    // Main thread, once per present before the drawable is acquired, so the drawable
    // and the pipeline always agree on the pixel format.
    private static void UpdateOutputColor(IMetalHostWindow window)
    {
        RecordEdrHeadroom(window);
        var guestFlipsPq = VideoOutBackendServices.IsHdrOutputRequested;
        var wantHdr = _hdrPresentPipeline != 0 &&
                      MetalV2HdrOutput.WantsEdrLayer(_videoOptions.HdrMode, _edrPotentialHeadroom, guestFlipsPq);
        if (wantHdr == _layerHdrActive)
        {
            return;
        }

        _layerHdrActive = wantHdr;
        MetalV2Native.SendVoidValue(
            _metalLayer,
            MetalNative.Selector("setPixelFormat:"),
            (nuint)(wantHdr ? MetalV2HdrOutput.PixelFormat : PixelFormatBgra8Unorm));
        MetalNative.SendVoid(
            _metalLayer, MetalNative.Selector("setColorspace:"), wantHdr ? _extendedLinearSrgb : _sdrColorSpace);
        MetalNative.SendVoidBool(_metalLayer, MetalNative.Selector("setWantsExtendedDynamicRangeContent:"), wantHdr);
        Console.Error.WriteLine(
            $"[METAL-V2][INFO] Output color: requested={_videoOptions.HdrMode} " +
            $"edr_display={MetalV2HdrOutput.IsEdrDisplay(_edrPotentialHeadroom)} guest_hdr={guestFlipsPq} " +
            $"active={wantHdr} format={(wantHdr ? "RGBA16Float" : "BGRA8Unorm")} " +
            $"colorspace={(wantHdr ? MetalV2HdrOutput.ColorSpaceName : "none")} " +
            $"headroom={_edrCurrentHeadroom.ToString("F3", CultureInfo.InvariantCulture)} " +
            $"potential={_edrPotentialHeadroom.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    // The current headroom follows brightness and the screen the window is on; it
    // only rises above 1.0 once an EDR layer is on screen.
    private static void RecordEdrHeadroom(IMetalHostWindow window)
    {
        var (current, potential) = window.EdrHeadroom;
        _edrCurrentHeadroom = current;
        lock (_hdrGate)
        {
            if (_edrStateKnown && potential == _edrPotentialHeadroom)
            {
                return;
            }

            _edrPotentialHeadroom = potential;
            _edrStateKnown = true;
            Monitor.PulseAll(_hdrGate);
        }
    }

    internal static bool TryCreateHdrPresentPipeline(nint device, out nint pipeline, out string error) =>
        TryCreatePresentPipeline(
            device,
            out pipeline,
            out error,
            MslFixedShaders.CreatePresentHdrFragment(),
            "present_hdr_fs",
            MetalV2HdrOutput.PixelFormat);

    /// <summary>Binds the HDR present pipeline and its parameters for one frame.</summary>
    internal static unsafe void EncodeHdrPresentState(nint encoder, nint pipeline, bool pqSource, double headroom)
    {
        MetalNative.SendVoid(encoder, MetalNative.Selector("setRenderPipelineState:"), pipeline);
        var parameters = MetalV2HdrOutput.Parameters(pqSource, headroom);
        MetalNative.SendSetBytes(
            encoder,
            MetalNative.Selector("setFragmentBytes:length:atIndex:"),
            (nint)(&parameters),
            (nuint)sizeof(MetalV2HdrOutput.PresentHdrParameters),
            0);
    }

    private static unsafe nint RetainColorSpace(nint colorSpace)
    {
        if (colorSpace == 0 ||
            !NativeLibrary.TryLoad(CoreGraphicsFramework, out var coreGraphics) ||
            !NativeLibrary.TryGetExport(coreGraphics, "CGColorSpaceRetain", out var retain))
        {
            return 0;
        }

        return ((delegate* unmanaged<nint, nint>)retain)(colorSpace);
    }

    private static unsafe nint CreateNamedColorSpace(string name)
    {
        if (!NativeLibrary.TryLoad(CoreGraphicsFramework, out var coreGraphics) ||
            !NativeLibrary.TryGetExport(coreGraphics, name, out var nameSymbol) ||
            !NativeLibrary.TryGetExport(coreGraphics, "CGColorSpaceCreateWithName", out var create))
        {
            return 0;
        }

        var cfName = Marshal.ReadIntPtr(nameSymbol);
        return cfName == 0 ? 0 : ((delegate* unmanaged<nint, nint>)create)(cfName);
    }
}
