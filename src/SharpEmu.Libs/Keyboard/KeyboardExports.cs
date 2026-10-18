// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Keyboard;

/// <summary>
/// libSceKeyboard. The host keyboard is polled through IHostInput.IsKeyDown
/// (Windows virtual-key semantics) on every read and translated to USB HID
/// usages; changes of the pressed set, modifiers or lock LEDs are queued as
/// events for sceKeyboardRead, while sceKeyboardReadState returns the current
/// state.
///
/// ABI:
///   SceKeyboardData (0x60): +0x00 u64 timestamp (us), +0x08 u8 intercepted,
///     +0x10 u8 connected, +0x14 s32 length, +0x18 u32 led, +0x1C u32
///     modifier_key, +0x20 u16 key_code[16], +0x40 u8 reserve2[32].
///   SceKeyboardCharData (0x14): +0x00 u8 processed, +0x04 s32 length,
///     +0x08 u16 char_code, +0x0A u8 reserve[8].
///   modifier_key bit n = HID usage 0xE0+n (LCtrl, LShift, LAlt, LGui, RCtrl,
///     RShift, RAlt, RGui); led: 1 NumLock, 2 CapsLock, 4 ScrollLock, 16 Kana.
///
/// The host API exposes no keyboard presence or lock-LED state, so the keyboard
/// is reported connected whenever a host input backend exists, and lock LEDs
/// are tracked from observed lock-key presses (starting cleared). Only
/// sceKeyboardGetConnection and the other device-management exports are not
/// implemented (their signatures are not verified).
/// </summary>
public static class KeyboardExports
{
    internal const int KeyboardErrorInvalidArg = unchecked((int)0x80DA0001);
    internal const int KeyboardErrorInvalidHandle = unchecked((int)0x80DA0003);
    internal const int KeyboardErrorAlreadyOpened = unchecked((int)0x80DA0004);
    internal const int KeyboardErrorNotInitialized = unchecked((int)0x80DA0005);

    internal const int DataSize = 0x60;
    internal const int MaxKeyCodes = 16;
    internal const int MaxDataNum = 16;
    internal const int Handle = 1;
    internal const int CharDataSize = 0x14;

    internal const int Arrangement101 = 0;
    internal const int Arrangement106 = 1;
    internal const uint LedNumLock = 1;
    internal const uint LedCapsLock = 2;
    internal const uint LedScrollLock = 4;
    internal const uint LedKana = 16;
    internal const uint ModifierLeftShift = 2;
    internal const uint ModifierRightShift = 32;

    private const string Us = "1234567890\n\0\b\t -=[]\\\0;'`,./";
    private const string UsShifted = "!@#$%^&*()\n\0\b\t _+{}|\0:\"~<>?";
    private const string Jis = "1234567890\n\0\b\t -^@[]];:\0,./";
    private const string JisShifted = "!\"#$%&'()\0\n\0\b\t =~`{}}+*\0<>?";
    private const string KeypadDigits = "1234567890.";

    private static readonly object Gate = new();
    private static readonly Queue<byte[]> Events = new();
    private static readonly List<ushort> Pressed = [];
    private static readonly HashSet<ushort> Down = [];
    private static bool _initialized;
    private static bool _opened;
    private static uint _modifiers;
    private static uint _led;
    private static Func<int, bool> _isKeyDown = DefaultIsKeyDown;
    private static Func<bool> _isConnected = DefaultIsConnected;
    private static Func<ulong> _timestamp = DefaultTimestamp;

    [SysAbiExport(
        Nid = "wadT3QBCGY0",
        ExportName = "sceKeyboardInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceKeyboard")]
    public static int KeyboardInit(CpuContext ctx)
    {
        lock (Gate)
        {
            _initialized = true;
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "HJ+KnEHcaxI",
        ExportName = "sceKeyboardOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceKeyboard")]
    public static int KeyboardOpen(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var index = unchecked((int)ctx[CpuRegister.Rdx]);
        lock (Gate)
        {
            if (!_initialized)
            {
                return ctx.SetReturn(KeyboardErrorNotInitialized);
            }

            if (userId < 0 || type != 0 || index != 0)
            {
                return ctx.SetReturn(KeyboardErrorInvalidArg);
            }

            if (_opened)
            {
                return ctx.SetReturn(KeyboardErrorAlreadyOpened);
            }

            _opened = true;
            ResetStateLocked();
            Events.Enqueue(SnapshotLocked());
        }

        return ctx.SetReturn(Handle);
    }

    [SysAbiExport(
        Nid = "0LWei+c7RNc",
        ExportName = "sceKeyboardClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceKeyboard")]
    public static int KeyboardClose(CpuContext ctx)
    {
        lock (Gate)
        {
            var status = CheckHandleLocked(unchecked((int)ctx[CpuRegister.Rdi]));
            if (status != 0)
            {
                return ctx.SetReturn(status);
            }

            _opened = false;
            ResetStateLocked();
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "xybbGMCr738",
        ExportName = "sceKeyboardRead",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceKeyboard")]
    public static int KeyboardRead(CpuContext ctx)
    {
        var dataAddress = ctx[CpuRegister.Rsi];
        var count = unchecked((int)ctx[CpuRegister.Rdx]);
        List<byte[]> events = [];
        lock (Gate)
        {
            var status = CheckHandleLocked(unchecked((int)ctx[CpuRegister.Rdi]));
            if (status != 0)
            {
                return ctx.SetReturn(status);
            }

            if (dataAddress == 0 || count < 1 || count > MaxDataNum)
            {
                return ctx.SetReturn(KeyboardErrorInvalidArg);
            }

            PollLocked();
            while (events.Count < count && Events.Count > 0)
            {
                events.Add(Events.Dequeue());
            }
        }

        for (var i = 0; i < events.Count; i++)
        {
            if (!ctx.Memory.TryWrite(dataAddress + (ulong)(i * DataSize), events[i]))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return ctx.SetReturn(events.Count);
    }

    [SysAbiExport(
        Nid = "6HpE68bzX6M",
        ExportName = "sceKeyboardReadState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceKeyboard")]
    public static int KeyboardReadState(CpuContext ctx)
    {
        var dataAddress = ctx[CpuRegister.Rsi];
        byte[] snapshot;
        lock (Gate)
        {
            var status = CheckHandleLocked(unchecked((int)ctx[CpuRegister.Rdi]));
            if (status != 0)
            {
                return ctx.SetReturn(status);
            }

            if (dataAddress == 0)
            {
                return ctx.SetReturn(KeyboardErrorInvalidArg);
            }

            PollLocked();
            snapshot = SnapshotLocked();
        }

        return ctx.SetReturn(ctx.Memory.TryWrite(dataAddress, snapshot)
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "yO9JwdRhtSA",
        ExportName = "sceKeyboardGetKey2Char",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceKeyboard")]
    public static int KeyboardGetKey2Char(CpuContext ctx)
    {
        var arrange = unchecked((int)ctx[CpuRegister.Rsi]);
        var led = unchecked((uint)ctx[CpuRegister.Rdx]);
        var modifier = unchecked((uint)ctx[CpuRegister.Rcx]);
        var keyCode = unchecked((ushort)ctx[CpuRegister.R8]);
        var outAddress = ctx[CpuRegister.R9];
        lock (Gate)
        {
            var status = CheckHandleLocked(unchecked((int)ctx[CpuRegister.Rdi]));
            if (status != 0)
            {
                return ctx.SetReturn(status);
            }
        }

        // The kana produced with the Kana LED on a 106-key layout is not known,
        // so that combination is rejected instead of answered wrongly.
        if (outAddress == 0 ||
            (arrange != Arrangement101 && arrange != Arrangement106) ||
            (arrange == Arrangement106 && (led & LedKana) != 0))
        {
            return ctx.SetReturn(KeyboardErrorInvalidArg);
        }

        var character = TranslateKey(arrange == Arrangement106, led, modifier, keyCode);
        var data = new byte[CharDataSize];
        data[0] = (byte)(character != 0 ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), character != 0 ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), character);
        return ctx.SetReturn(ctx.Memory.TryWrite(outAddress, data)
            ? 0
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    internal static ushort TranslateKey(bool jis, uint led, uint modifier, ushort keyCode)
    {
        var shift = (modifier & (ModifierLeftShift | ModifierRightShift)) != 0;
        if (keyCode is >= 0x04 and <= 0x1D)
        {
            var upper = shift != ((led & LedCapsLock) != 0);
            return (ushort)((upper ? 'A' : 'a') + keyCode - 0x04);
        }

        if (keyCode is >= 0x1E and <= 0x38)
        {
            var table = jis ? (shift ? JisShifted : Jis) : (shift ? UsShifted : Us);
            return table[keyCode - 0x1E];
        }

        if (jis && keyCode == 0x87)
        {
            return shift ? '_' : '\\';
        }

        if (jis && keyCode == 0x89)
        {
            return shift ? '|' : '\\';
        }

        switch (keyCode)
        {
            case 0x54: return '/';
            case 0x55: return '*';
            case 0x56: return '-';
            case 0x57: return '+';
            case 0x58: return '\n';
        }

        if (keyCode is >= 0x59 and <= 0x63 && (led & LedNumLock) != 0)
        {
            return KeypadDigits[keyCode - 0x59];
        }

        return 0;
    }

    private static int CheckHandleLocked(int handle)
    {
        if (!_initialized)
        {
            return KeyboardErrorNotInitialized;
        }

        return handle == Handle && _opened ? 0 : KeyboardErrorInvalidHandle;
    }

    private static void ResetStateLocked()
    {
        Events.Clear();
        Pressed.Clear();
        Down.Clear();
        _modifiers = 0;
    }

    private static void PollLocked()
    {
        var changed = false;
        foreach (var (usage, virtualKey) in KeyboardKeyMap.Entries)
        {
            var isDown = _isKeyDown(virtualKey);
            if (isDown == Down.Contains(usage))
            {
                continue;
            }

            changed = true;
            if (isDown)
            {
                Down.Add(usage);
            }
            else
            {
                Down.Remove(usage);
            }

            if (usage is >= KeyboardKeyMap.FirstModifierUsage and <= KeyboardKeyMap.LastModifierUsage)
            {
                var bit = 1u << (usage - KeyboardKeyMap.FirstModifierUsage);
                _modifiers = isDown ? _modifiers | bit : _modifiers & ~bit;
                continue;
            }

            if (isDown)
            {
                if (Pressed.Count < MaxKeyCodes)
                {
                    Pressed.Add(usage);
                }

                _led ^= usage switch
                {
                    KeyboardKeyMap.UsageCapsLock => LedCapsLock,
                    KeyboardKeyMap.UsageNumLock => LedNumLock,
                    KeyboardKeyMap.UsageScrollLock => LedScrollLock,
                    _ => 0u,
                };
            }
            else
            {
                Pressed.Remove(usage);
            }
        }

        if (!changed)
        {
            return;
        }

        if (Events.Count == MaxDataNum)
        {
            Events.Dequeue();
        }

        Events.Enqueue(SnapshotLocked());
    }

    private static byte[] SnapshotLocked()
    {
        var data = new byte[DataSize];
        BinaryPrimitives.WriteUInt64LittleEndian(data, _timestamp());
        data[0x10] = (byte)(_isConnected() ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x14), Math.Max(Pressed.Count, 1));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x18), _led);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x1C), _modifiers);
        for (var i = 0; i < Pressed.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x20 + (i * 2)), Pressed[i]);
        }

        return data;
    }

    private static bool DefaultIsKeyDown(int virtualKey)
    {
        try
        {
            var input = HostPlatform.Current.Input;
            return input.IsHostWindowFocused() && input.IsKeyDown(virtualKey);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool DefaultIsConnected()
    {
        try
        {
            return HostPlatform.Current.Input is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ulong DefaultTimestamp()
    {
        var ticks = Stopwatch.GetTimestamp();
        return ((ulong)(ticks / Stopwatch.Frequency) * 1_000_000UL) +
            ((ulong)(ticks % Stopwatch.Frequency) * 1_000_000UL / (ulong)Stopwatch.Frequency);
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _initialized = false;
            _opened = false;
            _led = 0;
            ResetStateLocked();
            _isKeyDown = DefaultIsKeyDown;
            _isConnected = DefaultIsConnected;
            _timestamp = DefaultTimestamp;
        }
    }

    internal static void SetHostForTests(Func<int, bool> isKeyDown, Func<bool> isConnected, Func<ulong> timestamp)
    {
        lock (Gate)
        {
            _isKeyDown = isKeyDown;
            _isConnected = isConnected;
            _timestamp = timestamp;
        }
    }
}
