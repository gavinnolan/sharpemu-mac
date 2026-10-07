// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.Fonts.Abstractions;

namespace SharpEmu.Libs.Font;

/// <summary>The process composition root selects the guest font adapter before guest work begins.</summary>
public static class GuestFontServices
{
    private static IFontFactory? factory;
    public static void Initialize(IFontFactory value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Interlocked.CompareExchange(ref factory, value, null) is { } current && !ReferenceEquals(current, value))
            throw new InvalidOperationException("Guest fonts have already been composed for this process.");
    }
    internal static IFontFactory Factory => Volatile.Read(ref factory)
        ?? throw new InvalidOperationException("No guest font factory was selected by the host composition root.");
}
