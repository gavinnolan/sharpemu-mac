// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Test automation: SHARPEMU_INPUT_FILE names a file whose contents are the keys held right
/// now, as whitespace-separated names (enter, esc, tab, backspace, space, up, down, left,
/// right, or a single letter or digit). A script rewrites the file to press and release keys,
/// so a game can be driven without window focus or accessibility permissions.
/// </summary>
internal static class ScriptedKeyInput
{
    private const long PollIntervalMs = 30;

    private static readonly string? FilePath = Environment.GetEnvironmentVariable("SHARPEMU_INPUT_FILE");
    private static HashSet<int> _held = [];
    private static long _nextRead;
    private static int _loggedKeys;

    public static bool IsEnabled => FilePath is not null;

    public static bool AnyHeld => HeldKeys().Count != 0;

    public static bool IsHeld(int virtualKey)
    {
        if (!HeldKeys().Contains(virtualKey))
        {
            return false;
        }

        if (Interlocked.Increment(ref _loggedKeys) <= 20)
        {
            Console.Error.WriteLine($"[INPUT][SCRIPT] key 0x{virtualKey:X2} held");
        }

        return true;
    }

    internal static int ParseKey(string name) => name.ToLowerInvariant() switch
    {
        "enter" => 0x0D,
        "esc" => 0x1B,
        "tab" => 0x09,
        "backspace" => 0x08,
        "space" => 0x20,
        "left" => 0x25,
        "up" => 0x26,
        "right" => 0x27,
        "down" => 0x28,
        { Length: 1 } single when char.IsAsciiLetterOrDigit(single[0]) => char.ToUpperInvariant(single[0]),
        _ => -1,
    };

    private static HashSet<int> HeldKeys()
    {
        if (FilePath is null)
        {
            return _held;
        }

        var now = Environment.TickCount64;
        if (now < Volatile.Read(ref _nextRead))
        {
            return Volatile.Read(ref _held);
        }

        Volatile.Write(ref _nextRead, now + PollIntervalMs);
        var held = new HashSet<int>();
        try
        {
            foreach (var name in File.ReadAllText(FilePath).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var key = ParseKey(name);
                if (key >= 0)
                {
                    held.Add(key);
                }
            }
        }
        catch (IOException)
        {
            // The script is rewriting the file; keep the previous state until the next poll.
            return Volatile.Read(ref _held);
        }

        Volatile.Write(ref _held, held);
        return held;
    }
}
