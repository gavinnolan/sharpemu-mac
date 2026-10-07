// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Host.Linux;

/// <summary>
/// Native ALSA output. Not the qualified default: SDL remains the Linux
/// default output (see <see cref="LinuxHostPlatform"/>), and promoting this
/// one requires latency, underrun, clock, device-change and shutdown evidence.
/// Composed explicitly by a caller that wants to measure or select it.
/// </summary>
internal sealed class LinuxAlsaAudio : IHostAudioOutput
{
    public string BackendName => "alsa";

    public IHostAudioStream OpenStereoPcm16Stream(uint sampleRate, int maxQueuedPcmBytes = 32 * 1024) =>
        new LinuxAlsaAudioStream(sampleRate, maxQueuedPcmBytes);
}
