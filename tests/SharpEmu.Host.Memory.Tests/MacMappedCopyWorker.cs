// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
#if SHARPEMU_HOST_MACOS
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Host.Memory.Tests;

// Source-prepared, separately granted fresh ordinary workers. No GPU, guest
// code, signals, tracker arming, permission probes or content certification.
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacMappedCopyWorker
{
    internal static int Run(string scenario)
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_MAPPED_COPY_NATIVE") != "1") return 80;
        if (!OperatingSystem.IsMacOS()) return 81;
        try
        {
            var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
            Assert.False(string.IsNullOrEmpty(expected)); Assert.Equal(expected, RuntimeInformation.ProcessArchitecture.ToString());
            var host = SharpEmu.Host.MacOS.MacOSHost.CreateViewMemory();
            Assert.IsAssignableFrom<IHostMappedCopyMemory>(host);
            var state = host.GetType().Assembly.GetType("SharpEmu.Host.MacOS.MacMappedCopyState")!;
            var ledger = (HostMappedCopyLedger)state.GetProperty("Ledger", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Assert.NotNull(ledger); var page = ledger.NativePageSize;
            Assert.True(page is 4096 or 16384); Assert.True(page % host.PageSize == 0);
            switch (scenario)
            {
                case "shared-copy": Execute(host, ledger, page, 0); break;
                case "map-aba": Execute(host, ledger, page, 1); break;
                case "alias-exclusion": Execute(host, ledger, page, 2); break;
                case "raced-copy": Execute(host, ledger, page, 3); break;
                case "derived-rw": ExecuteProtection(host, ledger, page, 0); break;
                case "guest-readonly": ExecuteProtection(host, ledger, page, 1); break;
                case "partial-derived": ExecuteProtection(host, ledger, page, 2); break;
                case "maximum-failure": ExecuteProtection(host, ledger, page, 3); break;
                default: return 82;
            }
            Console.WriteLine($"mapped-copy-native-pass case={scenario} architecture={RuntimeInformation.ProcessArchitecture} framework={RuntimeInformation.FrameworkDescription}; kernel_page={page}; provider_page={host.PageSize}; mapping-lifetime-only");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 83; }
    }

    private static void Execute(IHostViewMemory host, HostMappedCopyLedger ledger, ulong page, int scenario)
    {
        var views = new SharedBackingViews(host, page * 2);
        Hole? first = null, second = null; Exception? primary = null;
        var hookField = typeof(HostMappedCopyLedger).GetField("afterCaptureForControl", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalHook = hookField.GetValue(ledger);
        try
        {
            Assert.True(views.IsAvailable);
            first = new(host, page); second = new(host, page);
            Assert.True(views.TryMapReservedRange(first.Address, page, 0, HostPageProtection.ReadWrite, out _));
            Assert.True(views.TryMapReservedRange(second.Address, page, page, HostPageProtection.ReadWrite, out _));
            var seed = Enumerable.Range(0, 256).Select(i => (byte)(i * 19 + 7)).ToArray(); var bytes = new byte[seed.Length];
            Assert.True(views.TryWriteBacking(first.Address, seed));
            Assert.Equal(CpuMappedCopyResult.Copied, views.TryReadMappedCopy(first.Address, page, first.Address, bytes, out var old));
            Assert.Equal(seed, bytes); Assert.NotNull(old);
            Assert.Equal(page, old!.Segments[0].ProviderPageSize); Assert.Equal(0ul, old.Segments[0].MappingBackingOffset);
            Assert.True(views.IsMappedCopyObservationCurrent(old));
            if (scenario == 0)
            {
                Assert.True(views.TryCopyBacking(second.Address, first.Address, (ulong)seed.Length));
                Assert.Equal(CpuMappedCopyResult.Copied, views.TryReadMappedCopy(second.Address, page, second.Address, bytes, out var other));
                Assert.Equal(seed, bytes); Assert.Equal(page, other!.Segments[0].MappingBackingOffset);
                Assert.Equal(old.Segments[0].BackingIdentity, other.Segments[0].BackingIdentity);
                Assert.True(views.TryWriteBacking(first.Address, new byte[seed.Length]));
                Assert.Equal(seed, bytes); Assert.True(views.IsMappedCopyObservationCurrent(old)); // no content claim
            }
            else if (scenario == 1)
            {
                Assert.True(views.Unmap(first.Address, page, out _));
                Assert.False(views.IsMappedCopyObservationCurrent(old));
                Assert.True(views.TryMapReservedRange(first.Address, page, 0, HostPageProtection.ReadWrite, out _));
                Assert.Equal(CpuMappedCopyResult.Copied, views.TryReadMappedCopy(first.Address, page, first.Address, bytes, out var current));
                Assert.Equal(seed, bytes); Assert.NotEqual(old.Segments[0].MappingIncarnation, current!.Segments[0].MappingIncarnation);
                Assert.False(views.IsMappedCopyObservationCurrent(old));
                Assert.True(views.IsMappedCopyObservationCurrent(current));
            }
            else if (scenario == 2)
            {
                var raw = SharpEmu.Host.MacOS.MacOSHost.CreateMemory(); var alias = views.AliasBase;
                Assert.Equal(0ul, raw.AllocateRaw(alias, page, HostMemory.MEM_COMMIT, HostMemory.PAGE_READWRITE));
                Assert.False(raw.ProtectRaw(alias + 1, 1, HostMemory.PAGE_READONLY, out _));
                Assert.False(raw.FreeRaw(alias, 0, HostMemory.MEM_RELEASE));
                Assert.Equal((nuint)0, raw.QueryRaw(alias, out _));
                Assert.False(host.UnmapView(alias, page)); Assert.False(host.FreeOwnedRange(alias, page));
                Assert.True(views.IsMappedCopyObservationCurrent(old));
                Assert.Equal(CpuMappedCopyResult.Copied, views.TryReadMappedCopy(first.Address, page, first.Address, bytes, out _));
                Assert.Equal(seed, bytes);
            }
            else
            {
                hookField.SetValue(ledger, (Action)(() =>
                {
                    // The actual protection effect runs after resolution, outside
                    // the copy gate, while its canonical alias pin remains held.
                    Assert.True(host.ChangeAccess(first.Address, page, HostPageProtection.ReadOnly));
                }));
                Assert.Equal(CpuMappedCopyResult.Rejected, views.TryReadMappedCopy(first.Address, page, first.Address, bytes, out var rejected));
                Assert.Null(rejected); Assert.False(views.IsMappedCopyObservationCurrent(old));
            }
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            hookField.SetValue(ledger, originalHook);
            Exception? cleanup = null;
            try { views.Dispose(); if (!views.CleanupComplete) throw new IOException("Actual view cleanup incomplete."); }
            catch (Exception error) { cleanup = error; }
            // A failed view cleanup may still own native uses of both reservations.
            // Never release/reclaim such extents; process exit is the terminal owner.
            if (cleanup is not null) { first?.Quarantine(); second?.Quarantine(); }
            foreach (var hole in new[] { second, first })
                try { hole?.Dispose(); } catch (Exception error) { cleanup ??= error; }
            if (cleanup is not null)
            {
                if (primary is null) throw cleanup;
                try { primary.Data["MappedCopyWorkerCleanup"] = cleanup; } catch { }
            }
        }
    }

    private sealed class Hole : IDisposable
    {
        private readonly ulong extent;
        private readonly IHostViewMemory host;
        private readonly ulong allocated;
        private bool consumed;
        internal ulong Address { get; }
        internal Hole(IHostViewMemory host, ulong page, ulong window = 0)
        {
            this.host = host; window = window == 0 ? page : window;
            extent = window <= page ? 32768UL : 131072UL;
            if (window > 65536 || page > 16384) throw new ArgumentOutOfRangeException(nameof(window));
            ulong address = 0;
            var result = mach_vm_allocate(mach_task_self(), ref address, extent, 1);
            if (result != 0) throw new IOException($"Fixture Mach allocation declined: {result}.");
            allocated = address; var attempted = false;
            try
            {
                Assert.NotEqual(0ul, allocated); Address = checked((allocated + page - 1) & ~(page - 1));
                Assert.True(Address >= allocated && Address - allocated <= extent - window);
                // Never relinquish ownership before MAP_FIXED. Convert the WHOLE
                // successful Mach extent into the provider's owned placeholder.
                attempted = true;
                if (!host.UnmapView(allocated, extent)) throw new IOException("Owned placeholder conversion uncertain.");
                Console.WriteLine($"mapped-copy-fixture-owned extent={extent}; page={page}; native_allocation_return={result}");
            }
            catch (Exception error)
            {
                if (attempted) consumed = true; // unknown conversion: retain full extent, never retry
                else
                {
                    consumed = true; var release = mach_vm_deallocate(mach_task_self(), allocated, extent);
                    if (release != 0) try { error.Data["MachSetupCleanup"] = release; } catch { }
                }
                throw;
            }
        }
        internal void Quarantine() => consumed = true;
        public void Dispose()
        {
            if (consumed) return;
            consumed = true; // consume before uncertain native/kernel+bookkeeping outcome
            if (!host.FreeHole(allocated, extent)) throw new IOException("Owned full Mach extent cleanup uncertain; never retry.");
        }
    }
    [DllImport("libSystem.dylib")] private static extern uint mach_task_self();
    [DllImport("libSystem.dylib")] private static extern int mach_vm_allocate(uint task, ref ulong address, ulong size, int flags);
    [DllImport("libSystem.dylib")] private static extern int mach_vm_deallocate(uint task, ulong address, ulong size);
}

[SupportedOSPlatform("macos")]
public sealed class MacMappedCopyNativeTests(ITestOutputHelper output)
{
    [MappedCopyNativeTheory]
    [InlineData("shared-copy")]
    [InlineData("map-aba")]
    [InlineData("alias-exclusion")]
    [InlineData("raced-copy")]
    public async Task ActualMacMappingCopyWorker(string scenario)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_MAPPED_COPY_NATIVE"));
        var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), expected);
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var start = new ProcessStartInfo(Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, "dotnet"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(MacMappedCopyNativeTests).Assembly.Location);
        start.ArgumentList.Add("--mac-mapped-copy=" + scenario);
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var captured = await stdout.WaitAsync(TimeSpan.FromSeconds(2)); var errors = await stderr.WaitAsync(TimeSpan.FromSeconds(2));
            output.WriteLine(captured); if (errors.Length != 0) output.WriteLine(errors);
            Assert.Equal(0, child.ExitCode); Assert.Empty(errors);
            Assert.Contains("mapped-copy-native-pass case=" + scenario, captured);
            Assert.Contains("architecture=" + expected, captured); Assert.Contains("mapping-lifetime-only", captured);
        }
        finally
        {
            if (!child.HasExited)
            {
                _ = kill(child.Id, 15);
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            }
        }
    }
    [DllImport("libSystem.dylib")] private static extern int kill(int pid, int signal);
}
internal sealed class MappedCopyNativeTheoryAttribute : TheoryAttribute
{
    public MappedCopyNativeTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_MAPPED_COPY_NATIVE") != "1")
            Skip = "Actual Mac mapping copy requires separately authorized SHARPEMU_TEST_REQUIRE_MAPPED_COPY_NATIVE=1.";
    }
}
#endif
