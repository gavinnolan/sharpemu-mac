// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class HleDataSymbolsTests
{
    [Fact]
    public void EnvironIsAnEmptyNullTerminatedVector()
    {
        Assert.True(HleDataSymbols.TryGetAddress("+2thxYZ4syk", out var environ));
        var vector = Marshal.ReadIntPtr((nint)environ);
        Assert.NotEqual(0, vector);
        Assert.Equal(0, Marshal.ReadIntPtr(vector));
    }

    [Theory]
    [InlineData(0, "bgAcsbcEznc", "1TDo-ImqkJc")] // __stdinp, _Stdin
    [InlineData(1, "zqJhBxAKfsc", "2sWzhYqFH4E")] // __stdoutp, _Stdout
    [InlineData(2, "as8Od-tH1BI", "H8AprKeZtNg")] // __stderrp, _Stderr
    public void StdioPointersReferTheirFileObjects(int index, string pointerNid, string objectNid)
    {
        Assert.True(HleDataSymbols.TryGetAddress(pointerNid, out var pointer));
        Assert.True(HleDataSymbols.TryGetAddress(objectNid, out var fileObject));
        Assert.Equal(unchecked((nint)fileObject), Marshal.ReadIntPtr((nint)pointer));
        Assert.Equal(fileObject, HleDataSymbols.StandardStreamAddress(index));
        Assert.Contains(pointerNid, HleDataSymbols.EnumerateKnownNids());
        Assert.Contains(objectNid, HleDataSymbols.EnumerateKnownNids());
    }

    [Fact]
    public void StdioObjectsAreDistinct()
    {
        var addresses = Enumerable.Range(0, 3).Select(HleDataSymbols.StandardStreamAddress).ToArray();
        Assert.All(addresses, address => Assert.NotEqual(0UL, address));
        Assert.Equal(3, addresses.Distinct().Count());
        Assert.Equal(0UL, HleDataSymbols.StandardStreamAddress(3));
    }

    [Fact]
    public void EachStdioObjectHoldsTheGuestLibcFileImage()
    {
        // libc.prx copies a 0x1C8-byte FILE image into _Stdin, _Stdout and _Stderr;
        // a smaller object lets the copy overwrite the next object or host heap memory.
        Assert.True(HleDataSymbols.StdioObjectSize >= HleDataSymbols.MinimumGuestFileSize);
        var addresses = Enumerable.Range(0, 3).Select(HleDataSymbols.StandardStreamAddress).Order().ToArray();
        for (var index = 1; index < addresses.Length; index++)
        {
            Assert.True(addresses[index] - addresses[index - 1] >= HleDataSymbols.MinimumGuestFileSize);
        }
    }
}
