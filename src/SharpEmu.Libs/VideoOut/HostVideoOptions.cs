// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

public enum HostWindowMode
{
    Windowed,
    Borderless,
    ExclusiveFullscreen,
}

public enum HostScalingMode
{
    Fit,
    Cover,
    Stretch,
    Integer,
}

public enum HostHdrMode
{
    Auto,
    On,
    Off,
}

public enum PerformanceOverlayCorner
{
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,
}

public enum PerformanceOverlayMode
{
    Full,
    Minimal,
    TitleBar,
}

public sealed record HostVideoOptions
{
    public static HostVideoOptions Default { get; } = new();

    public HostWindowMode WindowMode { get; init; } = HostWindowMode.Windowed;

    public HostScalingMode ScalingMode { get; init; } = HostScalingMode.Fit;

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public int DisplayIndex { get; init; }

    public int RefreshRate { get; init; }

    public bool VSync { get; init; } = true;

    public HostHdrMode HdrMode { get; init; } = HostHdrMode.Auto;

    public bool OverlayEnabled { get; init; } = true;
    public PerformanceOverlayCorner OverlayCorner { get; init; } = PerformanceOverlayCorner.TopRight;
    public PerformanceOverlayMode OverlayMode { get; init; } = PerformanceOverlayMode.TitleBar;

    // Whether HdrMode.Auto may turn HDR on: a presenter capability, not an OS property. The
    // Metal presenter registers false because it has no HDR (EDR) output path yet, so
    // reporting HDR would move titles onto render paths it cannot present.
    internal static bool AutoHdrAllowed => HostVideoHost.AutoHdrAllowed;

    public bool CanUseHdr(bool displayHdrEnabled, bool surfaceSupportsHdr) =>
        CanUseHdr(displayHdrEnabled, surfaceSupportsHdr, AutoHdrAllowed);

    internal bool CanUseHdr(bool displayHdrEnabled, bool surfaceSupportsHdr, bool autoAllowed) =>
        surfaceSupportsHdr && (HdrMode == HostHdrMode.On || HdrMode == HostHdrMode.Auto && autoAllowed && displayHdrEnabled);

    public HostVideoOptions Normalize() => this with
    {
        Width = Math.Clamp(Width, 640, 16384),
        Height = Math.Clamp(Height, 360, 16384),
        DisplayIndex = Math.Max(0, DisplayIndex),
        RefreshRate = Math.Clamp(RefreshRate, 0, 1000),
        HdrMode = Enum.IsDefined(HdrMode) ? HdrMode : HostHdrMode.Auto,
        OverlayCorner = Enum.IsDefined(OverlayCorner) ? OverlayCorner : PerformanceOverlayCorner.TopRight,
        OverlayMode = Enum.IsDefined(OverlayMode) ? OverlayMode : PerformanceOverlayMode.TitleBar,
    };
}

/// <summary>
/// The presenter-configuration seam. The composed graphics backend registers its presenter's
/// window configuration and HDR capability here at startup, so neither the video-out exports
/// nor this class names a concrete presenter or checks the host OS to find one.
/// </summary>
public static class HostVideoHost
{
    private static HostVideoOptions _currentOptions = HostVideoOptions.Default;
    private static bool _videoConfigured;
    private static Func<HostVideoOptions, bool>? _configurePresenter;
    private static Func<bool> _queryHdrOutputSupport = static () => false;
    private static bool _autoHdrAllowed = true;

    public static HostVideoOptions CurrentOptions => Volatile.Read(ref _currentOptions);

    /// <summary>
    /// Installs the presenter behind <see cref="TryConfigureVideo"/>. Called once from the
    /// application's composition root, next to <c>GuestGpu.Register</c>.
    /// </summary>
    /// <param name="configure">Applies the window/display options to the presenter before it starts.</param>
    /// <param name="queryHdrOutputSupport">Whether the presenter can actually drive an HDR
    /// swapchain right now; only consulted once <paramref name="configure"/> has succeeded.</param>
    /// <param name="autoHdrAllowed">Whether <see cref="HostHdrMode.Auto"/> may enable HDR at all
    /// on this presenter.</param>
    public static void RegisterPresenter(
        Func<HostVideoOptions, bool> configure,
        Func<bool> queryHdrOutputSupport,
        bool autoHdrAllowed)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(queryHdrOutputSupport);
        _configurePresenter = configure;
        _queryHdrOutputSupport = queryHdrOutputSupport;
        _autoHdrAllowed = autoHdrAllowed;
    }

    internal static bool AutoHdrAllowed => Volatile.Read(ref _autoHdrAllowed);

    internal static bool IsHdrOutputSupported =>
        Volatile.Read(ref _videoConfigured) && CurrentOptions.HdrMode != HostHdrMode.Off &&
        _queryHdrOutputSupport();

    public static bool TryConfigureVideo(HostVideoOptions options)
    {
        var normalized = options.Normalize();
        Volatile.Write(ref _currentOptions, normalized);
        var presenter = _configurePresenter ?? throw new InvalidOperationException(
            "No presenter was registered. The application's composition root must call " +
            "HostVideoHost.RegisterPresenter before configuring video output.");
        var configured = presenter(normalized);
        if (configured)
            Volatile.Write(ref _videoConfigured, true);
        return configured;
    }
}
