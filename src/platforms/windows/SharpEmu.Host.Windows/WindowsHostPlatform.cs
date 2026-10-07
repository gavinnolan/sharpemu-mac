// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Host.Windows;

internal sealed class WindowsHostPlatform : IHostPlatform
{
    internal WindowsHostPlatform(IHostAudioOutput audio) => Audio = audio;

    public IHostMemory Memory { get; } = new WindowsHostMemory();

    public IHostThreading Threading { get; } = new WindowsHostThreading();

    public IHostSymbolResolver Symbols { get; } = new WindowsHostSymbolResolver();

    public IHostAudioOutput Audio { get; }

    public IHostInput Input { get; } = new WindowHostInput();
}
