// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class FrameTimeLogTests
{
    [Fact]
    public void WritesAHeaderThenOneNumberedMicrosecondLinePerFlip()
    {
        var writer = new StringWriter();
        using var log = new FrameTimeLog(writer, originTimestamp: 1_000, unixOriginMicroseconds: 1_700_000_000_000_000, frequency: 1_000_000, TimeSpan.Zero);

        log.Record(1_000);
        log.Record(17_667);
        log.Record(34_334);
        log.Flush();

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("# sharpemu-frame-times v1 clock=monotonic t0_unix_us=1700000000000000", lines[0]);
        Assert.Equal(["1 0", "2 16667", "3 33334"], lines[1..]);
    }

    [Fact]
    public void ConvertsTicksOfAnyFrequencyToMicroseconds()
    {
        var writer = new StringWriter();
        using var log = new FrameTimeLog(writer, originTimestamp: 0, unixOriginMicroseconds: 0, frequency: 24_000_000, TimeSpan.Zero);

        log.Record(24_000_000);

        Assert.EndsWith("1 1000000\n", writer.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public void RecordsNothingAfterDisposeAndDisposeIsRepeatable()
    {
        var writer = new StringWriter();
        var log = new FrameTimeLog(writer, 0, 0, 1_000_000, TimeSpan.Zero);
        log.Record(1);
        log.Dispose();
        log.Dispose();

        log.Record(2);

        Assert.Equal(2, writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void IsOffUnlessTheEnvironmentVariableNamesAFile()
    {
        var previous = Environment.GetEnvironmentVariable(FrameTimeLog.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(FrameTimeLog.EnvironmentVariable, null);
            Assert.Null(FrameTimeLog.FromEnvironment());

            var path = Path.Combine(Path.GetTempPath(), $"frame-times-{Guid.NewGuid():N}.txt");
            Environment.SetEnvironmentVariable(FrameTimeLog.EnvironmentVariable, path);
            using (var log = FrameTimeLog.FromEnvironment())
            {
                Assert.NotNull(log);
                log!.Record();
            }

            var lines = File.ReadAllLines(path);
            File.Delete(path);
            Assert.StartsWith("# sharpemu-frame-times v1", lines[0]);
            Assert.StartsWith("1 ", lines[1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FrameTimeLog.EnvironmentVariable, previous);
        }
    }
}
