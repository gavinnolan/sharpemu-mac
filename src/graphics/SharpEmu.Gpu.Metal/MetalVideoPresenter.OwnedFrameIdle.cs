// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Gpu.Metal;

internal static partial class MetalVideoPresenter
{
    // Diagnostic policy experiment, off by default until native surface and
    // controlled gameplay pacing are qualified. Read once, outside frame work.
    private static readonly bool _ownedFrameIdleEnabled =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_OWNED_FRAME_IDLE") == "1" &&
        Environment.GetEnvironmentVariable("MTL_HUD_ENABLED") is null or "" or "0";
    private static readonly MetalOwnedFrameIdlePolicy _ownedFrameIdle = new();
    private static bool _idleAfterOwnedFrameSkip;
    private static long _ownedFrameSkippedTicks;

    private static void WriteOwnedIdleDiagnostic(string message)
    {
        try { Console.Error.WriteLine(message); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static void IdleAfterOwnedFrameSkip()
    {
        // MacHostWindow invokes this after RenderFrame has released all CPU
        // payload borrows, its autorelease pool and the presenter gate. Never
        // turn an unchanged-front-buffer path into a main-thread busy loop.
        if (_idleAfterOwnedFrameSkip) Thread.Sleep(1);
    }
}

internal sealed class MetalOwnedFrameIdlePolicy
{
    private bool _committed;
    private long _sequence;
    private nint _texture;
    private double _width, _height;
    private bool _overlayVisible, _metalHudVisible;

    internal void Invalidate() => _committed = false;

    internal void Commit(long sequence, nint texture, double width, double height,
        bool overlayVisible, bool metalHudVisible)
    {
        _committed = true;
        _sequence = sequence; _texture = texture; _width = width; _height = height;
        _overlayVisible = overlayVisible; _metalHudVisible = metalHudVisible;
    }

    internal bool CanSkip(long sequence, nint texture, double width, double height,
        bool immutableOwnedSource, bool surfaceRestored, bool overlayVisible, bool metalHudVisible) =>
        _committed && sequence >= 0 && texture != 0 && immutableOwnedSource &&
        sequence == _sequence && texture == _texture && width == _width && height == _height &&
        !surfaceRestored && !overlayVisible && !_overlayVisible && !metalHudVisible && !_metalHudVisible;
}
