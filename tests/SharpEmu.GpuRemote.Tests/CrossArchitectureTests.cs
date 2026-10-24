// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.GpuRemote.Tests;

/// <summary>
/// The split as it will really run: an x86-64 process under Rosetta (the osx-x64 smoke tool,
/// standing in for SharpEmu.CLI) starting the native arm64 GPU host.
/// </summary>
public sealed class CrossArchitectureTests(ITestOutputHelper output)
{
    [CrossArchitectureFact]
    public void RosettaProcessStartsTheNativeHostAndTalksToIt()
    {
        var lines = RunSmoke("bench 10000", TimeSpan.FromMinutes(3));
        foreach (var (key, value) in lines)
        {
            output.WriteLine($"{key}={value}");
        }

        Assert.Equal("ok", lines.GetValueOrDefault("result"));
        Assert.Equal("X64", lines["smoke.arch"]);
        Assert.Equal("1", lines["smoke.translated"]);
        Assert.Equal("Arm64", lines["host.arch"]);
        Assert.Equal("0", lines["host.translated"]);
        Assert.Equal(GpuRemoteProtocol.Version.ToString(CultureInfo.InvariantCulture), lines["host.protocol"]);
        Assert.Equal("0", lines["host.exitcode"]);

        // Calls made from a signal handler on the translated side, as the fault path will.
        Assert.Equal("all", lines["signal.sync.spin.completed"]);
        Assert.Equal("all", lines["signal.sync.nospin.completed"]);
    }

    [CrossArchitectureFact]
    public void HostExitsWhenItsRosettaParentIsKilled()
    {
        using var smoke = Process.Start(new ProcessStartInfo(SmokeTool.Path, "orphan")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        try
        {
            var lines = new Dictionary<string, string>();
            var deadline = Stopwatch.StartNew();
            while (!lines.ContainsKey("orphan.ready") && deadline.Elapsed < TimeSpan.FromSeconds(60))
            {
                var line = smoke.StandardOutput.ReadLine();
                if (line is null)
                {
                    break;
                }

                AddLine(lines, line);
            }

            Assert.True(lines.ContainsKey("orphan.ready"), $"the smoke tool never got the host up: {string.Join(", ", lines)}");
            var hostPid = int.Parse(lines["host.pid"], CultureInfo.InvariantCulture);
            Assert.Equal(0, RemoteNative.kill(hostPid, 0));

            smoke.Kill();
            Assert.True(smoke.WaitForExit(10_000));

            var watch = Stopwatch.StartNew();
            while (RemoteNative.kill(hostPid, 0) == 0 && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(20);
            }

            output.WriteLine($"host {hostPid} gone {watch.Elapsed.TotalMilliseconds:F0} ms after its parent was killed");
            Assert.NotEqual(0, RemoteNative.kill(hostPid, 0));
        }
        finally
        {
            if (!smoke.HasExited)
            {
                smoke.Kill();
            }
        }
    }

    private static Dictionary<string, string> RunSmoke(string arguments, TimeSpan timeout)
    {
        using var smoke = Process.Start(new ProcessStartInfo(SmokeTool.Path, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stderr = smoke.StandardError.ReadToEndAsync();
        var stdout = smoke.StandardOutput.ReadToEndAsync();
        if (!smoke.WaitForExit(timeout))
        {
            smoke.Kill(entireProcessTree: true);
            throw new TimeoutException($"The smoke tool did not finish within {timeout}.");
        }

        var lines = new Dictionary<string, string>();
        foreach (var line in stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            AddLine(lines, line);
        }

        lines["stderr"] = stderr.Result.ReplaceLineEndings(" | ");
        lines["exit"] = smoke.ExitCode.ToString(CultureInfo.InvariantCulture);
        return lines;
    }

    private static void AddLine(Dictionary<string, string> lines, string line)
    {
        var separator = line.IndexOf('=');
        if (separator > 0)
        {
            lines[line[..separator]] = line[(separator + 1)..];
        }
    }
}
