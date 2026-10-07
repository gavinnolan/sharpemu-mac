// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Host.Linux;

internal sealed class LinuxHostPlatform : IHostPlatform
{
    internal LinuxHostPlatform(IHostAudioOutput audio) => Audio = audio;

    public IHostMemory Memory => HostMemoryAuthority.Current;

    public IHostThreading Threading { get; } = new LinuxHostThreading();

    public IHostSymbolResolver Symbols { get; } = new LinuxHostSymbolResolver();

    public IHostAudioOutput Audio { get; }

    public IHostInput Input { get; } = new WindowHostInput();
}
