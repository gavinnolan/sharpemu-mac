// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Network;

/// <summary>
/// libSceNetCtl in "offline" mode: the guest always sees a console whose network connection is
/// down. Every query reports that same state, so <c>GetState</c>, <c>GetInfo</c>,
/// <c>GetIfStat</c>, <c>GetNatInfo</c> and <c>GetResult</c> never contradict each other, and no
/// callback ever has a state change to deliver.
/// </summary>
/// <remarks>
/// Error codes (0x8041_21xx) are the ones both reference emulators agree on. The SDK headers also
/// define NOT_INITIALIZED/ALREADY_INITIALIZED style codes, but no reference pins their values, so
/// the library stays usable before sceNetCtlInit (system software initialises it for titles that
/// forget) and sceNetCtlInit/sceNetCtlTerm are plain, idempotent lifecycle calls.
/// </remarks>
public static class NetCtlExports
{
    private const int MaxCallbacks = 8;
    internal const int NetCtlErrorCallbackMax = unchecked((int)0x80412103);
    internal const int NetCtlErrorInvalidId = unchecked((int)0x80412105);
    internal const int NetCtlErrorInvalidCode = unchecked((int)0x80412106);
    internal const int NetCtlErrorInvalidAddress = unchecked((int)0x80412107);
    internal const int NetCtlErrorNotConnected = unchecked((int)0x80412108);

    internal const int NetCtlStateDisconnected = 0;
    internal const int NetCtlStateIpObtained = 3;

    private const int NetCtlInfoDevice = 1;
    private const int NetCtlInfoEtherAddress = 2;
    private const int NetCtlInfoMtu = 3;
    private const int NetCtlInfoLink = 4;
    private const int NetCtlInfoIpConfig = 11;
    private const int NetCtlInfoDhcpHostname = 12;
    private const int NetCtlInfoPppoeAuthName = 13;
    private const int NetCtlInfoIpAddress = 14;
    private const int NetCtlInfoNetmask = 15;
    private const int NetCtlInfoDefaultRoute = 16;
    private const int NetCtlInfoPrimaryDns = 17;
    private const int NetCtlInfoSecondaryDns = 18;
    private const int NetCtlInfoHttpProxyConfig = 19;
    private const int NetCtlInfoHttpProxyServer = 20;
    private const int NetCtlInfoHttpProxyPort = 21;
    private const int NetCtlInfoMax = NetCtlInfoHttpProxyPort;
    private const int NetCtlDeviceWired = 0;
    private const int NetCtlLinkDisconnected = 0;
    private const int NatInfoSize = 16;

    private static readonly object Gate = new();
    private static readonly CallbackRegistration[] Callbacks = new CallbackRegistration[MaxCallbacks];
    private static readonly CallbackRegistration[] CallbacksV6 = new CallbackRegistration[MaxCallbacks];
    private static bool _initialized;

    private readonly record struct CallbackRegistration(ulong Function, ulong Argument);

    /// <summary>Connection state reported by every query. Offline mode is permanently disconnected.</summary>
    internal static int ReportedState => NetCtlStateDisconnected;

    internal static bool IsInitialized
    {
        get
        {
            lock (Gate)
            {
                return _initialized;
            }
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _initialized = false;
            Array.Clear(Callbacks);
            Array.Clear(CallbacksV6);
        }
    }

    internal static bool IsCallbackRegistered(int callbackId, bool v6)
    {
        lock (Gate)
        {
            var table = v6 ? CallbacksV6 : Callbacks;
            return callbackId >= 0 && callbackId < MaxCallbacks && table[callbackId].Function != 0;
        }
    }

    [SysAbiExport(
        Nid = "gky0+oaNM4k",
        ExportName = "sceNetCtlInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlInit(CpuContext ctx)
    {
        lock (Gate)
        {
            _initialized = true;
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "Z4wwCFiBELQ",
        ExportName = "sceNetCtlTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlTerm(CpuContext ctx)
    {
        // Term has no result; registrations do not survive it.
        lock (Gate)
        {
            _initialized = false;
            Array.Clear(Callbacks);
            Array.Clear(CallbacksV6);
        }

        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "JO4yuTuMoKI",
        ExportName = "sceNetCtlGetNatInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlGetNatInfo(CpuContext ctx)
    {
        var natInfoAddress = ctx[CpuRegister.Rdi];
        if (natInfoAddress == 0)
        {
            return ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long));
        }

        // A NAT probe needs a live connection; offline there is nothing to report and the
        // structure is left untouched.
        return ctx.SetReturn(NetCtlErrorNotConnected, typeof(long));
    }

    [SysAbiExport(
        Nid = "iQw3iQPhvUQ",
        ExportName = "sceNetCtlCheckCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlCheckCallback(CpuContext ctx)
    {
        // The reported state never changes, so no registered callback has an event pending.
        return ctx.SetReturn(0, typeof(long));
    }

    [SysAbiExport(
        Nid = "uBPlr0lbuiI",
        ExportName = "sceNetCtlGetState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlGetState(CpuContext ctx) => WriteState(ctx);

    [SysAbiExport(
        Nid = "+lxqIKeU9UY",
        ExportName = "sceNetCtlGetStateV6",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlGetStateV6(CpuContext ctx) => WriteState(ctx);

    [SysAbiExport(
        Nid = "0cBgduPRR+M",
        ExportName = "sceNetCtlGetResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlGetResult(CpuContext ctx) => WriteResult(ctx);

    [SysAbiExport(
        Nid = "H5yARg37U5g",
        ExportName = "sceNetCtlGetResultV6",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlGetResultV6(CpuContext ctx) => WriteResult(ctx);

    [SysAbiExport(
        Nid = "teuK4QnJTGg",
        ExportName = "sceNetCtlGetIfStat",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlGetIfStat(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0)
        {
            return ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long));
        }

        // Interface counters only exist for an up interface.
        return ctx.SetReturn(NetCtlErrorNotConnected, typeof(long));
    }

    [SysAbiExport(
        Nid = "UJ+Z7Q+4ck0",
        ExportName = "sceNetCtlRegisterCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlRegisterCallback(CpuContext ctx) => RegisterCallback(ctx, Callbacks);

    [SysAbiExport(
        Nid = "1NE9OWdBIww",
        ExportName = "sceNetCtlRegisterCallbackV6",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlRegisterCallbackV6(CpuContext ctx) => RegisterCallback(ctx, CallbacksV6);

    [SysAbiExport(
        Nid = "Rqm2OnZMCz0",
        ExportName = "sceNetCtlUnregisterCallback",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlUnregisterCallback(CpuContext ctx) => UnregisterCallback(ctx, Callbacks);

    [SysAbiExport(
        Nid = "hIUVeUNxAwc",
        ExportName = "sceNetCtlUnregisterCallbackV6",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlUnregisterCallbackV6(CpuContext ctx) => UnregisterCallback(ctx, CallbacksV6);

    [SysAbiExport(
        Nid = "obuxdTiwkF8",
        ExportName = "sceNetCtlGetInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlGetInfo(CpuContext ctx) => GetInfo(ctx);

    [SysAbiExport(
        Nid = "Jy1EO5GdlcM",
        ExportName = "sceNetCtlGetInfoV6",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNetCtl")]
    public static int NetCtlGetInfoV6(CpuContext ctx) => GetInfo(ctx);

    private static int WriteState(CpuContext ctx)
    {
        var stateAddress = ctx[CpuRegister.Rdi];
        if (stateAddress == 0)
        {
            return ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long));
        }

        Span<byte> stateBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(stateBytes, ReportedState);
        return ctx.Memory.TryWrite(stateAddress, stateBytes)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    private static int WriteResult(CpuContext ctx)
    {
        // (eventType, int* errorCode). No connection attempt has ever failed, so the last
        // connection error is 0. The event type is not range-checked: no reference pins its range.
        var errorAddress = ctx[CpuRegister.Rsi];
        if (errorAddress == 0)
        {
            return ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long));
        }

        Span<byte> bytes = stackalloc byte[sizeof(int)];
        return ctx.Memory.TryWrite(errorAddress, bytes)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    private static int RegisterCallback(CpuContext ctx, CallbackRegistration[] table)
    {
        var function = ctx[CpuRegister.Rdi];
        var argument = ctx[CpuRegister.Rsi];
        var callbackIdAddress = ctx[CpuRegister.Rdx];
        if (function == 0 || callbackIdAddress == 0)
        {
            return ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long));
        }

        lock (Gate)
        {
            var callbackId = Array.FindIndex(table, static callback => callback.Function == 0);
            if (callbackId < 0)
            {
                return ctx.SetReturn(NetCtlErrorCallbackMax, typeof(long));
            }

            Span<byte> callbackIdBytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(callbackIdBytes, callbackId);
            if (!ctx.Memory.TryWrite(callbackIdAddress, callbackIdBytes))
            {
                return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
            }

            table[callbackId] = new CallbackRegistration(function, argument);
        }

        return ctx.SetReturn(0, typeof(long));
    }

    private static int UnregisterCallback(CpuContext ctx, CallbackRegistration[] table)
    {
        var callbackId = unchecked((int)ctx[CpuRegister.Rdi]);
        if (callbackId < 0 || callbackId >= MaxCallbacks)
        {
            return ctx.SetReturn(NetCtlErrorInvalidId, typeof(long));
        }

        // Unregistering a free slot succeeds: the one reference that covers it does the same.
        lock (Gate)
        {
            table[callbackId] = default;
        }

        return ctx.SetReturn(0, typeof(long));
    }

    private static int GetInfo(CpuContext ctx)
    {
        var code = unchecked((int)ctx[CpuRegister.Rdi]);
        var infoAddress = ctx[CpuRegister.Rsi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn(NetCtlErrorInvalidAddress, typeof(long));
        }

        if (code < NetCtlInfoDevice || code > NetCtlInfoMax)
        {
            return ctx.SetReturn(NetCtlErrorInvalidCode, typeof(long));
        }

        // Offline: only facts about the (down) link itself are available. Everything that needs
        // an address, a DNS server or a proxy, and the Wi-Fi codes (5-10), report NOT_CONNECTED.
        return code switch
        {
            NetCtlInfoDevice => WriteUInt32(ctx, infoAddress, NetCtlDeviceWired),
            NetCtlInfoEtherAddress => WriteBytes(ctx, infoAddress, OfflineEtherAddress),
            NetCtlInfoLink => WriteUInt32(ctx, infoAddress, NetCtlLinkDisconnected),
            _ => ctx.SetReturn(NetCtlErrorNotConnected, typeof(long)),
        };
    }

    // Matches the locally administered address sceNetGetMacAddress reports.
    private static ReadOnlySpan<byte> OfflineEtherAddress => [0x02, 0x53, 0x48, 0x41, 0x52, 0x50];

    private static int WriteBytes(CpuContext ctx, ulong address, ReadOnlySpan<byte> bytes)
    {
        return ctx.Memory.TryWrite(address, bytes)
            ? ctx.SetReturn(0, typeof(long))
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, typeof(long));
    }

    private static int WriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return WriteBytes(ctx, address, bytes);
    }
}
