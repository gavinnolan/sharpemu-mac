// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Rtc;
using Xunit;

namespace SharpEmu.Libs.Tests.Rtc;

public sealed class RtcFormatParseTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong TickAddress = Base + 0x100;
    private const ulong TextAddress = Base + 0x200;
    private const ulong OutAddress = Base + 0x400;

    private const int InvalidPointer = unchecked((int)0x80B50002);
    private const int InvalidValue = unchecked((int)0x80B50003);
    private const int BadParse = unchecked((int)0x80B50007);

    private readonly FakeCpuMemory _memory = new(Base, 0x10000);
    private readonly CpuContext _ctx;

    public RtcFormatParseTests() => _ctx = new CpuContext(_memory, Generation.Gen5);

    private static ulong TickOf(int y, int mo, int d, int h = 0, int mi = 0, int s = 0, int us = 0) =>
        (ulong)(new DateTime(y, mo, d, h, mi, s).Ticks / 10) + (ulong)us;

    private void WriteTick(ulong tick) => Assert.True(_ctx.TryWriteUInt64(TickAddress, tick));

    private void WriteText(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text + "\0");
        Assert.True(_memory.TryWrite(TextAddress, bytes));
    }

    private string ReadOut()
    {
        var bytes = new byte[96];
        Assert.True(_memory.TryRead(OutAddress, bytes));
        return Encoding.ASCII.GetString(bytes, 0, Array.IndexOf(bytes, (byte)0));
    }

    private int Format(Func<CpuContext, int> export, ulong tick, int zoneMinutes)
    {
        WriteTick(tick);
        _ctx[CpuRegister.Rdi] = OutAddress;
        _ctx[CpuRegister.Rsi] = TickAddress;
        _ctx[CpuRegister.Rdx] = unchecked((ulong)zoneMinutes);
        return export(_ctx);
    }

    private int Parse(Func<CpuContext, int> export, string text, out ulong tick)
    {
        WriteText(text);
        _ctx[CpuRegister.Rdi] = TickAddress;
        _ctx[CpuRegister.Rsi] = TextAddress;
        var result = export(_ctx);
        _ctx.TryReadUInt64(TickAddress, out tick);
        return result;
    }

    [Fact]
    public void FormatRfc3339_UtcUsesZ_AndCentiseconds()
    {
        Assert.Equal(0, Format(RtcExports.RtcFormatRfc3339, TickOf(2024, 2, 29, 13, 5, 9, 678_901), 0));
        Assert.Equal("2024-02-29T13:05:09.67Z", ReadOut());
    }

    [Theory]
    [InlineData(90, "2024-02-29T14:35:09.00+01:30")]
    [InlineData(-330, "2024-02-29T07:35:09.00-05:30")]
    public void FormatRfc3339_AppliesOffset(int minutes, string expected)
    {
        Assert.Equal(0, Format(RtcExports.RtcFormatRfc3339, TickOf(2024, 2, 29, 13, 5, 9), minutes));
        Assert.Equal(expected, ReadOut());
    }

    [Fact]
    public void FormatRfc2822_ProducesWeekdayAndNumericZone()
    {
        // 2024-02-29 is a Thursday.
        Assert.Equal(0, Format(RtcExports.RtcFormatRfc2822, TickOf(2024, 2, 29, 13, 5, 9), 120));
        Assert.Equal("Thu, 29 Feb 2024 15:05:09 +0200", ReadOut());
        Assert.Equal(0, Format(RtcExports.RtcFormatRfc2822, TickOf(1, 1, 1), 0));
        Assert.Equal("Mon, 01 Jan 0001 00:00:00 +0000", ReadOut());
    }

    [Fact]
    public void Format_ValidatesPointersAndZoneRange()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = TickAddress;
        Assert.Equal(InvalidPointer, RtcExports.RtcFormatRfc3339(_ctx));
        _ctx[CpuRegister.Rdi] = OutAddress;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(InvalidPointer, RtcExports.RtcFormatRfc2822(_ctx));
        Assert.Equal(InvalidValue, Format(RtcExports.RtcFormatRfc3339, TickOf(2024, 1, 1), 1440));
        Assert.Equal(InvalidValue, Format(RtcExports.RtcFormatRfc2822, TickOf(2024, 1, 1), -1440));
        // The zone shift cannot leave the representable range.
        Assert.Equal(InvalidValue, Format(RtcExports.RtcFormatRfc3339, 0, -60));
        Assert.Equal(InvalidValue, Format(RtcExports.RtcFormatRfc3339, ulong.MaxValue, 0));
    }

    [Fact]
    public void FormatLocalTime_UsesHostOffsetAtThatInstant()
    {
        var utc = new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var minutes = (int)TimeZoneInfo.Local.GetUtcOffset(utc).TotalMinutes;
        WriteTick((ulong)(utc.Ticks / 10));
        _ctx[CpuRegister.Rdi] = OutAddress;
        _ctx[CpuRegister.Rsi] = TickAddress;
        Assert.Equal(0, RtcExports.RtcFormatRfc3339LocalTime(_ctx));
        var local = ReadOut();

        Assert.Equal(0, Format(RtcExports.RtcFormatRfc3339, (ulong)(utc.Ticks / 10), minutes));
        Assert.Equal(ReadOut(), local);

        _ctx[CpuRegister.Rdi] = OutAddress;
        _ctx[CpuRegister.Rsi] = TickAddress;
        Assert.Equal(0, RtcExports.RtcFormatRfc2822LocalTime(_ctx));
        var local2822 = ReadOut();
        Assert.Equal(0, Format(RtcExports.RtcFormatRfc2822, (ulong)(utc.Ticks / 10), minutes));
        Assert.Equal(ReadOut(), local2822);
    }

    [Theory]
    [InlineData("2024-02-29T13:05:09Z")]
    [InlineData("2024-02-29t13:05:09z")]
    [InlineData("2024-02-29T14:35:09+01:30")]
    [InlineData("2024-02-29T07:35:09-05:30")]
    public void ParseRfc3339_ResolvesOffsetsToUtc(string text)
    {
        Assert.Equal(0, Parse(RtcExports.RtcParseRfc3339, text, out var tick));
        Assert.Equal(TickOf(2024, 2, 29, 13, 5, 9), tick);
    }

    [Fact]
    public void ParseRfc3339_KeepsMicrosecondsAndDropsExcessDigits()
    {
        Assert.Equal(0, Parse(RtcExports.RtcParseRfc3339, "2024-02-29T13:05:09.5Z", out var tick));
        Assert.Equal(TickOf(2024, 2, 29, 13, 5, 9, 500_000), tick);
        Assert.Equal(0, Parse(RtcExports.RtcParseRfc3339, "2024-02-29T13:05:09.1234567Z", out tick));
        Assert.Equal(TickOf(2024, 2, 29, 13, 5, 9, 123_456), tick);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2024-02-29")]
    [InlineData("2024-02-29 13:05:09Z")]
    [InlineData("2024-02-29T13:05:09")]
    [InlineData("2024-02-29T13:05:09+0130")]
    [InlineData("24-02-29T13:05:09Z")]
    public void ParseRfc3339_RejectsMalformedText_WithoutWriting(string text)
    {
        WriteTick(0xABCD);
        Assert.Equal(BadParse, Parse(RtcExports.RtcParseRfc3339, text, out var tick));
        Assert.Equal(0xABCDUL, tick);
    }

    [Theory]
    [InlineData("0000-01-01T00:00:00Z", unchecked((int)0x80B50008))]
    [InlineData("2024-13-01T00:00:00Z", unchecked((int)0x80B50009))]
    [InlineData("2023-02-29T00:00:00Z", unchecked((int)0x80B5000A))]
    [InlineData("2024-01-01T24:00:00Z", unchecked((int)0x80B5000B))]
    [InlineData("2024-01-01T00:60:00Z", unchecked((int)0x80B5000C))]
    [InlineData("2024-01-01T00:00:61Z", unchecked((int)0x80B5000D))]
    public void ParseRfc3339_ReportsFieldSpecificErrors(string text, int expected)
    {
        Assert.Equal(expected, Parse(RtcExports.RtcParseRfc3339, text, out _));
    }

    [Fact]
    public void ParseRfc3339_LeapSecondAndRangeEdges()
    {
        Assert.Equal(0, Parse(RtcExports.RtcParseRfc3339, "2016-12-31T23:59:60Z", out var tick));
        Assert.Equal(TickOf(2017, 1, 1), tick);
        // An offset that pushes the instant before year 1 is out of range.
        Assert.Equal(InvalidValue, Parse(RtcExports.RtcParseRfc3339, "0001-01-01T00:00:00+01:00", out _));
    }

    [Fact]
    public void Parse_ValidatesPointers()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = TextAddress;
        Assert.Equal(InvalidPointer, RtcExports.RtcParseRfc3339(_ctx));
        _ctx[CpuRegister.Rdi] = TickAddress;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(InvalidPointer, RtcExports.RtcParseDateTime(_ctx));
    }

    [Theory]
    [InlineData("2024-02-29T13:05:09Z")]
    [InlineData("  2024-02-29T14:35:09+01:30")]
    [InlineData("Thu, 29 Feb 2024 13:05:09 +0000")]
    [InlineData("Thu, 29 Feb 2024 15:05:09 +0200")]
    [InlineData("thursday, 29 february 2024 13:05:09 GMT")]
    [InlineData("Thu, 29 Feb 2024 08:05:09 EST")]
    [InlineData("Thu, 29 Feb 2024 14:05:09 +0100")]
    [InlineData("Thu, 29 Feb 24 13:05:09")]
    [InlineData("Thu Feb 29 13:05:09 2024")]
    public void ParseDateTime_AcceptsRfc3339Rfc2822AndAsctime(string text)
    {
        Assert.Equal(0, Parse(RtcExports.RtcParseDateTime, text, out var tick));
        Assert.Equal(TickOf(2024, 2, 29, 13, 5, 9), tick);
    }

    [Fact]
    public void ParseDateTime_AsctimeAcceptsPaddedDay()
    {
        Assert.Equal(0, Parse(RtcExports.RtcParseDateTime, "Fri Mar  1 00:00:00 2024", out var tick));
        Assert.Equal(TickOf(2024, 3, 1), tick);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("Thu, 29 Foo 2024 13:05:09 +0000")]
    [InlineData("Thu, 29 Feb 2024 13:05:09 +00")]
    [InlineData("Thu, 29 Feb 2024")]
    public void ParseDateTime_RejectsMalformedText(string text)
    {
        Assert.Equal(BadParse, Parse(RtcExports.RtcParseDateTime, text, out _));
    }

    [Fact]
    public void FormatThenParse_RoundTripsAtWholeCentiseconds()
    {
        var original = TickOf(2031, 11, 5, 23, 59, 58, 250_000);
        foreach (var zone in new[] { 0, 60, -300, 570, -1439, 1439 })
        {
            Assert.Equal(0, Format(RtcExports.RtcFormatRfc3339, original, zone));
            WriteText(ReadOut());
            _ctx[CpuRegister.Rdi] = TickAddress;
            _ctx[CpuRegister.Rsi] = TextAddress;
            Assert.Equal(0, RtcExports.RtcParseRfc3339(_ctx));
            _ctx.TryReadUInt64(TickAddress, out var parsed);
            Assert.Equal(original, parsed);
        }

        // RFC 2822 carries no fraction, so the round trip is exact to the second.
        var wholeSecond = TickOf(2031, 11, 5, 23, 59, 58);
        foreach (var zone in new[] { 0, 60, -300, 570 })
        {
            Assert.Equal(0, Format(RtcExports.RtcFormatRfc2822, wholeSecond, zone));
            Assert.Equal(0, Parse(RtcExports.RtcParseDateTime, ReadOut(), out var parsed));
            Assert.Equal(wholeSecond, parsed);
        }
    }
}
