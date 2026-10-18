// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Rtc;

public static class RtcExports
{
    private const long DateTimeTicksPerMicrosecond = 10;
    private const ulong MicrosecondsPerSecond = 1_000_000UL;
    private const ulong MicrosecondsPerMinute = 60_000_000UL;
    private const ulong MicrosecondsPerHour = 3_600_000_000UL;
    private const ulong MicrosecondsPerDay = 86_400_000_000UL;
    private const ulong MicrosecondsPerWeek = 604_800_000_000UL;
    private const ulong UnixEpochTicks = 62_135_596_800_000_000UL;
    private const ulong Win32FileTimeEpochTicks = 50_491_123_200_000_000UL;

    [SysAbiExport(
        Nid = "lPEBYdVX0XQ",
        ExportName = "sceRtcCheckValid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcCheckValid(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        if (timeAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        if (!TryReadRtcDateTime(ctx, timeAddress, out var time))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return ValidateRtcDateTime(time);
    }

    [SysAbiExport(
        Nid = "fNaZ4DbzHAE",
        ExportName = "sceRtcCompareTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcCompareTick(CpuContext ctx)
    {
        var tick1Address = ctx[CpuRegister.Rdi];
        var tick2Address = ctx[CpuRegister.Rsi];
        if (tick1Address == 0 || tick2Address == 0)
        {
            return unchecked((int)0x80B50002);
        }

        if (!ctx.TryReadUInt64(tick1Address, out var tick1) || !ctx.TryReadUInt64(tick2Address, out var tick2))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return tick1 < tick2 ? -1 : tick1 > tick2 ? 1 : 0;
    }

    [SysAbiExport(
        Nid = "8Yr143yEnRo",
        ExportName = "sceRtcConvertLocalTimeToUtc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcConvertLocalTimeToUtc(CpuContext ctx)
    {
        var tickLocalAddress = ctx[CpuRegister.Rdi];
        var tickUtcAddress = ctx[CpuRegister.Rsi];
        if (tickLocalAddress == 0 || tickUtcAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        if (!ctx.TryReadUInt64(tickLocalAddress, out var tickLocal))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!TryConvertTickToDateTime(tickLocal, out var localDateTime))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        // TryConvertTickToDateTime yields a Utc-kind DateTime, but here the tick is a local
        // wall-clock time. ConvertTimeToUtc throws ArgumentException when a Utc-kind value is
        // paired with a non-UTC source zone, so on any host not set to UTC this would always
        // fail. Re-tag as Unspecified so the value is interpreted as local time and converted.
        localDateTime = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);

        DateTime utcDateTime;
        try
        {
            utcDateTime = TimeZoneInfo.ConvertTimeToUtc(localDateTime, TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryWriteTickFromDateTime(ctx, tickUtcAddress, utcDateTime))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "M1TvFst-jrM",
        ExportName = "sceRtcConvertUtcToLocalTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcConvertUtcToLocalTime(CpuContext ctx)
    {
        var tickUtcAddress = ctx[CpuRegister.Rdi];
        var tickLocalAddress = ctx[CpuRegister.Rsi];
        if (tickUtcAddress == 0 || tickLocalAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        if (!ctx.TryReadUInt64(tickUtcAddress, out var tickUtc))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!TryConvertTickToDateTime(tickUtc, out var utcDateTime))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        DateTime localDateTime;
        try
        {
            localDateTime = TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryWriteTickFromDateTime(ctx, tickLocalAddress, localDateTime))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "8SljQx6pDP8",
        ExportName = "sceRtcEnd",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcEnd(CpuContext ctx)
    {
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "LN3Zcb72Q0c",
        ExportName = "sceRtcGetCurrentAdNetworkTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetCurrentAdNetworkTick(CpuContext ctx) => RtcGetCurrentTick(ctx);

    [SysAbiExport(
        Nid = "8lfvnRMqwEM",
        ExportName = "sceRtcGetCurrentClock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetCurrentClock(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        if (timeAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        var timeZoneMinutes = unchecked((int)ctx[CpuRegister.Rsi]);
        DateTimeOffset currentTime;
        try
        {
            currentTime = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(timeZoneMinutes));
        }
        catch (ArgumentOutOfRangeException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryWriteRtcDateTime(ctx, timeAddress, ToRtcDateTime(currentTime.DateTime)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "ZPD1YOKI+Kw",
        ExportName = "sceRtcGetCurrentClockLocalTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetCurrentClockLocalTime(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        if (timeAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        if (!TryWriteRtcDateTime(ctx, timeAddress, ToRtcDateTime(DateTimeOffset.Now.DateTime)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Ot1DE3gif84",
        ExportName = "sceRtcGetCurrentDebugNetworkTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetCurrentDebugNetworkTick(CpuContext ctx) => RtcGetCurrentTick(ctx);

    [SysAbiExport(
        Nid = "zO9UL3qIINQ",
        ExportName = "sceRtcGetCurrentNetworkTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetCurrentNetworkTick(CpuContext ctx) => RtcGetCurrentTick(ctx);

    [SysAbiExport(
        Nid = "HWxHOdbM-Pg",
        ExportName = "sceRtcGetCurrentRawNetworkTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetCurrentRawNetworkTick(CpuContext ctx) => RtcGetCurrentTick(ctx);

    // Diagnostic: middleware busy-wait loops typically poll sceRtcGetCurrentTick, so the
    // caller's return address pinpoints the loop. SHARPEMU_RTC_PROBE_RANGE=<start>-<end>
    // (hex guest addresses) dumps 0x100 bytes of code around the first matching caller,
    // once, for offline disassembly. Costs nothing when the variable is unset.
    private static readonly ulong[]? _rtcProbeRange = ParseRtcProbeRange();
    private static int _rtcProbeDone;

    private static ulong[]? ParseRtcProbeRange()
    {
        var value = Environment.GetEnvironmentVariable("SHARPEMU_RTC_PROBE_RANGE");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('-', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 &&
               TryParseHexAddress(parts[0], out var start) &&
               TryParseHexAddress(parts[1], out var end) &&
               start < end
            ? [start, end]
            : null;
    }

    private static bool TryParseHexAddress(string value, out ulong address)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        return ulong.TryParse(
            value,
            System.Globalization.NumberStyles.HexNumber,
            null,
            out address);
    }

    private static void ProbeRtcCaller(CpuContext ctx)
    {
        if (Volatile.Read(ref _rtcProbeDone) != 0 ||
            !ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out var ret) ||
            ret < _rtcProbeRange![0] ||
            ret >= _rtcProbeRange[1] ||
            Interlocked.CompareExchange(ref _rtcProbeDone, 1, 0) != 0)
        {
            return;
        }

        var start = ret - 0x60;
        Span<byte> code = stackalloc byte[0x100];
        if (ctx.Memory.TryRead(start, code))
        {
            Console.Error.WriteLine(
                $"[LOADER][DIAG] rtc.caller_code ret=0x{ret:X} @0x{start:X}: {System.Convert.ToHexString(code)}");
        }
    }

    [SysAbiExport(
        Nid = "18B2NS1y9UU",
        ExportName = "sceRtcGetCurrentTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetCurrentTick(CpuContext ctx)
    {
        var tickAddress = ctx[CpuRegister.Rdi];
        if (tickAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        if (_rtcProbeRange is not null)
        {
            ProbeRtcCaller(ctx);
        }

        var tickValue = unchecked((ulong)(DateTime.UtcNow.Ticks / DateTimeTicksPerMicrosecond));
        if (!ctx.TryWriteUInt64(tickAddress, tickValue))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "CyIK-i4XdgQ",
        ExportName = "sceRtcGetDayOfWeek",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetDayOfWeek(CpuContext ctx)
    {
        var year = unchecked((int)ctx[CpuRegister.Rdi]);
        var month = unchecked((int)ctx[CpuRegister.Rsi]);
        var day = unchecked((int)ctx[CpuRegister.Rdx]);
        if (!IsValidCalendarDate(year, month, day))
        {
            return unchecked((int)0x80B50004);
        }

        return (int)new DateTime(year, month, day).DayOfWeek;
    }

    [SysAbiExport(
        Nid = "3O7Ln8AqJ1o",
        ExportName = "sceRtcGetDaysInMonth",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetDaysInMonth(CpuContext ctx)
    {
        var year = unchecked((int)ctx[CpuRegister.Rdi]);
        var month = unchecked((int)ctx[CpuRegister.Rsi]);
        if (year < 1 || year > 9999)
        {
            return unchecked((int)0x80B50008);
        }

        if (month < 1 || month > 12)
        {
            return unchecked((int)0x80B50009);
        }

        return DateTime.DaysInMonth(year, month);
    }

    [SysAbiExport(
        Nid = "E7AR4o7Ny7E",
        ExportName = "sceRtcGetDosTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetDosTime(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        var dosTimeAddress = ctx[CpuRegister.Rsi];
        if (timeAddress == 0 || dosTimeAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        if (!TryReadRtcDateTime(ctx, timeAddress, out var time))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var validationResult = ValidateRtcDateTime(time);
        if (validationResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return validationResult;
        }

        // ValidateRtcDateTime accepts years 1-9999, but the DOS field is a 7-bit offset from
        // 1980 and only spans 1980-2107. Out-of-range years otherwise wrap into a valid-looking
        // date (1979 packs identically to 2107) and the call still reports success.
        if (time.Year < 1980 || time.Year > 2107)
        {
            return unchecked((int)0x80B50008);
        }

        uint dosTime = 0;
        dosTime |= (uint)((time.Second / 2) & 0x1F);
        dosTime |= (uint)(time.Minute & 0x3F) << 5;
        dosTime |= (uint)(time.Hour & 0x1F) << 11;
        dosTime |= (uint)(time.Day & 0x1F) << 16;
        dosTime |= (uint)(time.Month & 0x0F) << 21;
        dosTime |= (uint)((time.Year - 1980) & 0x7F) << 25;

        if (!ctx.TryWriteUInt32(dosTimeAddress, dosTime))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "8w-H19ip48I",
        ExportName = "sceRtcGetTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetTick(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        var tickAddress = ctx[CpuRegister.Rsi];
        if (timeAddress == 0 || tickAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        if (!TryReadRtcDateTime(ctx, timeAddress, out var time))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var validationResult = ValidateRtcDateTime(time);
        if (validationResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return validationResult;
        }

        if (!TryConvertRtcDateTimeToTick(time, out var tick))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!ctx.TryWriteUInt64(tickAddress, tick))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "jMNwqYr4R-k",
        ExportName = "sceRtcGetTickResolution",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetTickResolution(CpuContext ctx)
    {
        return (int)MicrosecondsPerSecond;
    }

    [SysAbiExport(
        Nid = "BtqmpTRXHgk",
        ExportName = "sceRtcGetTime_t",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetTimeT(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        var timeTAddress = ctx[CpuRegister.Rsi];
        if (timeAddress == 0 || timeTAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        if (!TryReadRtcDateTime(ctx, timeAddress, out var time))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var validationResult = ValidateRtcDateTime(time);
        if (validationResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return validationResult;
        }

        if (!TryConvertRtcDateTimeToTick(time, out var tick))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var unixSeconds = tick < UnixEpochTicks ? 0UL : (tick - UnixEpochTicks) / MicrosecondsPerSecond;
        if (!ctx.TryWriteUInt64(timeTAddress, unixSeconds))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "jfRO0uTjtzA",
        ExportName = "sceRtcGetWin32FileTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcGetWin32FileTime(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        var fileTimeAddress = ctx[CpuRegister.Rsi];
        if (timeAddress == 0 || fileTimeAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        if (!TryReadRtcDateTime(ctx, timeAddress, out var time))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var validationResult = ValidateRtcDateTime(time);
        if (validationResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return validationResult;
        }

        if (!TryConvertRtcDateTimeToTick(time, out var tick))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var win32Time = tick < Win32FileTimeEpochTicks ? 0UL : (tick - Win32FileTimeEpochTicks) * 10UL;
        if (!ctx.TryWriteUInt64(fileTimeAddress, win32Time))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "LlodCMDbk3o",
        ExportName = "sceRtcInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcInit(CpuContext ctx)
    {
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Ug8pCwQvh0c",
        ExportName = "sceRtcIsLeapYear",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcIsLeapYear(CpuContext ctx)
    {
        var year = unchecked((int)ctx[CpuRegister.Rdi]);
        if (year < 1 || year > 9999)
        {
            return unchecked((int)0x80B50008);
        }

        return DateTime.IsLeapYear(year) ? 1 : 0;
    }

    [SysAbiExport(
        Nid = "NR1J0N7L2xY",
        ExportName = "sceRtcTickAddDays",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddDays(CpuContext ctx) => AddTickDelta(ctx, MicrosecondsPerDay);

    [SysAbiExport(
        Nid = "MDc5cd8HfCA",
        ExportName = "sceRtcTickAddHours",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddHours(CpuContext ctx) => AddTickDelta(ctx, MicrosecondsPerHour);

    [SysAbiExport(
        Nid = "XPIiw58C+GM",
        ExportName = "sceRtcTickAddMicroseconds",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddMicroseconds(CpuContext ctx) => AddTickDelta(ctx, 1UL);

    [SysAbiExport(
        Nid = "mn-tf4QiFzk",
        ExportName = "sceRtcTickAddMinutes",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddMinutes(CpuContext ctx) => AddTickDelta(ctx, MicrosecondsPerMinute);

    [SysAbiExport(
        Nid = "CL6y9q-XbuQ",
        ExportName = "sceRtcTickAddMonths",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddMonths(CpuContext ctx)
    {
        return AddCalendarDelta(ctx, dateTime => dateTime.AddMonths(unchecked((int)ctx[CpuRegister.Rdx])));
    }

    [SysAbiExport(
        Nid = "07O525HgICs",
        ExportName = "sceRtcTickAddSeconds",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddSeconds(CpuContext ctx) => AddTickDelta(ctx, MicrosecondsPerSecond);

    [SysAbiExport(
        Nid = "AqVMssr52Rc",
        ExportName = "sceRtcTickAddTicks",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddTicks(CpuContext ctx) => AddTickDelta(ctx, 1UL);

    [SysAbiExport(
        Nid = "gI4t194c2W8",
        ExportName = "sceRtcTickAddWeeks",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddWeeks(CpuContext ctx) => AddTickDelta(ctx, MicrosecondsPerWeek);

    [SysAbiExport(
        Nid = "-5y2uJ62qS8",
        ExportName = "sceRtcTickAddYears",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcTickAddYears(CpuContext ctx)
    {
        return AddCalendarDelta(ctx, dateTime => dateTime.AddYears(unchecked((int)ctx[CpuRegister.Rdx])));
    }

    [SysAbiExport(
        Nid = "ueega6v3GUw",
        ExportName = "sceRtcSetTick",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcSetTick(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        var tickAddress = ctx[CpuRegister.Rsi];
        if (timeAddress == 0 || tickAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        if (!ctx.TryReadUInt64(tickAddress, out var tickValue))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!TryConvertTickToRtcDateTime(tickValue, out var time))
        {
            return unchecked((int)0x80B50004);
        }

        if (!TryWriteRtcDateTime(ctx, timeAddress, time))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "aYPCd1cChyg",
        ExportName = "sceRtcSetDosTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcSetDosTime(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        if (timeAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        var dosTime = unchecked((uint)ctx[CpuRegister.Rsi]);
        var time = new RtcDateTime(
            (ushort)(1980 + ((dosTime >> 25) & 0x7F)),
            (ushort)((dosTime >> 21) & 0x0F),
            (ushort)((dosTime >> 16) & 0x1F),
            (ushort)((dosTime >> 11) & 0x1F),
            (ushort)((dosTime >> 5) & 0x3F),
            (ushort)((dosTime << 1) & 0x3E),
            0);

        if (!TryWriteRtcDateTime(ctx, timeAddress, time))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "bDEVVP4bTjQ",
        ExportName = "sceRtcSetTime_t",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcSetTimeT(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        if (timeAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        var timeSeconds = unchecked((long)ctx[CpuRegister.Rsi]);
        if (timeSeconds < 0)
        {
            return unchecked((int)0x80B50003);
        }

        var tick = UnixEpochTicks + unchecked((ulong)timeSeconds) * MicrosecondsPerSecond;
        if (!TryConvertTickToRtcDateTime(tick, out var time))
        {
            return unchecked((int)0x80B50003);
        }

        if (!TryWriteRtcDateTime(ctx, timeAddress, time))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "n5JiAJXsbcs",
        ExportName = "sceRtcSetWin32FileTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcSetWin32FileTime(CpuContext ctx)
    {
        var timeAddress = ctx[CpuRegister.Rdi];
        if (timeAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        var fileTime = unchecked((long)ctx[CpuRegister.Rsi]);
        if (fileTime < 0)
        {
            return unchecked((int)0x80B50003);
        }

        var tick = Win32FileTimeEpochTicks + unchecked((ulong)fileTime / 10UL);
        if (!TryConvertTickToRtcDateTime(tick, out var time))
        {
            return unchecked((int)0x80B50003);
        }

        if (!TryWriteRtcDateTime(ctx, timeAddress, time))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- RFC 3339 / RFC 2822 formatting and parsing -------------------------------------
    //
    // Formatting follows kyty and anyps5, which agree on the RFC 3339 layout (centisecond
    // fraction, "Z" for a zero offset). RFC 2822 has a body only in anyps5. Parse failures use
    // SCE_RTC_ERROR_BAD_PARSE (anyps5); kyty answers INVALID_VALUE, which is not an SDK parse code.

    private const int RtcErrorInvalidPointer = unchecked((int)0x80B50002);
    private const int RtcErrorInvalidValue = unchecked((int)0x80B50003);
    private const int RtcErrorBadParse = unchecked((int)0x80B50007);
    private const int MaxTimeZoneMinutes = 1439;
    private const int MaxParseLength = 128;
    private const ulong MaxTick = 3_652_059UL * MicrosecondsPerDay - 1;

    private static readonly string[] WeekdayAbbreviations = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private static readonly string[] WeekdayNames =
        { "SUNDAY", "MONDAY", "TUESDAY", "WEDNESDAY", "THURSDAY", "FRIDAY", "SATURDAY" };
    private static readonly string[] MonthAbbreviations =
        { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
    private static readonly string[] MonthNames =
    {
        "JANUARY", "FEBRUARY", "MARCH", "APRIL", "MAY", "JUNE",
        "JULY", "AUGUST", "SEPTEMBER", "OCTOBER", "NOVEMBER", "DECEMBER",
    };

    // Zone names accepted by sceRtcParseDateTime, as minutes east of UTC.
    private static readonly (string Name, int Minutes)[] ZoneNames =
    {
        ("UT", 0), ("GMT", 0), ("WET", 0), ("BST", 60), ("WAT", -60), ("AST", -240), ("ADT", -180),
        ("EST", -300), ("EDT", -240), ("CST", -360), ("CDT", -300), ("MST", -420), ("MDT", -360),
        ("PST", -480), ("PDT", -420), ("CET", 60), ("MET", 60), ("MEST", 120), ("CEST", 120),
        ("EET", 120), ("JST", 540), ("KST", 540), ("AEST", 600), ("NZST", 720), ("NZDT", 780),
    };

    private static readonly (string Name, int Minutes)[] ZoneNamesLongestFirst =
        ZoneNames.OrderByDescending(z => z.Name.Length).ToArray();

    [SysAbiExport(
        Nid = "WJ3rqFwymew",
        ExportName = "sceRtcFormatRFC3339",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcFormatRfc3339(CpuContext ctx) =>
        FormatRfc(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], unchecked((int)ctx[CpuRegister.Rdx]), rfc2822: false);

    [SysAbiExport(
        Nid = "DwuHIlLGW8I",
        ExportName = "sceRtcFormatRFC3339LocalTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcFormatRfc3339LocalTime(CpuContext ctx) =>
        FormatRfcLocal(ctx, rfc2822: false);

    [SysAbiExport(
        Nid = "eiuobaF-hK4",
        ExportName = "sceRtcFormatRFC2822",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcFormatRfc2822(CpuContext ctx) =>
        FormatRfc(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], unchecked((int)ctx[CpuRegister.Rdx]), rfc2822: true);

    [SysAbiExport(
        Nid = "AxHBk3eat04",
        ExportName = "sceRtcFormatRFC2822LocalTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcFormatRfc2822LocalTime(CpuContext ctx) =>
        FormatRfcLocal(ctx, rfc2822: true);

    [SysAbiExport(
        Nid = "99bMGglFW3I",
        ExportName = "sceRtcParseRFC3339",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcParseRfc3339(CpuContext ctx) =>
        ParseDateTimeText(ctx, anyFormat: false);

    [SysAbiExport(
        Nid = "NxEI1KByvCI",
        ExportName = "sceRtcParseDateTime",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceRtc")]
    public static int RtcParseDateTime(CpuContext ctx) =>
        ParseDateTimeText(ctx, anyFormat: true);

    private static int FormatRfcLocal(CpuContext ctx, bool rfc2822)
    {
        var outputAddress = ctx[CpuRegister.Rdi];
        var tickAddress = ctx[CpuRegister.Rsi];
        if (outputAddress == 0 || tickAddress == 0)
        {
            return RtcErrorInvalidPointer;
        }

        if (!ctx.TryReadUInt64(tickAddress, out var tick))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!TryConvertTickToDateTime(tick, out var utc))
        {
            return RtcErrorInvalidValue;
        }

        var offsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(utc).TotalMinutes;
        return FormatRfc(ctx, outputAddress, tickAddress, offsetMinutes, rfc2822);
    }

    private static int FormatRfc(CpuContext ctx, ulong outputAddress, ulong tickAddress, int timeZoneMinutes, bool rfc2822)
    {
        if (outputAddress == 0 || tickAddress == 0)
        {
            return RtcErrorInvalidPointer;
        }

        if (timeZoneMinutes < -MaxTimeZoneMinutes || timeZoneMinutes > MaxTimeZoneMinutes)
        {
            return RtcErrorInvalidValue;
        }

        if (!ctx.TryReadUInt64(tickAddress, out var tick))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        // The shifted tick has to stay inside the representable range, which also rejects an
        // input beyond the maximum tick.
        var shifted = (long)tick + (long)timeZoneMinutes * (long)MicrosecondsPerMinute;
        if (tick > MaxTick || shifted < 0 || (ulong)shifted > MaxTick)
        {
            return RtcErrorInvalidValue;
        }

        var local = new DateTime(shifted * DateTimeTicksPerMicrosecond, DateTimeKind.Unspecified);
        var absoluteOffset = Math.Abs(timeZoneMinutes);
        var sign = timeZoneMinutes < 0 ? '-' : '+';
        string text;
        if (rfc2822)
        {
            var weekday = WeekdayAbbreviations[(int)local.DayOfWeek];
            var month = MonthAbbreviations[local.Month - 1];
            text = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{weekday}, {local.Day:D2} {month} {local.Year:D4} {local.Hour:D2}:{local.Minute:D2}:{local.Second:D2} " +
                $"{sign}{absoluteOffset / 60:D2}{absoluteOffset % 60:D2}");
        }
        else
        {
            var centiseconds = (int)(local.Ticks % TimeSpan.TicksPerSecond / (TimeSpan.TicksPerSecond / 100));
            var zone = timeZoneMinutes == 0
                ? "Z"
                : string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{sign}{absoluteOffset / 60:D2}:{absoluteOffset % 60:D2}");
            text = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{local.Year:D4}-{local.Month:D2}-{local.Day:D2}T{local.Hour:D2}:{local.Minute:D2}:{local.Second:D2}.{centiseconds:D2}{zone}");
        }

        Span<byte> output = stackalloc byte[text.Length + 1];
        System.Text.Encoding.ASCII.GetBytes(text, output);
        output[text.Length] = 0;
        return ctx.Memory.TryWrite(outputAddress, output)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    private static int ParseDateTimeText(CpuContext ctx, bool anyFormat)
    {
        var tickAddress = ctx[CpuRegister.Rdi];
        var textAddress = ctx[CpuRegister.Rsi];
        if (tickAddress == 0 || textAddress == 0)
        {
            return RtcErrorInvalidPointer;
        }

        if (!TryReadAsciiString(ctx, textAddress, out var text))
        {
            return RtcErrorBadParse;
        }

        var result = anyFormat
            ? ParseAnyDateTime(text, out var tick)
            : ParseRfc3339Text(text, out tick);
        if (result != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return result;
        }

        return ctx.TryWriteUInt64(tickAddress, tick)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    private static bool TryReadAsciiString(CpuContext ctx, ulong address, out string text)
    {
        Span<byte> one = stackalloc byte[1];
        Span<char> chars = stackalloc char[MaxParseLength];
        for (var index = 0; index < MaxParseLength; index++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)index, one))
            {
                break;
            }

            if (one[0] == 0)
            {
                text = new string(chars[..index]);
                return true;
            }

            chars[index] = (char)one[0];
        }

        text = string.Empty;
        return false;
    }

    // Combines parsed local fields and a zone offset (minutes east of UTC) into a UTC tick.
    private static int FinishParse(
        int year, int month, int day, int hour, int minute, int second, uint microsecond,
        int offsetMinutes, out ulong utcTick)
    {
        utcTick = 0;
        // A leap second (60) is accepted and rolls into the next minute.
        var leap = second == 60;
        var fields = new RtcDateTime(
            (ushort)Math.Clamp(year, 0, ushort.MaxValue),
            (ushort)Math.Clamp(month, 0, ushort.MaxValue),
            (ushort)Math.Clamp(day, 0, ushort.MaxValue),
            (ushort)Math.Clamp(hour, 0, ushort.MaxValue),
            (ushort)Math.Clamp(minute, 0, ushort.MaxValue),
            (ushort)(leap ? 59 : Math.Clamp(second, 0, ushort.MaxValue)),
            microsecond);
        var validation = ValidateRtcDateTime(fields);
        if (validation != 0)
        {
            return validation;
        }

        if (!TryConvertRtcDateTimeToTick(fields, out var local))
        {
            return RtcErrorInvalidValue;
        }

        var utc = (long)local + (leap ? (long)MicrosecondsPerSecond : 0) - (long)offsetMinutes * (long)MicrosecondsPerMinute;
        if (utc < 0 || (ulong)utc > MaxTick)
        {
            return RtcErrorInvalidValue;
        }

        utcTick = (ulong)utc;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private sealed class TextCursor
    {
        private readonly string _text;

        public TextCursor(string text) => _text = text;

        public int Position { get; private set; }

        public char Current => Position < _text.Length ? _text[Position] : '\0';

        public bool AtEnd => Position >= _text.Length;

        public void Advance() => Position++;

        public void SkipBlanks()
        {
            while (Current == ' ' || Current == '\t')
            {
                Position++;
            }
        }

        public bool Expect(char value)
        {
            if (Current != value)
            {
                return false;
            }

            Position++;
            return true;
        }

        // Reads exactly <count> digits.
        public bool TryReadFixed(int count, out int value)
        {
            value = 0;
            for (var i = 0; i < count; i++)
            {
                if (Current < '0' || Current > '9')
                {
                    return false;
                }

                value = value * 10 + (Current - '0');
                Position++;
            }

            return true;
        }

        // Reads between 1 and <max> digits and reports how many were read.
        public int ReadUpTo(int max, out int value)
        {
            value = 0;
            var count = 0;
            while (count < max && Current >= '0' && Current <= '9')
            {
                value = value * 10 + (Current - '0');
                Position++;
                count++;
            }

            return count;
        }

        // Case-insensitive match of a full name, or failing that of its first three letters.
        public bool TryReadName(string[] upperNames, out int index)
        {
            for (index = 0; index < upperNames.Length; index++)
            {
                if (TryMatch(upperNames[index], upperNames[index].Length) || TryMatch(upperNames[index], 3))
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryMatch(string upperWord, int length)
        {
            if (Position + length > _text.Length)
            {
                return false;
            }

            for (var i = 0; i < length; i++)
            {
                if (char.ToUpperInvariant(_text[Position + i]) != upperWord[i])
                {
                    return false;
                }
            }

            Position += length;
            return true;
        }
    }

    private static int ParseRfc3339Text(string text, out ulong utcTick)
    {
        utcTick = 0;
        var cursor = new TextCursor(text);
        if (!cursor.TryReadFixed(4, out var year) || !cursor.Expect('-')
            || !cursor.TryReadFixed(2, out var month) || !cursor.Expect('-')
            || !cursor.TryReadFixed(2, out var day))
        {
            return RtcErrorBadParse;
        }

        if (cursor.Current != 'T' && cursor.Current != 't')
        {
            return RtcErrorBadParse;
        }

        cursor.Advance();
        if (!cursor.TryReadFixed(2, out var hour) || !cursor.Expect(':')
            || !cursor.TryReadFixed(2, out var minute) || !cursor.Expect(':')
            || !cursor.TryReadFixed(2, out var second))
        {
            return RtcErrorBadParse;
        }

        uint microsecond = 0;
        if (cursor.Current == '.')
        {
            cursor.Advance();
            var scale = 100_000u;
            while (cursor.Current >= '0' && cursor.Current <= '9')
            {
                // Digits beyond microsecond precision are dropped.
                microsecond += (uint)(cursor.Current - '0') * scale;
                scale /= 10;
                cursor.Advance();
            }
        }

        var offsetMinutes = 0;
        if (cursor.Current == '+' || cursor.Current == '-')
        {
            var sign = cursor.Current == '-' ? -1 : 1;
            cursor.Advance();
            if (!cursor.TryReadFixed(2, out var offsetHours) || !cursor.Expect(':')
                || !cursor.TryReadFixed(2, out var offsetMinute))
            {
                return RtcErrorBadParse;
            }

            offsetMinutes = sign * (offsetHours * 60 + offsetMinute);
        }
        else if (cursor.Current != 'Z' && cursor.Current != 'z')
        {
            // The zone designator is mandatory (anyps5); kyty accepts a missing one as UTC.
            return RtcErrorBadParse;
        }

        return FinishParse(year, month, day, hour, minute, second, microsecond, offsetMinutes, out utcTick);
    }

    private static int ParseAnyDateTime(string text, out ulong utcTick)
    {
        utcTick = 0;
        var cursor = new TextCursor(text);
        cursor.SkipBlanks();
        if (cursor.Current >= '0' && cursor.Current <= '9')
        {
            return ParseRfc3339Text(text[cursor.Position..], out utcTick);
        }

        if (!cursor.TryReadName(WeekdayNames, out _))
        {
            return RtcErrorBadParse;
        }

        if (cursor.Current == ',')
        {
            cursor.Advance();
        }

        cursor.SkipBlanks();
        var isRfc2822 = cursor.Current >= '0' && cursor.Current <= '9';
        int year, month, day, hour, minute, second = 0;
        var offsetMinutes = 0;
        if (isRfc2822)
        {
            // "DD Mon YYYY HH:MM[:SS] [zone]" (a two-digit year is windowed at 50).
            if (cursor.ReadUpTo(2, out day) == 0 || (cursor.Current != ' ' && cursor.Current != '-'))
            {
                return RtcErrorBadParse;
            }

            cursor.Advance();
            if (!cursor.TryReadName(MonthNames, out var monthIndex) || (cursor.Current != ' ' && cursor.Current != '-'))
            {
                return RtcErrorBadParse;
            }

            month = monthIndex + 1;
            cursor.Advance();
            var yearDigits = cursor.ReadUpTo(4, out year);
            if ((yearDigits != 2 && yearDigits != 4) || !cursor.Expect(' '))
            {
                return RtcErrorBadParse;
            }

            if (yearDigits == 2)
            {
                year += year < 50 ? 2000 : 1900;
            }

            if (cursor.ReadUpTo(2, out hour) == 0 || !cursor.Expect(':') || cursor.ReadUpTo(2, out minute) == 0)
            {
                return RtcErrorBadParse;
            }

            if (cursor.Current == ':')
            {
                cursor.Advance();
                if (cursor.ReadUpTo(2, out second) == 0)
                {
                    return RtcErrorBadParse;
                }

                if (cursor.Current == ' ')
                {
                    cursor.Advance();
                    if (!TryParseZone(cursor, out offsetMinutes))
                    {
                        return RtcErrorBadParse;
                    }
                }
            }
            else if (cursor.Current != ' ')
            {
                return RtcErrorBadParse;
            }
        }
        else
        {
            // asctime: "Mon DD HH:MM:SS YYYY", the day possibly space padded.
            if (!cursor.TryReadName(MonthNames, out var monthIndex) || !cursor.Expect(' '))
            {
                return RtcErrorBadParse;
            }

            month = monthIndex + 1;
            if (cursor.Current == ' ')
            {
                cursor.Advance();
                if (cursor.ReadUpTo(1, out day) == 0)
                {
                    return RtcErrorBadParse;
                }
            }
            else if (cursor.ReadUpTo(2, out day) == 0)
            {
                return RtcErrorBadParse;
            }

            if (!cursor.Expect(' ') || cursor.ReadUpTo(2, out hour) == 0 || !cursor.Expect(':')
                || cursor.ReadUpTo(2, out minute) == 0 || !cursor.Expect(':')
                || cursor.ReadUpTo(2, out second) == 0 || !cursor.Expect(' ')
                || cursor.ReadUpTo(4, out year) != 4)
            {
                return RtcErrorBadParse;
            }
        }

        return FinishParse(year, month, day, hour, minute, second, 0, offsetMinutes, out utcTick);
    }

    // Zone after the seconds: nothing (UTC), "+hhmm"/"-hhmm", a known name, or a military letter.
    private static bool TryParseZone(TextCursor cursor, out int offsetMinutes)
    {
        offsetMinutes = 0;
        if (cursor.AtEnd)
        {
            return true;
        }

        if (cursor.Current == '+' || cursor.Current == '-')
        {
            var sign = cursor.Current == '-' ? -1 : 1;
            cursor.Advance();
            if (!cursor.TryReadFixed(2, out var hours) || !cursor.TryReadFixed(2, out var minutes))
            {
                return false;
            }

            offsetMinutes = sign * (hours * 60 + minutes);
            return true;
        }

        // Longest names first so that a short name never shadows one it prefixes.
        foreach (var zone in ZoneNamesLongestFirst)
        {
            if (cursor.TryMatch(zone.Name, zone.Name.Length))
            {
                offsetMinutes = zone.Minutes;
                return true;
            }
        }

        var letter = char.ToUpperInvariant(cursor.Current);
        if (letter == 'Z')
        {
            return true;
        }

        // RFC 822 military zones: A-I = +1..+9 hours, K-M = +10..+12, N-Y = -1..-12; J is unused.
        if (letter >= 'A' && letter <= 'I')
        {
            offsetMinutes = (letter - 'A' + 1) * 60;
            return true;
        }

        if (letter >= 'K' && letter <= 'M')
        {
            offsetMinutes = (letter - 'K' + 10) * 60;
            return true;
        }

        if (letter >= 'N' && letter <= 'Y')
        {
            offsetMinutes = -(letter - 'N' + 1) * 60;
            return true;
        }

        return false;
    }

    private static int AddTickDelta(CpuContext ctx, ulong microsecondsPerUnit)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var sourceAddress = ctx[CpuRegister.Rsi];
        var delta = unchecked((long)ctx[CpuRegister.Rdx]);
        if (destinationAddress == 0 || sourceAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        if (!ctx.TryReadUInt64(sourceAddress, out var sourceTick))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        try
        {
            var resultTick = checked((long)sourceTick + checked(delta * (long)microsecondsPerUnit));
            if (resultTick < 0)
            {
                return unchecked((int)0x80B50003);
            }

            if (!ctx.TryWriteUInt64(destinationAddress, unchecked((ulong)resultTick)))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }
        catch (OverflowException)
        {
            return unchecked((int)0x80B50003);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int AddCalendarDelta(CpuContext ctx, Func<DateTime, DateTime> transform)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var sourceAddress = ctx[CpuRegister.Rsi];
        if (destinationAddress == 0 || sourceAddress == 0)
        {
            return unchecked((int)0x80B50002);
        }

        if (!ctx.TryReadUInt64(sourceAddress, out var sourceTick))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!TryConvertTickToDateTime(sourceTick, out var sourceDateTime))
        {
            return unchecked((int)0x80B50003);
        }

        DateTime resultDateTime;
        try
        {
            resultDateTime = transform(sourceDateTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return unchecked((int)0x80B50003);
        }

        if (!TryWriteTickFromDateTime(ctx, destinationAddress, resultDateTime))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int ValidateRtcDateTime(RtcDateTime time)
    {
        if (time.Year < 1 || time.Year > 9999)
        {
            return unchecked((int)0x80B50008);
        }

        if (time.Month < 1 || time.Month > 12)
        {
            return unchecked((int)0x80B50009);
        }

        if (time.Day < 1 || time.Day > DateTime.DaysInMonth(time.Year, time.Month))
        {
            return unchecked((int)0x80B5000A);
        }

        if (time.Hour > 23)
        {
            return unchecked((int)0x80B5000B);
        }

        if (time.Minute > 59)
        {
            return unchecked((int)0x80B5000C);
        }

        if (time.Second > 59)
        {
            return unchecked((int)0x80B5000D);
        }

        if (time.Microsecond > 999_999)
        {
            return unchecked((int)0x80B5000E);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool IsValidCalendarDate(int year, int month, int day)
    {
        if (year < 1 || year > 9999 || month < 1 || month > 12)
        {
            return false;
        }

        return day >= 1 && day <= DateTime.DaysInMonth(year, month);
    }

    private static bool TryReadRtcDateTime(CpuContext ctx, ulong address, out RtcDateTime time)
    {
        Span<byte> buffer = stackalloc byte[16];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            time = default;
            return false;
        }

        time = new RtcDateTime(
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[0..2]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[2..4]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[4..6]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[6..8]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[8..10]),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer[10..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..16]));
        return true;
    }

    private static bool TryWriteRtcDateTime(CpuContext ctx, ulong address, RtcDateTime dateTime)
    {
        Span<byte> buffer = stackalloc byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[0..2], dateTime.Year);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[2..4], dateTime.Month);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[4..6], dateTime.Day);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..8], dateTime.Hour);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[8..10], dateTime.Minute);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[10..12], dateTime.Second);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..16], dateTime.Microsecond);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static RtcDateTime ToRtcDateTime(DateTime dateTime)
    {
        return new RtcDateTime(
            (ushort)dateTime.Year,
            (ushort)dateTime.Month,
            (ushort)dateTime.Day,
            (ushort)dateTime.Hour,
            (ushort)dateTime.Minute,
            (ushort)dateTime.Second,
            (uint)(dateTime.Ticks % TimeSpan.TicksPerSecond / DateTimeTicksPerMicrosecond));
    }

    private static RtcDateTime ToRtcDateTime(DateTimeOffset dateTimeOffset)
    {
        return ToRtcDateTime(dateTimeOffset.DateTime);
    }

    private static bool TryWriteTickFromDateTime(CpuContext ctx, ulong address, DateTime dateTime)
    {
        return ctx.TryWriteUInt64(address, unchecked((ulong)(dateTime.Ticks / DateTimeTicksPerMicrosecond)));
    }

    private static bool TryConvertTickToDateTime(ulong tick, out DateTime dateTime)
    {
        var maxSupportedTick = unchecked((ulong)(DateTime.MaxValue.Ticks / DateTimeTicksPerMicrosecond));
        if (tick > maxSupportedTick)
        {
            dateTime = default;
            return false;
        }

        try
        {
            dateTime = new DateTime(checked((long)(tick * (ulong)DateTimeTicksPerMicrosecond)), DateTimeKind.Utc);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            dateTime = default;
            return false;
        }
    }

    private static bool TryConvertRtcDateTimeToTick(RtcDateTime time, out ulong tick)
    {
        try
        {
            var year = time.Year;
            var month = time.Month;
            var day = time.Day;
            if (month > 2)
            {
                month -= 3;
            }
            else
            {
                month += 9;
                year -= 1;
            }

            var century = year / 100;
            var yearOfCentury = year - (100 * century);

            ulong days = ((146097UL * (ulong)century) >> 2)
                + ((1461UL * (ulong)yearOfCentury) >> 2)
                + ((153UL * (ulong)month + 2UL) / 5UL)
                + day;
            days -= 307UL;
            days *= MicrosecondsPerDay;

            var timeOfDay = (ulong)time.Hour * MicrosecondsPerHour
                + (ulong)time.Minute * MicrosecondsPerMinute
                + (ulong)time.Second * MicrosecondsPerSecond
                + time.Microsecond;

            tick = days + timeOfDay;
            return true;
        }
        catch (OverflowException)
        {
            tick = 0;
            return false;
        }
    }

    private static bool TryConvertTickToRtcDateTime(ulong tick, out RtcDateTime time)
    {
        try
        {
            var dateTime = new DateTime(checked((long)(tick * DateTimeTicksPerMicrosecond)), DateTimeKind.Utc);
            time = ToRtcDateTime(dateTime);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            time = default;
            return false;
        }
    }

    private readonly record struct RtcDateTime(
        ushort Year,
        ushort Month,
        ushort Day,
        ushort Hour,
        ushort Minute,
        ushort Second,
        uint Microsecond);
}
