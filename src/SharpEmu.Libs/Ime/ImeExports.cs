// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Ime;

public static class ImeExports
{
    // SCE_IME_ERROR_* (libIme error space, values as defined by kyty).
    internal const int ImeErrorBusy = unchecked((int)0x80BC0001);
    internal const int ImeErrorNotOpened = unchecked((int)0x80BC0002);
    internal const int ImeErrorInvalidUserId = unchecked((int)0x80BC0010);
    internal const int ImeErrorInvalidType = unchecked((int)0x80BC0011);
    internal const int ImeErrorInvalidMaxTextLength = unchecked((int)0x80BC0016);
    internal const int ImeErrorInvalidTextBuffer = unchecked((int)0x80BC0017);
    internal const int ImeErrorInvalidWork = unchecked((int)0x80BC0020);
    internal const int ImeErrorInvalidHandler = unchecked((int)0x80BC0022);
    internal const int ImeErrorInvalidMode = unchecked((int)0x80BC0024);
    internal const int ImeErrorInvalidParam = unchecked((int)0x80BC0030);
    private const int ImeErrorInvalidAddress = unchecked((int)0x80BC0031);

    // SceImeParam (0x60 bytes): userId @0x00, type @0x04, supportedLanguages @0x08,
    // enterLabel @0x10, inputMethod @0x14, filter @0x18, option @0x20, maxTextLength @0x24,
    // inputTextBuffer @0x28, posx @0x30, posy @0x34, horizontalAlignment @0x38,
    // verticalAlignment @0x3C, work @0x40, arg @0x48, handler @0x50, reserved[8] @0x58.
    internal const int ImeParamSize = 0x60;
    private const int ImeParamUserIdOffset = 0x00;
    private const int ImeParamTypeOffset = 0x04;
    private const int ImeParamMaxTextLengthOffset = 0x24;
    private const int ImeParamInputTextBufferOffset = 0x28;
    private const int ImeParamWorkOffset = 0x40;
    private const int ImeParamHandlerOffset = 0x50;
    private const uint ImeTypeNumber = 4;
    private const uint ImeMaxTextLength = 2048;
    // SceImeCaret { float x @0; float y @4; uint32 height @8; uint32 index @0xC }.
    private const int ImeCaretSize = 0x10;
    private const int ImeCaretIndexOffset = 0x0C;
    // Valid bits of the sceImeKeyboardSetMode mode mask.
    private const uint KeyboardModeMask = 0x7F;

    private static readonly object ImeGate = new();
    private static bool _imeOpen;
    private static uint _imeMaxTextLength;
    private static uint _imeTextLength;
    private static bool _keyboardOpen;

    internal static void ResetForTests()
    {
        lock (ImeGate)
        {
            _imeOpen = false;
            _imeMaxTextLength = 0;
            _imeTextLength = 0;
            _keyboardOpen = false;
        }
    }

    // SCE_IME_KEYBOARD_MAX_NUMBER.
    private const int KeyboardMaxNumber = 5;
    // SceImeKeyboardResourceIdArray { SceUserServiceUserId userId;
    // uint32_t resourceId[SCE_IME_KEYBOARD_MAX_NUMBER]; }.
    private const int KeyboardResourceIdArraySize = 4 + (KeyboardMaxNumber * 4);
    // SceImeKeyboardInfo { SceUserServiceUserId userId; SceImeKeyboardDeviceType device;
    // SceImeKeyboardType type; uint32_t repeatDelay; uint32_t repeatRate;
    // SceImeKeyboardStatus status; int8_t reserved[12]; } — all 4-byte members.
    private const int KeyboardInfoSize = 0x24;
    // SCE_IME_KEYBOARD_DEVICE_TYPE_OSK.
    private const uint KeyboardDeviceTypeOsk = 1;
    // SCE_IME_KEYBOARD_STATE_DISCONNECTED — no physical keyboard is ever attached.
    private const uint KeyboardStateDisconnected = 0;
    // The single local user this emulator exposes (see UserServiceExports).
    private const int PrimaryUserId = 0x10000000;

    // Quake (KEX) calls this from its main loop and from the audio bring-up path with
    // an event-handler pointer. No IME session ever exists here, so report success
    // without invoking the handler ("no pending IME events"). This NID was previously
    // misbound as an sceNgs2VoiceControl alias, which fed the game NGS2 errors.
    [SysAbiExport(
        Nid = "-4GCfYdNF1s",
        ExportName = "sceImeUpdate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeUpdate(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eaFXjfJv3xs",
        ExportName = "sceImeKeyboardOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardOpen(CpuContext ctx)
    {
        // Open stays unconditional (titles probe it before any other IME call); the state is
        // recorded so sceImeKeyboardClose/SetMode can report NOT_OPENED correctly.
        lock (ImeGate)
        {
            _keyboardOpen = true;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // int sceImeKeyboardClose(SceUserServiceUserId userId).
    [SysAbiExport(
        Nid = "PMVehSlfZ94",
        ExportName = "sceImeKeyboardClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardClose(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (ImeGate)
        {
            if (!_keyboardOpen)
            {
                return ctx.SetReturn(ImeErrorNotOpened);
            }

            if (userId == -1)
            {
                return ctx.SetReturn(ImeErrorInvalidUserId);
            }

            _keyboardOpen = false;
        }

        return ctx.SetReturn(0);
    }

    // int sceImeKeyboardSetMode(SceUserServiceUserId userId, uint32_t mode).
    [SysAbiExport(
        Nid = "ua+13Hk9kKs",
        ExportName = "sceImeKeyboardSetMode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardSetMode(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var mode = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (userId == -1)
        {
            return ctx.SetReturn(ImeErrorInvalidUserId);
        }

        lock (ImeGate)
        {
            if (!_keyboardOpen)
            {
                return ctx.SetReturn(ImeErrorNotOpened);
            }
        }

        return ctx.SetReturn((mode & ~KeyboardModeMask) == 0 ? 0 : ImeErrorInvalidMode);
    }

    // void sceImeParamInit(SceImeParam *param): zeroes the struct and marks userId invalid (-1).
    [SysAbiExport(
        Nid = "WmYDzdC4EHI",
        ExportName = "sceImeParamInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeParamInit(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress != 0)
        {
            Span<byte> param = stackalloc byte[ImeParamSize];
            param.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(param[ImeParamUserIdOffset..], -1);
            ctx.Memory.TryWrite(paramAddress, param);
        }

        // void function: nothing meaningful in rax.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // int sceImeOpen(const SceImeParam *param, const SceImeExtendedParam *extended).
    // The extended parameters are not validated. No on-screen keyboard exists, so a session is
    // opened but never produces events.
    [SysAbiExport(
        Nid = "RPydv-Jr1bc",
        ExportName = "sceImeOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeOpen(CpuContext ctx)
    {
        var paramAddress = ctx[CpuRegister.Rdi];
        if (paramAddress == 0)
        {
            return ctx.SetReturn(ImeErrorInvalidAddress);
        }

        lock (ImeGate)
        {
            if (_imeOpen)
            {
                return ctx.SetReturn(ImeErrorBusy);
            }

            Span<byte> param = stackalloc byte[ImeParamSize];
            if (!ctx.Memory.TryRead(paramAddress, param))
            {
                return ctx.SetReturn(ImeErrorInvalidAddress);
            }

            if (BinaryPrimitives.ReadInt32LittleEndian(param[ImeParamUserIdOffset..]) < 0)
            {
                return ctx.SetReturn(ImeErrorInvalidUserId);
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(param[ImeParamTypeOffset..]) > ImeTypeNumber)
            {
                return ctx.SetReturn(ImeErrorInvalidType);
            }

            var maxTextLength = BinaryPrimitives.ReadUInt32LittleEndian(param[ImeParamMaxTextLengthOffset..]);
            if (maxTextLength == 0 || maxTextLength > ImeMaxTextLength)
            {
                return ctx.SetReturn(ImeErrorInvalidMaxTextLength);
            }

            var bufferAddress = BinaryPrimitives.ReadUInt64LittleEndian(param[ImeParamInputTextBufferOffset..]);
            if (bufferAddress == 0)
            {
                return ctx.SetReturn(ImeErrorInvalidTextBuffer);
            }

            if (BinaryPrimitives.ReadUInt64LittleEndian(param[ImeParamWorkOffset..]) == 0)
            {
                return ctx.SetReturn(ImeErrorInvalidWork);
            }

            if (BinaryPrimitives.ReadUInt64LittleEndian(param[ImeParamHandlerOffset..]) == 0)
            {
                return ctx.SetReturn(ImeErrorInvalidHandler);
            }

            _imeOpen = true;
            _imeMaxTextLength = maxTextLength;
            _imeTextLength = CountInitialText(ctx, bufferAddress, maxTextLength);
        }

        return ctx.SetReturn(0);
    }

    // int sceImeClose(void).
    [SysAbiExport(
        Nid = "TmVP8LzcFcY",
        ExportName = "sceImeClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeClose(CpuContext ctx)
    {
        lock (ImeGate)
        {
            if (!_imeOpen)
            {
                return ctx.SetReturn(ImeErrorNotOpened);
            }

            _imeOpen = false;
            _imeTextLength = 0;
        }

        return ctx.SetReturn(0);
    }

    // int sceImeSetText(const char16_t *text, uint32_t length). Only the text length is tracked
    // (it bounds the valid caret indices); the guest buffer is updated through events, which
    // this HLE never raises.
    [SysAbiExport(
        Nid = "ieCNrVrzKd4",
        ExportName = "sceImeSetText",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeSetText(CpuContext ctx)
    {
        var textAddress = ctx[CpuRegister.Rdi];
        var length = unchecked((uint)ctx[CpuRegister.Rsi]);
        lock (ImeGate)
        {
            if (!_imeOpen)
            {
                return ctx.SetReturn(ImeErrorNotOpened);
            }

            if (textAddress == 0)
            {
                return ctx.SetReturn(ImeErrorInvalidAddress);
            }

            var clamped = Math.Min(length, _imeMaxTextLength);
            if (clamped != 0)
            {
                var text = new byte[checked((int)clamped * 2)];
                if (!ctx.Memory.TryRead(textAddress, text))
                {
                    return ctx.SetReturn(ImeErrorInvalidAddress);
                }
            }

            _imeTextLength = clamped;
        }

        return ctx.SetReturn(0);
    }

    // int sceImeSetCaret(const SceImeCaret *caret): index must lie within the current text.
    [SysAbiExport(
        Nid = "WLxUN2WMim8",
        ExportName = "sceImeSetCaret",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeSetCaret(CpuContext ctx)
    {
        var caretAddress = ctx[CpuRegister.Rdi];
        lock (ImeGate)
        {
            if (!_imeOpen)
            {
                return ctx.SetReturn(ImeErrorNotOpened);
            }

            if (caretAddress == 0)
            {
                return ctx.SetReturn(ImeErrorInvalidAddress);
            }

            Span<byte> caret = stackalloc byte[ImeCaretSize];
            if (!ctx.Memory.TryRead(caretAddress, caret))
            {
                return ctx.SetReturn(ImeErrorInvalidAddress);
            }

            var index = BinaryPrimitives.ReadUInt32LittleEndian(caret[ImeCaretIndexOffset..]);
            return ctx.SetReturn(index > _imeTextLength ? ImeErrorInvalidParam : 0);
        }
    }

    // Length (in char16 units) of the NUL-terminated initial text, capped at maxTextLength.
    private static uint CountInitialText(CpuContext ctx, ulong bufferAddress, uint maxTextLength)
    {
        Span<byte> unit = stackalloc byte[2];
        for (uint i = 0; i < maxTextLength; i++)
        {
            if (!ctx.Memory.TryRead(bufferAddress + (i * 2UL), unit)
                || BinaryPrimitives.ReadUInt16LittleEndian(unit) == 0)
            {
                return i;
            }
        }

        return maxTextLength;
    }

    // ABI: int sceImeKeyboardGetResourceId(SceUserServiceUserId userId,
    // SceImeKeyboardResourceIdArray *resourceIdArray). No keyboard is ever attached,
    // so publish the caller's user id with an all-zero id array instead of leaving
    // the caller's storage uninitialized — sceImeKeyboardGetInfo is called with
    // whatever this leaves behind.
    [SysAbiExport(
        Nid = "dKadqZFgKKQ",
        ExportName = "sceImeKeyboardGetResourceId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardGetResourceId(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var arrayAddress = ctx[CpuRegister.Rsi];
        if (arrayAddress == 0)
        {
            return ctx.SetReturn(ImeErrorInvalidAddress);
        }

        Span<byte> array = stackalloc byte[KeyboardResourceIdArraySize];
        array.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(array, userId);
        return ctx.Memory.TryWrite(arrayAddress, array)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(ImeErrorInvalidAddress);
    }

    // ABI: int sceImeKeyboardGetInfo(uint32_t resourceId,
    // SceImeKeyboardInfo *info) — rdi is a plain resource id, rsi the out struct,
    // written at exactly sizeof(SceImeKeyboardInfo) == 0x24.
    [SysAbiExport(
        Nid = "VkqLPArfFdc",
        ExportName = "sceImeKeyboardGetInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardGetInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rsi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn(ImeErrorInvalidAddress);
        }

        Span<byte> info = stackalloc byte[KeyboardInfoSize];
        info.Clear();
        //   +0x00 userId       = the single local user
        //   +0x04 device       = OSK (the host has no USB/BT keyboard bridge)
        //   +0x08 type         = 0 (no layout reported for a disconnected device)
        //   +0x0C repeatDelay  = 0, +0x10 repeatRate = 0
        //   +0x14 status       = DISCONNECTED
        BinaryPrimitives.WriteInt32LittleEndian(info[0x00..], PrimaryUserId);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x04..], KeyboardDeviceTypeOsk);
        BinaryPrimitives.WriteUInt32LittleEndian(info[0x14..], KeyboardStateDisconnected);
        return ctx.Memory.TryWrite(infoAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(ImeErrorInvalidAddress);
    }
}
