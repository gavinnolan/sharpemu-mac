// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Keyboard;

/// <summary>
/// USB HID usage (keyboard page 0x07) to host virtual-key mapping. The host
/// input API uses Windows virtual-key semantics (IHostInput.IsKeyDown).
/// </summary>
internal static class KeyboardKeyMap
{
    public const ushort FirstModifierUsage = 0xE0;
    public const ushort LastModifierUsage = 0xE7;
    public const ushort UsageEnter = 0x28;
    public const ushort UsageCapsLock = 0x39;
    public const ushort UsageScrollLock = 0x47;
    public const ushort UsageNumLock = 0x53;

    /// <summary>Every HID usage this map can read from the host, in usage order.</summary>
    public static readonly (ushort Usage, int VirtualKey)[] Entries = Build();

    private static (ushort, int)[] Build()
    {
        var list = new List<(ushort, int)>();
        for (var i = 0; i < 26; i++)
        {
            list.Add(((ushort)(0x04 + i), 0x41 + i)); // A-Z
        }

        for (var i = 0; i < 9; i++)
        {
            list.Add(((ushort)(0x1E + i), 0x31 + i)); // 1-9
        }

        list.Add((0x27, 0x30)); // 0
        list.Add((UsageEnter, 0x0D));
        list.Add((0x29, 0x1B)); // Escape
        list.Add((0x2A, 0x08)); // Backspace
        list.Add((0x2B, 0x09)); // Tab
        list.Add((0x2C, 0x20)); // Space
        list.Add((0x2D, 0xBD)); // - _
        list.Add((0x2E, 0xBB)); // = +
        list.Add((0x2F, 0xDB)); // [
        list.Add((0x30, 0xDD)); // ]
        list.Add((0x31, 0xDC)); // backslash
        list.Add((0x33, 0xBA)); // ;
        list.Add((0x34, 0xDE)); // '
        list.Add((0x35, 0xC0)); // `
        list.Add((0x36, 0xBC)); // ,
        list.Add((0x37, 0xBE)); // .
        list.Add((0x38, 0xBF)); // /
        list.Add((UsageCapsLock, 0x14));
        for (var i = 0; i < 12; i++)
        {
            list.Add(((ushort)(0x3A + i), 0x70 + i)); // F1-F12
        }

        list.Add((0x46, 0x2C)); // Print Screen
        list.Add((UsageScrollLock, 0x91));
        list.Add((0x48, 0x13)); // Pause
        list.Add((0x49, 0x2D)); // Insert
        list.Add((0x4A, 0x24)); // Home
        list.Add((0x4B, 0x21)); // Page Up
        list.Add((0x4C, 0x2E)); // Delete
        list.Add((0x4D, 0x23)); // End
        list.Add((0x4E, 0x22)); // Page Down
        list.Add((0x4F, 0x27)); // Right
        list.Add((0x50, 0x25)); // Left
        list.Add((0x51, 0x28)); // Down
        list.Add((0x52, 0x26)); // Up
        list.Add((UsageNumLock, 0x90));
        list.Add((0x54, 0x6F)); // keypad /
        list.Add((0x55, 0x6A)); // keypad *
        list.Add((0x56, 0x6D)); // keypad -
        list.Add((0x57, 0x6B)); // keypad +
        for (var i = 0; i < 9; i++)
        {
            list.Add(((ushort)(0x59 + i), 0x61 + i)); // keypad 1-9
        }

        list.Add((0x62, 0x60)); // keypad 0
        list.Add((0x63, 0x6E)); // keypad .
        list.Add((0xE0, 0xA2)); // Left Ctrl
        list.Add((0xE1, 0xA0)); // Left Shift
        list.Add((0xE2, 0xA4)); // Left Alt
        list.Add((0xE3, 0x5B)); // Left GUI
        list.Add((0xE4, 0xA3)); // Right Ctrl
        list.Add((0xE5, 0xA1)); // Right Shift
        list.Add((0xE6, 0xA5)); // Right Alt
        list.Add((0xE7, 0x5C)); // Right GUI
        return [.. list];
    }
}
