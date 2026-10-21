// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using SharpEmu.HLE;
using SharpEmu.HLE.GpuMemory;
using Xunit;
using Xunit.Abstractions;
using System.Runtime.InteropServices;

namespace SharpEmu.Host.Memory.Tests;

// Actual PageGuard/permission ledger; recording address space only, no syscall.
public sealed class PageGuardProtectionIntentTests(ITestOutputHelper output)
{
    private void Marker()
    {
        output.WriteLine($"page-guard protection-intent managed-only architecture={RuntimeInformation.ProcessArchitecture}; framework={RuntimeInformation.FrameworkDescription}");
        var expected = Environment.GetEnvironmentVariable("SHARPEMU_TEST_EXPECT_ARCH");
        if (!string.IsNullOrEmpty(expected)) Assert.Equal(expected, RuntimeInformation.ProcessArchitecture.ToString());
    }
    private const ulong Address = 0x7000000000, Page = 4096;
    private const GuestPageProtection RW = GuestPageProtection.Read | GuestPageProtection.Write;
    private readonly record struct Applied(ulong Address, ulong Size, GuestPageProtection Guest,
        GuestPageProtection Derived, bool Ordinary);
    private class LegacySpace : IGuestAddressSpace
    {
        internal readonly List<(ulong, ulong, GuestPageProtection)> LegacyCalls = new();
        public ulong ProtectionPageSize => Page;
        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection)
        { LegacyCalls.Add((address, size, protection)); return true; }
        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address) => throw new NotSupportedException();
        public bool TryFreeGuestMemory(ulong address) => throw new NotSupportedException();
        public ulong AllocateAt(ulong address, ulong size, bool executable = true, bool allowAlternative = true) => throw new NotSupportedException();
        public bool TryBackFixedRange(ulong address, ulong size, bool executable) => throw new NotSupportedException();
        public bool TryAllocateAtOrAbove(ulong address, ulong size, bool executable, ulong alignment, out ulong actualAddress) => throw new NotSupportedException();
        public bool TryEnsureRangeCommitted(ulong address, ulong size) => throw new NotSupportedException();
    }
    private sealed class ExplicitSpace : LegacySpace, IGuestDerivedProtectionAddressSpace
    {
        internal readonly List<Applied> Calls = new();
        public bool TryApplyDerivedProtection(ulong address, ulong size, GuestPageProtection guest,
            GuestPageProtection derived, bool ordinary)
        { Calls.Add(new(address, size, guest, derived, ordinary)); return true; }
    }

    [Fact]
    public void WatcherRestrictionsAndRestorationCarryOriginalGuestRightsWithoutOrdinaryEnrollment()
    {
        Marker(); var space = new ExplicitSpace(); using var pages = new PageGuard(space);
        pages.Permissions.Set(Address, Page * 2, RW); pages.Reapply(Address, Page * 2); space.Calls.Clear();
        pages.AddWatch(Address, Page * 2, false);
        Assert.Equal(new Applied(Address, Page * 2, RW, GuestPageProtection.Read, false), Assert.Single(space.Calls));
        space.Calls.Clear(); pages.RemoveWatch(Address, Page * 2, false);
        Assert.Equal(new Applied(Address, Page * 2, RW, RW, false), Assert.Single(space.Calls));
        Assert.Empty(space.LegacyCalls);
    }

    [Fact]
    public void BaseReadonlyChangePropagatesEvenWhenWatchedNativeReadonlyIsUnchanged()
    {
        Marker(); var space = new ExplicitSpace(); using var pages = new PageGuard(space);
        pages.Permissions.Set(Address, Page, RW); pages.Reapply(Address, Page); pages.AddWatch(Address, Page, false);
        space.Calls.Clear(); pages.Permissions.Set(Address, Page, GuestPageProtection.Read); pages.Reapply(Address, Page);
        Assert.Equal(new Applied(Address, Page, GuestPageProtection.Read, GuestPageProtection.Read, true), Assert.Single(space.Calls));
        pages.RemoveWatch(Address, Page, false);
    }

    [Fact]
    public void EqualNoAccessWatcherRunsCannotMergeDifferentBaseEntitlements()
    {
        Marker(); var space = new ExplicitSpace(); using var pages = new PageGuard(space);
        pages.Permissions.Set(Address, Page, RW); pages.Permissions.Set(Address + Page, Page, GuestPageProtection.Read);
        pages.AddWatch(Address, Page * 2, true);
        Assert.Equal(new[] { new Applied(Address, Page, RW, GuestPageProtection.None, false),
            new Applied(Address + Page, Page, GuestPageProtection.Read, GuestPageProtection.None, false) }, space.Calls);
        space.Calls.Clear(); pages.Reapply(Address, Page * 2);
        Assert.Equal(new[] { new Applied(Address, Page, RW, GuestPageProtection.None, true),
            new Applied(Address + Page, Page, GuestPageProtection.Read, GuestPageProtection.None, true) }, space.Calls);
        pages.RemoveWatch(Address, Page * 2, true);
    }

    [Fact]
    public void NonparticipatingAddressSpaceKeepsExistingDerivedProtectionCalls()
    {
        Marker(); var space = new LegacySpace(); using var pages = new PageGuard(space);
        pages.Permissions.Set(Address, Page, RW); pages.AddWatch(Address, Page, false); pages.RemoveWatch(Address, Page, false);
        Assert.Equal(new[] { (Address, Page, GuestPageProtection.Read), (Address, Page, RW) }, space.LegacyCalls);
    }
}
