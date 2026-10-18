// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Network;

// Shared bodies for the handle-table-backed libSceHttp setters and getters declared in
// HttpExports.Handles.cs. All of them take the target id in Rdi.
public static partial class HttpExports
{
    private static int SetSetting(CpuContext ctx, NodeKind? kind, string key, ulong value)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (Gate)
        {
            var error = CheckNode(id, kind, out var node);
            if (error != 0)
            {
                return ctx.SetReturn(error);
            }

            node.Settings[key] = value;
            return ctx.SetReturn(0);
        }
    }

    private static int SetBoolSetting(CpuContext ctx, string key)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var value = unchecked((uint)ctx[CpuRegister.Rsi]);
        lock (Gate)
        {
            var error = CheckNode(id, null, out var node);
            if (error != 0)
            {
                return ctx.SetReturn(error);
            }

            if (value > 1)
            {
                return ctx.SetReturn(HttpErrorInvalidValue);
            }

            node.Settings[key] = value;
            return ctx.SetReturn(0);
        }
    }

    private static int GetBoolSetting(CpuContext ctx, string key, int defaultValue)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var outputAddress = ctx[CpuRegister.Rsi];
        int value;
        lock (Gate)
        {
            var error = CheckNode(id, null, out var node);
            if (error != 0)
            {
                return ctx.SetReturn(error);
            }

            value = defaultValue;
            for (HttpNode? current = node; current is not null;
                current = Nodes.GetValueOrDefault(current.ParentId))
            {
                if (current.Settings.TryGetValue(key, out var stored))
                {
                    value = unchecked((int)stored);
                    break;
                }
            }
        }

        if (outputAddress == 0)
        {
            return ctx.SetReturn(HttpErrorInvalidValue);
        }

        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(outputAddress, bytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(HttpErrorInvalidValue);
    }

    // Timeouts reject 0; the other numeric options take any value (no reference pins a range).
    private static int SetUIntSetting(CpuContext ctx, string key, bool rejectZero)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var value = ctx[CpuRegister.Rsi];
        lock (Gate)
        {
            var error = CheckNode(id, null, out var node);
            if (error != 0)
            {
                return ctx.SetReturn(error);
            }

            if (rejectZero && unchecked((uint)value) == 0)
            {
                return ctx.SetReturn(HttpErrorInvalidValue);
            }

            node.Settings[key] = value;
            TraceHttp("set_" + key, id, value, 0, 0, 0);
            return ctx.SetReturn(0);
        }
    }

    private static int SetSslFlags(CpuContext ctx, bool clear)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var flags = unchecked((uint)ctx[CpuRegister.Rsi]);
        lock (Gate)
        {
            var error = CheckNode(id, null, out var node);
            if (error != 0)
            {
                return ctx.SetReturn(error);
            }

            node.Settings.TryGetValue("sslflags", out var current);
            node.Settings["sslflags"] = clear ? current & ~(ulong)flags : current | flags;
            return ctx.SetReturn(0);
        }
    }

    private static int SetCallbackSetting(CpuContext ctx, NodeKind? kind, string key)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (Gate)
        {
            var error = CheckNode(id, kind, out var node);
            if (error != 0)
            {
                return ctx.SetReturn(error);
            }

            // A null function clears the registration.
            if (ctx[CpuRegister.Rsi] == 0)
            {
                node.Settings.Remove(key);
                node.Settings.Remove(key + ".arg");
            }
            else
            {
                node.Settings[key] = ctx[CpuRegister.Rsi];
                node.Settings[key + ".arg"] = ctx[CpuRegister.Rdx];
            }

            return ctx.SetReturn(0);
        }
    }
}
