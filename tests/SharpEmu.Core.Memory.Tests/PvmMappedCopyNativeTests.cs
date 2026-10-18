// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SharpEmu.Core.Memory.Tests;

public sealed class PvmMappedCopyNativeTests(ITestOutputHelper output)
{
    [PvmMappedCopyNativeTheory]
    [InlineData("full-copy")]
    [InlineData("unrelated-canonical")]
    [InlineData("aba-close")]
    [InlineData("unsupported-wrapper")]
    [InlineData("single-view-write-unlocked")]
    [InlineData("single-view-write-publication-busy")]
    [InlineData("single-view-miss-keeps-read-watch")]
    public async Task ActualPvmAndTrackedForwarding(string scenario)
    {
        Assert.True(OperatingSystem.IsMacOS());
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_PVM_MAPPED_COPY_NATIVE"));
        var architecture = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), architecture);
        Assert.Equal(RuntimeInformation.FrameworkDescription, Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_FRAMEWORK"));
        Assert.True(ulong.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_KERNEL_PAGE"), out var kernelPage));
        Assert.True(kernelPage is 4096 or 16384);
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var start = new ProcessStartInfo(Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, "dotnet"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(PvmMappedCopyNativeTests).Assembly.Location);
        start.ArgumentList.Add("--pvm-mapped-copy=" + scenario);
        using var child = Process.Start(start) ?? throw new IOException("Fresh PVM worker failed to start.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        Exception? primary = null;
        string captured = "", errors = "";
        try
        {
            // Includes authority warm-up, real constructor self-test/search,
            // every operation and cleanup. Parent's owned-group bound is 60s.
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            Exception? cleanup = null;
            try
            {
                if (!child.HasExited)
                {
                    // This dedicated ordinary worker spawns no descendants.
                    // Do not signal any other emulator or foreign process.
                    if (kill(child.Id, 15) != 0 && !child.HasExited)
                        output.WriteLine($"owned-worker TERM declined errno={Marshal.GetLastPInvokeError()}");
                    try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (TimeoutException)
                    {
                        if (!child.HasExited) child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
            }
            catch (Exception error) { cleanup = error; }
            try
            {
                captured = await stdout.WaitAsync(TimeSpan.FromSeconds(10));
                errors = await stderr.WaitAsync(TimeSpan.FromSeconds(10));
                output.WriteLine(captured);
                if (errors.Length != 0) output.WriteLine(errors);
                output.WriteLine($"owned-worker pid={child.Id} exited={child.HasExited}" + (child.HasExited ? $" exit={child.ExitCode}" : ""));
            }
            catch (Exception error) { cleanup ??= error; }
            if (cleanup is not null)
            {
                if (primary is null) throw cleanup;
                try { primary.Data["PvmWorkerTermination"] = cleanup; } catch { }
                output.WriteLine($"secondary worker termination/read error: {cleanup}");
            }
        }
        Assert.Equal(0, child.ExitCode);
        Assert.Empty(errors);
        Assert.Contains($"stage=real-constructor-self-test-return case={scenario}", captured);
        Assert.Contains($"stage=owned-cleanup-return case={scenario}", captured);
        Assert.Contains($"stage=pass case={scenario}", captured);
        Assert.Contains($"architecture={architecture}", captured);
        Assert.Contains($"kernel_page={kernelPage}", captured);
    }

    [DllImport("/usr/lib/libSystem.B.dylib", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}

internal sealed class PvmMappedCopyNativeTheoryAttribute : TheoryAttribute
{
    public PvmMappedCopyNativeTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Actual PVM forwarding worker requires macOS; no native result on this host.";
        else if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_PVM_MAPPED_COPY_NATIVE") != "1")
            Skip = "Actual PVM forwarding is opt-in and requires a separately authorized native slot.";
    }
}
