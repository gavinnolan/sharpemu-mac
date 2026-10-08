// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Tests.Memory;
using Xunit;
using Xunit.Abstractions;
[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace SharpEmu.Host.Memory.Tests;

internal static class Composition
{
    internal static IHostMemoryRaw CreateMemory()
    {
#if SHARPEMU_HOST_MACOS
        return OperatingSystem.IsMacOS() ? SharpEmu.Host.MacOS.MacOSHost.CreateMemory() : throw new PlatformNotSupportedException();
#elif SHARPEMU_HOST_LINUX
        return OperatingSystem.IsLinux() ? SharpEmu.Host.Linux.LinuxHost.CreateMemory() : throw new PlatformNotSupportedException();
#elif SHARPEMU_HOST_WINDOWS
        return OperatingSystem.IsWindows() ? SharpEmu.Host.Windows.WindowsHost.CreateMemory() : throw new PlatformNotSupportedException();
#else
#error No host memory provider for this build host.
#endif
    }
    private static readonly Lazy<bool> Once = new(() =>
    {
#if SHARPEMU_HOST_MACOS
        HostMemoryAuthority.Initialize(CreateMemory);
        HostPlatform.Initialize(static () => OperatingSystem.IsMacOS() ? SharpEmu.Host.MacOS.MacOSHost.Create() : throw new PlatformNotSupportedException());
#elif SHARPEMU_HOST_LINUX
        HostMemoryAuthority.Initialize(CreateMemory);
        HostPlatform.Initialize(static () => OperatingSystem.IsLinux() ? SharpEmu.Host.Linux.LinuxHost.Create() : throw new PlatformNotSupportedException());
#elif SHARPEMU_HOST_WINDOWS
        HostMemoryAuthority.Initialize(CreateMemory);
        HostPlatform.Initialize(static () => OperatingSystem.IsWindows() ? SharpEmu.Host.Windows.WindowsHost.Create() : throw new PlatformNotSupportedException());
#else
#error No host memory provider for this build host.
#endif
        return true;
    });
    internal static void Ensure() => _ = Once.Value;
}

public static class UninitializedProbe
{
    public static unsafe int Main(string[] args)
    {
        if (args.Length != 1) return 2;
        if (args[0] == "--mismatched-platform-factory")
        {
            return AuthorityTests.RunMismatchedFactoryProbe();
        }
        if (args[0] == "--positive-warmup-failure" || args[0] == "--cleanup-warmup-failure" || args[0] == "--probe-and-cleanup-failure" ||
            args[0] == "--cleanup-throws" || args[0] == "--probe-and-cleanup-throws")
            return AuthorityTests.RunWarmupFailureProbe(args[0] != "--cleanup-warmup-failure" && args[0] != "--cleanup-throws",
                args[0] == "--positive-warmup-failure" ? 0 : args[0].EndsWith("throws", StringComparison.Ordinal) ? 2 : 1);
        if (args[0] != "--uninitialized-memory" || HostMemoryAuthority.IsInitialized) return 3;
        try { HostMemory.Alloc(null, (nuint)Environment.SystemPageSize, 0x3000, 4); return 4; }
        catch (InvalidOperationException e) when (e.Message.Contains("safe startup", StringComparison.Ordinal)) { }
        if (HostMemoryAuthority.IsInitialized) return 5;
        try { HostMemoryAuthority.Initialize(static () => null!); return 6; }
        catch (InvalidOperationException) { }
        if (HostMemoryAuthority.IsInitialized) return 7;
        try
        {
            HostMemoryAuthority.Initialize(() =>
            {
                HostMemoryAuthority.Initialize(static () => null!);
                return null!;
            });
            return 8;
        }
        catch (InvalidOperationException) { }
        if (HostMemoryAuthority.IsInitialized) return 9;
        Composition.Ensure();
        if (!HostMemoryAuthority.IsInitialized) return 10;
        Console.WriteLine("directed-startup-failure;provider-uninitialized;uninitialized-call-no-kernel-allocation;setup-rollback-reentrant-guard"); return 0;
    }
}

public sealed class AuthorityTests
{
    public AuthorityTests(ITestOutputHelper output)
    {
        Composition.Ensure();
        output.WriteLine($"memory-control architecture={RuntimeInformation.ProcessArchitecture}; native_page={Environment.SystemPageSize}; provider={HostMemoryAuthority.Current.GetType().FullName}");
    }
    private static ulong Page => (ulong)Environment.SystemPageSize;

    [Fact]
    public void CanonicalProviderIsImmutableAndIndependentOfGuestArchitecture()
    {
        var provider = HostMemoryAuthority.Current;
        var called = false;
        Assert.Throws<InvalidOperationException>(() => HostMemoryAuthority.Initialize(() => { called = true; return provider; }));
        Assert.False(called); Assert.Same(provider, HostMemoryAuthority.Current);
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64) Assert.Same(provider, HostPlatform.Current.Memory);
        else Assert.Throws<PlatformNotSupportedException>(() => HostPlatform.Current);
        Assert.Same(provider, HostMemoryAuthority.Current); // ARM plain memory works despite guest rejection
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public unsafe void BothEntrypointsShareActualAllocationProtectionAndRelease(bool legacyAllocate)
    {
        var provider = HostMemoryAuthority.Current;
        var address = legacyAllocate ? (ulong)HostMemory.Alloc(null, (nuint)(2 * Page), 0x3000, 4) : provider.Allocate(0, 2 * Page, HostPageProtection.ReadWrite);
        Assert.NotEqual(0ul, address);
        try
        {
            ((byte*)address)[0] = 0x57; ((byte*)address)[Page] = 0x93;
            Assert.True(provider.Query(address + Page, out var neutral));
            Assert.Equal(address, neutral.AllocationBase); Assert.Equal(HostPageProtection.ReadWrite, neutral.Protection);
            Assert.Equal((nuint)48, HostMemory.Query((void*)address, out var raw));
            Assert.Equal(address, raw.AllocationBase); Assert.Equal(0x20000u, raw.Type); Assert.Equal(4u, raw.Protect);
            if (!OperatingSystem.IsWindows()) { Assert.Equal(0u, raw.Alignment1); Assert.Equal(0u, raw.Alignment2); }
            if (legacyAllocate) Assert.True(provider.Protect(address + Page, Page, HostPageProtection.ReadOnly, out _));
            else Assert.True(HostMemory.Protect((void*)(address + Page), (nuint)Page, 2, out _));
            Assert.Equal(2u, NativePageProtection.Query(address + Page));
            Assert.Equal(4u, NativePageProtection.Query(address));
            Assert.Equal((nuint)48, HostMemory.Query((void*)(address + Page), out raw)); Assert.Equal(2u, raw.Protect);
            Assert.True(provider.Query(address + Page, out neutral)); Assert.Equal(HostPageProtection.ReadOnly, neutral.Protection);
            Assert.True(provider.ProtectRaw(address + Page, Page, 4, out var old)); Assert.Equal(2u, old);
            Assert.Equal(4u, NativePageProtection.Query(address + Page)); Assert.Equal(0x93, ((byte*)address)[Page]);
        }
        finally
        {
            if (legacyAllocate) Assert.True(provider.Free(address));
            else Assert.True(HostMemory.Free((void*)address, 0, 0x8000));
        }
        Assert.True(provider.Query(address, out var released)); Assert.Equal(HostRegionState.Free, released.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public unsafe void ReserveAndInteriorCommitRetainPlatformState(bool legacyReserve)
    {
        var provider = HostMemoryAuthority.Current;
        var address = legacyReserve ? (ulong)HostMemory.Alloc(null, (nuint)(2 * Page), 0x2000, 2) : provider.Reserve(0, 2 * Page, HostPageProtection.ReadOnly);
        Assert.NotEqual(0ul, address);
        try
        {
            Assert.Equal((nuint)48, HostMemory.Query((void*)address, out var before));
            Assert.Equal(OperatingSystem.IsWindows() ? 0x2000u : 0x1000u, before.State);
            Assert.True(provider.Commit(address + Page, Page, HostPageProtection.ReadWrite));
            Assert.Equal(4u, NativePageProtection.Query(address + Page)); ((byte*)address)[Page] = 0x6D;
            Assert.Equal((nuint)48, HostMemory.Query((void*)(address + Page), out var after)); Assert.Equal(0x1000u, after.State); Assert.Equal(4u, after.Protect);
        }
        finally { Assert.True(HostMemory.Free((void*)address, 0, 0x8000)); }
    }

    [Fact]
    public unsafe void CommitOverrunRejectsWholeScopeWithoutKernelPermissionChange()
    {
        var provider = HostMemoryAuthority.Current;
        var address = provider.Allocate(0, 2 * Page, HostPageProtection.ReadOnly); Assert.NotEqual(0ul, address);
        try
        {
            Assert.Equal(0ul, provider.AllocateRaw(address + Page, 2 * Page, 0x1000, 4));
            Assert.False(provider.Commit(address + Page, 2 * Page, HostPageProtection.ReadWrite));
            Assert.Equal(2u, NativePageProtection.Query(address)); Assert.Equal(2u, NativePageProtection.Query(address + Page));
            Assert.True(provider.Query(address + Page, out var info)); Assert.Equal(2u, info.RawProtection);
        }
        finally { Assert.True(provider.Free(address)); }
    }

    [Fact]
    public unsafe void FailedRawOperationsAndExactOverlapDoNotClobberLiveAllocation()
    {
        var p = HostMemoryAuthority.Current;
        Assert.Equal(0ul, p.AllocateRaw(0, 0, 0x3000, 4)); Assert.False(p.FreeRaw(0, 0, 0x8000));
        Assert.False(p.ProtectRaw(0, 0, 4, out _));
        var address = p.Allocate(0, 2 * Page, HostPageProtection.ReadWrite); Assert.NotEqual(0ul, address);
        try
        {
            ((byte*)address)[0] = 0xA7;
            Assert.Equal(0ul, p.AllocateRaw(address, 2 * Page, 0x3000, 4));
            Assert.False(p.ProtectRaw(address + Page, 2 * Page, 2, out _));
            // Free interior address cannot release a whole allocation.
            Assert.False(p.FreeRaw(address + Page, 0, 0x8000));
            Assert.Equal(0xA7, ((byte*)address)[0]); Assert.Equal(4u, NativePageProtection.Query(address));
            Assert.True(p.Query(address, out var info)); Assert.Equal(address, info.AllocationBase);
        }
        finally { Assert.True(p.Free(address)); }
    }

    [Fact]
    public unsafe void SparseQueryPreservesAllocationExtentAndProtectionRuns()
    {
        const ulong bytes = 256ul * 1024 * 1024;
        var p = HostMemoryAuthority.Current; var address = p.Reserve(0, bytes, HostPageProtection.ReadOnly); Assert.NotEqual(0ul, address);
        try
        {
            Assert.Equal((nuint)48, HostMemory.Query((void*)address, out var initial)); Assert.Equal(bytes, initial.RegionSize);
            if (OperatingSystem.IsWindows()) Assert.True(p.Commit(address, bytes, HostPageProtection.ReadOnly));
            Assert.True(p.Protect(address + bytes - Page, Page, HostPageProtection.ReadWrite, out _));
            Assert.Equal((nuint)48, HostMemory.Query((void*)address, out var before)); Assert.Equal(bytes - Page, before.RegionSize);
            Assert.Equal((nuint)48, HostMemory.Query((void*)(address + bytes - Page), out var last)); Assert.Equal(Page, last.RegionSize); Assert.Equal(4u, last.Protect);
        }
        finally { Assert.True(p.Free(address)); }
    }

    [Fact]
    public void GlobalPlatformRejectsDifferentInjectedMemoryBeforePublication()
    {
        var previous = HostMemoryAuthority.Current;
        Assert.Throws<InvalidOperationException>(() => HostPlatform.Initialize(new DifferentPlatform(new MemoryProxy(previous))));
        Assert.Same(previous, HostMemoryAuthority.Current);
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64) Assert.Same(previous, HostPlatform.Current.Memory);
    }

    internal static int RunWarmupFailureProbe(bool queryFailure, int cleanupFailure)
    {
        var backend = Composition.CreateMemory();
        var proxy = new FaultyRaw(backend, queryFailure, cleanupFailure);
        try { HostMemoryAuthority.Initialize(() => proxy); return 12; }
        catch (InvalidOperationException error)
        {
            if (queryFailure && !error.Message.Contains("positive startup probes", StringComparison.Ordinal)) return 17;
        }
        if (HostMemoryAuthority.LastStartupCleanupFailureAddress != (cleanupFailure != 0 ? proxy.Page : 0) ||
            HostMemoryAuthority.LastStartupCleanupFailureKind != cleanupFailure) return 18;
        if (HostMemoryAuthority.IsInitialized || proxy.Page == 0 || proxy.ReleaseCalls != 1 || !proxy.NativeReleased) return 13;
        if (!backend.Query(proxy.Page, out var released) || released.State != HostRegionState.Free) return 14;
        try { _ = HostMemoryAuthority.Current; return 15; } catch (InvalidOperationException) { }
        // The setup state is reset; one honest provider can be constructed and warmed.
        HostMemoryAuthority.Initialize(() => backend);
        if (!ReferenceEquals(HostMemoryAuthority.Current, backend)) return 16;
        Console.WriteLine("warmup-failure-unpublished;exact-native-page-release-once;honest-startup-retry"); return 0;
    }

    internal static int RunMismatchedFactoryProbe()
    {
        Composition.Ensure();
        HostPlatform.Initialize(static () => new DifferentPlatform(new MemoryProxy(HostMemoryAuthority.Current)));
        try { _ = HostPlatform.Current; return 11; }
        catch (InvalidOperationException e) when (e.Message.Contains("must reference", StringComparison.Ordinal))
        {
            Console.WriteLine("mismatched-platform-factory-directed-rejection"); return 0;
        }
    }

    [Theory]
    [InlineData("--uninitialized-memory", "directed-startup-failure;provider-uninitialized;uninitialized-call-no-kernel-allocation;setup-rollback-reentrant-guard")]
    [InlineData("--mismatched-platform-factory", "mismatched-platform-factory-directed-rejection")]
    [InlineData("--positive-warmup-failure", "warmup-failure-unpublished;exact-native-page-release-once;honest-startup-retry")]
    [InlineData("--cleanup-warmup-failure", "warmup-failure-unpublished;exact-native-page-release-once;honest-startup-retry")]
    [InlineData("--probe-and-cleanup-failure", "warmup-failure-unpublished;exact-native-page-release-once;honest-startup-retry")]
    [InlineData("--cleanup-throws", "warmup-failure-unpublished;exact-native-page-release-once;honest-startup-retry")]
    [InlineData("--probe-and-cleanup-throws", "warmup-failure-unpublished;exact-native-page-release-once;honest-startup-retry")]
    public async Task StartupAndInjectedFactoryFailureAreIsolated(string mode, string expected)
    {
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var host = Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var info = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(typeof(AuthorityTests).Assembly.Location); info.ArgumentList.Add(mode);
        using var worker = Process.Start(info)!;
        try
        {
            var stdout = worker.StandardOutput.ReadToEndAsync(); var stderr = worker.StandardError.ReadToEndAsync();
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, worker.ExitCode); Assert.Contains(expected, await stdout); Assert.Empty(await stderr);
        }
        finally { if (!worker.HasExited) worker.Kill(entireProcessTree: true); }
    }

#if SHARPEMU_HOST_WINDOWS
    [Fact]
    public unsafe void WindowsRawGuardModifierAndReserveStateRoundTripWithoutNeutralization()
    {
        var p = HostMemoryAuthority.Current;
        var address = p.AllocateRaw(0, 2 * Page, 0x2000, 4); Assert.NotEqual(0ul, address);
        try
        {
            Assert.Equal((nuint)48, HostMemory.Query((void*)address, out var reserved)); Assert.Equal(0x2000u, reserved.State);
            Assert.Equal(address, p.AllocateRaw(address, Page, 0x1000, 0x104));
            Assert.Equal((nuint)48, HostMemory.Query((void*)address, out var guarded)); Assert.Equal(0x104u, guarded.Protect);
            Assert.True(p.Query(address, out var neutral)); Assert.Equal(0x104u, neutral.RawProtection); Assert.Equal(HostPageProtection.ReadWrite, neutral.Protection);
            Assert.True(p.ProtectRaw(address, Page, 4, out var old)); Assert.Equal(0x104u, old);
            Assert.True(HostMemory.Protect((void*)address, (nuint)Page, old, out var restored)); Assert.Equal(4u, restored);
            Assert.Equal((nuint)48, HostMemory.Query((void*)address, out var final)); Assert.Equal(0x104u, final.Protect);
        }
        finally { Assert.True(p.FreeRaw(address, 0, 0x8000)); }
    }
#endif

    private sealed class DifferentPlatform(IHostMemory memory) : IHostPlatform
    {
        public IHostMemory Memory => memory;
        public IHostThreading Threading => throw new NotSupportedException();
        public IHostSymbolResolver Symbols => throw new NotSupportedException();
        public IHostAudioOutput Audio => throw new NotSupportedException();
        public IHostInput Input => throw new NotSupportedException();
    }
    private sealed class FaultyRaw(IHostMemoryRaw inner, bool queryFailure, int cleanupFailure) : MemoryProxy(inner), IHostMemoryRaw
    {
        public ulong Page { get; private set; }
        public int ReleaseCalls { get; private set; }
        public bool NativeReleased { get; private set; }
        public ulong AllocateRaw(ulong address, ulong size, uint type, uint protection)
        {
            var allocated = inner.AllocateRaw(address, size, type, protection);
            if (size != 0) Page = allocated;
            return allocated;
        }
        public bool FreeRaw(ulong address, ulong size, uint type)
        {
            var success = inner.FreeRaw(address, size, type);
            if (address != 0 && address == Page)
            {
                ReleaseCalls++; NativeReleased = success;
                // Simulate an uncertain/declined receipt after actually releasing this exact owned page.
                if (cleanupFailure == 2) throw new InvalidOperationException("Injected startup cleanup error after exact native release.");
                if (cleanupFailure == 1) return false;
            }
            return success;
        }
        public nuint QueryRaw(ulong address, out HostMemory.BasicInfo info)
        {
            var result = inner.QueryRaw(address, out info);
            return address != 0 && queryFailure ? 0 : result;
        }
    }

    private class MemoryProxy(IHostMemory inner) : IHostMemory
    {
        public ulong Allocate(ulong address, ulong size, HostPageProtection protection) => inner.Allocate(address, size, protection);
        public ulong Reserve(ulong address, ulong size, HostPageProtection protection) => inner.Reserve(address, size, protection);
        public bool Commit(ulong address, ulong size, HostPageProtection protection) => inner.Commit(address, size, protection);
        public bool Free(ulong address) => inner.Free(address);
        public bool Protect(ulong address, ulong size, HostPageProtection protection, out uint old) => inner.Protect(address, size, protection, out old);
        public bool ProtectRaw(ulong address, ulong size, uint protection, out uint old) => inner.ProtectRaw(address, size, protection, out old);
        public bool Query(ulong address, out HostRegionInfo info) => inner.Query(address, out info);
        public void FlushInstructionCache(ulong address, ulong size) => inner.FlushInstructionCache(address, size);
    }
}
