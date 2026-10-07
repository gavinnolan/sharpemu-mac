// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Adapters.Sdl;
using SharpEmu.HLE.Host;

namespace SharpEmu.Host.Windows;

internal sealed class WindowsHostPlatform : IHostPlatform
{
    public IHostMemory Memory { get; } = new WindowsHostMemory();

    public IHostThreading Threading { get; } = new WindowsHostThreading();

    public IHostSymbolResolver Symbols { get; } = new WindowsHostSymbolResolver();

    // SDL remains the qualified default; WindowsWaveOutAudio is the native
    // winmm alternative, kept for the latency/underrun comparison that a
    // default change requires.
    public IHostAudioOutput Audio { get; } = new SdlHostAudio();

    public IHostInput Input { get; } = new WindowHostInput();
}
