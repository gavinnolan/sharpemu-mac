// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.AvPlayer;

namespace SharpEmu.Libs.Media;

/// <summary>Frame delivery and layout services consumed by graphics presenters.</summary>
public static class MoviePresentationServices
{
    public static bool TryGetFallbackPresentationFrame(
        out byte[] pixels,
        out uint width,
        out uint height,
        out long serial) =>
        AvPlayerExports.TryGetFallbackPresentationFrame(out pixels, out width, out height, out serial);

    public static bool IsVideoBufferAddress(ulong address) =>
        AvPlayerExports.IsVideoBufferAddress(address);

    public static void SetPresentationSize(uint width, uint height) =>
        HostMovieBridge.SetPresentationSize(width, height);

    public static bool TryDecodeNextFrame(
        bool advanceClock,
        out byte[] pixels,
        out uint width,
        out uint height,
        out bool advanced,
        out long frameSerial,
        out string hostPath) =>
        HostMovieBridge.TryDecodeNextFrame(advanceClock, out pixels, out width, out height, out advanced, out frameSerial, out hostPath);

    public static int LumaLength(uint width, uint height) =>
        HostMovieYuv420.LumaLength(width, height);

    public static int ChromaLength(uint width, uint height) =>
        HostMovieYuv420.ChromaLength(width, height);

}
