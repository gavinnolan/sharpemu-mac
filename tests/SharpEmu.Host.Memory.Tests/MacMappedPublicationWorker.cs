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
internal static unsafe class MacMappedPublicationWorker
{
    internal static int Run(string scenario)
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_MAPPED_PUBLICATION_NATIVE") != "1") return 80;
        if (!OperatingSystem.IsMacOS()) return 81;
        try
        {
            var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
            Assert.False(string.IsNullOrEmpty(expected)); Assert.Equal(expected, RuntimeInformation.ProcessArchitecture.ToString());
            var host = SharpEmu.Host.MacOS.MacOSHost.CreateViewMemory();
            Assert.IsAssignableFrom<IHostMappedCopyMemory>(host);
            Assert.IsAssignableFrom<IHostMappedPublicationMemory>(host);
            var state = host.GetType().Assembly.GetType("SharpEmu.Host.MacOS.MacMappedCopyState")!;
            var ledger = (HostMappedCopyLedger)state.GetProperty("Ledger", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Assert.NotNull(ledger); var page = ledger.NativePageSize;
            Assert.True(page is 4096 or 16384); Assert.True(page % host.PageSize == 0);
            switch (scenario)
            {
                case "scatter": Execute(host, ledger, page, 0); break;
                case "continuity": Execute(host, ledger, page, 1); break;
                case "reservation": Execute(host, ledger, page, 2); break;
                case "aba": Execute(host, ledger, page, 3); break;
                default: return 82;
            }
            Console.WriteLine($"mapped-publication-native-pass case={scenario} architecture={RuntimeInformation.ProcessArchitecture} framework={RuntimeInformation.FrameworkDescription}; kernel_page={page}; provider_page={host.PageSize}; ordinary-alias-publication-only");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 83; }
    }

    private static void Execute(IHostViewMemory host, HostMappedCopyLedger ledger, ulong page, int scenario)
    {
        var views = new SharedBackingViews(host, page * 2);
        Hole? first = null, second = null; CpuMappedPublication? held = null; Exception? primary = null;
        try
        {
            Assert.True(views.IsAvailable);
            first = new(host, page); second = new(host, page);
            Assert.True(views.TryMapReservedRange(first.Address, page, 0, HostPageProtection.ReadWrite, out _));
            Assert.True(views.TryMapReservedRange(second.Address, page, 0, HostPageProtection.ReadWrite, out _));
            Assert.True(views.Clear(0, page * 2));
            var seed = Enumerable.Range(0, checked((int)page)).Select(i => (byte)(i * 19 + 7)).ToArray();
            Assert.True(views.TryWriteBacking(first.Address, seed));
            CpuMappedCopyObservation Capture()
            {
                var bytes = new byte[8];
                Assert.Equal(CpuMappedCopyResult.Copied,
                    views.TryReadMappedCopy(first.Address, page, first.Address + 31, bytes, out var result));
                Assert.Equal(seed.AsSpan(31, 8).ToArray(), bytes);
                return Assert.IsType<CpuMappedCopyObservation>(result);
            }
            CpuMappedPublication Prepare(CpuMappedCopyObservation original, byte[] bytes)
            {
                Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
                    views.TryPrepareMappedPublication(original, [new(first.Address + 31, bytes)], out var receipt));
                return Assert.IsType<CpuMappedPublication>(receipt);
            }
            var original = Capture();
            Assert.Equal(page, original.Segments[0].ProviderPageSize);
            Assert.Equal(0ul, original.Segments[0].MappingBackingOffset);
            if (scenario == 0)
            {
                foreach (var count in new[] { 1, 4, 8 })
                {
                    var input = Enumerable.Range(1, count).Select(i => (byte)(i * 23)).ToArray();
                    var expected = (byte[])seed.Clone(); input.CopyTo(expected, 31);
                    held = Prepare(original, input); Array.Fill(input, (byte)0xFF);
                    Assert.Equal(CpuMappedPublicationResult.Applied, held.TryCommit());
                    var next = Assert.IsType<CpuMappedCopyObservation>(held.SuccessfulObservation);
                    Assert.Equal(expected, new ReadOnlySpan<byte>((void*)first.Address, (int)page).ToArray());
                    Assert.Equal(expected, new ReadOnlySpan<byte>((void*)second.Address, (int)page).ToArray());
                    Assert.All(new ReadOnlySpan<byte>((void*)(views.AliasBase + page), (int)page).ToArray(), b => Assert.Equal((byte)0, b));
                    Assert.Equal(CpuMappedPublicationResult.Rejected, held.TryCommit());
                    held.Dispose(); held = null; original = next; seed = expected;
                }
            }
            else if (scenario == 1)
            {
                held = Prepare(original, [13, 27, 91, 203]);
                Assert.Equal(CpuMappedPublicationResult.Applied, held.TryCommit());
                var own = Assert.IsType<CpuMappedCopyObservation>(held.SuccessfulObservation);
                held.Dispose(); held = Prepare(own, [11, 31, 79, 211]);
                Assert.Equal(CpuMappedPublicationResult.Applied, held.TryCommit());
                var next = Assert.IsType<CpuMappedCopyObservation>(held.SuccessfulObservation);
                held.Dispose(); held = null;
                Assert.True(views.TryWriteBacking(second.Address + 40, new byte[] { 101 }));
                Assert.True(views.IsMappedCopyObservationCurrent(next));
                Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
                    views.TryPrepareMappedPublication(next, [new(first.Address + 31, new byte[] { 1 })], out _));
                Assert.Equal((byte)101, new ReadOnlySpan<byte>((void*)first.Address, (int)page)[40]);
            }
            else if (scenario == 2)
            {
                held = Prepare(original, [13, 27, 91, 203]);
                Assert.False(host.ChangeAccess(first.Address, page, HostPageProtection.ReadOnly));
                Assert.False(host.UnmapView(first.Address, page));
                Assert.False(views.Clear(0, 1));
                Assert.False(views.TryWriteBacking(second.Address, new byte[] { 201 }));
                Assert.False(views.TryCopyBacking(second.Address, first.Address + 1, 4));
                Assert.True(views.IsMappedCopyObservationCurrent(original));
                Assert.Equal(seed, new ReadOnlySpan<byte>((void*)first.Address, (int)page).ToArray());
                held.Dispose(); held.Dispose(); Assert.Null(held.SuccessfulObservation);
                Assert.Equal(CpuMappedPublicationResult.Rejected, held.TryCommit()); held = null;
                Assert.True(views.TryWriteBacking(second.Address + 40, new byte[] { 113 }));
            }
            else
            {
                Assert.True(views.Unmap(first.Address, page, out var preserved)); Assert.True(preserved);
                Assert.True(views.TryMapReservedRange(first.Address, page, 0, HostPageProtection.ReadWrite, out _));
                Assert.False(views.IsMappedCopyObservationCurrent(original));
                Assert.Equal(CpuMappedPublicationPreparationResult.Rejected,
                    views.TryPrepareMappedPublication(original, [new(first.Address + 31, new byte[] { 1 })], out _));
                var current = Capture(); Assert.NotEqual(original.Segments[0].MappingIncarnation, current.Segments[0].MappingIncarnation);
                held = Prepare(current, [13]); Assert.Equal(CpuMappedPublicationResult.Applied, held.TryCommit());
            }
            Console.WriteLine("publication-full-owned-alias-oracle-passed; no guest/native writer concurrency");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            Exception? cleanup = null;
            try { held?.Dispose(); } catch (Exception error) { cleanup = error; }
            try { views.Dispose(); if (!views.CleanupComplete) throw new IOException("Actual view cleanup incomplete."); }
            catch (Exception error) { cleanup ??= error; }
            // Unknown cleanup retains whole owned extents until this fresh process
            // exits; never dereference or reclaim them after attempted release.
            if (cleanup is not null) { first?.Quarantine(); second?.Quarantine(); }
            foreach (var hole in new[] { second, first })
                try { hole?.Dispose(); } catch (Exception error) { cleanup ??= error; }
            if (cleanup is not null)
            {
                if (primary is null) throw cleanup;
                try { primary.Data["MappedPublicationWorkerCleanup"] = cleanup; } catch { }
            }
            else Console.WriteLine("publication-owned-views-and-whole-Mach-extents-cleaned");
        }
    }

    private sealed class Hole : IDisposable
    {
        private const ulong Extent = 32768;
        private readonly IHostViewMemory host;
        private readonly ulong allocated;
        private bool consumed;
        internal ulong Address { get; }
        internal Hole(IHostViewMemory host, ulong page)
        {
            this.host = host; ulong address = 0;
            var result = mach_vm_allocate(mach_task_self(), ref address, Extent, 1);
            if (result != 0) throw new IOException($"Fixture Mach allocation declined: {result}.");
            allocated = address; var attempted = false;
            try
            {
                Assert.NotEqual(0ul, allocated); Address = checked((allocated + page - 1) & ~(page - 1));
                Assert.True(Address >= allocated && Address - allocated <= Extent - page);
                // Never relinquish ownership before MAP_FIXED. Convert the WHOLE
                // successful Mach extent into the provider's owned placeholder.
                attempted = true;
                if (!host.UnmapView(allocated, Extent)) throw new IOException("Owned placeholder conversion uncertain.");
                Console.WriteLine($"mapped-publication-fixture-owned extent={Extent}; page={page}; native_allocation_return={result}");
            }
            catch (Exception error)
            {
                if (attempted) consumed = true; // unknown conversion: retain full extent, never retry
                else
                {
                    consumed = true; var release = mach_vm_deallocate(mach_task_self(), allocated, Extent);
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
            if (!host.FreeHole(allocated, Extent)) throw new IOException("Owned full Mach extent cleanup uncertain; never retry.");
        }
    }
    [DllImport("libSystem.dylib")] private static extern uint mach_task_self();
    [DllImport("libSystem.dylib")] private static extern int mach_vm_allocate(uint task, ref ulong address, ulong size, int flags);
    [DllImport("libSystem.dylib")] private static extern int mach_vm_deallocate(uint task, ulong address, ulong size);
}

[SupportedOSPlatform("macos")]
public sealed class MacMappedPublicationNativeTests(ITestOutputHelper output)
{
    [MappedPublicationNativeTheory]
    [InlineData("scatter")]
    [InlineData("continuity")]
    [InlineData("reservation")]
    [InlineData("aba")]
    public async Task ActualMacMappedPublicationWorker(string scenario)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_MAPPED_PUBLICATION_NATIVE"));
        var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), expected);
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var start = new ProcessStartInfo(Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, "dotnet"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(MacMappedPublicationNativeTests).Assembly.Location);
        start.ArgumentList.Add("--mac-mapped-publication=" + scenario);
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var captured = await stdout.WaitAsync(TimeSpan.FromSeconds(2)); var errors = await stderr.WaitAsync(TimeSpan.FromSeconds(2));
            output.WriteLine(captured); if (errors.Length != 0) output.WriteLine(errors);
            Assert.Equal(0, child.ExitCode); Assert.Empty(errors);
            Assert.Contains("mapped-publication-native-pass case=" + scenario, captured);
            Assert.Contains("architecture=" + expected, captured); Assert.Contains("ordinary-alias-publication-only", captured);
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
internal sealed class MappedPublicationNativeTheoryAttribute : TheoryAttribute
{
    public MappedPublicationNativeTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_MAPPED_PUBLICATION_NATIVE") != "1")
            Skip = "Actual Mac publication requires separately authorized SHARPEMU_TEST_REQUIRE_MAPPED_PUBLICATION_NATIVE=1.";
    }
}
#endif
