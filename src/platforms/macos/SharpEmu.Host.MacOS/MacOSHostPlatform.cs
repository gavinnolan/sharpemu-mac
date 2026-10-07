// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Host.MacOS;

internal sealed class MacOSHostPlatform : IHostPlatform
{
    internal MacOSHostPlatform(IHostAudioOutput audio) => Audio = audio;

    public IHostMemory Memory { get; } = new MacHostMemory();

    public IHostThreading Threading { get; } = new MacHostThreading();

    public IHostSymbolResolver Symbols { get; } = new MacHostSymbolResolver();

    // Defaulted by MacOSHost.Create to the native CoreAudio output: this
    // package does not reference SDL at all, so a macOS process never
    // initialises SDL audio.
    public IHostAudioOutput Audio { get; }

    public IHostInput Input { get; } = new WindowHostInput();
}
