// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
namespace SharpEmu.Application;

/// <summary>Parses the existing WIDTHxHEIGHT setting without enumerating host displays.</summary>
public static class DisplayResolution
{
    public static bool TryParse(string? value, out int width, out int height)
    {
        width = height = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var separator = value.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        return separator > 0 && separator < value.Length - 1 &&
            int.TryParse(value.AsSpan(0, separator), out width) &&
            int.TryParse(value.AsSpan(separator + 1), out height) && width > 0 && height > 0;
    }
}
