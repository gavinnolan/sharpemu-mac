// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.Posix;

/// <summary>
/// ALSA audio output for Linux. The stream accepts the seam's interleaved
/// stereo PCM16 and paces the guest via device-queue backpressure.
///
/// macOS no longer comes through here: it has a native CoreAudio backend under
/// Host/MacOS, which <see cref="PosixHostPlatform"/> selects directly.
/// </summary>
internal sealed class PosixHostAudio : IHostAudioOutput
{
    public string BackendName => "alsa";

    public IHostAudioStream OpenStereoPcm16Stream(uint sampleRate, int maxQueuedPcmBytes = 32 * 1024)
        => new PosixAlsaAudioStream(sampleRate, maxQueuedPcmBytes);
}
