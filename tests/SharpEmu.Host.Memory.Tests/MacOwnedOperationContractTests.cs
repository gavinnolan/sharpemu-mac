// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
#if SHARPEMU_HOST_MACOS
using System.Collections;
using System.Reflection;
using System.Runtime.Versioning;
using SharpEmu.HLE.Host;
using SharpEmu.Host.MacOS;
using Xunit;

namespace SharpEmu.Host.Memory.Tests;

// This selection does not initialize Composition/Authority, allocate host pages,
// or execute any native operation. Phantom metadata below is an exclusion oracle
// only, never an allocation receipt or permitted ExecuteOnce input.
[SupportedOSPlatform("macos")]
public sealed class MacOwnedOperationContractTests
{
    private static readonly Type Provider = typeof(MacOSHost).Assembly.GetType("SharpEmu.Host.MacOS.MacHostMemory")!;
    private static readonly Type Darwin = Provider.GetNestedType("Darwin", BindingFlags.NonPublic)!;
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static object? Call(string method, object?[] arguments) => Darwin.GetMethod(method, StaticPrivate)!.Invoke(null, arguments);

    [Theory]
    [InlineData(1, 4096UL, true, 4096UL)]
    [InlineData(64, 16384UL, true, 1048576UL)]
    [InlineData(0, 4096UL, false, 0UL)]
    [InlineData(-1, 4096UL, false, 0UL)]
    [InlineData(65, 16384UL, false, 0UL)]
    [InlineData(int.MaxValue, 16384UL, false, 0UL)]
    [InlineData(1, 8192UL, false, 0UL)]
    [InlineData(1, 0UL, false, 0UL)]
    public void OwnedGeometryHasExplicitNativeArchitectureAndAllocationBounds(int pages, ulong nativePage,
        bool accepted, ulong bytes)
    {
        object?[] arguments = [pages, nativePage, 0UL];
        Assert.Equal(accepted, Call("OwnedGeometry", arguments)); Assert.Equal(bytes, arguments[2]);
    }

    [Theory]
    [InlineData(0x4000UL, 1UL, 4096UL, 0x4000UL, 0x5000UL)]
    [InlineData(0x4001UL, 4096UL, 4096UL, 0x4000UL, 0x6000UL)]
    [InlineData(0x4001UL, 4096UL, 16384UL, 0x4000UL, 0x8000UL)]
    [InlineData(0x3FFFUL, 2UL, 16384UL, 0UL, 0x8000UL)]
    public void EffectEnvelopeCoversActualKernelPagesRatherThanOnlyLogicalGuestPages(ulong address, ulong bytes,
        ulong nativePage, ulong expectedStart, ulong expectedEnd)
    {
        object?[] arguments = [address, bytes, nativePage, 0UL, 0UL];
        Assert.Equal(true, Call("NativeEffectEnvelope", arguments));
        Assert.Equal(expectedStart, arguments[3]); Assert.Equal(expectedEnd, arguments[4]);
    }

    [Theory]
    [InlineData(0UL, 0UL, 4096UL)]
    [InlineData(ulong.MaxValue - 1, 2UL, 4096UL)]
    [InlineData(ulong.MaxValue - 4095, 1UL, 4096UL)]
    [InlineData(0UL, 1UL, 0UL)]
    [InlineData(0UL, 1UL, 12288UL)]
    public void UnrepresentableEffectDeclinesBeforeAnyNativeWork(ulong address, ulong bytes, ulong nativePage)
    {
        object?[] arguments = [address, bytes, nativePage, 0UL, 0UL];
        Assert.Equal(false, Call("NativeEffectEnvelope", arguments));
    }

    [Fact]
    public void CookieExclusionCoversInteriorCrossRangeAndNativePaddingAndPreservesUnmarkedFastPath()
    {
        var gate = Darwin.GetField("Gate", StaticPrivate)!.GetValue(null)!;
        lock (gate)
        {
            var list = (IList)Darwin.GetField("ExclusiveAllocations", StaticPrivate)!.GetValue(null)!;
            var count = Darwin.GetField("_exclusiveAllocationCount", StaticPrivate)!;
            var before = (int)count.GetValue(null)!;
            var nativePage = (ulong)Environment.SystemPageSize;
            var ownerType = Darwin.GetNestedType("OwnedAnonymousAllocation", BindingFlags.Public)!;
            var owner = Activator.CreateInstance(ownerType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                [MacOSHost.CreateMemory(), 1, nativePage, 2U, false], null)!;
            var region = ownerType.GetField("_region", InstanceFields)!.GetValue(owner)!;
            region.GetType().GetField("Base")!.SetValue(region, 0x20000UL);
            list.Add(owner); count.SetValue(null, before + 1);
            try
            {
                Assert.Equal(true, Call("OverlapsOwnedEffectLocked", [0x20001UL, 1UL]));
                Assert.Equal(true, Call("OverlapsOwnedEffectLocked", [0x1FFFFUL, 2UL]));
                Assert.Equal(true, Call("OverlapsOwnedEffectLocked", [0x20000UL + nativePage - 1, 2UL]));
                Assert.Equal(true, Call("OverlapsOwnedEffectLocked", [0UL, 0x20001UL]));
                Assert.Equal(true, Call("OverlapsOwnedEffectLocked", [ulong.MaxValue - 1, 2UL]));
                Assert.Equal(false, Call("OverlapsOwnedEffectLocked", [0x1FFFFUL, 1UL]));
                Assert.Equal(false, Call("OverlapsOwnedEffectLocked", [0x20000UL + nativePage, 1UL]));
            }
            finally { list.Remove(owner); count.SetValue(null, before); }
            if (before == 0) Assert.Equal(false, Call("OverlapsOwnedEffectLocked", [ulong.MaxValue - 1, 2UL]));
        }
    }

    [Fact]
    public void NoncanonicalFactoryCannotAdoptMemoryOrEmitNativeReceiptAndTypesStayPrivate()
    {
        var provider = MacOSHost.CreateMemory(); // constructor only, no safe-startup native probe
        var initialized = HostMemoryAuthority.IsInitialized;
        object?[] arguments = [3, true, 4U, null, null];
        Assert.Equal(false, Provider.GetMethod("TryCreateOwnedAnonymousControl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(provider, arguments));
        Assert.Null(arguments[3]); Assert.Equal(initialized, HostMemoryAuthority.IsInitialized);
        Assert.Equal("NoInvocation", arguments[4]!.GetType().GetProperty("Outcome")!.GetValue(arguments[4])!.ToString());
        Assert.True(Darwin.IsNestedPrivate);
        Assert.DoesNotContain(typeof(MacOSHost).Assembly.GetExportedTypes(), type => type.Name.Contains("OwnedOperation") || type.Name.Contains("OwnedAnonymous"));
    }

    [Fact]
    public void HoleOrUnknownCookieExcludesKernelChosenPlacementAndUnknownUnregisteredQuery()
    {
        var gate = Darwin.GetField("Gate", StaticPrivate)!.GetValue(null)!;
        lock (gate)
        {
            var list = (IList)Darwin.GetField("ExclusiveAllocations", StaticPrivate)!.GetValue(null)!;
            var count = Darwin.GetField("_exclusiveAllocationCount", StaticPrivate)!;
            var before = (int)count.GetValue(null)!;
            var pageSize = (ulong)Environment.SystemPageSize;
            var type = Darwin.GetNestedType("OwnedAnonymousAllocation", BindingFlags.Public)!;
            var provider = MacOSHost.CreateMemory();
            var owner = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                [provider, 2, pageSize, 2U, false], null)!;
            var region = type.GetField("_region", InstanceFields)!.GetValue(owner)!;
            region.GetType().GetField("Base")!.SetValue(region, 0x20000UL);
            var mapped = (bool[])type.GetField("_mapped", InstanceFields)!.GetValue(owner)!;
            var unknown = (bool[])type.GetField("_unknown", InstanceFields)!.GetValue(owner)!;
            Array.Fill(mapped, true);
            list.Add(owner); count.SetValue(null, before + 1);
            try
            {
                Assert.Equal(false, Call("NewAnonymousPlacementExcludedLocked", []));
                mapped[1] = false;
                Assert.Equal(true, Call("NewAnonymousPlacementExcludedLocked", []));
                mapped[1] = true; unknown[1] = true;
                Assert.Equal(true, Call("NewAnonymousPlacementExcludedLocked", []));
                Assert.Equal(false, Call("ContainsUnknownOwnedPageLocked", [0x20000UL]));
                Assert.Equal(true, Call("ContainsUnknownOwnedPageLocked", [0x20000UL + pageSize]));
                // QueryRaw is the managed logical lookup, not an OS query. No
                // Region was inserted: independent retained cookie still wins.
                Assert.Equal((nuint)0, provider.QueryRaw(0x20000UL + pageSize, out var info));
                Assert.Equal(0UL, info.RegionSize);
            }
            finally { list.Remove(owner); count.SetValue(null, before); }
        }
    }
}
#endif
