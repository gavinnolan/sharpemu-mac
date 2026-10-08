// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
#if SHARPEMU_HOST_MACOS
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Tests.Memory;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Host.Memory.Tests;

// Each case is an explicit opt-in, fresh process. No signal/guest code is installed.
// Reflection reaches the dormant private adapter; it cannot substitute syscall receipts.
[SupportedOSPlatform("macos")]
internal static class MacOwnedNativeWorker
{
    internal static int Run(string scenario)
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_OWNED_NATIVE") != "1") return 80;
        if (!OperatingSystem.IsMacOS()) return 81;
        try
        {
            HostMemoryAuthority.Initialize(Composition.CreateMemory);
            Assert.True(Environment.SystemPageSize is 4096 or 16384);
            using var owner = new Owner(scenario == "reserve-commit");
            switch (scenario)
            {
                case "reserve-commit": ReserveCommit(owner); break;
                case "exclusion": Exclusion(owner); break;
                case "held-pin": HeldPin(owner); break;
                case "failure-first": PermissionFailure(owner, 0); break;
                case "failure-middle": PermissionFailure(owner, 1); break;
                case "failure-last": PermissionFailure(owner, 2); break;
                case "partial-release": PartialRelease(owner); break;
                case "bookkeeping-protect": Bookkeeping(owner, release: false); break;
                case "bookkeeping-release": Bookkeeping(owner, release: true); break;
                default: return 82;
            }
            Console.WriteLine($"owned-native-pass case={scenario} architecture={RuntimeInformation.ProcessArchitecture} native_page={Environment.SystemPageSize}; no-callback-or-retry-authorization");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 83; }
    }

    private static unsafe void ReserveCommit(Owner owner)
    {
        Assert.True(Get<bool>(owner.Cookie, "ReserveOnly"));
        for (var page = 0; page < 3; page++) Assert.Equal(1u, NativePageProtection.Query(owner.At(page)));
        var commit = owner.Prepare("TryPrepareProtection", [0, 3, 4u, true, null]);
        owner.Execute(commit); Receipt(commit, "AppliedWholeScope", 3, 3);
        for (var page = 0; page < 3; page++) { Assert.Equal(4u, NativePageProtection.Query(owner.At(page))); ((byte*)owner.At(page))[0] = (byte)(0x31 + page); }
        var oldDomain = owner.Domain;
        var protect = owner.Prepare("TryPrepareProtection", [1, 1, 2u, false, null]);
        Assert.True(oldDomain.IsClosed); owner.Execute(protect); Receipt(protect, "AppliedWholeScope", 1, 1);
        Assert.NotSame(oldDomain, owner.Domain); Assert.Equal(oldDomain.DomainEpoch + 1, owner.Domain.DomainEpoch);
        Assert.Equal(4u, NativePageProtection.Query(owner.At(0))); Assert.Equal(2u, NativePageProtection.Query(owner.At(1)));
        Assert.Equal(4u, NativePageProtection.Query(owner.At(2))); Assert.Equal(0x32, ((byte*)owner.At(1))[0]);
        var writable = owner.Prepare("TryPrepareProtection", [1, 1, 4u, false, null]); owner.Execute(writable);
        Assert.Equal(4u, NativePageProtection.Query(owner.At(1))); ((byte*)owner.At(1))[0] = 0x74;
    }

    private static unsafe void Exclusion(Owner owner)
    {
        var provider = HostMemoryAuthority.Current;
        var generation = HostMemory.MappingGeneration;
        Assert.False(provider.ProtectRaw(owner.At(1), owner.Page, 2, out _));
        Assert.False(provider.Protect(owner.At(1), owner.Page, HostPageProtection.ReadOnly, out _));
        Assert.False(provider.ProtectRaw(owner.Address - 1, 2, 2, out _));
        Assert.False(provider.ProtectRaw(owner.At(2), owner.Page + 1, 2, out _));
        Assert.Equal(0ul, provider.AllocateRaw(owner.At(1), owner.Page, 0x1000, 2));
        Assert.False(provider.Commit(owner.At(1), owner.Page, HostPageProtection.ReadOnly));
        Assert.False(provider.FreeRaw(owner.Address, 0, 0x8000)); Assert.False(provider.Free(owner.Address));
        Assert.False(HostMemory.Protect((void*)owner.At(1), (nuint)owner.Page, 2, out _));
        Assert.Equal((nint)0, (nint)HostMemory.Alloc((void*)owner.At(1), (nuint)owner.Page, 0x1000, 2));
        Assert.False(HostMemory.Free((void*)owner.Address, 0, 0x8000));
        Assert.Equal(generation, HostMemory.MappingGeneration);
        for (var page = 0; page < 3; page++) Assert.Equal(4u, NativePageProtection.Query(owner.At(page)));
        Assert.Equal("Active", Get<object>(owner.Cookie, "State").ToString());
    }

    private static void HeldPin(Owner owner)
    {
        var domain = owner.Domain;
        Assert.True(domain.TryAcquireWritePin(owner.At(1), HostRecoveryAccess.Write, 1, domain.DomainEpoch, out var pin));
        try
        {
            var operation = owner.Prepare("TryPrepareProtection", [0, 3, 2u, false, null]);
            Assert.True(domain.IsClosed); Assert.Equal(1, domain.ActivePins);
            Assert.False(domain.TryAcquireWritePin(owner.Address, HostRecoveryAccess.Write, 2, domain.DomainEpoch, out _));
            Assert.False((bool)Call(operation, "TryDrain", [TimeSpan.FromMilliseconds(20)])!);
            Assert.False((bool)Call(operation, "ExecuteOnce", [])!); Receipt(operation, "NoInvocation", 0, 0);
            for (var page = 0; page < 3; page++) Assert.Equal(4u, NativePageProtection.Query(owner.At(page)));
            // A second mutation cannot replace the exact pending operation.
            Assert.False((bool)Call(owner.Cookie, "TryPrepareRelease", [null])!);
            Assert.True(domain.Release(pin)); Assert.False(domain.Release(pin));
            owner.Execute(operation); Receipt(operation, "AppliedWholeScope", 3, 3);
        }
        finally { domain.Release(pin); }
    }

    private static void PermissionFailure(Owner owner, int failPage)
    {
        var readOnly = owner.Prepare("TryPrepareProtection", [0, 3, 2u, false, null]); owner.Execute(readOnly);
        var restriction = owner.Prepare("TryPrepareMaximumRestriction", [failPage, 1, 2u, null]);
        owner.Execute(restriction); Receipt(restriction, "AppliedWholeScope", 1, 1);
        var write = owner.Prepare("TryPrepareProtection", [0, 3, 4u, false, null]);
        owner.Execute(write); Receipt(write, failPage == 0 ? "Failed" : "Partial", failPage + 1, failPage);
        var records = Get<System.Collections.IEnumerable>(Get<object>(write, "Result"), "Pages").Cast<object>().ToArray();
        for (var page = 0; page < 3; page++)
        {
            Assert.Equal(page < failPage ? 4u : 2u, NativePageProtection.Query(owner.At(page)));
            Assert.Equal(page < failPage ? "Applied" : page == failPage ? "Failed" : "Unattempted", Get<object>(records[page], "Outcome").ToString());
        }
        Assert.Equal("PosixErrno", Get<object>(records[failPage], "ErrorDomain").ToString());
        Assert.Equal(-1, Get<int>(records[failPage], "NativeReturn")); Assert.True(Get<int>(records[failPage], "NativeError") > 0);
        Assert.Equal("Quarantined", Get<object>(owner.Cookie, "State").ToString()); Assert.Null(Get<object?>(owner.Cookie, "CurrentDomain"));
        Assert.False((bool)Call(owner.Cookie, "TryPrepareProtection", [0, 1, 2u, false, null])!);
    }

    private static void PartialRelease(Owner owner)
    {
        var release = owner.Prepare("TryPreparePageRelease", [1, 1, null]); owner.Execute(release);
        Receipt(release, "AppliedWholeScope", 1, 1);
        Assert.False(IsMapped(owner.At(1))); Assert.Equal(4u, NativePageProtection.Query(owner.At(0))); Assert.Equal(4u, NativePageProtection.Query(owner.At(2)));
        Assert.True(HostMemoryAuthority.Current.Query(owner.At(1), out var info)); Assert.Equal(HostRegionState.Free, info.State);
        var generation = HostMemory.MappingGeneration;
        Assert.Equal(0ul, HostMemoryAuthority.Current.AllocateRaw(0, owner.Page, 0x3000, 4));
        Assert.Equal(0ul, HostMemoryAuthority.Current.AllocateRaw(owner.At(1), owner.Page, 0x3000, 4));
        Assert.Equal(generation, HostMemory.MappingGeneration);
        object?[] attempt = [1, false, 4u, null, null];
        Assert.False((bool)Factory().Invoke(HostMemoryAuthority.Current, attempt)!); Assert.Null(attempt[3]);
        owner.Release(); // exactly two per-page syscalls, never old full extent
        Assert.False(IsMapped(owner.At(0))); Assert.False(IsMapped(owner.At(2)));
    }

    private static void Bookkeeping(Owner owner, bool release)
    {
        var operation = release ? owner.Prepare("TryPreparePageRelease", [1, 1, null]) : owner.Prepare("TryPrepareProtection", [0, 3, 2u, false, null]);
        var failure = new InvalidOperationException("owned-worker-post-native-bookkeeping-control");
        operation.GetType().GetField("_bookkeepingFailurePageForControl", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(operation, 0);
        operation.GetType().GetField("_bookkeepingFailureForControl", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(operation, failure);
        Assert.True((bool)Call(operation, "TryDrain", [TimeSpan.FromSeconds(1)])!);
        var wrapped = Assert.Throws<TargetInvocationException>(() => Call(operation, "ExecuteOnce", [])); Assert.Same(failure, wrapped.InnerException);
        Receipt(operation, "Unknown", 1, 1); Assert.Same(failure, Get<object>(Get<object>(operation, "Result"), "BookkeepingFailure"));
        Assert.Equal("Quarantined", Get<object>(owner.Cookie, "State").ToString()); Assert.Null(Get<object?>(owner.Cookie, "CurrentDomain"));
        if (release) { Assert.False(IsMapped(owner.At(1))); Assert.Equal(4u, NativePageProtection.Query(owner.At(0))); Assert.Equal(4u, NativePageProtection.Query(owner.At(2))); }
        else { Assert.Equal(2u, NativePageProtection.Query(owner.At(0))); Assert.Equal(4u, NativePageProtection.Query(owner.At(1))); }
        Assert.False((bool)Call(operation, "ExecuteOnce", [])!);
    }

    private static MethodInfo Factory() => HostMemoryAuthority.Current.GetType().GetMethod("TryCreateOwnedAnonymousControl", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object? Call(object target, string method, object?[] args) => target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, args);
    private static T Get<T>(object target, string name) => (T)target.GetType().GetProperty(name)!.GetValue(target)!;
    private static void Receipt(object operation, string outcome, int invocations, int successes)
    {
        var result = Get<object>(operation, "Result");
        Assert.Equal(outcome, Get<object>(result, "Outcome").ToString()); Assert.Equal(invocations, Get<int>(result, "NativeInvocations")); Assert.Equal(successes, Get<int>(result, "SuccessfulPages"));
        foreach (var page in Get<System.Collections.IEnumerable>(result, "Pages"))
            Console.WriteLine($"receipt op={Get<long>(result, "OperationIdentity")} outcome={Get<object>(page, "Outcome")} return={Get<int>(page, "NativeReturn")} error_domain={Get<object>(page, "ErrorDomain")} error={Get<int>(page, "NativeError")}");
    }

    private sealed class Owner : IDisposable
    {
        internal readonly object Cookie;
        internal ulong Address => Get<ulong>(Cookie, "Address");
        internal ulong Page => Get<ulong>(Cookie, "NativePageSize");
        internal ulong At(int page) => Address + (ulong)page * Page;
        internal HostRecoveryOwner Domain => Get<HostRecoveryOwner>(Cookie, "CurrentDomain");
        internal Owner(bool reserve)
        {
            object?[] args = [3, reserve, reserve ? 1u : 4u, null, null];
            Assert.True((bool)Factory().Invoke(HostMemoryAuthority.Current, args)!); Cookie = args[3]!;
            Assert.Equal("AppliedWholeScope", Get<object>(args[4]!, "Outcome").ToString()); Assert.Equal((ulong)Environment.SystemPageSize, Page);
            Assert.True(IsMapped(Address)); // warm exact readback path before deliberate holes
        }
        internal object Prepare(string method, object?[] args) { Assert.True((bool)Call(Cookie, method, args)!); return args[^1]!; }
        internal void Execute(object operation)
        {
            Assert.True((bool)Call(operation, "TryDrain", [TimeSpan.FromSeconds(1)])!);
            Assert.True((bool)Call(operation, "ExecuteOnce", [])!); Assert.False((bool)Call(operation, "ExecuteOnce", [])!);
        }
        internal void Release()
        {
            if (Get<object>(Cookie, "State").ToString() == "Released") return;
            var mappedBefore = (bool[])Cookie.GetType().GetField("_mapped", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Cookie)!;
            var remaining = mappedBefore.Count(static mapped => mapped);
            var operation = Prepare("TryPrepareRelease", [null]); Execute(operation);
            Receipt(operation, "AppliedWholeScope", remaining, remaining);
            // Native per-page release skips only positively recorded removed pages.
            var mapped = (bool[])Cookie.GetType().GetField("_mapped", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Cookie)!;
            Assert.All(mapped, value => Assert.False(value)); Assert.Equal("Released", Get<object>(Cookie, "State").ToString());
        }
        public void Dispose() => Release();
    }

    private static unsafe bool IsMapped(ulong address)
    {
        var found = address; ulong bytes = 0; uint count = 9; var info = stackalloc int[9];
        var task = MachTaskSelf(); var result = MachVmRegion(task, ref found, ref bytes, 9, info, ref count, out var name);
        try { return result == 0 && found <= address && address - found < bytes; }
        finally { if (name != 0) Assert.Equal(0, MachPortDeallocate(task, name)); }
    }
    [DllImport("libSystem.dylib", EntryPoint = "mach_task_self")] private static extern uint MachTaskSelf();
    [DllImport("libSystem.dylib", EntryPoint = "mach_vm_region")] private static extern unsafe int MachVmRegion(uint task, ref ulong address, ref ulong size, int flavor, int* info, ref uint count, out uint name);
    [DllImport("libSystem.dylib", EntryPoint = "mach_port_deallocate")] private static extern int MachPortDeallocate(uint task, uint name);
}

[SupportedOSPlatform("macos")]
public sealed class MacOwnedNativeOperationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("reserve-commit")]
    [InlineData("exclusion")]
    [InlineData("held-pin")]
    [InlineData("failure-first")]
    [InlineData("failure-middle")]
    [InlineData("failure-last")]
    [InlineData("partial-release")]
    [InlineData("bookkeeping-protect")]
    [InlineData("bookkeeping-release")]
    public async Task CanonicalProviderOwnedNativeWorker(string scenario)
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_OWNED_NATIVE"));
        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var host = Path.Combine(runtime.Parent!.Parent!.Parent!.FullName, "dotnet");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(MacOwnedNativeOperationTests).Assembly.Location); start.ArgumentList.Add("--mac-owned-native=" + scenario);
        using var worker = Process.Start(start)!;
        var stdout = worker.StandardOutput.ReadToEndAsync(); var stderr = worker.StandardError.ReadToEndAsync();
        try
        {
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            var captured = await stdout; var errors = await stderr;
            output.WriteLine(captured); if (errors.Length != 0) output.WriteLine(errors);
            Assert.Equal(0, worker.ExitCode); Assert.Empty(errors); Assert.Contains("owned-native-pass case=" + scenario, captured);
            Assert.Contains("architecture=" + RuntimeInformation.ProcessArchitecture, captured);
        }
        finally
        {
            if (!worker.HasExited) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
#endif
