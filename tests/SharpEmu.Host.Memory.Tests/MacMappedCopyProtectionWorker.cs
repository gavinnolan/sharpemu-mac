// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
#if SHARPEMU_HOST_MACOS
using System.Reflection;
using System.Runtime.Versioning;
using SharpEmu.HLE;
using SharpEmu.HLE.GuestMemory;
using SharpEmu.HLE.Host;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Host.Memory.Tests;

[SupportedOSPlatform("macos")]
internal static unsafe partial class MacMappedCopyWorker
{
    private const ulong ProtectionExtent = 65536, ProtectionSlice = 16384;
    private const GuestPageProtection ProtectionRW = GuestPageProtection.Read | GuestPageProtection.Write;

    private static void ExecuteProtection(IHostViewMemory host, HostMappedCopyLedger ledger, ulong page, int scenario)
    {
        Assert.True(ProtectionExtent % page == 0 && ProtectionSlice % page == 0);
        var explicitHost = Assert.IsAssignableFrom<IHostDerivedProtectionMemory>(host);
        var views = new SharedBackingViews(host, ProtectionExtent);
        Hole? hole = null; Exception? primary = null;
        try
        {
            Assert.True(views.IsAvailable); hole = new(host, page, ProtectionExtent);
            Assert.True(views.TryMapReservedRange(hole.Address, ProtectionExtent, 0, HostPageProtection.ReadWrite, out _));
            var seed = Enumerable.Range(0, (int)ProtectionExtent).Select(i => (byte)(i * 13 + i / 4096 + 7)).ToArray();
            Assert.True(views.TryWriteBacking(hole.Address, seed));
            var original = CopyProtection(views, hole.Address, seed);
            if (scenario == 0)
            {
                // Exercise the actual optional interface as well as its identical
                // syscall core. A watcher cannot erase original guest write rights.
                Assert.True(explicitHost.ChangeDerivedAccess(hole.Address, ProtectionExtent, ProtectionRW, HostPageProtection.ReadOnly, false));
                var watched = CopyProtection(views, hole.Address, seed);
                Assert.Equal(ProtectionRW, Assert.Single(watched.Segments).GuestAccess);
                Assert.False(views.IsMappedCopyObservationCurrent(original));
                CommitProtectionByte(views, watched, hole.Address + ProtectionSlice + 13, 211);
                seed[(int)ProtectionSlice + 13] = 211;
                _ = CopyProtection(views, hole.Address, seed); // full neighbors/padding unchanged
                ChangeProtectionWithReceipt(host, hole.Address, ProtectionExtent, ProtectionRW, HostPageProtection.ReadWrite, false, true);
                Assert.False(views.IsMappedCopyObservationCurrent(watched));
                _ = CopyProtection(views, hole.Address, seed);
            }
            else if (scenario == 1)
            {
                Assert.True(host.ChangeAccess(hole.Address, ProtectionExtent, HostPageProtection.ReadOnly)); // actual base effect
                ChangeProtectionWithReceipt(host, hole.Address, ProtectionExtent, GuestPageProtection.Read, HostPageProtection.ReadOnly, false, true);
                var current = CopyProtection(views, hole.Address, seed);
                Assert.Equal(GuestPageProtection.Read, Assert.Single(current.Segments).GuestAccess);
                Assert.Equal(CpuMappedPublicationPreparationResult.Rejected, views.TryPrepareMappedPublication(current,
                    [new(hole.Address + 13, new byte[] { 211 })], out var rejected));
                Assert.Null(rejected); Assert.False(views.IsMappedCopyObservationCurrent(original));
            }
            else if (scenario == 2)
            {
                // Exact64K original physically mapped view, exact16K partial effect.
                ChangeProtectionWithReceipt(host, hole.Address + ProtectionSlice, ProtectionSlice,
                    ProtectionRW, HostPageProtection.ReadOnly, true, true);
                var split = CopyProtection(views, hole.Address, seed);
                Assert.Equal(new[] { 0UL, ProtectionSlice, ProtectionSlice * 2 }, split.Segments.Select(s => s.MappingBackingOffset));
                Assert.Equal(new[] { ProtectionSlice, ProtectionSlice, ProtectionSlice * 2 }, split.Segments.Select(s => s.MappingByteCount));
                Assert.All(split.Segments, s => Assert.Equal(ProtectionRW, s.GuestAccess));
                CommitProtectionByte(views, split, hole.Address + ProtectionSlice + 13, 211); seed[(int)ProtectionSlice + 13] = 211;
                Assert.True(host.ChangeAccess(hole.Address + ProtectionSlice, ProtectionSlice, HostPageProtection.ReadOnly));
                var baseReadonly = CopyProtection(views, hole.Address, seed);
                Assert.Equal(GuestPageProtection.Read, baseReadonly.Segments[1].GuestAccess);
                Assert.Equal(CpuMappedPublicationPreparationResult.Rejected, views.TryPrepareMappedPublication(baseReadonly,
                    [new(hole.Address + ProtectionSlice + 14, new byte[] { 211 })], out _));
                CommitProtectionByte(views, baseReadonly, hole.Address + 13, 211); seed[13] = 211;
                _ = CopyProtection(views, hole.Address, seed);
                Assert.False(views.IsMappedCopyObservationCurrent(original));
                // Real Rosetta4K logical request, measured kernel-rounded footprint.
                ChangeProtectionWithReceipt(host, hole.Address, host.PageSize, ProtectionRW, HostPageProtection.ReadOnly, false, true);
                _ = CopyProtection(views, hole.Address, seed); // alias bytes, never virtual permission proof
            }
            else
            {
                var method = host.GetType().GetMethod("SetReadonlyMaximumForControl", BindingFlags.Instance | BindingFlags.NonPublic)!;
                object?[] arguments = [hole.Address, ProtectionExtent, ProtectionRW, 0];
                Assert.True((bool)method.Invoke(host, arguments)!);
                Assert.Equal(0, (int)arguments[3]!);
                Console.WriteLine("derived-protection maximum_return=0; actual provider Mach maximum restricted to Read");
                var restricted = CopyProtection(views, hole.Address, seed);
                ChangeProtectionWithReceipt(host, hole.Address, ProtectionExtent, ProtectionRW, HostPageProtection.ReadWrite, false, false);
                Assert.Equal(CpuMappedCopyResult.Rejected, views.TryReadMappedCopy(hole.Address, ProtectionExtent,
                    hole.Address, new byte[seed.Length], out var failed)); Assert.Null(failed);
                Assert.False(views.IsMappedCopyObservationCurrent(restricted));
                // A later independently successful RO receipt can certify retained
                // mapping availability; it never pretends the failed RW succeeded.
                ChangeProtectionWithReceipt(host, hole.Address, ProtectionExtent, ProtectionRW, HostPageProtection.ReadOnly, false, true);
                _ = CopyProtection(views, hole.Address, seed);
                Assert.False(views.IsMappedCopyObservationCurrent(original));
            }
            Console.WriteLine($"derived-protection full_bytes={ProtectionExtent}; logical_provider_page={host.PageSize}; measured_kernel_page={page}; scenario={scenario}; permission_recovery_off=true");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            Exception? cleanup = null;
            try { views.Dispose(); if (!views.CleanupComplete) throw new IOException("Protection fixture view cleanup incomplete."); }
            catch (Exception error) { cleanup = error; hole?.Quarantine(); }
            try { hole?.Dispose(); } catch (Exception error) { cleanup ??= error; }
            if (cleanup is not null)
            {
                if (primary is null) throw cleanup;
                try { primary.Data["ProtectionWorkerCleanup"] = cleanup; } catch { }
            }
        }
    }

    private static CpuMappedCopyObservation CopyProtection(SharedBackingViews views, ulong address, byte[] expected)
    {
        var bytes = new byte[expected.Length];
        Assert.Equal(CpuMappedCopyResult.Copied, views.TryReadMappedCopy(address, ProtectionExtent, address, bytes, out var observation));
        Assert.NotNull(observation); Assert.Equal(expected, bytes);
        Assert.True(views.IsMappedCopyObservationCurrent(observation!));
        return observation!;
    }

    private static void CommitProtectionByte(SharedBackingViews views, CpuMappedCopyObservation original, ulong address, byte value)
    {
        Assert.Equal(CpuMappedPublicationPreparationResult.Prepared,
            views.TryPrepareMappedPublication(original, [new(address, new byte[] { value })], out var publication));
        using (publication) Assert.Equal(CpuMappedPublicationResult.Applied, publication!.TryCommit());
    }

    private static void ChangeProtectionWithReceipt(IHostViewMemory host, ulong address, ulong size,
        GuestPageProtection guest, HostPageProtection derived, bool ordinary, bool expectedSuccess)
    {
        var method = host.GetType().GetMethod("ChangeDerivedAccessCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] arguments = [address, size, guest, derived, ordinary, 0, 0];
        Assert.Equal(expectedSuccess, (bool)method.Invoke(host, arguments)!);
        var result = (int)arguments[5]!; var error = (int)arguments[6]!;
        Console.WriteLine($"derived-protection syscall=mprotect address=0x{address:X}; size={size}; guest={guest}; derived={derived}; ordinary={ordinary}; native_return={result}; immediate_errno={error}");
        if (expectedSuccess) { Assert.Equal(0, result); Assert.Equal(0, error); }
        else { Assert.Equal(-1, result); Assert.True(error > 0); }
    }
}

[SupportedOSPlatform("macos")]
public sealed class MacMappedCopyProtectionNativeTests(ITestOutputHelper output)
{
    [MappedCopyNativeTheory]
    [InlineData("derived-rw")]
    [InlineData("guest-readonly")]
    [InlineData("partial-derived")]
    [InlineData("maximum-failure")]
    public Task ActualMacProtectionIntentWorker(string scenario) =>
        new MacMappedCopyNativeTests(output).ActualMacMappingCopyWorker(scenario);
}
#endif
