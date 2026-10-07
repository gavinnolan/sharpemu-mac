// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Host.MacOS;

internal sealed class MacOSHostPlatform : IHostPlatform
{
    public IHostMemory Memory { get; } = new MacHostMemory();

    public IHostThreading Threading { get; } = new MacHostThreading();

    public IHostSymbolResolver Symbols { get; } = new MacHostSymbolResolver();

    // Native CoreAudio, not the SDL adapter: this package does not reference
    // SDL at all, so a macOS process never initialises SDL audio.
    public IHostAudioOutput Audio { get; } = new MacCoreAudioOutput();

    public IHostInput Input { get; } = new WindowHostInput();
}
