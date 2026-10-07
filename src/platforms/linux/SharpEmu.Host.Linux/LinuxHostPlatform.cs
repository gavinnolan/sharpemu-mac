// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Adapters.Sdl;
using SharpEmu.HLE.Host;

namespace SharpEmu.Host.Linux;

internal sealed class LinuxHostPlatform : IHostPlatform
{
    public IHostMemory Memory { get; } = new LinuxHostMemory();

    public IHostThreading Threading { get; } = new LinuxHostThreading();

    public IHostSymbolResolver Symbols { get; } = new LinuxHostSymbolResolver();

    // SDL remains the qualified default; LinuxAlsaAudio is the native
    // alternative, kept for the latency/underrun comparison that a default
    // change requires.
    public IHostAudioOutput Audio { get; } = new SdlHostAudio();

    public IHostInput Input { get; } = new WindowHostInput();
}
