// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host.MacOS;
using SharpEmu.HLE.Host.Sdl;

namespace SharpEmu.HLE.Host.Posix;

internal sealed class PosixHostPlatform : IHostPlatform
{
    public IHostMemory Memory { get; } = new PosixHostMemory();

    public IHostThreading Threading { get; } = new PosixHostThreading();

    public IHostSymbolResolver Symbols { get; } = new PosixHostSymbolResolver();

    // macOS is a fully native build: CoreAudio directly, so SDL audio is never
    // initialised in a macOS process.
    public IHostAudioOutput Audio { get; } =
        OperatingSystem.IsMacOS() ? new MacCoreAudioOutput() : new SdlHostAudio();

    public IHostInput Input { get; } = new WindowHostInput();
}
