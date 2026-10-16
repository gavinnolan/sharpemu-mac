// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using System.Buffers.Binary;

namespace SharpEmu.Libs.SystemService;

public static class SystemServiceExports
{
    private const int OrbisSystemServiceErrorParameter = unchecked((int)0x80A10003);
    private const int OrbisSystemServiceErrorNoEvent = unchecked((int)0x80A10004);
    private const int SystemServiceStatusSize = 0x0C;
    private const int DisplaySafeAreaInfoSize = sizeof(float) + 128;
    private const int HdrToneMapLuminanceSize = sizeof(float) * 3;

    private const int TitleIdFieldSize = 0x10;

    // SceSystemServiceEvent: int32 eventType followed by an 8192-byte payload (kyty and anyps5
    // agree on this layout).
    internal const int EventDataSize = 8192;
    private const int EventRecordSize = sizeof(int) + EventDataSize;
    // Kernel EOPNOTSUPP; the SDK documents no dedicated "unsupported" code for the launch calls.
    private const int OrbisKernelErrorNotSupported = unchecked((int)0x8002002D);
    private const int RunningAppId = 1;
    private const int MaxSystemNameLength = 65;

    private const int ParamIdLang = 1;
    private const int ParamIdDateFormat = 2;
    private const int ParamIdTimeFormat = 3;
    private const int ParamIdTimeZone = 4;
    private const int ParamIdSummertime = 5;
    private const int ParamIdSystemName = 6;
    private const int ParamIdGameParentalLevel = 7;
    private const int ParamIdCcEnable = 100;
    private const int ParamIdScreenReader = 208;
    private const int ParamIdLangPs5 = 400;
    private const int ParamIdEnterButtonAssign = 1000;

    private readonly record struct QueuedEvent(int EventType, byte[] Data);

    private static readonly object _eventLock = new();
    private static readonly Queue<QueuedEvent> _events = new();

    private static string? _mainAppTitleId;
    private static int _noticeScreenSkipFlag;
    private static int _systemLanguage = 1;

    [SysAbiExport(
        Nid = "656LMQSrg6U",
        ExportName = "sceSystemServiceReceiveEvent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceReceiveEvent(CpuContext ctx)
    {
        var eventAddress = ctx[CpuRegister.Rdi];
        if (eventAddress == 0)
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);

        // An empty poll must leave the caller's event record untouched and report NO_EVENT.
        // A queued event is removed only once the whole record has been written, so a
        // faulting buffer does not lose it.
        QueuedEvent queued;
        lock (_eventLock)
        {
            if (_events.Count == 0)
                return ctx.SetReturn(OrbisSystemServiceErrorNoEvent);
            queued = _events.Peek();
        }

        var record = new byte[EventRecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(record, queued.EventType);
        queued.Data.CopyTo(record.AsSpan(sizeof(int)));
        if (!ctx.Memory.TryWrite(eventAddress, record))
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);

        lock (_eventLock)
        {
            // Another receiver may have raced us to the same event; only drop what we delivered.
            if (_events.Count != 0 && ReferenceEquals(_events.Peek().Data, queued.Data))
                _events.Dequeue();
        }

        return ctx.SetReturn(0);
    }

    /// <summary>
    /// Queues an event for <c>sceSystemServiceReceiveEvent</c>. Events are delivered first in,
    /// first out. <paramref name="data"/> fills the record's payload area (at most
    /// <see cref="EventDataSize"/> bytes, the rest is zeroed); a longer payload is rejected.
    /// </summary>
    internal static bool EnqueueEvent(int eventType, ReadOnlySpan<byte> data = default)
    {
        if (data.Length > EventDataSize)
            return false;

        var payload = new byte[EventDataSize];
        data.CopyTo(payload);
        lock (_eventLock)
            _events.Enqueue(new QueuedEvent(eventType, payload));
        return true;
    }

    private static int PendingEventCount
    {
        get
        {
            lock (_eventLock)
                return _events.Count;
        }
    }

    public static void ConfigureApplicationInfo(string? titleId, int systemLanguage = 1)
    {
        _mainAppTitleId = string.IsNullOrWhiteSpace(titleId) ? null : titleId.Trim();
        Volatile.Write(ref _systemLanguage, systemLanguage);
    }

    [SysAbiExport(
        Nid = "x1UB9bwDSOw",
        ExportName = "sceSystemServiceDisableMusicPlayer",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceDisableMusicPlayer(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "9kPCz7Or+1Y",
        ExportName = "sceSystemServiceReenableMusicPlayer",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceReenableMusicPlayer(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "3RQ5aQfnstU",
        ExportName = "sceSystemServiceGetNoticeScreenSkipFlag",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetNoticeScreenSkipFlag(CpuContext ctx)
    {
        var flagAddress = ctx[CpuRegister.Rdi];
        if (flagAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        // Keep the flag state even though the emulator does not display the
        // system notice screen. Titles use this service as a normal preference
        // store and expect a later get to observe the value they set.
        Span<byte> flagBytes = stackalloc byte[1];
        flagBytes[0] = unchecked((byte)Volatile.Read(ref _noticeScreenSkipFlag));
        return ctx.Memory.TryWrite(flagAddress, flagBytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "8Lo6Zv94aho",
        ExportName = "sceSystemServiceDisableNoticeScreenSkipFlagAutoSet",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceDisableNoticeScreenSkipFlagAutoSet(CpuContext ctx) =>
        ctx.SetReturn(0);

    // Settings entry calls this immediately before spawning SaveModTime/Load
    // threads. An unresolved stub returns NOT_FOUND and the title can stall in
    // that path; accept the write and report success.
    [SysAbiExport(
        Nid = "Q3utJvma4Mo",
        ExportName = "sceSystemServiceSetNoticeScreenSkipFlag",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceSetNoticeScreenSkipFlag(CpuContext ctx)
    {
        // The native API takes the flag value in the first argument. Treat any
        // non-zero value as true, matching the bool-like PS5 ABI.
        Volatile.Write(ref _noticeScreenSkipFlag, ctx[CpuRegister.Rdi] != 0 ? 1 : 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "4veE0XiIugA",
        ExportName = "sceSystemServiceGetMainAppTitleId",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetMainAppTitleId(CpuContext ctx)
    {
        var titleIdAddress = ctx[CpuRegister.Rdi];
        if (titleIdAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        // Title IDs are a fixed 9-char format written into a 0x10-byte field;
        // bound the length so a malformed param.json cannot drive an unbounded
        // stack allocation or overrun the guest buffer.
        var titleId = _mainAppTitleId ?? "PPSA00000";
        var length = Math.Min(titleId.Length, TitleIdFieldSize - 1);
        Span<byte> titleIdBytes = stackalloc byte[TitleIdFieldSize];
        titleIdBytes.Clear();
        System.Text.Encoding.ASCII.GetBytes(titleId.AsSpan(0, length), titleIdBytes);
        return ctx.Memory.TryWrite(titleIdAddress, titleIdBytes[..(length + 1)])
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "fZo48un7LK4",
        ExportName = "sceSystemServiceParamGetInt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceParamGetInt(CpuContext ctx)
    {
        var parameterId = unchecked((int)ctx[CpuRegister.Rdi]);
        var valueAddress = ctx[CpuRegister.Rsi];
        if (valueAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        // Ids outside the documented set are rejected rather than answered with 0. kyty and
        // anyps5 both answer 0 for unknown ids; the SDK reports PARAMETER, which wins.
        int value;
        switch (parameterId)
        {
            case ParamIdLang:
            case ParamIdLangPs5:
                value = Volatile.Read(ref _systemLanguage);
                break;
            case ParamIdDateFormat:
                value = GetHostDateFormat();
                break;
            case ParamIdTimeFormat:
                value = GetHostTimeFormat();
                break;
            case ParamIdTimeZone:
                value = GetHostTimeZoneMinutes();
                break;
            case ParamIdSummertime:
                value = GetHostSummertime();
                break;
            case ParamIdEnterButtonAssign:
                value = 1; // cross
                break;
            case ParamIdGameParentalLevel:
            case ParamIdCcEnable:
            case ParamIdScreenReader:
                value = 0;
                break;
            default:
                return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> valueBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(valueBytes, value);
        return ctx.Memory.TryWrite(valueAddress, valueBytes)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "SsC-m-S9JTA",
        ExportName = "sceSystemServiceParamGetString",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceParamGetString(CpuContext ctx)
    {
        var parameterId = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var bufferSize = unchecked((int)ctx[CpuRegister.Rdx]);
        if (bufferAddress == 0 || bufferSize <= 0 || parameterId != ParamIdSystemName)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        // The system name (SCE_SYSTEM_SERVICE_PARAM_ID_SYSTEM_NAME, buffers of 65 bytes) is the
        // only string parameter. anyps5 reports the console default "PS5"; callers that gate UI
        // setup on a successful read stall on failure, so a shorter buffer is truncated (and
        // NUL-terminated) instead of rejected, which neither reference defines.
        var value = System.Text.Encoding.UTF8.GetBytes("PS5");
        var writeLength = Math.Min(value.Length, Math.Min(bufferSize, MaxSystemNameLength) - 1);
        Span<byte> output = stackalloc byte[writeLength + 1];
        value.AsSpan(0, writeLength).CopyTo(output);
        output[writeLength] = 0;
        return ctx.Memory.TryWrite(bufferAddress, output)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "rPo6tV8D9bM",
        ExportName = "sceSystemServiceGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetStatus(CpuContext ctx)
    {
        var statusAddress = ctx[CpuRegister.Rdi];
        if (statusAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> status = stackalloc byte[SystemServiceStatusSize];
        status.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(status, PendingEventCount);
        status[0x06] = 1;

        return ctx.Memory.TryWrite(statusAddress, status)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "1n37q1Bvc5Y",
        ExportName = "sceSystemServiceGetDisplaySafeAreaInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetDisplaySafeAreaInfo(CpuContext ctx)
    {
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> info = stackalloc byte[DisplaySafeAreaInfoSize];
        info.Clear();
        BinaryPrimitives.WriteSingleLittleEndian(info, 1.0f);

        return ctx.Memory.TryWrite(infoAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "mPpPxv5CZt4",
        ExportName = "sceSystemServiceGetHdrToneMapLuminance",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetHdrToneMapLuminance(CpuContext ctx)
    {
        var luminanceAddress = ctx[CpuRegister.Rdi];
        if (luminanceAddress == 0)
        {
            return ctx.SetReturn(OrbisSystemServiceErrorParameter);
        }

        Span<byte> luminance = stackalloc byte[HdrToneMapLuminanceSize];
        BinaryPrimitives.WriteSingleLittleEndian(luminance, 1000.0f);
        BinaryPrimitives.WriteSingleLittleEndian(luminance[sizeof(float)..], 1000.0f);
        BinaryPrimitives.WriteSingleLittleEndian(luminance[(sizeof(float) * 2)..], 0.01f);
        return ctx.Memory.TryWrite(luminanceAddress, luminance)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "Vo5V8KAwCmk",
        ExportName = "sceSystemServiceHideSplashScreen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceHideSplashScreen(CpuContext ctx)
    {
        GuestGpu.Current.HideSplashScreen();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "3s8cHiCBKBE",
        ExportName = "sceSystemServiceReportAbnormalTermination",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceReportAbnormalTermination(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "XbbJC3E+L5M",
        ExportName = "sceSystemServicePowerTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServicePowerTick(CpuContext ctx) => ctx.SetReturn(0);

    // The host has no shell to hand the launch to and the SDK documents no "unsupported"
    // code for it, so the launch calls report the kernel's EOPNOTSUPP like anyps5 does for
    // the web browser. LoadExec("exit") and the title relaunch are not modelled.
    [SysAbiExport(
        Nid = "JoBqSQt1yyA",
        ExportName = "sceSystemServiceLoadExec",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceLoadExec(CpuContext ctx) =>
        ctx[CpuRegister.Rdi] == 0
            ? ctx.SetReturn(OrbisSystemServiceErrorParameter)
            : ctx.SetReturn(OrbisKernelErrorNotSupported);

    [SysAbiExport(
        Nid = "-+3hY+y8bNo",
        ExportName = "sceSystemServiceLaunchWebBrowser",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceLaunchWebBrowser(CpuContext ctx) =>
        ctx[CpuRegister.Rdi] == 0
            ? ctx.SetReturn(OrbisSystemServiceErrorParameter)
            : ctx.SetReturn(OrbisKernelErrorNotSupported);

    [SysAbiExport(
        Nid = "l4FB3wNa-Ac",
        ExportName = "sceSystemServiceLaunchApp",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceLaunchApp(CpuContext ctx) =>
        ctx[CpuRegister.Rdi] == 0
            ? ctx.SetReturn(OrbisSystemServiceErrorParameter)
            : ctx.SetReturn(OrbisKernelErrorNotSupported);

    // The app id is emulator-local (anyps5 also reports 1); only the running title exists.
    [SysAbiExport(
        Nid = "NwT2R5lo8P8",
        ExportName = "sceSystemServiceGetAppIdOfRunningBigApp",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceGetAppIdOfRunningBigApp(CpuContext ctx) =>
        ctx.SetReturn(RunningAppId);

    // Ending the running title is the shell's job on a console and has no host hook here:
    // an unknown app id is PARAMETER, the running one reports not-supported instead of exiting.
    [SysAbiExport(
        Nid = "N4RkyJh7FtA",
        ExportName = "sceSystemServiceKillApp",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceKillApp(CpuContext ctx) =>
        unchecked((int)ctx[CpuRegister.Rdi]) != RunningAppId
            ? ctx.SetReturn(OrbisSystemServiceErrorParameter)
            : ctx.SetReturn(OrbisKernelErrorNotSupported);

    [SysAbiExport(
        Nid = "w9wlKcHrmm8",
        ExportName = "sceSystemServiceShowControllerSettings",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceShowControllerSettings(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "64nkF7LGk8w",
        ExportName = "sceSystemServiceDisableMediaPlay",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int SystemServiceDisableMediaPlay(CpuContext ctx) => ctx.SetReturn(0);

    // Host-sourced settings. kyty hardcodes UTC+3 / DD.MM.YYYY / 24h and anyps5 UTC / DD.MM.YYYY /
    // 24h; deriving them from the host is the more sensible default.
    internal static int GetHostTimeZoneMinutes()
    {
        // The console time zone excludes daylight saving, which is reported separately.
        var minutes = (int)TimeZoneInfo.Local.BaseUtcOffset.TotalMinutes;
        return Math.Clamp(minutes, -1439, 1439);
    }

    internal static int GetHostSummertime() =>
        TimeZoneInfo.Local.IsDaylightSavingTime(DateTimeOffset.Now) ? 1 : 0;

    // 0 = YYYY/MM/DD, 1 = DD/MM/YYYY, 2 = MM/DD/YYYY.
    internal static int GetHostDateFormat()
    {
        var pattern = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
        var year = pattern.IndexOf('y');
        var month = pattern.IndexOf('M');
        var day = pattern.IndexOf('d');
        if (year >= 0 && year < month && year < day)
            return 0;
        return month >= 0 && month < day ? 2 : 1;
    }

    // 0 = 12-hour, 1 = 24-hour.
    internal static int GetHostTimeFormat() =>
        System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('t') ? 0 : 1;

    internal static void ResetForTests()
    {
        Volatile.Write(ref _noticeScreenSkipFlag, 0);
        Volatile.Write(ref _systemLanguage, 1);
        lock (_eventLock)
            _events.Clear();
    }
}
