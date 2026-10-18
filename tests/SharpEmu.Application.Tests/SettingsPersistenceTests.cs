// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using SharpEmu.Logging;
using Xunit;
namespace SharpEmu.Application.Tests;

public sealed class SettingsPersistenceTests(Xunit.Abstractions.ITestOutputHelper testOutput)
{
    [Theory]
    [InlineData("global")]
    [InlineData("game")]
    [InlineData("environment")]
    public async Task ExplicitSaveReportsActualFilesystemFailureAndRetryPreservesSchema(string kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sharpemu-persistence-{Guid.NewGuid():N}");
        var worker = Path.Combine(AppContext.BaseDirectory, "SharpEmu.Application.Persistence.Worker.dll");
        Assert.True(File.Exists(worker), "Isolated persistence worker must be built with this project.");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(worker); start.ArgumentList.Add(kind); start.ArgumentList.Add(directory);
        start.Environment[SharpEmuPaths.DataRootVariable] = directory;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var stdout = await output; var stderr = await error;
            testOutput.WriteLine(stdout);
            Assert.True(process.ExitCode == 0, $"Worker exit {process.ExitCode}: {stderr}");
            Assert.Contains("EXPECTED_FILESYSTEM_FAILURE:", stdout);
            Assert.Contains("PRIOR_VALID_FILE_PRESERVED", stdout);
            if (!OperatingSystem.IsWindows()) Assert.Contains("REPLACEMENT_AND_CLEANUP_FAILURE_PRESERVED:", stdout);
            if (!OperatingSystem.IsWindows() && kind == "game") Assert.Contains("EMPTY_OVERRIDE_REMOVAL_DENIAL_PRESERVED", stdout);
            Assert.Contains($"ACTUAL_PERSISTENCE_FAILURE_AND_RETRY:{kind}", stdout);
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
