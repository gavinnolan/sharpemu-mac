// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Host.MacOS;

internal sealed class MacOSHostPlatform : IHostPlatform
{
    public IHostMemory Memory { get; } = new MacHostMemory();

    public IHostThreading Threading { get; } = new MacHostThreading();

    public IHostSymbolResolver Symbols { get; } = new MacHostSymbolResolver();

    // TODO(macos-audio): MacCoreAudioOutput merged separately. This package
    // must not reference SDL, so until the native CoreAudio output lands the
    // audio seam is a placeholder that fails loudly instead of silently
    // dropping guest audio.
    public IHostAudioOutput Audio { get; } = new UnavailableAudioOutput();

    public IHostInput Input { get; } = new WindowHostInput();

    private sealed class UnavailableAudioOutput : IHostAudioOutput
    {
        public string BackendName => "none";

        public IHostAudioStream OpenStereoPcm16Stream(uint sampleRate, int maxQueuedPcmBytes = 32 * 1024) =>
            throw new PlatformNotSupportedException(
                "The macOS host package has no audio output yet. The native CoreAudio " +
                "implementation (MacCoreAudioOutput) is being added separately; SDL audio is " +
                "deliberately not referenced from SharpEmu.Host.MacOS.");
    }
}
